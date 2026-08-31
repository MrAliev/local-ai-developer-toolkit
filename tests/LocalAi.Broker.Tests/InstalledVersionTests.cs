using System.Text;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;

namespace LocalAi.Broker.Tests;

/// <summary>
/// What an installation knows about itself, and the comparison every surface asks of it.
///
/// The defect this pins (#255) was not a wrong comparison — it was a comparison between two
/// different things. The pointer records a version *directory*, which is a commit id, and the
/// manifest names a release *version*; asking whether "0.1.51" is newer than "467ed5f0f9bf"
/// fails to parse and answers "no", so `doctor` reported an installation as up to date while
/// the state file beside it named a newer release.
///
/// Every fixture here writes the pointer the way LocalAiPackageInstaller writes it — a
/// directory name — because the old tests wrote a release version there and pinned an
/// assumption instead of the system.
/// </summary>
public sealed class InstalledVersionTests : IDisposable
{
    private const string Directory51 = "467ed5f0f9bf";
    private const string Directory50 = "be08af033a2a";

    private static readonly DateTimeOffset Checked =
        new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-installed-version-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void A_machine_with_nothing_installed_knows_nothing()
    {
        var installed = InstalledVersionReader.Read(root);

        Assert.False(installed.Exists);
        Assert.Null(installed.DisplayName);
    }

    [Fact]
    public void The_pointer_alone_names_the_directory_and_no_release()
    {
        WritePointer(Directory51);

        var installed = InstalledVersionReader.Read(root);

        Assert.True(installed.Exists);
        Assert.Equal(Directory51, installed.VersionDirectory);
        Assert.Null(installed.ReleaseVersion);
        // The directory is a poor name for a version, but it is the true one.
        Assert.Equal(Directory51, installed.DisplayName);
    }

    [Fact]
    public void A_recorded_release_is_reported_as_the_version()
    {
        WritePointer(Directory51);
        Record(Directory51, "0.1.51");

        var installed = InstalledVersionReader.Read(root);

        Assert.Equal("0.1.51", installed.ReleaseVersion);
        Assert.Equal("0.1.51", installed.DisplayName);
    }

    /// <summary>
    /// A rollback moves the pointer without a manifest, leaving the record describing a
    /// directory that is no longer active. Believing it would name the wrong release with
    /// complete confidence.
    /// </summary>
    [Fact]
    public void A_record_describing_another_directory_is_ignored()
    {
        WritePointer(Directory50);
        Record(Directory51, "0.1.51");

        var installed = InstalledVersionReader.Read(root);

        Assert.Equal(Directory50, installed.VersionDirectory);
        Assert.Null(installed.ReleaseVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"schemaVersion":9,"versionDirectory":"467ed5f0f9bf","releaseVersion":"0.1.51"}""")]
    public void An_unreadable_record_is_ignored(string content)
    {
        WritePointer(Directory51);
        File.WriteAllText(
            Path.Combine(root, "bin", InstalledRelease.FileName),
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.Null(InstalledVersionReader.Read(root).ReleaseVersion);
    }

    [Fact]
    public void A_pointer_with_a_byte_order_mark_still_reads()
    {
        System.IO.Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(
            Path.Combine(root, "bin", "current.json"),
            """{"schemaVersion":1,"version":"467ed5f0f9bf"}""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(Directory51, InstalledVersionReader.Read(root).VersionDirectory);
    }

    // ---------------------------------------------------------------- comparison

    /// <summary>
    /// The exact defect: a verified newer release against an installation that records only
    /// its directory. Before the shared comparison this answered "up to date".
    /// </summary>
    [Fact]
    public void A_newer_release_is_seen_even_when_the_release_version_was_never_recorded()
    {
        WritePointer(Directory50);

        var availability = UpdateComparison.Compare(
            Verified("0.1.51", Directory51),
            InstalledVersionReader.Read(root));

        Assert.Equal(UpdateAvailability.Available, availability);
    }

    [Fact]
    public void The_release_that_is_installed_is_not_offered_again()
    {
        WritePointer(Directory51);

        var availability = UpdateComparison.Compare(
            Verified("0.1.51", Directory51),
            InstalledVersionReader.Read(root));

        Assert.Equal(UpdateAvailability.UpToDate, availability);
    }

    [Fact]
    public void With_the_release_recorded_the_comparison_is_by_version()
    {
        WritePointer(Directory50);
        Record(Directory50, "0.1.9");

        // Directories differ and versions differ; the version comparison is the one that must
        // decide, because "0.1.9" is behind "0.1.10" and a string comparison says otherwise.
        var availability = UpdateComparison.Compare(
            Verified("0.1.10", Directory51),
            InstalledVersionReader.Read(root));

        Assert.Equal(UpdateAvailability.Available, availability);
    }

    [Fact]
    public void An_installation_ahead_of_the_published_release_is_up_to_date()
    {
        WritePointer(Directory51);
        Record(Directory51, "0.1.52");

        var availability = UpdateComparison.Compare(
            Verified("0.1.51", Directory50),
            InstalledVersionReader.Read(root));

        Assert.Equal(UpdateAvailability.UpToDate, availability);
    }

    [Fact]
    public void An_unverified_state_is_never_compared()
    {
        WritePointer(Directory50);

        var availability = UpdateComparison.Compare(
            new UpdateCheckState(1, UpdateCheckStatus.Unavailable, Checked, "9.9.9", null, "ffff"),
            InstalledVersionReader.Read(root));

        Assert.Equal(UpdateAvailability.Unknown, availability);
    }

    /// <summary>
    /// A state written before the directory was recorded, read by an installation that never
    /// recorded its release either: nothing can be compared, and the honest answer is unknown
    /// rather than the reassuring one.
    /// </summary>
    [Fact]
    public void Two_unknowns_produce_unknown_rather_than_up_to_date()
    {
        WritePointer(Directory50);

        var availability = UpdateComparison.Compare(
            new UpdateCheckState(1, UpdateCheckStatus.Verified, Checked, "0.1.51", null),
            InstalledVersionReader.Read(root));

        Assert.Equal(UpdateAvailability.Unknown, availability);
    }

    private static UpdateCheckState Verified(string version, string directory) =>
        new(1, UpdateCheckStatus.Verified, Checked, version, null, directory);

    /// <summary>The pointer exactly as LocalAiPackageInstaller writes it: a directory name.</summary>
    private void WritePointer(string versionDirectory)
    {
        var binRoot = Path.Combine(root, "bin");
        System.IO.Directory.CreateDirectory(binRoot);
        File.WriteAllBytes(
            Path.Combine(binRoot, "current.json"),
            CurrentPointerSnapshot.CreateCanonicalBytes(versionDirectory));
    }

    private void Record(string versionDirectory, string releaseVersion) =>
        new InstalledReleaseStore(Path.Combine(root, "bin"))
            .Write(versionDirectory, releaseVersion);
}
