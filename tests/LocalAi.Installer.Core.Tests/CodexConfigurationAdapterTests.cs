using System.Text;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Tests;

public sealed class CodexConfigurationAdapterTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "localai-codex-" + Guid.NewGuid().ToString("N"));
    private readonly string install = @"C:\LocalAi\bin";
    private readonly DateTimeOffset now = new(2026, 7, 31, 10, 11, 12, TimeSpan.Zero);

    [Theory]
    [InlineData(AgentIntegrationChoice.McpOnly, true, false)]
    [InlineData(AgentIntegrationChoice.InstructionsOnly, false, true)]
    [InlineData(AgentIntegrationChoice.McpAndInstructions, true, true)]
    [InlineData(AgentIntegrationChoice.NoChange, false, false)]
    public void Preview_honors_independent_selection_modes(
        AgentIntegrationChoice choice,
        bool config,
        bool instructions)
    {
        var adapter = Adapter();

        var plan = adapter.Preview(choice);

        Assert.Equal(config, plan.Files.Any(file => file.Path.EndsWith(@".codex\config.toml", StringComparison.Ordinal)));
        Assert.Equal(instructions, plan.Files.Any(file => file.Path.EndsWith(@".codex\AGENTS.md", StringComparison.Ordinal)));
        Assert.Equal(choice != AgentIntegrationChoice.NoChange, plan.HasChanges);
    }

    [Fact]
    public void Preview_for_new_codex_files_is_exact_and_uses_client_command_plan()
    {
        var plan = Adapter().Preview(AgentIntegrationChoice.McpAndInstructions);

        var config = SinglePreview(@".codex\config.toml", plan);
        var instructions = SinglePreview(@".codex\AGENTS.md", plan);

        Assert.Equal(
            "[mcp_servers.codesearch]\n" +
            "command = \"C:\\\\LocalAi\\\\bin\\\\launcher\\\\localai-launcher.exe\"\n" +
            "args = [\"run\", \"codesearch-mcp\"]\n\n" +
            "[mcp_servers.locallm]\n" +
            "command = \"C:\\\\LocalAi\\\\bin\\\\launcher\\\\localai-launcher.exe\"\n" +
            "args = [\"run\", \"locallm-mcp\"]\n",
            config.AfterText);
        Assert.Equal(ManagedInstructionBlock.Block + Environment.NewLine, instructions.AfterText);
    }

    [Fact]
    public void Existing_supported_toml_and_instructions_preserve_unrelated_bytes_and_values()
    {
        var configPath = Path.Combine(home, ".codex", "config.toml");
        var instructionsPath = Path.Combine(home, ".codex", "AGENTS.md");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "approval_policy = \"never\"\n\n[mcp_servers.github]\ncommand = \"github-mcp\"\nargs = [\"--stdio\"]\n\n[mcp_servers.codesearch]\ncommand = \"old\"\nargs = [\"bad\"]\n", Encoding.UTF8);
        File.WriteAllText(instructionsPath, "User header\n" + ManagedInstructionBlock.BeginMarker + "\nstale\n" + ManagedInstructionBlock.EndMarker + "\n", Encoding.UTF8);

        var plan = Adapter().Preview(AgentIntegrationChoice.McpAndInstructions);

        Assert.Contains("approval_policy = \"never\"", SinglePreview(@".codex\config.toml", plan).AfterText, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.github]\ncommand = \"github-mcp\"\nargs = [\"--stdio\"]", SinglePreview(@".codex\config.toml", plan).AfterText, StringComparison.Ordinal);
        Assert.DoesNotContain("command = \"old\"", SinglePreview(@".codex\config.toml", plan).AfterText, StringComparison.Ordinal);
        Assert.StartsWith("User header\n", SinglePreview(@".codex\AGENTS.md", plan).AfterText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[mcp_servers.codesearch]\ncommand = 1\nargs = []\n")]
    [InlineData("[mcp_servers.codesearch]\ncommand = \"x\"\nargs = [1]\n")]
    [InlineData("[mcp_servers.codesearch]\ncommand = \"x\"\nargs = []\n[mcp_servers.codesearch]\ncommand = \"y\"\nargs = []\n")]
    [InlineData("[mcp_servers.codesearch]\ncommand = \"x\"\nargs = []\n[mcp_servers.codesearch.env]\nTOKEN = \"secret\"\n")]
    [InlineData("[mcp_servers]\ncodesearch = { command = \"old\", args = [] }\n")]
    [InlineData("mcp_servers.codesearch.command = \"old\"\n")]
    [InlineData("not valid toml = [\n")]
    [InlineData("approval_policy = \"never\n")]
    public void Unknown_or_malformed_toml_blocks_writes(string config)
    {
        var path = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, config, Encoding.UTF8);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Adapter().Preview(AgentIntegrationChoice.McpOnly));

        Assert.Contains("TOML", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_uses_timestamped_backup_optimistic_hash_atomic_readback_and_can_roll_back()
    {
        var path = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "approval_policy = \"never\"\n", Encoding.UTF8);
        var adapter = Adapter();

        var stale = adapter.Preview(AgentIntegrationChoice.McpOnly);
        File.AppendAllText(path, "# concurrent\n", Encoding.UTF8);
        var concurrency = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ApplyAsync(stale, TestContext.Current.CancellationToken));
        Assert.Contains("concurrent", concurrency.Message, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(path, "approval_policy = \"never\"\n", Encoding.UTF8);
        var plan = adapter.Preview(AgentIntegrationChoice.McpOnly);
        await adapter.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(SinglePreview(@".codex\config.toml", plan).AfterText, File.ReadAllText(path, Encoding.UTF8));
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(path)!), file => file.EndsWith(".20260731-101112.bak", StringComparison.Ordinal));

        File.WriteAllText(path, "approval_policy = \"never\"\n# rollback\n", Encoding.UTF8);
        var rollbackAdapter = Adapter(readBackOverride: _ => Encoding.UTF8.GetBytes("corrupted"));
        var rollbackPlan = rollbackAdapter.Preview(AgentIntegrationChoice.McpOnly);
        var before = File.ReadAllBytes(path);
        var readback = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rollbackAdapter.ApplyAsync(rollbackPlan, TestContext.Current.CancellationToken));
        Assert.Contains("read-back", readback.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Preview_redacts_existing_credential_keys()
    {
        var path = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "apiKey = 'secret'\nclientSecret = \"hidden\"\nAuthorization = \"Bearer token-value\"\n[mcp_servers.codesearch]\ncommand = \"old\"\nargs = []\n", Encoding.UTF8);

        var plan = Adapter().Preview(AgentIntegrationChoice.McpOnly);

        Assert.DoesNotContain("secret", plan.PreviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", plan.PreviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", plan.PreviewText, StringComparison.Ordinal);
        Assert.Contains("apiKey = '<redacted>'", plan.PreviewText, StringComparison.Ordinal);
        Assert.Contains("clientSecret = \"<redacted>\"", plan.PreviewText, StringComparison.Ordinal);
        Assert.Contains("Authorization = \"<redacted>\"", plan.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multi_file_apply_rolls_back_prior_files_when_later_file_fails_readback()
    {
        var configPath = Path.Combine(home, ".codex", "config.toml");
        var instructionsPath = Path.Combine(home, ".codex", "AGENTS.md");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "approval_policy = \"never\"\n", Encoding.UTF8);
        File.WriteAllText(instructionsPath, "User guidance\n", Encoding.UTF8);
        var beforeConfig = File.ReadAllBytes(configPath);
        var beforeInstructions = File.ReadAllBytes(instructionsPath);
        var adapter = Adapter(
            path => path.EndsWith("AGENTS.md", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes("bad")
                : File.ReadAllBytes(path));
        var plan = adapter.Preview(AgentIntegrationChoice.McpAndInstructions);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ApplyAsync(plan, TestContext.Current.CancellationToken));

        Assert.Equal(beforeConfig, File.ReadAllBytes(configPath));
        Assert.Equal(beforeInstructions, File.ReadAllBytes(instructionsPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(home))
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private CodexConfigurationAdapter Adapter(Func<string, byte[]>? readBackOverride = null) =>
        new(home, install, new FixedTimeProvider(now), readBackOverride);

    private static AgentConfigurationFilePlan SinglePreview(string suffix, AgentConfigurationPlan plan) =>
        plan.Files.Single(file => file.Path.EndsWith(suffix, StringComparison.Ordinal));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
