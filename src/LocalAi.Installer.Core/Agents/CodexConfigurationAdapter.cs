using System.Text;
using System.Text.RegularExpressions;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Agents;

public sealed class CodexConfigurationAdapter(
    string homeDirectory,
    string installationDirectory,
    TimeProvider? timeProvider = null,
    Func<string, byte[]>? readBackOverride = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<string, byte[]> readBack = readBackOverride ?? File.ReadAllBytes;

    public AgentConfigurationPlan Preview(AgentIntegrationChoice choice)
    {
        var files = new List<AgentConfigurationFilePlan>();
        if (choice is AgentIntegrationChoice.McpOnly or AgentIntegrationChoice.McpAndInstructions)
        {
            var path = Path.Combine(homeDirectory, ".codex", "config.toml");
            var before = ReadExisting(path);
            var after = UpdateToml(AgentConfigurationFileOperations.DecodeUtf8(before));
            if (!before.SequenceEqual(Encoding.UTF8.GetBytes(after)))
            {
                files.Add(AgentConfigurationFileOperations.FilePlan(path, before, after, timeProvider.GetUtcNow()));
            }
        }

        if (choice is AgentIntegrationChoice.InstructionsOnly or AgentIntegrationChoice.McpAndInstructions)
        {
            AddInstructions(files, Path.Combine(homeDirectory, ".codex", "AGENTS.md"));
        }

        return new("Codex", files, BuildPreview(files));
    }

    public Task ApplyAsync(AgentConfigurationPlan plan, CancellationToken cancellationToken) =>
        AgentConfigurationFileOperations.ApplyAsync(plan, readBack, cancellationToken);

    private string UpdateToml(string before)
    {
        ValidateToml(before);
        var withoutManaged = RemoveManagedTomlSections(before).TrimEnd();
        var sections = ClientCommandPlan.Plan(installationDirectory).CodexTomlSections;
        var suffix = string.Join("\n\n", sections) + "\n";
        return withoutManaged.Length == 0
            ? suffix
            : withoutManaged + "\n\n" + suffix;
    }

    private static void ValidateToml(string toml)
    {
        foreach (var line in toml.Replace("\r\n", "\n").Split('\n'))
        {
            ValidateSupportedTomlLine(line);
        }

        if (toml.Count(character => character == '[') != toml.Count(character => character == ']'))
        {
            throw new InvalidOperationException("Malformed TOML MCP layout.");
        }

        if (Regex.IsMatch(toml, @"(?m)^\s*\[mcp_servers\.(?:codesearch|locallm)\."))
        {
            throw new InvalidOperationException("Unsupported TOML MCP layout.");
        }

        if (Regex.IsMatch(toml, @"(?m)^\s*\[mcp_servers\]\s*$") ||
            Regex.IsMatch(toml, @"(?m)^\s*mcp_servers\.(?:codesearch|locallm)\."))
        {
            throw new InvalidOperationException("Unsupported TOML MCP layout.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(toml, @"(?m)^\s*\[mcp_servers\.([A-Za-z0-9_-]+)\]\s*$"))
        {
            var name = match.Groups[1].Value;
            if (!seen.Add(name))
            {
                throw new InvalidOperationException("Unsupported TOML MCP layout.");
            }

            var sectionStart = match.Index + match.Length;
            var next = Regex.Match(toml[sectionStart..], @"(?m)^\s*\[");
            var body = next.Success ? toml.Substring(sectionStart, next.Index) : toml[sectionStart..];
            if (name is ("codesearch" or "locallm") &&
                (!Regex.IsMatch(body, @"(?m)^\s*command\s*=\s*""[^""]*""\s*$") ||
                !Regex.IsMatch(body, @"(?m)^\s*args\s*=\s*\[(\s*""[^""]*""\s*,?)*\s*\]\s*$"))
            )
            {
                throw new InvalidOperationException("Malformed TOML MCP layout.");
            }
        }
    }

    private static void ValidateSupportedTomlLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return;
        }

        if (trimmed.StartsWith('['))
        {
            if (!Regex.IsMatch(trimmed, @"^\[[A-Za-z0-9_.-]+\]$"))
            {
                throw new InvalidOperationException("Malformed TOML MCP layout.");
            }

            return;
        }

        var equals = trimmed.IndexOf('=');
        if (equals <= 0)
        {
            throw new InvalidOperationException("Malformed TOML MCP layout.");
        }

        var key = trimmed[..equals].Trim();
        var value = trimmed[(equals + 1)..].Trim();
        if (!Regex.IsMatch(key, @"^[A-Za-z0-9_.-]+$") || !IsSupportedTomlValue(value))
        {
            throw new InvalidOperationException("Malformed TOML MCP layout.");
        }
    }

    private static bool IsSupportedTomlValue(string value)
    {
        if (Regex.IsMatch(value, "^\"([^\"\\\\]|\\\\.)*\"$") ||
            Regex.IsMatch(value, "^'[^']*'$") ||
            Regex.IsMatch(value, "^(true|false)$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(value, "^[+-]?[0-9]+$"))
        {
            return true;
        }

        return Regex.IsMatch(value, @"^\[(\s*""([^""\\]|\\.)*""\s*,?)*\s*\]$");
    }

    private static string RemoveManagedTomlSections(string toml) =>
        Regex.Replace(
            toml,
            @"(?ms)^\s*\[mcp_servers\.(?:codesearch|locallm)\]\s*.*?(?=^\s*\[|\z)",
            string.Empty).TrimEnd();

    private void AddInstructions(List<AgentConfigurationFilePlan> files, string path)
    {
        var before = ReadExisting(path);
        var updated = ManagedInstructionBlock.Upsert(AgentConfigurationFileOperations.DecodeUtf8(before));
        if (updated.Changed)
        {
            files.Add(AgentConfigurationFileOperations.FilePlan(path, before, updated.Content, timeProvider.GetUtcNow()));
        }
    }

    private static byte[] ReadExisting(string path) =>
        File.Exists(path) ? File.ReadAllBytes(path) : [];

    private static string BuildPreview(IEnumerable<AgentConfigurationFilePlan> files) =>
        string.Join(
            "\n---\n",
            files.Select(file =>
                file.Path + "\n" +
                "Before:\n" + AgentConfigurationFileOperations.Redact(file.BeforeText) + "\n" +
                "After:\n" + AgentConfigurationFileOperations.Redact(file.AfterText)));
}
