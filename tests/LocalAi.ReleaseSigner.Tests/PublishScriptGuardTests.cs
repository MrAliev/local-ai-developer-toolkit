using System.Diagnostics;

namespace LocalAi.ReleaseSigner.Tests;

/// <summary>
/// The publish script deletes its publish root recursively. It used to delete the raw
/// -PublishRoot parameter before canonicalizing it, so a mistyped value — a drive root,
/// '..', a wrong variable — could take an arbitrary tree with it (#196). These tests run
/// the real script inside a sandbox that mimics the repository layout and prove two
/// things for every dangerous input: the script refuses before deleting anything, and a
/// sentinel file at the would-be target survives.
///
/// The sandbox exists because testing refusal against the real repository would bet the
/// repository on the guard under test.
/// </summary>
public sealed class PublishScriptGuardTests : IDisposable
{
    private const string RefusalMarker = "PublishRoot must";

    private readonly string _sandbox = Path.Combine(
        Path.GetTempPath(),
        "localai-publish-guard-" + Guid.NewGuid().ToString("N"));

    private readonly string _root;
    private readonly string _sibling;

    public PublishScriptGuardTests()
    {
        _root = Path.Combine(_sandbox, "root");
        _sibling = Path.Combine(_sandbox, "sibling");
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        Directory.CreateDirectory(_sibling);
        File.Copy(
            RealScriptPath(),
            Path.Combine(_root, "scripts", "publish.ps1"));
        File.WriteAllText(Path.Combine(_sandbox, "sentinel-parent.txt"), "alive");
        File.WriteAllText(Path.Combine(_root, "sentinel-root.txt"), "alive");
        File.WriteAllText(Path.Combine(_sibling, "sentinel-sibling.txt"), "alive");
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public void A_relative_escape_is_refused_and_deletes_nothing(string publishRoot)
    {
        var run = RunScript(publishRoot);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(RefusalMarker, run.Output, StringComparison.Ordinal);
        AssertSentinelsAlive();
    }

    [Fact]
    public void An_absolute_path_outside_the_repository_is_refused()
    {
        var run = RunScript(_sibling);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(RefusalMarker, run.Output, StringComparison.Ordinal);
        AssertSentinelsAlive();
    }

    [Fact]
    public void A_drive_root_is_refused()
    {
        var run = RunScript(Path.GetPathRoot(_sandbox)!);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(RefusalMarker, run.Output, StringComparison.Ordinal);
        AssertSentinelsAlive();
    }

    [Fact]
    public void A_reparse_point_publish_root_is_refused()
    {
        var junction = Path.Combine(_root, "publish");
        var creation = Run(
            "pwsh",
            ["-NoProfile", "-Command", $"New-Item -ItemType Junction -Path '{junction}' -Target '{_sibling}' | Out-Null"]);
        Assert.True(creation.ExitCode == 0, "Could not create a junction: " + creation.Output);

        var run = RunScript("publish");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("reparse point", run.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_sibling, "sentinel-sibling.txt")));
    }

    /// <summary>
    /// The guard must not refuse the one value every real run uses. In the sandbox the
    /// script proceeds past validation and fails later for lack of a solution — that
    /// later failure is fine; what matters is that the refusal marker never appears.
    /// </summary>
    [Fact]
    public void The_default_publish_subtree_passes_the_guard()
    {
        var run = RunScript("publish");

        Assert.DoesNotContain(RefusalMarker, run.Output, StringComparison.Ordinal);
        AssertSentinelsAlive();
    }

    private void AssertSentinelsAlive()
    {
        Assert.True(File.Exists(Path.Combine(_sandbox, "sentinel-parent.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "sentinel-root.txt")));
        Assert.True(File.Exists(Path.Combine(_sibling, "sentinel-sibling.txt")));
    }

    private (int ExitCode, string Output) RunScript(string publishRoot) =>
        Run(
            "pwsh",
            [
                "-NoProfile",
                "-File", Path.Combine(_root, "scripts", "publish.ps1"),
                "-ReleaseVersion", "9.9.9",
                "-PublishRoot", publishRoot,
            ]);

    private static (int ExitCode, string Output) Run(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private static string RealScriptPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "scripts",
                "publish-localai-release-win-x64-self-contained.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "The publish script was not found above the test output directory.");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_sandbox, recursive: true);
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
}
