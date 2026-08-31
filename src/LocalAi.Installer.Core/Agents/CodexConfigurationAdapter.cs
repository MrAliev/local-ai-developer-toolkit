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

    /// <summary>
    /// What disconnecting this client would change: both managed server sections go, together
    /// with the per-tool sub-tables underneath them, and the managed instructions block goes
    /// with them.
    ///
    /// The sub-tables are removed here precisely because the installer created them. They hold
    /// approvals for tools that will not exist on this machine any more, and leaving
    /// <c>[mcp_servers.codesearch.tools.search_code]</c> behind would leave Codex carrying
    /// settings for a server it can no longer start. Sections belonging to anybody else are
    /// untouched, as are the parts of the file between them.
    /// </summary>
    public AgentConfigurationPlan PreviewRemoval()
    {
        var files = new List<AgentConfigurationFilePlan>();
        var path = Path.Combine(homeDirectory, ".codex", "config.toml");
        var before = ReadExisting(path);
        if (before.Length > 0)
        {
            var after = RemoveManagedSections(
                AgentConfigurationFileOperations.DecodeUtf8(before));
            if (!before.SequenceEqual(Encoding.UTF8.GetBytes(after)))
            {
                files.Add(AgentConfigurationFileOperations.FilePlan(
                    path,
                    before,
                    after,
                    timeProvider.GetUtcNow()));
            }
        }

        RemoveInstructions(files, Path.Combine(homeDirectory, ".codex", "AGENTS.md"));
        return new("Codex", files, BuildPreview(files));
    }

    public Task ApplyAsync(AgentConfigurationPlan plan, CancellationToken cancellationToken) =>
        AgentConfigurationFileOperations.ApplyAsync(plan, readBack, cancellationToken);

    /// <summary>
    /// Cuts each managed table out whole, from its header to the start of the next table.
    ///
    /// A section's body runs to the next header, so removing that span takes the sub-tables
    /// with it only if they are removed by the same rule — which is why the pattern matches
    /// <c>[mcp_servers.codesearch]</c> and <c>[mcp_servers.codesearch.tools.*]</c> alike, and
    /// why the spans are cut from the end backwards: an earlier cut would move every later
    /// offset. Multiline strings are refused for the same reason the rewrite refuses them,
    /// because a line inside one can look exactly like a header.
    /// </summary>
    private static string RemoveManagedSections(string before)
    {
        if (before.Contains("\"\"\"", StringComparison.Ordinal) ||
            before.Contains("'''", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Unsupported TOML MCP layout: multiline strings cannot be edited line by line.");
        }

        var headers = Regex.Matches(
                before,
                @"(?m)^[ \t]*\[mcp_servers\.(?:codesearch|locallm)(?:\.[^\]\r\n]+)?\][ \t]*$")
            .Cast<Match>()
            .ToArray();
        var updated = before;
        foreach (var header in headers.Reverse())
        {
            var bodyStart = header.Index + header.Length;
            var next = Regex.Match(updated[bodyStart..], @"(?m)^[ \t]*\[");
            var end = next.Success ? bodyStart + next.Index : updated.Length;
            updated = updated[..header.Index] + updated[end..];
        }

        return updated;
    }

    /// <summary>
    /// Updates the two managed servers in place rather than replacing them.
    ///
    /// The previous version deleted each managed section and appended a fresh one, which is why
    /// it had to refuse a configuration that carried sub-tables such as
    /// <c>[mcp_servers.codesearch.tools.search_code]</c>. Those sub-tables are the user's
    /// per-tool approval settings, and a real Codex accumulates them; deleting them to update a
    /// command line would be a worse outcome than not configuring anything at all.
    ///
    /// So only the command and its arguments are rewritten, inside the section that is already
    /// there, and everything else the user put in it survives untouched.
    ///
    /// A tool that has no sub-table yet gets one, with the approval the matrix assigns it. A
    /// tool that already has one is rebuilt only when it still carries the literal
    /// <c>approve</c> an earlier installer wrote — that value is the installer's to own, and
    /// leaving it made an upgrade unable to ever tighten anything (#208). Any other value is
    /// the user's decision: <c>deny</c> and <c>prompt</c> survive every rebuild, so a
    /// deviation towards stricter is permanent and a deviation towards looser is not.
    /// </summary>
    private string UpdateToml(string before)
    {
        ValidateToml(before);
        var plan = ClientCommandPlan.Plan(installationDirectory);
        var updated = UpsertServer(before, "codesearch", plan.CodeSearch);
        updated = UpsertServer(updated, "locallm", plan.LocalLm);
        updated = RebuildToolSections(updated, "codesearch", plan.CodeSearch.Tools);
        return RebuildToolSections(updated, "locallm", plan.LocalLm.Tools);
    }

    private static string RebuildToolSections(
        string toml,
        string server,
        IReadOnlyList<string> tools)
    {
        var missing = new List<string>();
        foreach (var tool in tools)
        {
            var header = Regex.Match(
                toml,
                @"(?m)^[ \t]*\[mcp_servers\." + Regex.Escape(server) +
                @"\.tools\." + Regex.Escape(tool) + @"\][ \t]*$");
            if (!header.Success)
            {
                missing.Add(tool);
                continue;
            }

            var bodyStart = header.Index + header.Length;
            var next = Regex.Match(toml[bodyStart..], @"(?m)^[ \t]*\[");
            var bodyEnd = next.Success ? bodyStart + next.Index : toml.Length;
            var body = RebuildInstallerAssignment(
                toml[bodyStart..bodyEnd],
                "approval_mode",
                ApprovalValue(server, tool));
            toml = toml[..bodyStart] + body + toml[bodyEnd..];
        }

        if (missing.Count == 0)
        {
            return toml;
        }

        var sections = string.Join(
            "\n\n",
            missing.Select(tool =>
                "[mcp_servers." + server + ".tools." + tool + "]\n" +
                "approval_mode = " + TomlString(ApprovalValue(server, tool))));
        return toml.TrimEnd() + "\n\n" + sections + "\n";
    }

    private static string ApprovalValue(string server, string tool) =>
        McpToolNames.ApprovalFor(server, tool) == McpToolApproval.Approve
            ? "approve"
            : "prompt";

    private static string UpsertServer(
        string toml,
        string name,
        ClientToolRegistration registration)
    {
        var header = Regex.Match(toml, @"(?m)^[ \t]*\[mcp_servers\." + name + @"\][ \t]*$");
        if (!header.Success)
        {
            var prefix = toml.TrimEnd();
            var section = TomlSection(name, registration);
            return prefix.Length == 0 ? section + "\n" : prefix + "\n\n" + section + "\n";
        }

        // The section runs to the next table header, which is where its own sub-tables begin.
        var bodyStart = header.Index + header.Length;
        var next = Regex.Match(toml[bodyStart..], @"(?m)^[ \t]*\[");
        var bodyEnd = next.Success ? bodyStart + next.Index : toml.Length;
        var body = toml[bodyStart..bodyEnd];
        body = ReplaceAssignment(body, "command", TomlString(registration.Command));
        body = ReplaceAssignment(
            body,
            "args",
            "[" + string.Join(
                ", ",
                registration.Arguments.Select(TomlString)) + "]");
        body = RebuildInstallerAssignment(
            body,
            DefaultApprovalKey,
            DefaultApprovalValue);
        return toml[..bodyStart] + body + toml[bodyEnd..];
    }

    /// <summary>
    /// Codex resolves a tool's approval as per-tool override, then this server default, then
    /// <c>auto</c>. Left at <c>auto</c> a tool that declares no annotations is prompted for —
    /// <c>destructive_hint.unwrap_or(true) || open_world_hint.unwrap_or(true)</c> — and none of
    /// these tools declares any, so the default is a prompt for each one.
    ///
    /// Setting it here means a tool added by a later release is covered the moment it exists.
    /// The value is <c>prompt</c> on purpose (#208): a server-wide <c>approve</c> pre-approved
    /// every future tool before anyone had assessed its side effects, which was stated as the
    /// goal and was still the wrong goal. A new tool now asks until a release classifies it in
    /// the matrix — the per-tool rows are what carry the approvals.
    /// </summary>
    private const string DefaultApprovalKey = "default_tools_approval_mode";

    private const string DefaultApprovalValue = "prompt";

    /// <summary>
    /// The one value the earlier installer ever wrote, and therefore the one value the
    /// matrix may rebuild. Anything else in an approval slot is the user's own decision.
    /// </summary>
    private const string InstallerLegacyApproval = "approve";

    /// <summary>
    /// Sets <paramref name="key"/> when it is absent, and rewrites it when it still carries
    /// the literal <c>approve</c> an earlier installer wrote. Every other current value is a
    /// user decision and survives — which makes stricter-than-matrix permanent and
    /// looser-than-matrix impossible to keep through an upgrade, deliberately.
    /// </summary>
    private static string RebuildInstallerAssignment(string body, string key, string value)
    {
        var assignment = Regex.Match(
            body,
            @"(?m)^[ \t]*" + Regex.Escape(key) + @"[ \t]*=[ \t]*(?<value>.*?)[ \t]*$");
        if (!assignment.Success)
        {
            return body.TrimEnd('\n') + "\n" + key + " = " + TomlString(value) + "\n";
        }

        var current = assignment.Groups["value"].Value.Trim();
        if (current != "\"" + InstallerLegacyApproval + "\"" &&
            current != "'" + InstallerLegacyApproval + "'")
        {
            return body;
        }

        return body[..assignment.Index] + key + " = " + TomlString(value) +
            body[(assignment.Index + assignment.Length)..];
    }

    private static string ReplaceAssignment(string body, string key, string value)
    {
        var assignment = Regex.Match(body, @"(?m)^[ \t]*" + key + @"[ \t]*=.*$");
        return assignment.Success
            ? body[..assignment.Index] + key + " = " + value +
                body[(assignment.Index + assignment.Length)..]
            : body.TrimEnd('\n') + "\n" + key + " = " + value + "\n";
    }

    private static string TomlString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string TomlSection(string name, ClientToolRegistration registration) =>
        "[mcp_servers." + name + "]\n" +
        "command = " + TomlString(registration.Command) + "\n" +
        "args = [" + string.Join(", ", registration.Arguments.Select(TomlString)) + "]\n" +
        DefaultApprovalKey + " = " + TomlString(DefaultApprovalValue);

    /// <summary>
    /// Validates what the line-oriented rewriter actually depends on, and nothing else.
    ///
    /// The previous shape validated every line of the whole user file against the handful
    /// of constructs it happened to understand, so a valid float, datetime, inline table or
    /// multiline array in a section this installer never touches refused the install
    /// (#209/m1). Unrelated sections are preserved byte-for-byte, so they need no policing —
    /// with one exception: a multiline string could contain a line that looks exactly like a
    /// managed section header, and the rewriter would edit inside somebody's string. That
    /// one construct is refused everywhere; strict per-line validation applies only inside
    /// the managed sections the rewriter edits.
    /// </summary>
    private static void ValidateToml(string toml)
    {
        if (toml.Contains("\"\"\"", StringComparison.Ordinal) ||
            toml.Contains("'''", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Unsupported TOML MCP layout: multiline strings cannot be edited line by line.");
        }

        foreach (Match managed in Regex.Matches(
                     toml,
                     @"(?m)^[ \t]*\[mcp_servers\.(?:codesearch|locallm)(?:\.[A-Za-z0-9_.-]+)?\][ \t]*$"))
        {
            var sectionStart = managed.Index + managed.Length;
            var next = Regex.Match(toml[sectionStart..], @"(?m)^\s*\[");
            var section = next.Success
                ? toml.Substring(sectionStart, next.Index)
                : toml[sectionStart..];
            foreach (var line in section.Replace("\r\n", "\n").Split('\n'))
            {
                ValidateSupportedTomlLine(line);
            }
        }

        if (toml.Count(character => character == '[') != toml.Count(character => character == ']'))
        {
            throw new InvalidOperationException("Malformed TOML MCP layout.");
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
            // Both quote forms, and each assignment on one line. The literal form is the
            // natural way to write a Windows path — Codex writes it that way itself — and
            // insisting on double quotes refused a configuration it had produced. The
            // one-line requirement stays: the rewrite replaces an assignment line, so an
            // array split across lines would leave half of itself behind.
            if (name is ("codesearch" or "locallm") &&
                (!Regex.IsMatch(body, @"(?m)^[ \t]*command[ \t]*=[ \t]*" + TomlStringValue + @"[ \t]*$") ||
                !Regex.IsMatch(
                    body,
                    @"(?m)^[ \t]*args[ \t]*=[ \t]*\[[ \t]*(?:" + TomlStringValue +
                    @"[ \t]*,?[ \t]*)*\][ \t]*$"))
            )
            {
                throw new InvalidOperationException("Malformed TOML MCP layout.");
            }
        }
    }

    /// <summary>
    /// A TOML key is a dotted path whose segments are bare or quoted. The quoted form used to
    /// be rejected outright, which declared a perfectly valid configuration malformed and
    /// refused to touch it — and Codex writes quoted segments itself, for every plugin
    /// (<c>[plugins."github@openai-api-curated"]</c>) and every project path
    /// (<c>[projects.'r:\repo']</c>). The result was that a Codex anybody actually used could
    /// never be configured, while an empty one could.
    /// </summary>
    private const string TomlStringValue = @"(?:""(?:[^""\\]|\\.)*""|'[^']*')";

    private const string KeySegment = @"(?:[A-Za-z0-9_-]+|""(?:[^""\\]|\\.)*""|'[^']*')";

    private static readonly Regex KeyPath = new(
        "^" + KeySegment + @"(?:[ \t]*\.[ \t]*" + KeySegment + ")*$",
        RegexOptions.Compiled);

    private static void ValidateSupportedTomlLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return;
        }

        if (trimmed.StartsWith('['))
        {
            if (!trimmed.EndsWith(']') ||
                !KeyPath.IsMatch(trimmed[1..^1].Trim()))
            {
                throw new InvalidOperationException("Malformed TOML MCP layout.");
            }

            return;
        }

        var equals = IndexOfAssignment(trimmed);
        if (equals <= 0)
        {
            throw new InvalidOperationException("Malformed TOML MCP layout.");
        }

        var key = trimmed[..equals].Trim();
        var value = trimmed[(equals + 1)..].Trim();
        if (!KeyPath.IsMatch(key) || !IsSupportedTomlValue(value))
        {
            throw new InvalidOperationException("Malformed TOML MCP layout.");
        }
    }

    /// <summary>
    /// The first '=' outside a quoted key segment. A quoted key may contain one — a Windows
    /// path in a project section routinely does — and splitting on the first '=' anywhere would
    /// cut such a line in half and call the result malformed.
    /// </summary>
    private static int IndexOfAssignment(string line)
    {
        var quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (character == '\\' && quote == '"')
                {
                    index++;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '=')
            {
                return index;
            }
        }

        return -1;
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

    private void AddInstructions(List<AgentConfigurationFilePlan> files, string path)
    {
        var before = ReadExisting(path);
        var updated = ManagedInstructionBlock.Upsert(AgentConfigurationFileOperations.DecodeUtf8(before));
        if (updated.Changed)
        {
            files.Add(AgentConfigurationFileOperations.FilePlan(path, before, updated.Content, timeProvider.GetUtcNow()));
        }
    }

    private void RemoveInstructions(List<AgentConfigurationFilePlan> files, string path)
    {
        var before = ReadExisting(path);
        if (before.Length == 0)
        {
            return;
        }

        var updated = ManagedInstructionBlock.Remove(
            AgentConfigurationFileOperations.DecodeUtf8(before));
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
