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
