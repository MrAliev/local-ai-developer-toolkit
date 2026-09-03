using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// Turning the update check on is consent to a network call, so what the command prints while
/// taking that consent is part of the feature rather than decoration around it.
/// </summary>
public sealed class PolicyCommandUpdateCheckTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-policy-cli-" + Guid.NewGuid().ToString("N"));

    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public void Dispose()
    {
        output.Dispose();
        error.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void A_fresh_machine_reports_the_check_off_and_nothing_known()
    {
        var exit = Run("show");

        Assert.Equal(0, exit);
        Assert.Contains("update check: off", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "nothing has been checked yet",
            output.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The language is named rather than inherited. The suite pins English, so an assertion
    /// about the English paragraph would pass here whatever the command decided — including
    /// if it decided nothing and kept printing one language at every reader.
    /// </summary>
    [Fact]
    public void Switching_it_on_writes_the_consent_and_says_what_it_agreed_to()
    {
        using var reading = TestCulture.Reading("en");

        var exit = Run("set", "--update-check", "on");

        Assert.Equal(0, exit);
        Assert.True(new UpdateCheckPolicyStore(root).Read().Enabled);
        Assert.Contains("update check: on", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            UpdateCheckPolicy.Disclosure,
            output.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Consent is taken in the language it is read in. A terminal that answers in Russian
    /// everywhere else must not ask for this one permission in English — the paragraph is
    /// the whole of what the reader is agreeing to.
    /// </summary>
    [Fact]
    public void The_consent_is_taken_in_the_language_the_reader_reads()
    {
        using var reading = TestCulture.Reading("ru");

        Assert.Equal(0, Run("set", "--update-check", "on"));

        Assert.Contains(
            UpdateCheckPolicy.DisclosureRussian,
            output.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            UpdateCheckPolicy.Disclosure,
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_it_off_again_says_nothing_more_will_be_fetched()
    {
        Run("set", "--update-check", "on");
        output.GetStringBuilder().Clear();

        var exit = Run("set", "--update-check", "off");

        Assert.Equal(0, exit);
        Assert.False(new UpdateCheckPolicyStore(root).Read().Enabled);
        Assert.Contains(
            "No release information will be fetched",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_interval_can_be_set_before_the_check_is_ever_turned_on()
    {
        var exit = Run("set", "--update-check-interval-hours", "6");

        Assert.Equal(0, exit);
        var policy = new UpdateCheckPolicyStore(root).Read();
        Assert.Equal(6, policy.IntervalHours);
        Assert.False(policy.Enabled);
        Assert.Contains(
            "off; interval set to 6 hours",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("100000")]
    [InlineData("soon")]
    public void An_interval_nobody_could_have_meant_changes_nothing(string value)
    {
        var exit = Run("set", "--update-check-interval-hours", value);

        Assert.Equal(2, exit);
        Assert.Contains("Invalid update check interval", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            UpdateCheckPolicy.DefaultIntervalHours,
            new UpdateCheckPolicyStore(root).Read().IntervalHours);
    }

    [Fact]
    public void An_unknown_setting_is_refused_rather_than_guessed_at()
    {
        var exit = Run("set", "--update-check", "maybe");

        Assert.Equal(2, exit);
        Assert.Contains("Unknown update check setting", error.ToString(), StringComparison.Ordinal);
        Assert.False(new UpdateCheckPolicyStore(root).Read().Enabled);
    }

    /// <summary>
    /// `policy show` answers about this machine from the state file. It must never be a second
    /// caller of the network — that is what the throttle in the broker exists to be.
    /// </summary>
    [Fact]
    public void Show_reports_what_the_last_check_learned_without_looking_anything_up()
    {
        new UpdateCheckStateStore(root).Write(new UpdateCheckState(
            1,
            UpdateCheckStatus.Verified,
            new DateTimeOffset(2026, 8, 31, 9, 30, 0, TimeSpan.Zero),
            "0.1.51",
            "https://example.invalid/releases/v0.1.51"));

        Run("show");

        Assert.Contains(
            "latest verified release: 0.1.51 (checked 2026-08-31 09:30 UTC)",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_that_produced_nothing_is_reported_as_unknown_not_as_an_error()
    {
        new UpdateCheckStateStore(root).Write(new UpdateCheckState(
            1,
            UpdateCheckStatus.Unavailable,
            new DateTimeOffset(2026, 8, 31, 9, 30, 0, TimeSpan.Zero),
            null,
            null));

        var exit = Run("show");

        Assert.Equal(0, exit);
        Assert.Contains(
            "unknown; the last check produced nothing to believe",
            output.ToString(),
            StringComparison.Ordinal);
    }

    private int Run(params string[] args) =>
        PolicyCommand.Execute(args, root, output, error);
}
