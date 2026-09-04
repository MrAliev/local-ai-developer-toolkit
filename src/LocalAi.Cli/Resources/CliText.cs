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

    public static string QueueStalled(int queued, string minutes) =>
        Catalogue.Format(nameof(QueueStalled), queued, minutes);

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

    // What `localai update` prints while it installs. The keys above answer "is there a
    // newer one"; these answer to somebody who decided to take it.

    public static string UpdateCancelled => Catalogue.Get(nameof(UpdateCancelled));

    public static string UpdateClientsPickItUp => Catalogue.Get(nameof(UpdateClientsPickItUp));

    public static string UpdateDidNotComplete(object status, string? reason) =>
        Catalogue.Format(nameof(UpdateDidNotComplete), status, reason);

    public static string UpdateFailed(string reason) =>
        Catalogue.Format(nameof(UpdateFailed), reason);

    public static string UpdateInstalled(string? version, string versionPath) =>
        Catalogue.Format(nameof(UpdateInstalled), version, versionPath);

    public static string UpdateLookingForRelease => Catalogue.Get(nameof(UpdateLookingForRelease));

    public static string UpdateNoInstallation(string runtimeRoot) =>
        Catalogue.Format(nameof(UpdateNoInstallation), runtimeRoot);

    public static string UpdateNothingToInstall(string? latest) =>
        Catalogue.Format(nameof(UpdateNothingToInstall), latest);

    public static string UpdateQueueBusy(int queued) =>
        Catalogue.Format(nameof(UpdateQueueBusy), queued);

    public static string UpdateQueueStillBusy(int queued, int minutes) =>
        Catalogue.Format(nameof(UpdateQueueStillBusy), queued, minutes);

    public static string UpdateQueueWaiting(int queued) =>
        Catalogue.Format(nameof(UpdateQueueWaiting), queued);

    public static string UpdateUnknownOption(string option) =>
        Catalogue.Format(nameof(UpdateUnknownOption), option);

    public static string UpdateUsage(int maximumWaitMinutes) =>
        Catalogue.Format(nameof(UpdateUsage), maximumWaitMinutes).ReplaceLineEndings();

    // What `localai prune` accounts for. Every row is a count of what went, not a sentence
    // about it: the same rows print under --dry-run, where nothing went at all.

    public static string PruneArchive(int jobs, int responseBodies, string megabytes) =>
        Catalogue.Format(nameof(PruneArchive), jobs, responseBodies, megabytes);

    public static string PruneDryRun => Catalogue.Get(nameof(PruneDryRun));

    public static string PruneLauncherBackups(int removed, string megabytes) =>
        Catalogue.Format(nameof(PruneLauncherBackups), removed, megabytes);

    public static string PruneOverlaysLeftAlone(string repositoryId, string reason) =>
        Catalogue.Format(nameof(PruneOverlaysLeftAlone), repositoryId, reason);

    public static string PruneRecordLeftAlone(string repositoryId, string reason) =>
        Catalogue.Format(nameof(PruneRecordLeftAlone), repositoryId, reason);

    public static string PruneQuarantine(int entries) =>
        Catalogue.Format(nameof(PruneQuarantine), entries);

    public static string PruneReclaimed(string megabytes) =>
        Catalogue.Format(nameof(PruneReclaimed), megabytes);

    public static string PruneRecordCheckoutGone(string repositoryId, string megabytes) =>
        Catalogue.Format(nameof(PruneRecordCheckoutGone), repositoryId, megabytes);

    public static string PruneRecordNeverIndexed(string repositoryId, string megabytes) =>
        Catalogue.Format(nameof(PruneRecordNeverIndexed), repositoryId, megabytes);

    public static string PruneRepositorySwept(string repositoryId, int generations, int overlays, int staleFiles, string megabytes) =>
        Catalogue.Format(nameof(PruneRepositorySwept), repositoryId, generations, overlays, staleFiles, megabytes);

    public static string PruneTelemetry(int records, string megabytes) =>
        Catalogue.Format(nameof(PruneTelemetry), records, megabytes);

    public static string PruneVersions(int removed, string megabytes) =>
        Catalogue.Format(nameof(PruneVersions), removed, megabytes);

    public static string PruneVersionsNoPointer => Catalogue.Get(nameof(PruneVersionsNoPointer));

    public static string PruneVersionsPointerUnreadable(string reason) =>
        Catalogue.Format(nameof(PruneVersionsPointerUnreadable), reason);

    public static string PruneVolumeUnavailable(string worktreePath) =>
        Catalogue.Format(nameof(PruneVolumeUnavailable), worktreePath);

    public static string PruneWouldReclaim(string megabytes) =>
        Catalogue.Format(nameof(PruneWouldReclaim), megabytes);

    // What the entry point itself says: the guard's outcome, the argument refusals, and what
    // `hooks install` reports. The localai: prefix stays at the call site in the guard, where

    public static string HookEventMissing(string usage) =>
        Catalogue.Format(nameof(HookEventMissing), usage);

    public static string HookEventUnknown(string requested, string supportedEvents) =>
        Catalogue.Format(nameof(HookEventUnknown), requested, supportedEvents);

    public static string HooksChainBlocked(string hookPath, string previousPath) =>
        Catalogue.Format(nameof(HooksChainBlocked), hookPath, previousPath);

    public static string HooksChained(string suffix, string hooks) =>
        Catalogue.Format(nameof(HooksChained), suffix, hooks);

    public static string HooksExcluded => Catalogue.Get(nameof(HooksExcluded));

    public static string HooksInstalled(int hooks, string hooksDirectory) =>
        Catalogue.Format(nameof(HooksInstalled), hooks, hooksDirectory);

    public static string HooksLauncherRequired => Catalogue.Get(nameof(HooksLauncherRequired));

    public static string RepositoryOutsideGit(string directory) =>
        Catalogue.Format(nameof(RepositoryOutsideGit), directory);

    public static string RepositoryPathNotDirectory(string path) =>
        Catalogue.Format(nameof(RepositoryPathNotDirectory), path);

    public static string RunCancelled => Catalogue.Get(nameof(RunCancelled));

    public static string SyncInlineLimitInvalid => Catalogue.Get(nameof(SyncInlineLimitInvalid));

    // What `localai telemetry` reports and what `localai bootstrap --dry-run` lists. Neither
    // changes anything: one answers a question, the other names work done elsewhere.

    public static string BootstrapAlreadyConnected => Catalogue.Get(nameof(BootstrapAlreadyConnected));

    public static string BootstrapDryRun => Catalogue.Get(nameof(BootstrapDryRun));

    public static string BootstrapStepClients => Catalogue.Get(nameof(BootstrapStepClients));

    public static string BootstrapStepHooks => Catalogue.Get(nameof(BootstrapStepHooks));

    public static string BootstrapStepInitializing => Catalogue.Get(nameof(BootstrapStepInitializing));

    public static string BootstrapStepLegacyKept => Catalogue.Get(nameof(BootstrapStepLegacyKept));

    public static string BootstrapStepModels => Catalogue.Get(nameof(BootstrapStepModels));

    public static string BootstrapStepState(string repositoryId) =>
        Catalogue.Format(nameof(BootstrapStepState), repositoryId);

    public static string TelemetryByModel => Catalogue.Get(nameof(TelemetryByModel));

    public static string TelemetryByProfile => Catalogue.Get(nameof(TelemetryByProfile));

    public static string TelemetryFailureOutlier(string model, string share, int failures, int totalFailures) =>
        Catalogue.Format(nameof(TelemetryFailureOutlier), model, share, failures, totalFailures);

    public static string TelemetryNone(string runtimeRoot) =>
        Catalogue.Format(nameof(TelemetryNone), runtimeRoot);

    public static string TelemetryNoneUnreadable(string runtimeRoot) =>
        Catalogue.Format(nameof(TelemetryNoneUnreadable), runtimeRoot);

    public static string TelemetryRecorded(int jobs, string earliest, string latest) =>
        Catalogue.Format(nameof(TelemetryRecorded), jobs, earliest, latest);

    public static string TelemetrySaved(string net, string gross, string verifying) =>
        Catalogue.Format(nameof(TelemetrySaved), net, gross, verifying);

    public static string TelemetryUnreadable(int records) =>
        Catalogue.Format(nameof(TelemetryUnreadable), records);

    public static string RepositoryNotConnected(string root) =>
        Catalogue.Format(nameof(RepositoryNotConnected), root);

    public static string RepositoryState(object state, string generation) =>
        Catalogue.Format(nameof(RepositoryState), state, generation);

    public static string GenerationNone => Catalogue.Get(nameof(GenerationNone));

    // What `localai sync` says while it works. The `SYNCED` and `REFUSED` lines are not
    // here and never will be: another process parses them out of stdout, so they are a wire
    // format rather than prose, and they stay ASCII for every reader.

    public static string AdaptersFailed(string reasons) =>
        Catalogue.Format(nameof(AdaptersFailed), reasons);

    public static string CoverageNoCsharp => Catalogue.Get(nameof(CoverageNoCsharp));

    public static string CoverageProjectsUncovered(int uncovered, string projects) =>
        Catalogue.Format(nameof(CoverageProjectsUncovered), uncovered, projects);

    public static string EmbeddingCheckpointNotRemoved(string checkpoint, string reason) =>
        Catalogue.Format(nameof(EmbeddingCheckpointNotRemoved), checkpoint, reason);

    public static string MainlineMissing(string refs) =>
        Catalogue.Format(nameof(MainlineMissing), refs);

    public static string OverlayDegraded(string workingRoot, string reasons) =>
        Catalogue.Format(nameof(OverlayDegraded), workingRoot, reasons);

    public static string OverlayDiscarded(string worktree) =>
        Catalogue.Format(nameof(OverlayDiscarded), worktree);

    public static string RetentionRemoved(int generations, int overlays, int stagingFiles, string megabytes) =>
        Catalogue.Format(nameof(RetentionRemoved), generations, overlays, stagingFiles, megabytes);

    public static string RetentionSweepSkipped(string reason) =>
        Catalogue.Format(nameof(RetentionSweepSkipped), reason);

    public static string SemanticCheckpointNotRemoved(string checkpoint, string reason) =>
        Catalogue.Format(nameof(SemanticCheckpointNotRemoved), checkpoint, reason);

    public static string SemanticCheckpointUnusable(string checkpoint, string reason) =>
        Catalogue.Format(nameof(SemanticCheckpointUnusable), checkpoint, reason);

    public static string SemanticPhaseResumed(string checkpoint) =>
        Catalogue.Format(nameof(SemanticPhaseResumed), checkpoint);

    public static string SyncBusy(string repositoryId) =>
        Catalogue.Format(nameof(SyncBusy), repositoryId);

    public static string WorktreeGone(string worktree) =>
        Catalogue.Format(nameof(WorktreeGone), worktree);

    public static string WorktreeNotInspectable(string worktree, string reason) =>
        Catalogue.Format(nameof(WorktreeNotInspectable), worktree, reason);

    public static string WorktreeVanished(string worktree) =>
        Catalogue.Format(nameof(WorktreeVanished), worktree);

    public static string SummaryProblems(int failed, int warned) =>
        Catalogue.Format(nameof(SummaryProblems), failed, warned);

    public static string SummaryNoProblemsWithNotes(int warned) =>
        Catalogue.Format(nameof(SummaryNoProblemsWithNotes), warned);

    public static string SummaryNoProblems => Catalogue.Get(nameof(SummaryNoProblems));

    public static string JsonNotSupported => Catalogue.Get(nameof(JsonNotSupported));

    public static string AskPromptMissing(string usage) =>
        Catalogue.Format(nameof(AskPromptMissing), usage);

    public static string ProfileUnknown(string command, string profile, string accepted) =>
        Catalogue.Format(nameof(ProfileUnknown), command, profile, accepted);

    public static string AskProfileNotSupported(string profile, string accepted) =>
        Catalogue.Format(nameof(AskProfileNotSupported), profile, accepted);

    public static string OptionValueMissing(string command, string option, string usage) =>
        Catalogue.Format(nameof(OptionValueMissing), command, option, usage);

    public static string CommandUnknownArgument(string command, string argument, string usage) =>
        Catalogue.Format(nameof(CommandUnknownArgument), command, argument, usage);

    public static string TriageNoSource(string usage) =>
        Catalogue.Format(nameof(TriageNoSource), usage);

    public static string TriageEmptyInput => Catalogue.Get(nameof(TriageEmptyInput));

    public static string TriageOneLog(string usage) =>
        Catalogue.Format(nameof(TriageOneLog), usage);

    public static string ReadImageQuestionMissing(string usage) =>
        Catalogue.Format(nameof(ReadImageQuestionMissing), usage);

    public static string ReadImageNoImages(string usage) =>
        Catalogue.Format(nameof(ReadImageNoImages), usage);

    public static string ReadImageProfileNotSupported(string profile, string accepted) =>
        Catalogue.Format(nameof(ReadImageProfileNotSupported), profile, accepted);

    public static string TranslateLanguageMissing(string usage) =>
        Catalogue.Format(nameof(TranslateLanguageMissing), usage);

    public static string TranslateNoSource(string usage) =>
        Catalogue.Format(nameof(TranslateNoSource), usage);

    public static string TranslateEmptyInput => Catalogue.Get(nameof(TranslateEmptyInput));

    public static string TranslateOneText(string usage) =>
        Catalogue.Format(nameof(TranslateOneText), usage);

    public static string TranslateWriteDocument => Catalogue.Get(nameof(TranslateWriteDocument));

    public static string OutputNotWritten(string path, string reason) =>
        Catalogue.Format(nameof(OutputNotWritten), path, reason);

    public static string RepoStatusRootWithoutDirectory => Catalogue.Get(nameof(RepoStatusRootWithoutDirectory));

    public static string RepoStatusTwoRepositories => Catalogue.Get(nameof(RepoStatusTwoRepositories));

    public static string RepoStatusUnknownArgument(string argument, string usage) =>
        Catalogue.Format(nameof(RepoStatusUnknownArgument), argument, usage);

    public static string RepoStatusNotConfigured(string commonDirectory) =>
        Catalogue.Format(nameof(RepoStatusNotConfigured), commonDirectory);

    public static string SemanticCommandFailed(string operation, string reason) =>
        Catalogue.Format(nameof(SemanticCommandFailed), operation, reason);

    public static string PositionNotFromOne(int line, int column) =>
        Catalogue.Format(nameof(PositionNotFromOne), line, column);

    public static string SemanticOperationUnknown(string operation) =>
        Catalogue.Format(nameof(SemanticOperationUnknown), operation);

    public static string NoCurrentGeneration => Catalogue.Get(nameof(NoCurrentGeneration));

    public static string SemanticEvaluateNoSuite(string path) =>
        Catalogue.Format(nameof(SemanticEvaluateNoSuite), path);

    public static string NativeOperationUnknown(string operation, string operations) =>
        Catalogue.Format(nameof(NativeOperationUnknown), operation, operations);
}
