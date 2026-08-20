using System.Diagnostics;
using System.Text.Json;
using CodeSearch.Core.Semantics;
using LocalAi.Cli;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

public sealed class CodeSearchSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-sync-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A runtime of this test's own. A sync writes a manifest and a progress file before it can
    /// fail, and doing that in the real %LOCALAPPDATA%\LocalAi puts this test in contention with
    /// whatever else is indexing at the time.
    /// </summary>
    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "localai-sync-runtime-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, "refs/heads/main")]
    [InlineData(true, "refs/heads/dev")]
    public async Task Sync_selects_local_mainline_without_a_mainline_worktree(
        bool createDev,
        string expectedRef)
    {
        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main", ".");
        Git("config", "user.email", "localai-tests@example.invalid");
        Git("config", "user.name", "LocalAi Tests");
        Git("commit", "--allow-empty", "-m", "main");
        if (createDev)
        {
            Git("branch", "dev");
        }

        Git("switch", "-c", "feature");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CodeSearchSyncCommand.ExecuteAsync(
                _root,
                cancellationToken: TestContext.Current.CancellationToken,
                runtimeRoot: _runtimeRoot));
        Assert.Contains("Nothing was embedded", error.Message);

        var commonDirectory = Git("rev-parse", "--path-format=absolute", "--git-common-dir");
        var identity = RepositoryIdentity.FromCommonDirectory(commonDirectory);
        var repositoryRuntimeRoot = Path.Combine(
            _runtimeRoot,
            "repositories",
            identity.Id);
        var manifest = Assert.IsType<LocalAi.Contracts.RepositoryManifest>(
            new RepositoryManifestStore(repositoryRuntimeRoot).Read());
        Assert.Equal(expectedRef, manifest.DevRef);
        Assert.Equal(
            LocalAi.Contracts.RepositoryIndexState.Initializing,
            manifest.State);
    }

    [Fact]
    public void Semantic_generation_accepts_succeeded_and_skipped_adapters()
    {
        CodeSearchSyncCommand.EnsureSemanticAdaptersSucceeded(
            [
                Status("typescript", SemanticAdapterState.Succeeded, "indexed"),
                Status("python", SemanticAdapterState.Skipped, "no files"),
            ]);
    }

    [Fact]
    public void Semantic_generation_rejects_failed_adapters_before_publish()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodeSearchSyncCommand.EnsureSemanticAdaptersSucceeded(
                [
                    Status("typescript", SemanticAdapterState.Failed, "bad shim"),
                    Status("python", SemanticAdapterState.Failed, "bad interpreter"),
                ]));

        Assert.Contains("typescript: bad shim", exception.Message);
        Assert.Contains("python: bad interpreter", exception.Message);
        Assert.Contains("not published", exception.Message);
    }

    [Fact]
    public void A_branch_is_not_blocked_by_a_failed_adapter_but_is_told_about_it()
    {
        // The decision, not an oversight: the base generation refuses to publish when an adapter
        // fails, because its boundaries are immutable, and a branch carries on, because refusing
        // would fail the post-commit hook on every commit until someone repairs a Node package.
        // What a branch owes in exchange is saying which worktree it just degraded.
        var written = CaptureError(() =>
            CodeSearchSyncCommand.WarnDegradedSemanticOverlay(
                [
                    Status("typescript", SemanticAdapterState.Failed, "bad shim"),
                    Status("python", SemanticAdapterState.Skipped, "no files"),
                ],
                @"D:\repo\worktree"));

        Assert.Contains(@"D:\repo\worktree", written);
        Assert.Contains("typescript: bad shim", written);
        Assert.Contains("line window", written);
        // Skipped is not failed: a repository with no Python is not a degraded repository.
        Assert.DoesNotContain("python", written);
    }

    [Fact]
    public void A_branch_whose_adapters_worked_says_nothing()
    {
        var written = CaptureError(() =>
            CodeSearchSyncCommand.WarnDegradedSemanticOverlay(
                [
                    Status("typescript", SemanticAdapterState.Succeeded, "indexed"),
                    Status("python", SemanticAdapterState.Skipped, "no files"),
                ],
                @"D:\repo\worktree"));

        Assert.Empty(written);
    }

    private static string CaptureError(Action action)
    {
        var original = Console.Error;
        var writer = new StringWriter();
        Console.SetError(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetError(original);
        }

        return writer.ToString();
    }

    [Fact]
    public void Synthetic_typescript_project_includes_javascript_without_touching_repository()
    {
        Directory.CreateDirectory(_root);
        var relative = Path.Combine("wwwroot", "app.js");
        var source = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "window.app = true;");

        var workspace = CodeSearchSyncCommand.CreateSyntheticTypeScriptWorkspace(
            _root,
            [relative]);
        try
        {
            Assert.Equal(
                Path.GetTempPath(),
                Path.GetDirectoryName(workspace) + Path.DirectorySeparatorChar);
            var configPath = Path.Combine(workspace, "tsconfig.json");
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            Assert.True(config.RootElement
                .GetProperty("compilerOptions")
                .GetProperty("allowJs")
                .GetBoolean());
            Assert.Equal(
                relative.Replace('\\', '/'),
                Assert.Single(config.RootElement.GetProperty("files").EnumerateArray())
                    .GetString());
            Assert.True(File.Exists(Path.Combine(workspace, relative)));
            Assert.False(File.Exists(Path.Combine(_root, "tsconfig.json")));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_runtimeRoot))
        {
            Directory.Delete(_runtimeRoot, recursive: true);
        }

        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(
                         _root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git exited with {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout.Trim();
    }

    private static SemanticAdapterStatus Status(
        string name,
        SemanticAdapterState state,
        string message) => new(name, state, message, 0);
}
