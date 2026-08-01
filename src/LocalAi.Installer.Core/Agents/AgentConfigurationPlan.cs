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
    public static string DecodeUtf8(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
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

    public static async Task ApplyAsync(
        AgentConfigurationPlan plan,
        Func<string, byte[]> readBack,
        CancellationToken cancellationToken)
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
                if (File.Exists(file.Path))
                {
                    await File.WriteAllBytesAsync(file.BackupPath, current, cancellationToken);
                }

                var temporary = file.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, file.AfterBytes, cancellationToken);
                    if (File.Exists(file.Path))
                    {
                        File.Replace(temporary, file.Path, null);
                    }
                    else
                    {
                        File.Move(temporary, file.Path);
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
        catch
        {
            for (var index = applied.Count - 1; index >= 0; index--)
            {
                Restore(applied[index]);
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

    private static void Restore(AgentConfigurationFilePlan file)
    {
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
    }
}
