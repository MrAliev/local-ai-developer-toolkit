using LocalAi.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSearch.Core.Semantics;

public sealed record HeuristicNavigationPolicy(
    int SchemaVersion,
    bool Enabled,
    int MaximumFiles,
    int MaximumFileBytes,
    int MaximumResults,
    int MaximumIdentifierLength,
    bool CaseSensitive)
{
    public const string FileName = "semantic-navigation.json";

    public static HeuristicNavigationPolicy Default { get; } = new(
        SchemaVersion: 1,
        Enabled: true,
        MaximumFiles: 2_000,
        MaximumFileBytes: 1024 * 1024,
        MaximumResults: 200,
        MaximumIdentifierLength: 256,
        CaseSensitive: true);
}

public sealed class HeuristicNavigationPolicyStore
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
        RuntimeDirectories.SettingsFile(_runtimeRoot, HeuristicNavigationPolicy.FileName);

    private string WritePath =>
        RuntimeDirectories.SettingsFileForWriting(_runtimeRoot, HeuristicNavigationPolicy.FileName);

    public HeuristicNavigationPolicyStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _runtimeRoot = System.IO.Path.GetFullPath(runtimeRoot);
    }

    public static string DefaultRuntimeRoot => SemanticIndexingPolicyStore.DefaultRuntimeRoot;
    /// <summary>Where the file is now, which may still be the legacy path.</summary>
    public string Path => ReadPath;

    public HeuristicNavigationPolicy Read()
    {
        try
        {
            if (!File.Exists(ReadPath))
            {
                return HeuristicNavigationPolicy.Default;
            }

            var policy = JsonSerializer.Deserialize<HeuristicNavigationPolicy>(
                File.ReadAllBytes(ReadPath), SerializerOptions);
            return policy is not null && IsValid(policy)
                ? policy
                : HeuristicNavigationPolicy.Default;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return HeuristicNavigationPolicy.Default;
        }
    }

    public void Write(HeuristicNavigationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!IsValid(policy))
        {
            throw new ArgumentException("Heuristic navigation policy is invalid.", nameof(policy));
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
            RuntimeDirectories.DiscardLegacySettingsFile(_runtimeRoot, HeuristicNavigationPolicy.FileName);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsValid(HeuristicNavigationPolicy policy) =>
        policy.SchemaVersion == HeuristicNavigationPolicy.Default.SchemaVersion &&
        policy.MaximumFiles is > 0 and <= 1_000_000 &&
        policy.MaximumFileBytes is > 0 and <= 256 * 1024 * 1024 &&
        policy.MaximumResults is > 0 and <= 100_000 &&
        policy.MaximumIdentifierLength is > 0 and <= 4096;
}
