using System.Diagnostics;
using LocalAi.Cli;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

public sealed class CodeSearchSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-sync-" + Guid.NewGuid().ToString("N"));
    private string? _repositoryRuntimeRoot;

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
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Nothing was embedded", error.Message);

        var commonDirectory = Git("rev-parse", "--path-format=absolute", "--git-common-dir");
        var identity = RepositoryIdentity.FromCommonDirectory(commonDirectory);
        _repositoryRuntimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalAi",
            "repositories",
            identity.Id);
        var manifest = Assert.IsType<LocalAi.Contracts.RepositoryManifest>(
            new RepositoryManifestStore(_repositoryRuntimeRoot).Read());
        Assert.Equal(expectedRef, manifest.DevRef);
        Assert.Equal(
            LocalAi.Contracts.RepositoryIndexState.Initializing,
            manifest.State);
    }

    public void Dispose()
    {
        if (_repositoryRuntimeRoot is not null &&
            Directory.Exists(_repositoryRuntimeRoot))
        {
            Directory.Delete(_repositoryRuntimeRoot, recursive: true);
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
}
