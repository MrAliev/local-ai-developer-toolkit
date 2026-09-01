using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

/// <summary>
/// Whether this installation may look up whether a newer release exists, and how often.
///
/// Off by default, and off after any doubt. The product promises the runtime collects nothing
/// and that its only network use is downloading a release it verifies; a check is a request to
/// GitHub, so it happens because somebody said yes, never because a file could not be read. A
/// corrupt or unknown-schema policy therefore lands on disabled — the same rule the residency
/// policy follows, pointed the same way: a parse error must never turn into permission.
/// </summary>
public sealed record UpdateCheckPolicy(
    int SchemaVersion,
    bool Enabled,
    int IntervalHours)
{
    public const string FileName = "update-check.json";

    /// <summary>
    /// Daily. Often enough that a release is noticed within a day of being published, rare
    /// enough that it is one request per machine per day — and the check is throttled from the
    /// state file rather than from a timer, so a machine that is off for a week makes one
    /// request when it comes back, not seven.
    /// </summary>
    public const int DefaultIntervalHours = 24;

    public const int MinimumIntervalHours = 1;

    /// <summary>
    /// A ceiling only so a typo cannot silently mean "never". A month is already far past the
    /// point where the answer is useful.
    /// </summary>
    public const int MaximumIntervalHours = 24 * 30;

    public static UpdateCheckPolicy Default { get; } =
        new(1, Enabled: false, DefaultIntervalHours);

    /// <summary>
    /// What enabling this actually does, in the words the installer's checkbox and the CLI both
    /// use. One sentence, no euphemism: the point of an opt-in nobody understands is nothing.
    /// </summary>
    public const string Disclosure =
        "Fetches the latest release manifest and its signature from GitHub over anonymous " +
        "HTTPS, at most once per interval, and verifies the signature before believing the " +
        "version. Nothing about this machine is sent: no identifier, no account, no usage. " +
        "Nothing is ever downloaded or installed without you asking for it.";
}

public sealed class UpdateCheckPolicyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    private readonly string _runtimeRoot;

    /// <summary>
    /// Where this reads from: the settings directory, falling back to the loose file an
    /// installation from before the split still has. Writing only ever goes to the settings
    /// directory, so the fallback empties itself rather than becoming a second source of truth.
    /// </summary>
    private string ReadPath =>
        RuntimeDirectories.SettingsFile(_runtimeRoot, UpdateCheckPolicy.FileName);

    private string WritePath =>
        RuntimeDirectories.SettingsFileForWriting(_runtimeRoot, UpdateCheckPolicy.FileName);

    public UpdateCheckPolicyStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
    }

    /// <summary>Where the file is now, which may still be the legacy path.</summary>
    public string FilePath => ReadPath;

    public static UpdateCheckPolicy ReadDefault() =>
        new UpdateCheckPolicyStore(ModelResidencyPolicyStore.DefaultRuntimeRoot).Read();

    public UpdateCheckPolicy Read()
    {
        try
        {
            if (!File.Exists(ReadPath))
            {
                return UpdateCheckPolicy.Default;
            }

            var policy = JsonSerializer.Deserialize<UpdateCheckPolicy>(
                File.ReadAllBytes(ReadPath),
                SerializerOptions);
            if (policy is null ||
                policy.SchemaVersion != UpdateCheckPolicy.Default.SchemaVersion ||
                policy.IntervalHours < UpdateCheckPolicy.MinimumIntervalHours ||
                policy.IntervalHours > UpdateCheckPolicy.MaximumIntervalHours)
            {
                return UpdateCheckPolicy.Default;
            }

            return policy;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return UpdateCheckPolicy.Default;
        }
    }

    public void Write(UpdateCheckPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.IntervalHours < UpdateCheckPolicy.MinimumIntervalHours ||
            policy.IntervalHours > UpdateCheckPolicy.MaximumIntervalHours)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.IntervalHours,
                "The update check interval must be between " +
                UpdateCheckPolicy.MinimumIntervalHours + " and " +
                UpdateCheckPolicy.MaximumIntervalHours + " hours.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(WritePath)!);
        File.WriteAllBytes(
            WritePath,
            JsonSerializer.SerializeToUtf8Bytes(policy, SerializerOptions));
    }
}
