using System.Security.Cryptography;
using System.Text;
using LocalAi.Contracts.Activation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum UpdateCheckStatus
{
    /// <summary>Nothing has been looked up yet, or what was written could not be read.</summary>
    Unknown,

    /// <summary>A manifest was fetched and its signature verified against the embedded key.</summary>
    Verified,

    /// <summary>
    /// The last attempt produced no answer worth believing — the network, or a signature that
    /// did not verify. Deliberately the same outcome for both: a manifest nobody signed says
    /// nothing about what the newest release is, and the difference belongs in a log rather
    /// than in a banner urging somebody to act.
    /// </summary>
    Unavailable,
}

/// <summary>
/// What the last update check learned, and when.
///
/// Every surface reads this file and none of them touches the network: `doctor`, the trailing
/// line on `index_status`, and anything added later all answer from the same small record, so
/// a person asking twice gets the same answer twice and no surface can become a second,
/// unthrottled caller of GitHub.
/// </summary>
public sealed record UpdateCheckState(
    int SchemaVersion,
    UpdateCheckStatus Status,
    DateTimeOffset? CheckedAtUtc,
    string? LatestVersion,
    string? ReleaseUrl,
    /// <summary>
    /// The manifest's version directory for that release. Recorded because an installation
    /// made before the release version was written down knows only its own directory name,
    /// and comparing two directory names still answers "is this a different release" — which
    /// is the question the surfaces actually ask.
    /// </summary>
    string? LatestVersionDirectory = null)
{
    public const string FileName = "update-state.json";

    public static UpdateCheckState Unknown { get; } = new(1, UpdateCheckStatus.Unknown, null, null, null);

    /// <summary>
    /// Whether <paramref name="installedVersion"/> is behind what was last verified. Compared
    /// as versions rather than as strings, because "0.1.9" is not newer than "0.1.10" and a
    /// string comparison says it is. Anything that does not parse is not an update: a version
    /// nobody can order is not one to act on.
    /// </summary>
    public bool IsNewerThan(string? installedVersion) =>
        Status == UpdateCheckStatus.Verified &&
        TryParseVersion(LatestVersion, out var latest) &&
        TryParseVersion(installedVersion, out var installed) &&
        latest > installed;

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Releases are tagged v0.1.50 as often as 0.1.50; both name the same release.
        var trimmed = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(trimmed, out version!);
    }
}

public sealed class UpdateCheckStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    private readonly string _path;

    public UpdateCheckStateStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _path = Path.Combine(Path.GetFullPath(runtimeRoot), UpdateCheckState.FileName);
    }

    public string FilePath => _path;

    public UpdateCheckState Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return UpdateCheckState.Unknown;
            }

            var state = JsonSerializer.Deserialize<UpdateCheckState>(
                File.ReadAllBytes(_path),
                SerializerOptions);
            return state is null || state.SchemaVersion != UpdateCheckState.Unknown.SchemaVersion
                ? UpdateCheckState.Unknown
                : state;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return UpdateCheckState.Unknown;
        }
    }

    /// <summary>
    /// Replaces the record atomically. The file is small and read by every surface, so a
    /// half-written one would be read as "unknown" by whoever arrived mid-write — harmless but
    /// avoidable, and the same temp-then-move the rest of the runtime uses costs nothing.
    /// </summary>
    public void Write(UpdateCheckState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

/// <summary>
/// When the next check is allowed. A pure function of the policy, the last result and the
/// clock, so the rule can be tested without a network, a broker or a wait.
/// </summary>
public static class UpdateCheckSchedule
{
    /// <summary>
    /// How far a machine's check drifts from the exact interval, as a fraction of it.
    ///
    /// Without this, every machine installed from the same image checks at the same moment
    /// after every restart, and the publisher sees a fleet arrive in one spike. The offset is
    /// derived from the runtime root rather than drawn at random, so a machine keeps its own
    /// slot across restarts instead of walking around the clock.
    /// </summary>
    public const double JitterFraction = 0.1;

    public static bool IsDue(
        UpdateCheckPolicy policy,
        UpdateCheckState state,
        DateTimeOffset now,
        string seed)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(state);
        if (!policy.Enabled)
        {
            return false;
        }

        // Never checked: due now, so saying yes produces an answer rather than a day of
        // "unknown" that looks like the setting did not take.
        return state.CheckedAtUtc is not { } checkedAt || now >= NextDue(policy, checkedAt, seed);
    }

    public static DateTimeOffset NextDue(
        UpdateCheckPolicy policy,
        DateTimeOffset checkedAtUtc,
        string seed)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var interval = TimeSpan.FromHours(policy.IntervalHours);
        return checkedAtUtc + interval + (interval * JitterFraction * Offset(seed));
    }

    /// <summary>A stable number in [0, 1) for this machine.</summary>
    private static double Offset(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToLowerInvariant()));
        // Four bytes is plenty of resolution for a ten-percent window, and keeps the
        // arithmetic in a range where nothing rounds surprisingly.
        var value = BitConverter.ToUInt32(hash, 0);
        return value / (double)uint.MaxValue;
    }
}

/// <summary>
/// Whether a newer release exists, as far as this machine can tell.
/// </summary>
public enum UpdateAvailability
{
    /// <summary>Nothing verified, or nothing on this machine to compare against.</summary>
    Unknown,

    UpToDate,

    Available,
}

/// <summary>
/// The one comparison every surface uses.
///
/// It exists because there were three of them, each reading the version pointer itself and
/// each comparing a commit id against a release version — so `doctor` reported "up to date"
/// while `policy show`, reading the same file, named a newer release (#255).
/// </summary>
public static class UpdateComparison
{
    public static UpdateAvailability Compare(UpdateCheckState state, InstalledVersion installed)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(installed);
        if (state.Status != UpdateCheckStatus.Verified || !installed.Exists)
        {
            return UpdateAvailability.Unknown;
        }

        // The precise answer, when this installation recorded which release it came from.
        if (state.IsNewerThan(installed.ReleaseVersion))
        {
            return UpdateAvailability.Available;
        }

        if (installed.ReleaseVersion is not null &&
            state.LatestVersion is not null &&
            Comparable(installed.ReleaseVersion) &&
            Comparable(state.LatestVersion))
        {
            return UpdateAvailability.UpToDate;
        }

        // The fallback: an installation that predates the record still knows its directory,
        // and a manifest naming a different one is a different release. "Different" is not
        // "newer", which is why the surfaces word this case as a release being available
        // rather than as being behind.
        if (state.LatestVersionDirectory is { Length: > 0 } latestDirectory &&
            installed.VersionDirectory is { Length: > 0 } installedDirectory)
        {
            return string.Equals(latestDirectory, installedDirectory, StringComparison.Ordinal)
                ? UpdateAvailability.UpToDate
                : UpdateAvailability.Available;
        }

        return UpdateAvailability.Unknown;
    }

    private static bool Comparable(string value) =>
        Version.TryParse(value.Trim().TrimStart('v', 'V'), out _);
}
