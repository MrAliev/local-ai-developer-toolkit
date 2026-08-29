using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;

namespace CodeSearch.Core.Semantics;

public sealed class LoadedRoslynSolution : IAsyncDisposable
{
    private readonly MSBuildWorkspace workspace;
    private readonly Solution solution;
    private readonly RoslynBuildHostLease? buildHostLease;

    internal LoadedRoslynSolution(
        MSBuildWorkspace workspace,
        Solution solution,
        RoslynBuildHostLease? buildHostLease = null,
        IReadOnlyList<string>? uncoveredProjects = null)
    {
        this.workspace = workspace;
        this.solution = solution;
        this.buildHostLease = buildHostLease;
        UncoveredProjects = uncoveredProjects ?? [];
    }

    /// <summary>
    /// Projects in this repository that this load did not cover.
    ///
    /// Only ever non-empty when there is no solution file: a repository with one is opened whole,
    /// but without one a single project is chosen and the rest get no precise navigation at all.
    /// Reported as a list rather than left to a console line, because the caller has to be able
    /// to fail on it -- a repository where nine projects out of ten are missing otherwise reads
    /// exactly like one where none are.
    /// </summary>
    public IReadOnlyList<string> UncoveredProjects { get; }

    public Task<SemanticIndex> BuildIndexAsync(
        string repositoryRoot,
        SemanticIndexBuildIdentity identity,
        CancellationToken cancellationToken = default) =>
        new RoslynSemanticIndexer().BuildAsync(
            solution,
            repositoryRoot,
            identity,
            cancellationToken);

    public ValueTask DisposeAsync()
    {
        workspace.Dispose();
        buildHostLease?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Loads the repository's primary solution using the installed dotnet SDK.</summary>
public static class RoslynSolutionLoader
{
    private static readonly object RegistrationLock = new();
    private static readonly IDictionary<string, string> WorkspaceProperties =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Semantic indexing only evaluates the project graph. Vulnerability audit
            // warnings must not become build-stopping errors in repositories that use
            // TreatWarningsAsErrors; production restore/build remains unchanged.
            ["NuGetAudit"] = "false",
        };

    public static async Task<LoadedRoslynSolution?> LoadAsync(
        string repositoryRoot,
        Action<string>? diagnostic = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var candidates = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
                (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) &&
                !HasBuildOutputSegment(root, path))
            .OrderBy(path => Depth(root, path))
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var projectCandidates = candidates.Length == 0
            ? Directory
                .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !HasBuildOutputSegment(root, path))
                .OrderBy(path => Depth(root, path))
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : [];
        var selected = candidates.FirstOrDefault() ?? projectCandidates.FirstOrDefault();
        if (selected is null)
        {
            return null;
        }

        await RestoreProjectDependenciesAsync(
            selected,
            diagnostic,
            cancellationToken).ConfigureAwait(false);

        using (await RoslynBuildHostLease.AcquireLoadLockAsync(cancellationToken))
        {
            EnsureMsBuildRegistered();
            var buildHostLease = RoslynBuildHostLease.CreateIfNeeded();
            var originalBaseDirectory = AppContext.GetData(
                RoslynBuildHostLease.BaseDirectoryDataName);
            if (buildHostLease is not null)
            {
                AppContext.SetData(
                    RoslynBuildHostLease.BaseDirectoryDataName,
                    buildHostLease.RootPath);
            }

            var workspace = MSBuildWorkspace.Create(WorkspaceProperties);
            workspace.RegisterWorkspaceFailedHandler(
                args => diagnostic?.Invoke(args.Diagnostic.Message));
            try
            {
                var solution = selected.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    ? (await workspace.OpenProjectAsync(
                        selected,
                        cancellationToken: cancellationToken)).Solution
                    : await workspace.OpenSolutionAsync(
                        selected,
                        cancellationToken: cancellationToken);
                if (candidates.Length + projectCandidates.Length > 1)
                {
                    diagnostic?.Invoke(
                        $"Multiple C# entry points found; semantic indexing selected '{selected}'.");
                }

                // Only the project fallback leaves anything out. A solution is opened whole, so
                // several solutions in one tree means the others were not asked for rather than
                // silently dropped.
                var uncovered = projectCandidates
                    .Where(path => !string.Equals(path, selected, StringComparison.Ordinal))
                    .ToArray();
                return new LoadedRoslynSolution(
                    workspace,
                    solution,
                    buildHostLease,
                    uncovered);
            }
            catch
            {
                workspace.Dispose();
                buildHostLease?.Dispose();
                throw;
            }
            finally
            {
                AppContext.SetData(
                    RoslynBuildHostLease.BaseDirectoryDataName,
                    originalBaseDirectory);
            }
        }
    }

    private static void EnsureMsBuildRegistered()
    {
        lock (RegistrationLock)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }

    private static async Task RestoreProjectDependenciesAsync(
        string entryPoint,
        Action<string>? diagnostic,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "restore",
                     entryPoint,
                     "--nologo",
                     "--verbosity",
                     "quiet",
                     "-p:NuGetAudit=false",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                diagnostic?.Invoke(
                    $"Project dependency restore did not start for '{entryPoint}'.");
                return;
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            diagnostic?.Invoke(
                $"Project dependencies were not restored: {exception.Message}");
            return;
        }

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            diagnostic?.Invoke(
                $"Project dependency restore timed out for '{entryPoint}'.");
            return;
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            detail = detail.Trim();
            if (detail.Length > 2_000)
            {
                detail = detail[^2_000..];
            }

            diagnostic?.Invoke(
                $"Project dependency restore failed for '{entryPoint}' " +
                $"with exit code {process.ExitCode}: {detail}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static bool HasBuildOutputSegment(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));

    private static int Depth(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Count(character =>
                character == Path.DirectorySeparatorChar ||
                character == Path.AltDirectorySeparatorChar);
}
