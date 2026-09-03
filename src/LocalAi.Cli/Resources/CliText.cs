using LocalAi.Contracts.Localization;

namespace LocalAi.Cli.Resources;

/// <summary>
/// What the CLI says, in the language the reader's machine is set to.
///
/// The same rule as the search tools, in a second costume. Anything to the left of the first
/// colon is a field name and stays English — and in a report that separates name from value with
/// two spaces instead of punctuation, the left column is that same field name. So the left
/// thirty-two columns of a `doctor` report are byte-identical in both languages, and a Russian
/// paste and an English one into the same issue still diff line for line.
///
/// Status markers stay too. <c>ok  </c>, <c>note</c> and <c>FAIL</c> are a three-value column
/// that is fixed-width by construction, and they are the same kind of thing as the
/// <c>STALE</c>/<c>precise</c> pair the search tools keep.
/// </summary>
public static class CliText
{
    public static TextCatalogue Catalogue { get; } = new(
        "LocalAi.Cli.Resources.CliText",
        typeof(CliText).Assembly);

    public static string VersionNoPointer(string binRoot) =>
        Catalogue.Format(nameof(VersionNoPointer), binRoot);

    public static string VersionPointerUnreadable(string reason) =>
        Catalogue.Format(nameof(VersionPointerUnreadable), reason);

    public static string VersionPointerEmpty => Catalogue.Get(nameof(VersionPointerEmpty));

    public static string VersionDirectoryMissing(string version) =>
        Catalogue.Format(nameof(VersionDirectoryMissing), version);

    public static string VersionFilesMissing(string version, string missing) =>
        Catalogue.Format(nameof(VersionFilesMissing), version, missing);

    public static string VersionComplete(string version, int binaries) =>
        Catalogue.Format(nameof(VersionComplete), version, binaries);

    public static string LauncherMissing(string path) =>
        Catalogue.Format(nameof(LauncherMissing), path);

    public static string BrokerNotRunning => Catalogue.Get(nameof(BrokerNotRunning));

    public static string BrokerStateUnreadable(string reason) =>
        Catalogue.Format(nameof(BrokerStateUnreadable), reason);

    public static string BrokerStateEmpty => Catalogue.Get(nameof(BrokerStateEmpty));

    public static string BrokerSilent(int processId, string minutes) =>
        Catalogue.Format(nameof(BrokerSilent), processId, minutes);

    public static string BrokerAlive(int processId, string seconds) =>
        Catalogue.Format(nameof(BrokerAlive), processId, seconds);

    public static string QueueQuarantined(int queued, int quarantined) =>
        Catalogue.Format(nameof(QueueQuarantined), queued, quarantined);

    public static string QueueClean(int queued) => Catalogue.Format(nameof(QueueClean), queued);

    public static string PolicyModels(object residency, int keepAliveSeconds) =>
        Catalogue.Format(nameof(PolicyModels), residency, keepAliveSeconds);

    public static string PolicyRetention(int generations, int versions, int telemetryDays) =>
        Catalogue.Format(nameof(PolicyRetention), generations, versions, telemetryDays);

    public static string PolicyLanguageServersEnabled(string languages) =>
        Catalogue.Format(nameof(PolicyLanguageServersEnabled), languages);

    public static string PolicyLanguageServersDisabled =>
        Catalogue.Get(nameof(PolicyLanguageServersDisabled));

    public static string PolicyDefaults(string detail) =>
        Catalogue.Format(nameof(PolicyDefaults), detail);

    // What `localai policy` itself prints. The report above answers about a machine; these
    // answer to somebody changing it, and say what the change costs.

    /// <summary>
    /// The whole usage block, holes and all.
    ///
    /// One string rather than a line each because its two columns only mean anything together:
    /// the description column sits at 23, set by the longest caption, and a caption translated a
    /// few characters longer moves it for every row at once. The interval range is a hole because
    /// the validator reads those same two constants, and a usage block naming a range nothing
    /// enforces is a lie no parity test can catch.
    ///
    /// Line endings are normalised because XML normalises them the other way: a resource file
    /// written with CRLF hands back LF, and this prints beside a WriteLine.
    /// </summary>
    public static string PolicyUsage(int minimumIntervalHours, int maximumIntervalHours) =>
        Catalogue.Format(nameof(PolicyUsage), minimumIntervalHours, maximumIntervalHours)
            .ReplaceLineEndings();

