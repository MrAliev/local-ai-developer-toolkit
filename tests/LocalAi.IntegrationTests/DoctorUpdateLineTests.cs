using System.Text;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The `update` line in `localai doctor`.
///
/// Two rules it exists to keep. It never reaches the network — `doctor` answers about this
/// machine, and a diagnostic that quietly called GitHub would be a second, unthrottled caller
/// of the thing the policy rations. And a newer release is a warning, never a failure: an
/// installation one version behind works perfectly well, and a diagnostic that exits non-zero
/// over it teaches whoever wired it into a script to ignore the exit code.
/// </summary>
public sealed class DoctorUpdateLineTests : IDisposable
{
    private const string Directory50 = "be08af033a2a";
    private const string Directory51 = "467ed5f0f9bf";

    private static readonly DateTimeOffset Checked =
        new(2026, 8, 31, 9, 30, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-doctor-update-" + Guid.NewGuid().ToString("N"));

    public DoctorUpdateLineTests() => Install(Directory50, "0.1.50");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void A_machine_that_never_opted_in_is_told_how_to()
    {
        var check = Check();

        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.Contains("check disabled", check.Detail, StringComparison.Ordinal);
        Assert.Contains(
            "localai policy set --update-check on",
            check.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_enabled_check_that_has_not_run_says_so_without_alarm()
    {
        Enable();

        var check = Check();

        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.Contains("nothing has been checked yet", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Being_up_to_date_is_reported_as_ok()
    {
        Enable();
        Learned("0.1.50", Directory50);

        var check = Check();

        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.Contains("up to date at 0.1.50", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_release_is_a_warning_and_names_where_to_read_about_it()
    {
        Enable();
        Learned("0.1.51", Directory51);

        var report = DoctorCommand.Inspect(root);
        var check = report.Checks.Single(c => c.Name == "update");

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Contains("0.1.51 is available", check.Detail, StringComparison.Ordinal);
        Assert.Contains("this installation is 0.1.50", check.Detail, StringComparison.Ordinal);
        Assert.Contains("https://example.invalid", check.Detail, StringComparison.Ordinal);
        // A version behind is not a fault: the exit code is what scripts read.
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void A_check_that_found_nothing_to_believe_is_not_an_error()
    {
        Enable();
        new UpdateCheckStateStore(root).Write(
            new UpdateCheckState(1, UpdateCheckStatus.Unavailable, Checked, null, null));

        var check = Check();

        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.Contains("nothing to believe", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unverified answer is not information, however new the version inside it looks — the
    /// same rule the state file enforces, checked here because this is where a person reads it.
    /// </summary>
    [Fact]
    public void An_unverified_version_never_becomes_a_warning()
    {
        Enable();
        new UpdateCheckStateStore(root).Write(
            new UpdateCheckState(1, UpdateCheckStatus.Unavailable, Checked, "9.9.9", null));

        var check = Check();

        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.DoesNotContain("9.9.9", check.Detail, StringComparison.Ordinal);
    }

    private DoctorCheck Check() =>
        DoctorCommand.Inspect(root).Checks.Single(check => check.Name == "update");

    private void Enable() =>
        new UpdateCheckPolicyStore(root).Write(
            UpdateCheckPolicy.Default with { Enabled = true });

    private void Learned(string version, string versionDirectory) =>
        new UpdateCheckStateStore(root).Write(new UpdateCheckState(
            1,
            UpdateCheckStatus.Verified,
            Checked,
            version,
            "https://example.invalid/releases/tag/v" + version,
            versionDirectory));

    /// <summary>
    /// An installation as LocalAiPackageInstaller leaves one: the pointer names the version
    /// *directory*, and a separate record says which release that directory came from. The
    /// earlier fixture put the release version in the pointer, which is why every one of these
    /// tests passed while the product reported an available update as "up to date" (#255).
    /// </summary>
    private void Install(string directory, string release)
    {
        var versionDirectory = Path.Combine(root, "bin", "versions", directory);
        Directory.CreateDirectory(versionDirectory);
        foreach (var file in LocalAiPackageLayout.RequiredFiles)
        {
            File.WriteAllText(Path.Combine(versionDirectory, file), "binary");
        }

        Directory.CreateDirectory(Path.Combine(root, "bin", "launcher"));
        File.WriteAllText(
            Path.Combine(root, "bin", "launcher", LocalAiPackageLayout.StableLauncherFile),
            "binary");
        File.WriteAllBytes(
            Path.Combine(root, "bin", "current.json"),
            CurrentPointerSnapshot.CreateCanonicalBytes(directory));
        new InstalledReleaseStore(Path.Combine(root, "bin")).Write(directory, release);
    }
}
