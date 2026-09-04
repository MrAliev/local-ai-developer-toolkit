using LocalAi.Cli;
using LocalAi.Cli.Resources;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// Two reports that answer questions rather than change anything, and each had a sentence that
/// argued with the sentence beside it.
/// </summary>
public sealed class TelemetryAndBootstrapFollowTheReaderTests
{
    /// <summary>
    /// The empty report used to say "nothing has been delegated here" and then, on the next line,
    /// that some records could not be read. Unreadable records are proof that something was
    /// delegated — so the two lines cannot both be printed, and which one is printed is the whole
    /// information.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void An_empty_report_does_not_argue_with_itself(string language)
    {
        using var reading = TestCulture.Reading(language);

        var nothingAtAll = CliText.TelemetryNone(@"C:\runtime");
        var nothingReadable = CliText.TelemetryNoneUnreadable(@"C:\runtime");

        Assert.NotEqual(nothingAtAll, nothingReadable, StringComparer.Ordinal);
    }

    /// <summary>
    /// A reader on UTC+3 read the range as local time and concluded the report was three hours
    /// stale. Every other timestamp this product prints says UTC.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_recorded_range_says_which_clock_it_is(string language)
    {
        using var reading = TestCulture.Reading(language);

        Assert.Contains(
            "UTC",
            CliText.TelemetryRecorded(12, "2026-09-04 09:30", "2026-09-04 11:00"),
            StringComparison.Ordinal);
    }

    /// <summary>A count with its noun in front of it, correct for one, two and five alike.</summary>
    [Fact]
    public void A_job_count_reads_correctly_in_russian_for_any_number()
    {
        using var reading = TestCulture.Reading("ru");

        foreach (var jobs in new[] { 1, 2, 5, 21 })
        {
            Assert.Contains(
                $"заданий {jobs}",
                CliText.TelemetryRecorded(jobs, "2026-09-04 09:30", "2026-09-04 11:00"),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// `localai bootstrap` has no spelling without `--dry-run`: the dispatcher matches that form
    /// and no other. So its six imperatives describe work this binary cannot do, and without a
    /// line saying so they read as a queue about to run.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_bootstrap_plan_says_it_changed_nothing(string language)
    {
        using var reading = TestCulture.Reading(language);

        var frame = CliText.BootstrapDryRun;

        Assert.Contains("--dry-run:", frame, StringComparison.Ordinal);
        Assert.NotEqual(CliText.PruneDryRun, frame, StringComparer.Ordinal);
    }

    /// <summary>
    /// The steps name work done elsewhere — by the installer, by `localai sync`, by
    /// `localai hooks install`, by an MCP tool — so the commands they name have to be the ones
    /// that exist.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_steps_name_the_commands_that_do_them(string language)
    {
        using var reading = TestCulture.Reading(language);

        Assert.Contains("INITIALIZING", CliText.BootstrapStepInitializing, StringComparison.Ordinal);
        Assert.Contains("local_models_sync", CliText.BootstrapStepModels, StringComparison.Ordinal);
        Assert.Contains("CodeSearch", CliText.BootstrapStepClients, StringComparison.Ordinal);
        Assert.Contains("LocalLm", CliText.BootstrapStepClients, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plan is what a reader consents to, so a repository that needs nothing says so in one
    /// line rather than listing six steps it will not take.
    /// </summary>
    [Fact]
    public void A_connected_repository_gets_one_line_not_six()
    {
        using var reading = TestCulture.Reading("ru");

        Assert.DoesNotContain(
            "INITIALIZING",
            CliText.BootstrapAlreadyConnected,
            StringComparison.Ordinal);
    }
}
