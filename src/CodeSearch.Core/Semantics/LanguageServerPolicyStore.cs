using LocalAi.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSearch.Core.Semantics;

/// <param name="InitializationOptions">
/// Passed to the server verbatim as the LSP <c>initializationOptions</c> member. Optional, and
/// absent in the default policy, so an existing <c>language-servers.json</c> keeps deserializing
/// unchanged.
///
/// This is what makes typescript-language-server usable in a workspace that has no TypeScript of
/// its own — <c>{"tsserver":{"path":"…/lib/tsserver.js"}}</c> — and it has to be configured
/// rather than discovered. LocalAi will not pick a tsserver for the operator: navigating with a
/// different TypeScript than the project builds with is a wrong answer that looks like a right
/// one.
/// </param>
public sealed record LanguageServerAdapterPolicy(
    bool Enabled,
    string Executable,
    string[] Arguments,
    JsonElement? InitializationOptions = null);

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
            MaximumStandardErrorBytes,
            adapter.InitializationOptions);
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

    private readonly string _runtimeRoot;

    /// <summary>
    /// Where this reads from: the settings directory, falling back to the loose file an
    /// installation from before the split still has. Writing only ever goes to the settings
    /// directory, so the fallback empties itself rather than becoming a second source of truth.
    /// </summary>
    private string ReadPath =>
        RuntimeDirectories.SettingsFile(_runtimeRoot, LanguageServerPolicy.FileName);

    private string WritePath =>
        RuntimeDirectories.SettingsFileForWriting(_runtimeRoot, LanguageServerPolicy.FileName);

    public LanguageServerPolicyStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _runtimeRoot = System.IO.Path.GetFullPath(runtimeRoot);
    }

    public static string DefaultRuntimeRoot => SemanticIndexingPolicyStore.DefaultRuntimeRoot;
    /// <summary>Where the file is now, which may still be the legacy path.</summary>
    public string Path => ReadPath;

    public LanguageServerPolicy Read() => ReadWithSource().Policy;

    /// <summary>The policy, and whether the file on disk is what produced it.</summary>
    public PolicyRead<LanguageServerPolicy> ReadWithSource()
    {
        var path = ReadPath;
        var found = File.Exists(path);
        try
        {
            if (!found)
            {
                return new PolicyRead<LanguageServerPolicy>(
                    LanguageServerPolicy.Default,
                    path,
                    false,
                    false);
            }

            var policy = JsonSerializer.Deserialize<LanguageServerPolicy>(
                File.ReadAllBytes(path),
                SerializerOptions);
            return policy is not null && IsValid(policy)
                ? new PolicyRead<LanguageServerPolicy>(policy, path, true, true)
                : new PolicyRead<LanguageServerPolicy>(LanguageServerPolicy.Default, path, true, false);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new PolicyRead<LanguageServerPolicy>(
                LanguageServerPolicy.Default,
                path,
                found,
                false);
        }
    }

    public void Write(LanguageServerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!IsValid(policy))
        {
            throw new ArgumentException("Language-server policy is invalid.", nameof(policy));
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
            RuntimeDirectories.DiscardLegacySettingsFile(_runtimeRoot, LanguageServerPolicy.FileName);
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
                    policy.MaximumStandardErrorBytes,
                    adapter.InitializationOptions).Validate();
            }

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
