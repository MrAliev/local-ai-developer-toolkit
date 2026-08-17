using System.Text.Json;

namespace LocalLm.Core;

/// <summary>
/// Operator-controlled bounds for streaming log triage. The capacity probe still decides which
/// model/context actually fits in VRAM; this policy only bounds how that context is consumed.
/// </summary>
public sealed record LogTriagePolicy(
    int SchemaVersion,
    int MaximumContextTokens,
    int ReservedContextTokens,
    double CharactersPerToken,
    int MaximumFragmentCharacters,
    int MaximumOverlapCharacters,
    int MaximumPartialSummaryCharacters,
    int PromptOverheadCharacters)
{
    public const string FileName = "log-triage.json";

    public static LogTriagePolicy Default { get; } = new(
        SchemaVersion: 1,
        MaximumContextTokens: 262_144,
        ReservedContextTokens: 4_096,
        CharactersPerToken: 2.0,
        MaximumFragmentCharacters: 1_000_000,
        MaximumOverlapCharacters: 2_048,
        MaximumPartialSummaryCharacters: 2_000,
        PromptOverheadCharacters: 768);

    public LogTriagePolicy Normalized() => this with
    {
        SchemaVersion = 1,
        MaximumContextTokens = Math.Clamp(MaximumContextTokens, 1_024, 1_048_576),
        ReservedContextTokens = Math.Clamp(ReservedContextTokens, 256, 262_144),
        CharactersPerToken = double.IsFinite(CharactersPerToken)
            ? Math.Clamp(CharactersPerToken, 0.5, 8.0)
            : Default.CharactersPerToken,
        MaximumFragmentCharacters = Math.Clamp(MaximumFragmentCharacters, 128, 4_000_000),
        MaximumOverlapCharacters = Math.Clamp(MaximumOverlapCharacters, 0, 1_000_000),
        MaximumPartialSummaryCharacters = Math.Clamp(
            MaximumPartialSummaryCharacters,
            256,
            100_000),
        PromptOverheadCharacters = Math.Clamp(PromptOverheadCharacters, 128, 100_000),
    };
}

/// <summary>
/// Reads the hand-editable log-triage profile on every operation, so tuning takes effect without
/// rebuilding or restarting LocalAi. Missing or malformed files safely yield the defaults.
/// </summary>
public sealed class LogTriagePolicyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string path;

    public LogTriagePolicyStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        path = System.IO.Path.Combine(runtimeRoot, LogTriagePolicy.FileName);
    }

    public string Path => path;

    public static string DefaultRuntimeRoot =>
        LocalAi.Contracts.ModelResidencyPolicyStore.DefaultRuntimeRoot;

    public static LogTriagePolicy ReadDefault() =>
        new LogTriagePolicyStore(DefaultRuntimeRoot).Read();

    public LogTriagePolicy Read()
    {
        try
        {
            if (!File.Exists(path))
            {
                return LogTriagePolicy.Default;
            }

            var policy = JsonSerializer.Deserialize<LogTriagePolicy>(
                File.ReadAllBytes(path),
                SerializerOptions);
            return policy is null || policy.SchemaVersion != 1
                ? LogTriagePolicy.Default
                : policy.Normalized();
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return LogTriagePolicy.Default;
        }
    }

    public void Write(LogTriagePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(policy.Normalized(), SerializerOptions));
    }
}
