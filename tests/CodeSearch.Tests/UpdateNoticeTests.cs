using System.Text;
using CodeSearch.Mcp;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// The line an agent is shown about a newer release — and, mostly, the cases where it is shown
/// nothing.
///
/// Silence is the default and the important half. A person who never asked for release lookups
/// must never see one mentioned, and an agent must not be told about an update on every status
/// call it makes once the machine is current again.
/// </summary>
public sealed class UpdateNoticeTests : IDisposable
{
    private static readonly DateTimeOffset Checked =
        new(2026, 8, 31, 9, 30, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-update-notice-" + Guid.NewGuid().ToString("N"));

    public UpdateNoticeTests() => Install("0.1.50");

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
    public void A_newer_verified_release_is_announced_once_with_where_to_read_about_it()
    {
        Enable();
        Learned("0.1.51");

        var notice = UpdateNotice.ForStatus(root);

        Assert.Contains("LocalAi 0.1.51 is available", notice, StringComparison.Ordinal);
        Assert.Contains("this installation is 0.1.50", notice, StringComparison.Ordinal);
        Assert.Contains("https://example.invalid", notice, StringComparison.Ordinal);
        // Trusted output: this is the installation talking about itself, not repository text.
        Assert.DoesNotContain("<untrusted-content", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_that_never_opted_in_is_told_nothing()
    {
        Learned("0.1.51");

        Assert.Equal(string.Empty, UpdateNotice.ForStatus(root));
    }

    [Fact]
    public void An_installation_that_is_current_is_told_nothing()
    {
        Enable();
        Learned("0.1.50");

        Assert.Equal(string.Empty, UpdateNotice.ForStatus(root));
    }

    [Fact]
    public void A_check_that_never_ran_says_nothing_rather_than_unknown()
    {
        Enable();

        Assert.Equal(string.Empty, UpdateNotice.ForStatus(root));
    }

    [Fact]
    public void An_unverified_answer_is_never_announced()
    {
        Enable();
        new UpdateCheckStateStore(root).Write(
            new UpdateCheckState(1, UpdateCheckStatus.Unavailable, Checked, "9.9.9", null));

        Assert.Equal(string.Empty, UpdateNotice.ForStatus(root));
    }

    /// <summary>
    /// Ordering by version, not by text: an installation on 0.1.9 is behind 0.1.10, and a
    /// string comparison says the opposite.
    /// </summary>
    [Fact]
    public void The_comparison_is_by_version_not_by_text()
    {
        Install("0.1.9");
        Enable();
        Learned("0.1.10");

        Assert.Contains("0.1.10 is available", UpdateNotice.ForStatus(root), StringComparison.Ordinal);
    }

    [Fact]
    public void A_runtime_root_that_is_not_there_says_nothing()
    {
        var missing = Path.Combine(root, "nowhere");

        Assert.Equal(string.Empty, UpdateNotice.ForStatus(missing));
    }

    /// <summary>
    /// A pointer written with a byte order mark is still a valid document to every other
    /// reader of it, and a notice that went silent over one would be a puzzle with no clue.
    /// </summary>
    [Fact]
    public void A_version_pointer_with_a_byte_order_mark_still_reads()
    {
        Enable();
        Learned("0.1.51");
        File.WriteAllText(
            Path.Combine(root, "bin", "current.json"),
            """{"schemaVersion":1,"version":"0.1.50"}""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Contains(
            "this installation is 0.1.50",
            UpdateNotice.ForStatus(root),
            StringComparison.Ordinal);
    }

    private void Enable() =>
        new UpdateCheckPolicyStore(root).Write(
            UpdateCheckPolicy.Default with { Enabled = true });

    private void Learned(string version) =>
        new UpdateCheckStateStore(root).Write(new UpdateCheckState(
            1,
            UpdateCheckStatus.Verified,
            Checked,
            version,
            "https://example.invalid/releases/tag/v" + version));

    private void Install(string version)
    {
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(
            Path.Combine(root, "bin", "current.json"),
            "{\"schemaVersion\":1,\"version\":\"" + version + "\"}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
