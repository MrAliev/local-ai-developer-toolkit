using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

/// <summary>
/// How long the runtime root keeps the things it produces.
///
/// Every part of LocalAi wrote history and nothing ever read it back: terminal broker jobs kept
/// their full response bodies forever, every indexed commit left its generation behind, and every
/// install left its predecessor in place. None of that is wrong on its own — it is only wrong
/// without a bound, and a machine that indexes daily reaches tens of gigabytes without a single
/// component doing anything it was not asked to.
///
/// The numbers below are bounds, not targets. They are deliberately generous: the cost of keeping
/// one generation too many is disk, and the cost of dropping one too few is a rebuild.
/// </summary>
public sealed record RuntimeRetentionPolicy(
    int SchemaVersion,
    int ResponseGraceMinutes,
    int ResponseRetentionMinutes,
    long ResponseBudgetBytes,
    int ArchiveRetentionDays,
    int ArchiveEntryLimit,
    int GenerationsPerRepository,
    int InstalledVersions,
    int LauncherBackups,
    int SweepIntervalSeconds,
    int MaximumActionsPerSweep,
    int TelemetryRetentionDays = 30,
    // Quarantined jobs used to be the one artifact no bound covered (#204): a corrupt job
    // moved wholesale - prompts, source text, images included - and stayed forever. The
    // grace is hours, not minutes, because a quarantined entry exists to be investigated;
    // nothing may delete one younger than that, whatever the other bounds say.
    int QuarantineRetentionDays = 14,
    int QuarantineEntryLimit = 200,
    long QuarantineBudgetBytes = 256L * 1024 * 1024,
    int QuarantineGraceHours = 24)
{
    public const string FileName = "retention.json";

    /// <summary>
    /// The bounds a machine gets when it has never been configured.
    ///
    /// <c>ResponseGraceMinutes</c> is the one value here that is a correctness floor rather than
    /// a preference. A client reads a response on its next poll after the job turns terminal —
    /// 100 ms later in the normal case — so ten minutes is roughly four orders of magnitude of
    /// headroom. Nothing may drop a response body younger than this, whatever the other bounds
    /// say, and a client that misses the window gets a protocol error on that one job rather
    /// than a wrong answer.
    /// </summary>
    public static RuntimeRetentionPolicy Default { get; } = new(
        SchemaVersion: 1,
        ResponseGraceMinutes: 10,
        ResponseRetentionMinutes: 60,
        ResponseBudgetBytes: 512L * 1024 * 1024,
        ArchiveRetentionDays: 14,
        ArchiveEntryLimit: 2000,
        GenerationsPerRepository: 1,
        InstalledVersions: 3,
        LauncherBackups: 3,
        SweepIntervalSeconds: 60,
        MaximumActionsPerSweep: 256,
        // Longer than the archive: one telemetry record is a few hundred bytes describing one
        // job, and the value of a month of them is that a routing or residency question can be
        // answered from measurement rather than memory.
        TelemetryRetentionDays: 30);

    /// <summary>
    /// The same policy with everything an operator can get wrong pulled back into range.
    ///
    /// A hand-edited file that asks for zero generations or a negative grace must not be able to
    /// delete the index that is currently being served, so the floors are enforced here rather
    /// than trusted at every call site.
    /// </summary>
    public RuntimeRetentionPolicy Normalized() => this with
    {
        SchemaVersion = 1,
        ResponseGraceMinutes = Math.Clamp(ResponseGraceMinutes, 1, 1440),
        ResponseRetentionMinutes = Math.Max(
            Math.Clamp(ResponseRetentionMinutes, 1, 43200),
            Math.Clamp(ResponseGraceMinutes, 1, 1440)),
        ResponseBudgetBytes = Math.Max(ResponseBudgetBytes, 16L * 1024 * 1024),
        ArchiveRetentionDays = Math.Clamp(ArchiveRetentionDays, 1, 3650),
        ArchiveEntryLimit = Math.Max(ArchiveEntryLimit, 32),
        GenerationsPerRepository = Math.Max(GenerationsPerRepository, 1),
        InstalledVersions = Math.Max(InstalledVersions, 2),
        LauncherBackups = Math.Max(LauncherBackups, 1),
        SweepIntervalSeconds = Math.Clamp(SweepIntervalSeconds, 5, 86400),
        MaximumActionsPerSweep = Math.Clamp(MaximumActionsPerSweep, 1, 100000),
        TelemetryRetentionDays = Math.Clamp(TelemetryRetentionDays, 1, 3650),
        QuarantineRetentionDays = Math.Clamp(QuarantineRetentionDays, 1, 3650),
        QuarantineEntryLimit = Math.Max(QuarantineEntryLimit, 8),
        QuarantineBudgetBytes = Math.Max(QuarantineBudgetBytes, 16L * 1024 * 1024),
        QuarantineGraceHours = Math.Clamp(QuarantineGraceHours, 1, 168),
    };

    [JsonIgnore]
    public TimeSpan ResponseGrace => TimeSpan.FromMinutes(ResponseGraceMinutes);

    [JsonIgnore]
    public TimeSpan ResponseRetention => TimeSpan.FromMinutes(ResponseRetentionMinutes);

    [JsonIgnore]
    public TimeSpan ArchiveRetention => TimeSpan.FromDays(ArchiveRetentionDays);

    [JsonIgnore]
    public TimeSpan TelemetryRetention => TimeSpan.FromDays(TelemetryRetentionDays);

    [JsonIgnore]
    public TimeSpan SweepInterval => TimeSpan.FromSeconds(SweepIntervalSeconds);

    [JsonIgnore]
    public TimeSpan QuarantineRetention => TimeSpan.FromDays(QuarantineRetentionDays);

    [JsonIgnore]
    public TimeSpan QuarantineGrace => TimeSpan.FromHours(QuarantineGraceHours);
}

