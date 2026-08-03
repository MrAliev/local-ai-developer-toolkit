using System.Text;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Tests;

public sealed class ClaudeConfigurationAdapterTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "localai-claude-" + Guid.NewGuid().ToString("N"));
    private readonly string install = @"C:\LocalAi\bin";
    private readonly DateTimeOffset now = new(2026, 7, 31, 10, 11, 12, TimeSpan.Zero);

    [Fact]
    public void Preview_for_supported_json_preserves_unrelated_values_and_mutates_only_managed_servers()
    {
        var mcp = Path.Combine(home, ".claude.json");
        Directory.CreateDirectory(home);
        File.WriteAllText(
            mcp,
            "{\"mcpServers\":{\"keep\":{\"command\":\"tool\",\"args\":[\"x\"],\"env\":{\"API_KEY\":\"secret\"}},\"codesearch\":{\"command\":\"old\",\"args\":[\"bad\"]}}}",
            Encoding.UTF8);

        var plan = Adapter().Preview(AgentIntegrationChoice.McpOnly);
        var after = SinglePreview(@".claude.json", plan).AfterText;

        Assert.Contains("\"keep\"", after, StringComparison.Ordinal);
        Assert.Contains("\"API_KEY\":\"secret\"", after, StringComparison.Ordinal);
        Assert.Contains("\"codesearch\"", after, StringComparison.Ordinal);
        Assert.Contains("\"locallm\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("\"command\":\"old\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", plan.PreviewText, StringComparison.Ordinal);
        Assert.Contains("\"API_KEY\":\"<redacted>\"", plan.PreviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The client owns this file and reserialises the whole document in its own style whenever
    /// it saves, so a registration that is already correct never looks like the text this
    /// adapter would write. Comparing bytes therefore reported a change on every install: the
    /// file was rewritten and a backup left behind, with nothing about the meaning moved.
    /// </summary>
    [Fact]
    public void An_already_correct_registration_is_left_alone_whatever_its_formatting()
    {
        var mcp = Path.Combine(home, ".claude.json");
        Directory.CreateDirectory(home);
        var launcher = @"C:\\LocalAi\\bin\\launcher\\localai-launcher.exe";
        // Written the way the client writes it: UTF-8 with no byte order mark.
        File.WriteAllText(
            mcp,
            "{\n  \"mcpServers\": {\n" +
            "    \"codesearch\": {\n      \"command\": \"" + launcher + "\",\n" +
            "      \"args\": [\n        \"run\",\n        \"codesearch-mcp\"\n      ]\n    },\n" +
            "    \"locallm\": {\n      \"command\": \"" + launcher + "\",\n" +
            "      \"args\": [\n        \"run\",\n        \"locallm-mcp\"\n      ]\n    }\n  }\n}");

        var plan = Adapter().Preview(AgentIntegrationChoice.McpOnly);

        // The client's pretty-printed form differs from this adapter's compact one in every
        // byte of whitespace, and means exactly the same thing.
        Assert.DoesNotContain(
            plan.Files,
            file => file.Path.EndsWith(".claude.json", StringComparison.Ordinal));
    }

    [Fact]
    public void A_registration_that_moved_is_updated_and_keeps_its_other_settings()
    {
        var mcp = Path.Combine(home, ".claude.json");
        Directory.CreateDirectory(home);
        File.WriteAllText(
            mcp,
            "{\"mcpServers\":{\"codesearch\":{\"command\":\"C:\\\\Old\\\\launcher.exe\"," +
            "\"args\":[\"run\",\"codesearch-mcp\"],\"env\":{\"TRACE\":\"1\"},\"disabled\":false}}}",
            Encoding.UTF8);

        var after = SinglePreview(
            @".claude.json",
            Adapter().Preview(AgentIntegrationChoice.McpOnly)).AfterText;

        Assert.Contains(
            "\"command\":\"C:\\\\LocalAi\\\\bin\\\\launcher\\\\localai-launcher.exe\"",
            after,
            StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\\\Old\\\\launcher.exe", after, StringComparison.Ordinal);
        // The entry is replaced wholesale, so anything not re-emitted would be destroyed —
        // a command-line update must not cost the user their own settings.
        Assert.Contains("\"env\":{\"TRACE\":\"1\"}", after, StringComparison.Ordinal);
        Assert.Contains("\"disabled\":false", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_preserves_unrelated_json_bytes_outside_managed_servers()
    {
        var mcp = Path.Combine(home, ".claude.json");
        Directory.CreateDirectory(home);
        File.WriteAllText(
            mcp,
            "{\n  \"theme\":\"dark\",\n  \"mcpServers\":{\n    \"keep\":{\"command\":\"tool\",\"args\":[\"x\"]},\n    \"codesearch\":{\"command\":\"old\",\"args\":[\"bad\"]}\n  },\n  \"tail\":[1,2,3]\n}",
            Encoding.UTF8);

        var after = SinglePreview(@".claude.json", Adapter().Preview(AgentIntegrationChoice.McpOnly)).AfterText;

        Assert.Contains("  \"theme\":\"dark\"", after, StringComparison.Ordinal);
        Assert.Contains("    \"keep\":{\"command\":\"tool\",\"args\":[\"x\"]}", after, StringComparison.Ordinal);
        Assert.Contains("  \"tail\":[1,2,3]", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_preserves_nested_unrelated_server_property_names()
    {
        var mcp = Path.Combine(home, ".claude.json");
        Directory.CreateDirectory(home);
        File.WriteAllText(
            mcp,
            "{\"mcpServers\":{\"keep\":{\"command\":\"tool\",\"args\":[],\"meta\":{\"codesearch\":{\"command\":\"nested\",\"args\":[]}}},\"codesearch\":{\"command\":\"old\",\"args\":[]}}}",
            Encoding.UTF8);

        var after = SinglePreview(@".claude.json", Adapter().Preview(AgentIntegrationChoice.McpOnly)).AfterText;

        Assert.Contains("\"meta\":{\"codesearch\":{\"command\":\"nested\",\"args\":[]}}", after, StringComparison.Ordinal);
        Assert.DoesNotContain("\"codesearch\":{\"command\":\"old\"", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_adds_missing_mcp_servers_without_reserializing_existing_json()
    {
        var mcp = Path.Combine(home, ".claude.json");
        Directory.CreateDirectory(home);
        File.WriteAllText(mcp, "{\n  \"theme\":\"dark\"\n}", Encoding.UTF8);

        var after = SinglePreview(@".claude.json", Adapter().Preview(AgentIntegrationChoice.McpOnly)).AfterText;

        Assert.Contains("  \"theme\":\"dark\"", after, StringComparison.Ordinal);
        Assert.Contains("\"mcpServers\"", after, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"mcpServers\":[]}")]
    [InlineData("{\"mcpServers\":{\"codesearch\":{\"command\":1,\"args\":[]}}}")]
    [InlineData("{\"mcpServers\":{\"codesearch\":{\"command\":\"x\",\"args\":[1]}}}")]
    public void Unknown_or_malformed_json_blocks_writes(string json)
    {
        var mcp = Path.Combine(home, ".claude.json");
        Directory.CreateDirectory(home);
        File.WriteAllText(mcp, json, Encoding.UTF8);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Adapter().Preview(AgentIntegrationChoice.McpOnly));

        Assert.Contains("JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_creates_timestamped_byte_backup_verifies_hash_readback_and_rolls_back()
    {
        var mcp = Path.Combine(home, ".claude.json");
        Directory.CreateDirectory(home);
        File.WriteAllText(mcp, "{\"mcpServers\":{}}", Encoding.UTF8);
        var adapter = Adapter();

        var stale = adapter.Preview(AgentIntegrationChoice.McpOnly);
        File.AppendAllText(mcp, "\n", Encoding.UTF8);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ApplyAsync(stale, TestContext.Current.CancellationToken));

        File.WriteAllText(mcp, "{\"mcpServers\":{}}", Encoding.UTF8);
        var plan = adapter.Preview(AgentIntegrationChoice.McpOnly);
        await adapter.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(SinglePreview(@".claude.json", plan).AfterText, File.ReadAllText(mcp, Encoding.UTF8));
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(mcp)!), file => file.EndsWith(".20260731-101112.bak", StringComparison.Ordinal));

        File.WriteAllText(mcp, "{\"mcpServers\":{},\"rollback\":true}", Encoding.UTF8);
        var rollback = Adapter(readBackOverride: _ => Encoding.UTF8.GetBytes("bad"));
        var rollbackPlan = rollback.Preview(AgentIntegrationChoice.McpOnly);
        var before = File.ReadAllBytes(mcp);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rollback.ApplyAsync(rollbackPlan, TestContext.Current.CancellationToken));
        Assert.Equal(before, File.ReadAllBytes(mcp));

        var newFileRollback = Adapter(readBackOverride: _ => Encoding.UTF8.GetBytes("bad"));
        var newFilePlan = newFileRollback.Preview(AgentIntegrationChoice.InstructionsOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            newFileRollback.ApplyAsync(newFilePlan, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(home, ".claude", "CLAUDE.md")));
    }

    [Fact]
    public void Instructions_are_managed_for_claude_markdown()
    {
        var plan = Adapter().Preview(AgentIntegrationChoice.InstructionsOnly);

        var instructions = SinglePreview(@".claude\CLAUDE.md", plan);

        Assert.Equal(ManagedInstructionBlock.Block + Environment.NewLine, instructions.AfterText);
    }

    public void Dispose()
    {
        if (Directory.Exists(home))
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private ClaudeConfigurationAdapter Adapter(Func<string, byte[]>? readBackOverride = null) =>
        new(home, install, new FixedTimeProvider(now), readBackOverride);

    private static AgentConfigurationFilePlan SinglePreview(string suffix, AgentConfigurationPlan plan) =>
        plan.Files.Single(file => file.Path.EndsWith(suffix, StringComparison.Ordinal));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
