using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeSearch.Core.Semantics;

public sealed class LoadedRoslynSolution : IAsyncDisposable
{
    private readonly MSBuildWorkspace workspace;
    private readonly Solution solution;
    private readonly RoslynBuildHostLease? buildHostLease;

    internal LoadedRoslynSolution(
        MSBuildWorkspace workspace,
        Solution solution,
        RoslynBuildHostLease? buildHostLease = null)
    {
        this.workspace = workspace;
        this.solution = solution;
        this.buildHostLease = buildHostLease;
    }

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

            var workspace = MSBuildWorkspace.Create();
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

                return new LoadedRoslynSolution(workspace, solution, buildHostLease);
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