/// <summary>
/// Reads <c>retention.json</c> from the runtime root, next to <c>policy.json</c>, so the broker,
/// the CLI and the installer answer the same question the same way.
///
/// A missing or malformed document yields the defaults. Unlike the residency policy there is no
/// unsafe direction to fail towards — the defaults are the bounded behaviour — so a parse error
/// costs nothing beyond the operator's intent.
/// </summary>
public sealed class RuntimeRetentionPolicyStore
{
    // Deliberately not LocalAiJson.Strict. This document is hand-editable, and an operator who
    // adds a field a future build will understand should get the defaults for it, not a reset of
    // everything else in the file.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _runtimeRoot;

    /// <summary>
    /// Where this reads from: the settings directory, falling back to the loose file an
    /// installation from before the split still has. Writing only ever goes to the settings
    /// directory, so the fallback empties itself rather than becoming a second source of truth.
    /// </summary>
    private string ReadPath =>
        RuntimeDirectories.SettingsFile(_runtimeRoot, RuntimeRetentionPolicy.FileName);

    private string WritePath =>
        RuntimeDirectories.SettingsFileForWriting(_runtimeRoot, RuntimeRetentionPolicy.FileName);

    public RuntimeRetentionPolicyStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _runtimeRoot = runtimeRoot;
    }

    public static string DefaultRuntimeRoot => ModelResidencyPolicyStore.DefaultRuntimeRoot;

    public static RuntimeRetentionPolicy ReadDefault() =>
        new RuntimeRetentionPolicyStore(DefaultRuntimeRoot).Read();

    public RuntimeRetentionPolicy Read()
    {
        try
        {
            if (!File.Exists(ReadPath))
            {
                return RuntimeRetentionPolicy.Default;
            }

            var policy = JsonSerializer.Deserialize<RuntimeRetentionPolicy>(
                File.ReadAllBytes(ReadPath),
                SerializerOptions);
            return policy is null || policy.SchemaVersion != 1
                ? RuntimeRetentionPolicy.Default
                : policy.Normalized();
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return RuntimeRetentionPolicy.Default;
        }
    }

    public void Write(RuntimeRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Directory.CreateDirectory(Path.GetDirectoryName(WritePath)!);
        File.WriteAllBytes(
            WritePath,
            JsonSerializer.SerializeToUtf8Bytes(policy.Normalized(), SerializerOptions));
        RuntimeDirectories.DiscardLegacySettingsFile(
            _runtimeRoot,
            RuntimeRetentionPolicy.FileName);
    }
}
