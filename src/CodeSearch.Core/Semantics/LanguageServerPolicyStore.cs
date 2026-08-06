using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSearch.Core.Semantics;

public sealed record LanguageServerAdapterPolicy(
    bool Enabled,
    string Executable,
    string[] Arguments);

public sealed record LanguageServerPolicy(
    int SchemaVersion,
    bool Enabled,
    int RequestTimeoutSeconds,
    int ShutdownTimeoutSeconds,
    int MaximumMessageBytes,
    int MaximumStandardErrorBytes,
    Dictionary<string, LanguageServerAdapterPolicy> Languages)
{
    public const string FileName = "language-servers.json";

    public static LanguageServerPolicy Default { get; } = new(
        SchemaVersion: 1,
        Enabled: false,
        RequestTimeoutSeconds: 15,
        ShutdownTimeoutSeconds: 3,
        MaximumMessageBytes: 16 * 1024 * 1024,
        MaximumStandardErrorBytes: 1024 * 1024,
        Languages: new Dictionary<string, LanguageServerAdapterPolicy>(StringComparer.Ordinal)
        {
            ["typescript"] = new(false, "typescript-language-server", ["--stdio"]),
            ["javascript"] = new(false, "typescript-language-server", ["--stdio"]),
            ["python"] = new(false, "pyright-langserver", ["--stdio"]),
            ["html"] = new(false, "vscode-html-language-server", ["--stdio"]),
            ["csharp"] = new(false, "csharp-ls", []),
        });

    public LanguageServerProcessSpec ProcessSpec(string languageId)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("Live language-server integration is disabled.");
        }

        if (!Languages.TryGetValue(languageId, out var adapter) || !adapter.Enabled)
        {
            throw new InvalidOperationException(
                $"Language server '{languageId}' is not configured and enabled.");
        }

        return new LanguageServerProcessSpec(
            adapter.Executable,
            adapter.Arguments,
            TimeSpan.FromSeconds(RequestTimeoutSeconds),
            TimeSpan.FromSeconds(ShutdownTimeoutSeconds),
            MaximumMessageBytes,
            MaximumStandardErrorBytes);
    }
}

/// <summary>Installation-wide, file-backed language-server policy.</summary>
public sealed class LanguageServerPolicyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _path;

    public LanguageServerPolicyStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _path = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(runtimeRoot),
            LanguageServerPolicy.FileName);
    }

    public static string DefaultRuntimeRoot => SemanticIndexingPolicyStore.DefaultRuntimeRoot;
    public string Path => _path;

    public LanguageServerPolicy Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return LanguageServerPolicy.Default;
            }

            var policy = JsonSerializer.Deserialize<LanguageServerPolicy>(
                File.ReadAllBytes(_path),
                SerializerOptions);
            return policy is not null && IsValid(policy)
                ? policy
                : LanguageServerPolicy.Default;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return LanguageServerPolicy.Default;
        }
    }

    public void Write(LanguageServerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!IsValid(policy))
        {
            throw new ArgumentException("Language-server policy is invalid.", nameof(policy));
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

    private static bool IsValid(LanguageServerPolicy policy)
    {
        if (policy.SchemaVersion != LanguageServerPolicy.Default.SchemaVersion ||
            policy.RequestTimeoutSeconds <= 0 || policy.ShutdownTimeoutSeconds <= 0 ||
            policy.MaximumMessageBytes <= 0 || policy.MaximumStandardErrorBytes <= 0 ||
            policy.Languages is null || policy.Languages.Count > 100)
        {
            return false;
        }

        try
        {
            foreach (var (languageId, adapter) in policy.Languages)
            {
                if (string.IsNullOrWhiteSpace(languageId) || languageId.Length > 128 ||
                    languageId.Any(character => !(char.IsAsciiLetterOrDigit(character) ||
                                                   character is '-' or '_' or '.')))
                {
                    return false;
                }

                ArgumentNullException.ThrowIfNull(adapter);
                new LanguageServerProcessSpec(
                    adapter.Executable,
                    adapter.Arguments,
                    TimeSpan.FromSeconds(policy.RequestTimeoutSeconds),
                    TimeSpan.FromSeconds(policy.ShutdownTimeoutSeconds),
                    policy.MaximumMessageBytes,
                    policy.MaximumStandardErrorBytes).Validate();
            }

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
