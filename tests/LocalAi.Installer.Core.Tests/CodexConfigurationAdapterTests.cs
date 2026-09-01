using System.Text;
using LocalAi.Contracts;
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

    /// <summary>
    /// Codex has no import mechanism and no skills, so it gets both halves inline — and must
    /// not be told to invoke something that does not exist on its side.
    /// </summary>
    [Fact]
    public void Codex_gets_the_reference_material_inline_rather_than_a_pointer_to_a_skill()
    {
        Directory.CreateDirectory(home);

        var after = SinglePreview(Path.Combine(".codex", "AGENTS.md"),
            Adapter().Preview(AgentIntegrationChoice.InstructionsOnly)).AfterText;
        var flat = string.Join(" ", after.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains("core.hooksPath", flat, StringComparison.Ordinal);
        Assert.Contains("quote the refusal verbatim", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke it before", flat, StringComparison.Ordinal);
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
            "args = [\"run\", \"codesearch-mcp\"]\n" +
            "default_tools_approval_mode = \"prompt\"\n\n" +
            "[mcp_servers.locallm]\n" +
            "command = \"C:\\\\LocalAi\\\\bin\\\\launcher\\\\localai-launcher.exe\"\n" +
            "args = [\"run\", \"locallm-mcp\"]\n" +
            "default_tools_approval_mode = \"prompt\"\n\n" +
            ToolSections("codesearch", McpToolNames.CodeSearch) + "\n\n" +
            ToolSections("locallm", McpToolNames.LocalLm) + "\n",
            config.AfterText);
        // Codex receives both halves inline, so its text is not the core Claude gets.
        Assert.Equal(
            ManagedInstructionBlock.CodexBlock + Environment.NewLine,
            instructions.AfterText);
    }

    /// <summary>
    /// The upgrade path #208 chose: the managed sections rebuild to the approval matrix, and
    /// only the literal approve an earlier installer wrote is the installer's to rebuild.
    /// Everything else is the user's — deny survives, and a stricter-than-matrix prompt on a
    /// read tool survives too, so a deviation towards stricter is permanent.
    /// </summary>
    [Fact]
    public void An_upgrade_rebuilds_installer_written_approvals_and_keeps_the_users()
    {
        var configPath = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(
            configPath,
            "[mcp_servers.codesearch]\n" +
            "command = \"old\"\n" +
            "args = [\"bad\"]\n" +
            "default_tools_approval_mode = \"approve\"\n\n" +
            "[mcp_servers.locallm]\n" +
            "command = \"old\"\n" +
            "args = [\"bad\"]\n" +
            "default_tools_approval_mode = \"approve\"\n\n" +
            "[mcp_servers.locallm.tools.local_models_sync]\n" +
            "approval_mode = \"approve\"\n\n" +
            "[mcp_servers.locallm.tools.local_model_feedback]\n" +
            "approval_mode = \"deny\"\n\n" +
            "[mcp_servers.codesearch.tools.search_code]\n" +
            "approval_mode = \"prompt\"\n",
            Encoding.UTF8);

        var after = SinglePreview(
            @".codex\config.toml",
            Adapter().Preview(AgentIntegrationChoice.McpOnly)).AfterText;

        Assert.DoesNotContain(
            "default_tools_approval_mode = \"approve\"",
            after,
            StringComparison.Ordinal);
        Assert.Contains(
            "[mcp_servers.locallm.tools.local_models_sync]\n" +
            "approval_mode = \"prompt\"",
            after,
            StringComparison.Ordinal);
        Assert.Contains(
            "[mcp_servers.locallm.tools.local_model_feedback]\n" +
            "approval_mode = \"deny\"",
            after,
            StringComparison.Ordinal);
        Assert.Contains(
            "[mcp_servers.codesearch.tools.search_code]\n" +
            "approval_mode = \"prompt\"",
            after,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The matrix itself: the two heavyweights prompt, the rest run, and a tool the matrix
    /// has never heard of prompts — the fail-safe for a future release's tool.
    /// </summary>
    [Fact]
    public void The_matrix_prompts_exactly_the_heavyweights_and_every_stranger()
    {
        Assert.Equal(
            McpToolApproval.Prompt,
            McpToolNames.ApprovalFor("locallm", "local_models_sync"));
        Assert.Equal(
            McpToolApproval.Prompt,
            McpToolNames.ApprovalFor("locallm", "local_model_feedback"));
        Assert.All(
            McpToolNames.CodeSearch,
            tool => Assert.Equal(
                McpToolApproval.Approve,
                McpToolNames.ApprovalFor("codesearch", tool)));
        Assert.All(
            McpToolNames.LocalLm.Except(
                ["local_models_sync", "local_model_feedback"]),
            tool => Assert.Equal(
                McpToolApproval.Approve,
                McpToolNames.ApprovalFor("locallm", tool)));
        Assert.Equal(
            McpToolApproval.Prompt,
            McpToolNames.ApprovalFor("locallm", "some_future_tool"));
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

    /// <summary>
    /// The shape a Codex anybody uses actually has: quoted key segments for every plugin and
    /// every project path, sub-tables carrying per-tool approvals, and Windows paths written as
    /// literal strings. All of it was refused as "malformed", so the only Codex that could be
    /// configured was an empty one.
    /// </summary>
    [Fact]
    public void A_real_codex_configuration_is_updated_rather_than_refused()
    {
        var configPath = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(
            configPath,
            "approval_policy = \"never\"\n\n" +
            "[plugins.\"github@openai-api-curated\"]\nenabled = true\n\n" +
            "[projects.'r:\\intelwash']\ntrust_level = \"trusted\"\n\n" +
            "[mcp_servers.codesearch]\n" +
            "command = 'C:\\Old\\localai-launcher.exe'\n" +
            "args = [\"run\", \"codesearch-mcp\"]\n\n" +
            "[mcp_servers.codesearch.tools.search_code]\napproval_mode = \"approve\"\n\n" +
            "[mcp_servers.locallm]\n" +
            "command = 'C:\\Old\\localai-launcher.exe'\n" +
            "args = [\"run\", \"locallm-mcp\"]\n\n" +
            "[mcp_servers.locallm.tools.read_image]\napproval_mode = \"approve\"\n",
            Encoding.UTF8);

        var after = SinglePreview(
            @".codex\config.toml",
            Adapter().Preview(AgentIntegrationChoice.McpOnly)).AfterText;

        // The command is repointed...
        Assert.Contains(
            "command = \"C:\\\\LocalAi\\\\bin\\\\launcher\\\\localai-launcher.exe\"",
            after,
            StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Old\\localai-launcher.exe", after, StringComparison.Ordinal);
        // ...and the user's own settings are still there. Deleting a per-tool approval to
        // update a command line would be worse than not configuring anything.
        Assert.Contains(
            "[mcp_servers.codesearch.tools.search_code]\napproval_mode = \"approve\"",
            after,
            StringComparison.Ordinal);
        Assert.Contains(
            "[mcp_servers.locallm.tools.read_image]\napproval_mode = \"approve\"",
            after,
            StringComparison.Ordinal);
        Assert.Contains("[plugins.\"github@openai-api-curated\"]", after, StringComparison.Ordinal);
        Assert.Contains("[projects.'r:\\intelwash']", after, StringComparison.Ordinal);
        Assert.Contains("approval_policy = \"never\"", after, StringComparison.Ordinal);
        // Each server is still declared exactly once.
        Assert.Equal(
            1,
            after.Split("[mcp_servers.codesearch]\n", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Every_tool_a_managed_server_exposes_gets_a_row()
    {
        var configPath = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        // The shape a long-lived machine is in: rows for the tools that were reached for at
        // least once, and nothing for the ones that shipped later.
        File.WriteAllText(
            configPath,
            "[mcp_servers.codesearch]\n" +
            "command = \"C:\\\\Old\\\\localai-launcher.exe\"\n" +
            "args = [\"run\", \"codesearch-mcp\"]\n\n" +
            "[mcp_servers.codesearch.tools.search_code]\napproval_mode = \"approve\"\n\n" +
            "[mcp_servers.locallm]\n" +
            "command = \"C:\\\\Old\\\\localai-launcher.exe\"\n" +
            "args = [\"run\", \"locallm-mcp\"]\n\n" +
            "[mcp_servers.locallm.tools.ask_local]\napproval_mode = \"approve\"\n",
            Encoding.UTF8);

        var after = SinglePreview(
            @".codex\config.toml",
            Adapter().Preview(AgentIntegrationChoice.McpOnly)).AfterText;

        foreach (var tool in McpToolNames.CodeSearch)
        {
            Assert.Contains(
                $"[mcp_servers.codesearch.tools.{tool}]",
                after,
                StringComparison.Ordinal);
        }

        foreach (var tool in McpToolNames.LocalLm)
        {
            Assert.Contains(
                $"[mcp_servers.locallm.tools.{tool}]",
                after,
                StringComparison.Ordinal);
        }

        // Written once each, not once per run.
        Assert.Equal(
            1,
            after.Split(
                "[mcp_servers.codesearch.tools.search_code]",
                StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void A_server_level_approval_the_user_chose_is_not_overwritten()
    {
        var configPath = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(
            configPath,
            "[mcp_servers.codesearch]\n" +
            "command = \"C:\\\\Old\\\\localai-launcher.exe\"\n" +
            "args = [\"run\", \"codesearch-mcp\"]\n" +
            "default_tools_approval_mode = \"prompt\"\n",
            Encoding.UTF8);

        var after = SinglePreview(
            @".codex\config.toml",
            Adapter().Preview(AgentIntegrationChoice.McpOnly)).AfterText;

        // The same rule as a per-tool row: the installer supplies a default where there is none
        // and does not argue with one that is already there.
        Assert.Contains(
            "default_tools_approval_mode = \"prompt\"",
            after,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[mcp_servers.codesearch]\ncommand = \"C:\\\\LocalAi\\\\bin\\\\launcher\\\\" +
            "localai-launcher.exe\"\nargs = [\"run\", \"codesearch-mcp\"]\n" +
            "default_tools_approval_mode = \"approve\"",
            after,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_the_user_refused_keeps_its_refusal()
    {
        var configPath = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(
            configPath,
            "[mcp_servers.codesearch]\n" +
            "command = \"C:\\\\Old\\\\localai-launcher.exe\"\n" +
            "args = [\"run\", \"codesearch-mcp\"]\n\n" +
            "[mcp_servers.codesearch.tools.lsp_open_document]\napproval_mode = \"deny\"\n",
            Encoding.UTF8);

        var after = SinglePreview(
            @".codex\config.toml",
            Adapter().Preview(AgentIntegrationChoice.McpOnly)).AfterText;

        // Only absence is filled. An entry that says no is a decision, not an omission, and the
        // installer has no business promoting it to yes.
        Assert.Contains(
            "[mcp_servers.codesearch.tools.lsp_open_document]\napproval_mode = \"deny\"",
            after,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            after.Split(
                "[mcp_servers.codesearch.tools.lsp_open_document]",
                StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void A_configuration_that_already_lists_every_tool_is_left_alone()
    {
        var configPath = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllBytes(configPath, []);
        var first = SinglePreview(
            @".codex\config.toml",
            Adapter().Preview(AgentIntegrationChoice.McpOnly)).AfterBytes;
        // Written the way ApplyAsync writes it — the installer emits no BOM, and a test that
        // added one would be measuring its own encoding rather than idempotence.
        File.WriteAllBytes(configPath, first);

        var plan = Adapter().Preview(AgentIntegrationChoice.McpOnly);

        Assert.Empty(plan.Files);
    }

    [Fact]
    public void A_quoted_key_containing_an_equals_sign_is_not_cut_in_half()
    {
        var configPath = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(
            configPath,
            "[projects]\n'c:\\repo\\a=b' = \"trusted\"\n",
            Encoding.UTF8);

        var after = SinglePreview(
            @".codex\config.toml",
            Adapter().Preview(AgentIntegrationChoice.McpOnly)).AfterText;

        Assert.Contains("'c:\\repo\\a=b' = \"trusted\"", after, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[mcp_servers.codesearch]\ncommand = 1\nargs = []\n")]
    [InlineData("[mcp_servers.codesearch]\ncommand = \"x\"\nargs = [1]\n")]
    [InlineData("[mcp_servers.codesearch]\ncommand = \"x\"\nargs = []\n[mcp_servers.codesearch]\ncommand = \"y\"\nargs = []\n")]
    [InlineData("[mcp_servers]\ncodesearch = { command = \"old\", args = [] }\n")]
    [InlineData("mcp_servers.codesearch.command = \"old\"\n")]
    [InlineData("not valid toml = [\n")]
    [InlineData("[mcp_servers.codesearch]\ncommand = \"x\"\nargs = []\ntimeout = 1.5\n")]
    public void Unknown_or_malformed_toml_blocks_writes(string config)
    {
        var path = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, config, Encoding.UTF8);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Adapter().Preview(AgentIntegrationChoice.McpOnly));

        Assert.Contains("TOML", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #209/m1: valid TOML the installer does not manage used to refuse the whole install
    /// because the validator policed every line of the user's file against the few
    /// constructs it understood. Foreign sections are preserved byte-for-byte, so they
    /// need tolerance, not policing.
    /// </summary>
    [Theory]
    [InlineData("[profiles.fast]\ntemperature = 0.7\n", "temperature = 0.7")]
    [InlineData("[history]\nsince = 2026-08-31T12:00:00Z\n", "since = 2026-08-31T12:00:00Z")]
    [InlineData(
        "[limits]\nquota = { cpu = 2, memory = \"4g\" }\n",
        "quota = { cpu = 2, memory = \"4g\" }")]
    [InlineData("[sandbox]\nallow = [\n  \"read\",\n  \"write\",\n]\n", "  \"read\",")]
    [InlineData("broken_but_not_ours = \"never\n", "broken_but_not_ours = \"never")]
    public void Toml_the_installer_does_not_manage_is_tolerated_and_preserved(
        string foreign,
        string survivingLine)
    {
        var path = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, foreign, Encoding.UTF8);

        var after = SinglePreview(
            @".codex\config.toml",
            Adapter().Preview(AgentIntegrationChoice.McpOnly)).AfterText;

        Assert.Contains(survivingLine, after, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.codesearch]", after, StringComparison.Ordinal);
    }

    [Fact]
    public void A_multiline_string_is_refused_because_it_can_spoof_a_managed_header()
    {
        var path = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            "[notes]\ntext = \"\"\"\n[mcp_servers.codesearch]\n\"\"\"\n",
            Encoding.UTF8);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Adapter().Preview(AgentIntegrationChoice.McpOnly));

        Assert.Contains("multiline", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #209/m2: bytes that are not valid UTF-8 used to decode into U+FFFD replacement
    /// characters, and apply would re-encode those over the user's original bytes.
    /// </summary>
    [Fact]
    public void A_config_that_is_not_valid_utf8_is_refused_before_any_plan_exists()
    {
        var path = Path.Combine(home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x5B, 0x6E, 0x5D, 0x0A, 0xC3, 0x28]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Adapter().Preview(AgentIntegrationChoice.McpOnly));

        Assert.Contains("UTF-8", exception.Message, StringComparison.Ordinal);
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

    private static string ToolSections(string server, IReadOnlyList<string> tools) =>
        string.Join(
            "\n\n",
            tools.Select(tool =>
                $"[mcp_servers.{server}.tools.{tool}]\napproval_mode = \"" +
                (McpToolNames.ApprovalFor(server, tool) == McpToolApproval.Approve
                    ? "approve"
                    : "prompt") +
                "\""));

    private CodexConfigurationAdapter Adapter(Func<string, byte[]>? readBackOverride = null) =>
        new(home, install, new FixedTimeProvider(now), readBackOverride);

    private static AgentConfigurationFilePlan SinglePreview(string suffix, AgentConfigurationPlan plan) =>
        plan.Files.Single(file => file.Path.EndsWith(suffix, StringComparison.Ordinal));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
