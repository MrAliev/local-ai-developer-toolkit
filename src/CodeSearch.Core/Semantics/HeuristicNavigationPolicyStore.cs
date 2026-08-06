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

    private readonly string _path;

    public HeuristicNavigationPolicyStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _path = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(runtimeRoot),
            HeuristicNavigationPolicy.FileName);
    }

    public static string DefaultRuntimeRoot => SemanticIndexingPolicyStore.DefaultRuntimeRoot;
    public string Path => _path;

    public HeuristicNavigationPolicy Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return HeuristicNavigationPolicy.Default;
            }

            var policy = JsonSerializer.Deserialize<HeuristicNavigationPolicy>(
                File.ReadAllBytes(_path), SerializerOptions);
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

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(policy, SerializerOptions));
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

    private static bool IsValid(HeuristicNavigationPolicy policy) =>
        policy.SchemaVersion == HeuristicNavigationPolicy.Default.SchemaVersion &&
        policy.MaximumFiles is > 0 and <= 1_000_000 &&
        policy.MaximumFileBytes is > 0 and <= 256 * 1024 * 1024 &&
        policy.MaximumResults is > 0 and <= 100_000 &&
        policy.MaximumIdentifierLength is > 0 and <= 4096;
}
