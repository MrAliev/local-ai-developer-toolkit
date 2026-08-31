using System.Diagnostics;
using CodeSearch.Core.Indexing;
using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

/// <summary>
/// An overlay's name promises an exact snapshot, dirty hash included, and the build reads
/// the live worktree for minutes (#197). An edit landing mid-build must discard the
/// artifacts rather than leave them to poison the moment the worktree returns to the
/// captured state.
/// </summary>
public sealed class OverlayDriftTests : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(),
        "localai-overlay-drift-" + Guid.NewGuid().ToString("N"));

    private readonly string _repository;
    private readonly string _runtime;

    public OverlayDriftTests()
    {
        _repository = Path.Combine(_work, "repo");
        _runtime = Path.Combine(_work, "runtime");
        Directory.CreateDirectory(_repository);
        Git("init", "-b", "main");
        Git("config", "user.email", "drift@test");
        Git("config", "user.name", "drift");
        File.WriteAllText(Path.Combine(_repository, "A.cs"), "class A {}");
        Git("add", "-A");
        Git("commit", "-m", "init");
    }

    [Fact]
    public void An_unchanged_worktree_keeps_its_overlay()
    {
        var captured = RuntimeIndexLayout.Inspect(_repository, _runtime);
        var (overlay, semantic) = WriteArtifacts();

        var discarded = CodeSearchSyncCommand.DiscardDriftedOverlay(
            captured,
            overlay,
            semantic,
            _runtime);

        Assert.False(discarded);
        Assert.True(File.Exists(overlay));
        Assert.True(File.Exists(semantic));
    }

    [Fact]
    public void An_edit_during_the_build_discards_the_artifacts()
    {
        var captured = RuntimeIndexLayout.Inspect(_repository, _runtime);
        var (overlay, semantic) = WriteArtifacts();
        // The edit lands after capture, exactly where a build would still be reading files.
        File.WriteAllText(Path.Combine(_repository, "A.cs"), "class A { int Changed; }");

        var discarded = CodeSearchSyncCommand.DiscardDriftedOverlay(
            captured,
            overlay,
            semantic,
            _runtime);

        Assert.True(discarded);
        Assert.False(File.Exists(overlay));
        Assert.False(File.Exists(semantic));
        Assert.False(File.Exists(overlay + ".embedding-checkpoint"));
    }

    [Fact]
    public void A_worktree_that_vanished_counts_as_drifted()
    {
        var captured = RuntimeIndexLayout.Inspect(_repository, _runtime);
        var (overlay, semantic) = WriteArtifacts();
        DeleteTree(_repository);

        var discarded = CodeSearchSyncCommand.DiscardDriftedOverlay(
            captured,
            overlay,
            semantic,
            _runtime);

        Assert.True(discarded);
        Assert.False(File.Exists(overlay));
        Assert.False(File.Exists(semantic));
    }

    private (string Overlay, string Semantic) WriteArtifacts()
    {
        var artifacts = Path.Combine(_work, "artifacts");
        Directory.CreateDirectory(artifacts);
        var overlay = Path.Combine(artifacts, "clean.cidx");
        var semantic = Path.Combine(artifacts, "clean.ssidx");
        File.WriteAllText(overlay, "overlay");
        File.WriteAllText(semantic, "semantic");
        File.WriteAllText(overlay + ".embedding-checkpoint", "checkpoint");
        return (overlay, semantic);
    }

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: " +
            process.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        try
        {
            DeleteTree(_work);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Git object files are read-only; a recursive delete refuses them as they are.</summary>
    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     path,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
