using LocalAi.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSearch.Core.Semantics;

public sealed record ScipLanguageAdapterPolicy(
    bool Enabled,
    string Executable,
    string[] Arguments,
    string OutputFile = "index.scip",
    ScipPositionEncoding? UnspecifiedPositionEncoding = null);

/// <summary>
/// What the external indexers are allowed to do, and which of them there are.
/// </summary>
/// <remarks>
/// Two languages, named rather than listed, because adding one is not configuration. A third
/// would need a field here, its own file detection and workspace preparation in the runner — the
/// TypeScript adapter needs a synthetic tsconfig built for it — a strict CI fixture that fails
/// rather than skips, and a bump of the semantic generation version, which rebuilds every
/// repository's base generation whether or not it contains that language.
///
/// The SCIP ecosystem does have scip-go and scip-java, so the question is demand rather than
/// availability, and demand is measurable: across the five repositories connected to this
/// installation — two C# solutions, a React front end and two plugin repositories — there is not
/// one Go, Java, Kotlin, Rust, PHP or Scala file. Until one of them acquires some, a third
/// adapter would cost every repository a rebuild to index nothing.
/// </remarks>
public sealed record SemanticIndexingPolicy(
    int SchemaVersion,
    bool Enabled,
    int TimeoutSeconds,
    int MaximumProcessOutputBytes,
    int MaximumScipBytes,
    int MaximumScipDocuments,
    int MaximumScipOccurrences,
    int MaximumScipSymbols,
    int MaximumScipStringBytes,
    ScipLanguageAdapterPolicy TypeScript,
    ScipLanguageAdapterPolicy Python)
{
    public const string FileName = "semantic-indexing.json";

    public static SemanticIndexingPolicy Default { get; } = new(
        SchemaVersion: 1,
        Enabled: true,
        TimeoutSeconds: 300,
        MaximumProcessOutputBytes: 1024 * 1024,
        MaximumScipBytes: 256 * 1024 * 1024,
        MaximumScipDocuments: 1_000_000,
        MaximumScipOccurrences: 10_000_000,
        MaximumScipSymbols: 5_000_000,
        MaximumScipStringBytes: 4 * 1024 * 1024,
        TypeScript: new(
            Enabled: true,
            Executable: "scip-typescript",
            Arguments: ["index"],
            OutputFile: "index.scip",
            UnspecifiedPositionEncoding: ScipPositionEncoding.Utf16),
        Python: new(
            Enabled: true,
            Executable: "scip-python",
            Arguments:
            [
                "index", ".", "--project-name", "{projectName}",
                "--project-version", "_"
            ],
            OutputFile: "index.scip",
            UnspecifiedPositionEncoding: ScipPositionEncoding.Utf32));

    public ScipImportLimits ImportLimits() => new(
        MaximumScipBytes,
        MaximumScipDocuments,
        MaximumScipOccurrences,
        MaximumScipSymbols,
        MaximumScipStringBytes);
}

/// <summary>Installation-wide, file-backed semantic indexing policy.</summary>
public sealed class SemanticIndexingPolicyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _runtimeRoot;

    /// <summary>
    /// Where this reads from: the settings directory, falling back to the loose file an
    /// installation from before the split still has. Writing only ever goes to the settings
    /// directory, so the fallback empties itself rather than becoming a second source of truth.
    /// </summary>
    private string ReadPath =>
        RuntimeDirectories.SettingsFile(_runtimeRoot, SemanticIndexingPolicy.FileName);

    private string WritePath =>
        RuntimeDirectories.SettingsFileForWriting(_runtimeRoot, SemanticIndexingPolicy.FileName);

    public SemanticIndexingPolicyStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _runtimeRoot = System.IO.Path.GetFullPath(runtimeRoot);
    }

    public static string DefaultRuntimeRoot =>
        LocalAi.Contracts.ModelResidencyPolicyStore.DefaultRuntimeRoot;

    /// <summary>Where the file is now, which may still be the legacy path.</summary>
    public string Path => ReadPath;

    public static SemanticIndexingPolicy ReadDefault() =>
        new SemanticIndexingPolicyStore(DefaultRuntimeRoot).Read();

    public SemanticIndexingPolicy Read()
    {
        try
        {
            if (!File.Exists(ReadPath))
            {
                return SemanticIndexingPolicy.Default;
            }

            var policy = JsonSerializer.Deserialize<SemanticIndexingPolicy>(
                File.ReadAllBytes(ReadPath),
                SerializerOptions);
            return policy is not null && IsValid(policy)
                ? policy
                : SemanticIndexingPolicy.Default;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return SemanticIndexingPolicy.Default;
        }
    }

    public void Write(SemanticIndexingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!IsValid(policy))
        {
            throw new ArgumentException("Semantic indexing policy is invalid.", nameof(policy));
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(WritePath)!);
        var target = WritePath;
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(policy, SerializerOptions));
            File.Move(temporary, target, overwrite: true);
            RuntimeDirectories.DiscardLegacySettingsFile(_runtimeRoot, SemanticIndexingPolicy.FileName);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsValid(SemanticIndexingPolicy policy)
    {
        if (policy.SchemaVersion != SemanticIndexingPolicy.Default.SchemaVersion ||
            policy.TimeoutSeconds <= 0 ||
            policy.MaximumProcessOutputBytes <= 0)
        {
            return false;
        }

        try
        {
            policy.ImportLimits().Validate();
            ValidateAdapter(policy.TypeScript);
            ValidateAdapter(policy.Python);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateAdapter(ScipLanguageAdapterPolicy adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        new ScipAdapterSpec(
            "validation",
            adapter.Executable,
            adapter.Arguments,
            adapter.OutputFile,
            UnspecifiedPositionEncoding: adapter.UnspecifiedPositionEncoding).Validate();
    }
}