    public static string PolicyLanguageUnknown(string requested, string supported) =>
        Catalogue.Format(nameof(PolicyLanguageUnknown), requested, supported);

    public static string PolicyLanguageSystem => Catalogue.Get(nameof(PolicyLanguageSystem));

    public static string PolicyLanguageRestartNote =>
        Catalogue.Get(nameof(PolicyLanguageRestartNote));

    public static string PolicyKeepAlive(int seconds) =>
        Catalogue.Format(nameof(PolicyKeepAlive), seconds);

    public static string PolicyKeepAliveInvalid(string requested) =>
        Catalogue.Format(nameof(PolicyKeepAliveInvalid), requested);

    public static string PolicyResidencyRelaxed => Catalogue.Get(nameof(PolicyResidencyRelaxed));

    public static string PolicyResidencyMarks => Catalogue.Get(nameof(PolicyResidencyMarks));

    public static string PolicyResidencyNowCpu => Catalogue.Get(nameof(PolicyResidencyNowCpu));

    public static string PolicyResidencyNowPartial =>
        Catalogue.Get(nameof(PolicyResidencyNowPartial));

    public static string PolicyResidencyUnknown(string requested) =>
        Catalogue.Format(nameof(PolicyResidencyUnknown), requested);

    public static string PolicyRestartNote => Catalogue.Get(nameof(PolicyRestartNote));

    public static string PolicyUpdateCheckOn(int intervalHours) =>
        Catalogue.Format(nameof(PolicyUpdateCheckOn), intervalHours);

    public static string PolicyUpdateCheckOffWithInterval(int intervalHours) =>
        Catalogue.Format(nameof(PolicyUpdateCheckOffWithInterval), intervalHours);

    public static string PolicyUpdateCheckUnknown(string requested) =>
        Catalogue.Format(nameof(PolicyUpdateCheckUnknown), requested);

    public static string PolicyUpdateCheckIntervalInvalid(
        string requested,
        int minimumIntervalHours,
        int maximumIntervalHours) =>
        Catalogue.Format(
            nameof(PolicyUpdateCheckIntervalInvalid),
            requested,
            minimumIntervalHours,
            maximumIntervalHours);

    public static string PolicyUpdateCheckNothingFetched =>
        Catalogue.Get(nameof(PolicyUpdateCheckNothingFetched));

    public static string UpdateCheckDisabled => Catalogue.Get(nameof(UpdateCheckDisabled));

    public static string UpdateAvailable(string? latest, string? installed, string? url) =>
        Catalogue.Format(nameof(UpdateAvailable), latest, installed, url);

    public static string UpdateUpToDate(string? installed, string? checkedAt) =>
        Catalogue.Format(nameof(UpdateUpToDate), installed, checkedAt);

    public static string UpdateUnknownUnavailable(string? triedAt) =>
        Catalogue.Format(nameof(UpdateUnknownUnavailable), triedAt);

    public static string UpdateIncomparable(string? latest) =>
        Catalogue.Format(nameof(UpdateIncomparable), latest);

    public static string UpdateNeverChecked => Catalogue.Get(nameof(UpdateNeverChecked));

    /// <summary>
    /// What the last check verified. The timestamp arrives already formatted, so the resource
    /// carries no format specifier for a translator to alter.
    /// </summary>
    public static string UpdateVerifiedRelease(string? latest, string? checkedAt) =>
        Catalogue.Format(nameof(UpdateVerifiedRelease), latest, checkedAt);

    public static string RepositoryNotConnected(string root) =>
        Catalogue.Format(nameof(RepositoryNotConnected), root);

    public static string RepositoryState(object state, string generation) =>
        Catalogue.Format(nameof(RepositoryState), state, generation);

    public static string GenerationNone => Catalogue.Get(nameof(GenerationNone));

    public static string SummaryProblems(int failed, int warned) =>
        Catalogue.Format(nameof(SummaryProblems), failed, warned);

    public static string SummaryNoProblemsWithNotes(int warned) =>
        Catalogue.Format(nameof(SummaryNoProblemsWithNotes), warned);

    public static string SummaryNoProblems => Catalogue.Get(nameof(SummaryNoProblems));
}
