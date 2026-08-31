using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalAi.Installer.Core.Agents;

public sealed record AgentConfigurationFilePlan(
    string Path,
    byte[] BeforeBytes,
    byte[] AfterBytes,
    string ExpectedSha256,
    string AfterSha256,
    string BackupPath)
{
    public string BeforeText => AgentConfigurationFileOperations.DecodeUtf8(BeforeBytes);
    public string AfterText => AgentConfigurationFileOperations.DecodeUtf8(AfterBytes);
}

public sealed record AgentConfigurationPlan(
    string AgentName,
    IReadOnlyList<AgentConfigurationFilePlan> Files,
    string PreviewText)
{
    public bool HasChanges => Files.Count > 0;
}

internal static class AgentConfigurationFileOperations
{
    // throwOnInvalidBytes: a config that is not valid UTF-8 (another encoding, or a
    // corrupted file) must refuse the plan up front. The lenient default would decode it
    // with U+FFFD replacement characters, the rewrite would silently re-encode those, and
    // apply would corrupt the parts of the user's file the installer promised to preserve
    // byte-for-byte (#209/m2).
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string DecodeUtf8(byte[] bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidOperationException(
                "The existing configuration file is not valid UTF-8; refusing to rewrite " +
                "it. Fix or remove the file and run the installer again.",
                error);
        }

        return text.Length > 0 && text[0] == '\uFEFF'
            ? text[1..]
            : text;
    }

    public static AgentConfigurationFilePlan FilePlan(
        string path,
        byte[] before,
        string afterText,
        DateTimeOffset now)
    {
        var after = Encoding.UTF8.GetBytes(afterText);
        return new(
            path,
            before.ToArray(),
            after,
            Sha256(before),
            Sha256(after),
            path + "." + now.UtcDateTime.ToString("yyyyMMdd-HHmmss") + ".bak");
    }

    /// <summary>
    /// Applies the plan as a compare-and-swap against another writer, not merely a check.
    ///
    /// The old shape read the file, compared its hash, and replaced it — and a Codex, Claude
    /// or editor save landing between the check and the replace was silently clobbered, with
    /// the backup made from the old content rather than from the version actually displaced
    /// (#203). The swap itself now captures what it displaced: File.Replace parks the real
    /// displaced bytes at the backup path, and a displaced hash that does not match the
    /// expectation means the concurrent writer lost the race into the backup slot — it is
    /// put back and the apply refuses, exactly as if the check had caught it.
    ///
    /// <paramref name="beforeSwap"/> exists for the tests that pin this window: it runs
    /// after the temporary is written and immediately before the swap.
    /// </summary>
    public static async Task ApplyAsync(
        AgentConfigurationPlan plan,
        Func<string, byte[]> readBack,
        CancellationToken cancellationToken,
        Action<string>? beforeSwap = null)
    {
        var applied = new List<AgentConfigurationFilePlan>();
        try
        {
            foreach (var file in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = File.Exists(file.Path)
                    ? await File.ReadAllBytesAsync(file.Path, cancellationToken)
                    : [];
                if (!string.Equals(Sha256(current), file.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Refusing to apply agent configuration because the file changed concurrently.");
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file.Path)!);
                var temporary = file.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, file.AfterBytes, cancellationToken);
                    beforeSwap?.Invoke(file.Path);
                    if (File.Exists(file.Path))
                    {
                        File.Replace(
                            temporary,
                            file.Path,
                            file.BackupPath,
                            ignoreMetadataErrors: true);
                        var displaced = await File.ReadAllBytesAsync(
                            file.BackupPath,
                            CancellationToken.None);
                        if (!string.Equals(
                                Sha256(displaced),
                                file.ExpectedSha256,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(file.BackupPath, file.Path, overwrite: true);
                            throw new InvalidOperationException(
                                "Refusing to apply agent configuration because the file changed concurrently.");
                        }
                    }
                    else
                    {
                        try
                        {
                            File.Move(temporary, file.Path);
                        }
                        catch (IOException) when (File.Exists(file.Path))
                        {
                            // The file appeared between the plan and the swap: somebody
                            // else's content is there now, and it stays theirs.
                            throw new InvalidOperationException(
                                "Refusing to apply agent configuration because the file changed concurrently.");
                        }
                    }

                    applied.Add(file);
                    var actual = readBack(file.Path);
                    if (!actual.SequenceEqual(file.AfterBytes))
                    {
                        throw new InvalidOperationException("Agent configuration read-back verification failed.");
                    }
                }
                catch
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }

                    throw;
                }
            }
        }
        catch (Exception exception)
        {
            var keptExternal = new List<string>();
            for (var index = applied.Count - 1; index >= 0; index--)
            {
                if (!Restore(applied[index]))
                {
                    keptExternal.Add(applied[index].Path);
                }
            }

            if (keptExternal.Count > 0)
            {
                throw new InvalidOperationException(
                    "Agent configuration was rolled back, except files changed by another " +
                    $"writer after they were applied — left as found: {string.Join(", ", keptExternal)}.",
                    exception);
            }

            throw;
        }
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    public static string Redact(string text) =>
        Regex.Replace(
            text,
            "(?im)(\"?(?:api[_-]?key|apikey|client[_-]?secret|clientsecret|authorization|token|secret|password)\"?\\s*[:=]\\s*)([\"'])(.*?)\\2",
            "$1$2<redacted>$2");

    /// <summary>
    /// Restores one applied file, but only while it still holds what the apply wrote: an
    /// external edit made after the apply is somebody's work, and rolling the installer's
    /// change back must not take theirs with it (#203). Returns false when the file was
    /// left as found.
    /// </summary>
    private static bool Restore(AgentConfigurationFilePlan file)
    {
        var current = File.Exists(file.Path)
            ? File.ReadAllBytes(file.Path)
            : [];
        if (!string.Equals(
                Sha256(current),
                file.AfterSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (File.Exists(file.BackupPath))
        {
            File.Copy(file.BackupPath, file.Path, overwrite: true);
        }
        else if (file.BeforeBytes.Length == 0)
        {
            File.Delete(file.Path);
        }
        else
        {
            File.WriteAllBytes(file.Path, file.BeforeBytes);
        }

        return true;
    }
}
