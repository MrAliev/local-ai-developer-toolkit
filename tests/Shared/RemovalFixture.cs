using System.Text;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Removal;
using LocalAi.Repository;

namespace LocalAi.TestFixtures;

/// <summary>
/// A machine LocalAi has been installed on: a populated runtime root, two configured clients,
/// and three connected repositories — one with the hooks where Git looks by default, one with
/// husky's layout, and one that has since been deleted.
///
/// Real directories rather than a mock file system, because every mistake these tests exist to
/// catch is about what is actually on disk: an unclassified file, a repository that moved, a
/// hooks directory somewhere other than $GIT_DIR/hooks, a file another program is holding open.
/// None of those survive being described by a fake.
/// </summary>
internal sealed class RemovalFixture : IDisposable
{
    public const string ManagedDispatcher =
        "#!/bin/sh\n# LocalAi managed dispatcher\nlocalai-launcher.exe hook post-commit\n";

    public const string ChainedHookBody = "#!/bin/sh\necho mine\n";

    public const string ForeignHookBody = "#!/bin/sh\necho entirely mine\n";

    public const string ClaudeInstructionsPreamble = "# My own notes\r\n\r\nKeep this paragraph.\r\n";

    public const string CodexInstructionsPreamble = "# Codex house rules\n\nAlways run the tests.\n";

    /// <summary>The version the current-version pointer names, and the one on disk.</summary>
    public const string InstalledVersion = "0.1.50";

    private readonly Dictionary<string, string?> hooksPaths =
        new(StringComparer.OrdinalIgnoreCase);

    public RemovalFixture()
    {
        Directory.CreateDirectory(LocalAppData);
        Directory.CreateDirectory(Home);
        Populate();
    }

    public string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.RemovalTests",
        Guid.NewGuid().ToString("N"));

    public string LocalAppData => Path.Combine(Root, "appdata");

    public string Runtime => Path.Combine(LocalAppData, "LocalAi");

    public string Home => Path.Combine(Root, "home");

    public string LauncherPath => Path.Combine(
        Runtime,
        "bin",
        "launcher",
        "localai-launcher.exe");

    public string PlainRepository => Path.Combine(Root, "plain");

    public string HuskyRepository => Path.Combine(Root, "husky");

    public string PlainHooks => Path.Combine(PlainRepository, ".git", "hooks");

    public string HuskyHooks => Path.Combine(HuskyRepository, ".husky");

    public string HuskyExclude => Path.Combine(HuskyRepository, ".git", "info", "exclude");

    public InstallationLayout Layout => InstallationLayout.FromLocalAppData(LocalAppData);

    /// <summary>
    /// Stands in for `git config --get core.hooksPath`. Real Git is not run here: these
    /// repositories are directory trees rather than clones, and what the tests are about is
    /// where the answer sends the search, not how Git produces it.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>> HooksPathReader =>
        (workingDirectory, _) => Task.FromResult(
            hooksPaths.GetValueOrDefault(Path.GetFullPath(workingDirectory)));

    public Task<UninstallPlan> PlanAsync(
        RemovalSelection selection,
        CancellationToken cancellationToken,
        string? registrySubKey = null,
        bool installationFollows = false) =>
        new UninstallPlanner(
                Layout,
                Home,
                HooksPathReader,
                registrySubKey: registrySubKey ?? UnusedRegistrySubKey)
            .PlanAsync(selection, cancellationToken, installationFollows);

    /// <summary>
    /// A key nothing writes, so a planner built by a test never reads the machine's real
    /// Apps &amp; features entry — including the one a developer running these tests may have
    /// installed for themselves.
    /// </summary>
    public const string UnusedRegistrySubKey =
        @"Software\LocalAi.Tests\no-entry-here\Uninstall\LocalAi";

    public static AgentConfigurationFilePlan PlannedFile(
        UninstallPlan plan,
        string agent,
        string suffix) =>
        plan.AgentConfigurations
            .Single(configuration => configuration.AgentName == agent)
            .Files
            .Single(file => file.Path.EndsWith(suffix, StringComparison.Ordinal));

    /// <summary>Every path in a tree with the bytes it holds, for comparing before against after.</summary>
    public static IReadOnlyDictionary<string, string> Snapshot(string directory)
    {
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directory,
                     "*",
                     SearchOption.AllDirectories))
        {
            contents[path] = File.Exists(path)
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(path)))
                : "<directory>";
        }

        return contents;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Populate()
    {
        WriteFile(
            Path.Combine(Runtime, "bin", "current.json"),
            "{\"schemaVersion\":1,\"version\":\"" + InstalledVersion + "\"}");
        // Every file a version directory must hold, so an inspector reading this machine
        // recognises the installation rather than calling it broken.
        foreach (var required in LocalAiPackageLayout.RequiredFiles)
        {
            WriteFile(
                Path.Combine(Runtime, "bin", "versions", InstalledVersion, required),
                "binary");
        }

        WriteFile(LauncherPath, "binary");
        WriteFile(Path.Combine(Runtime, "installer", "backups", "launcher-1", "localai-launcher.exe"), "binary");
        foreach (var name in RemovalMatrix.SettingsFileNames)
        {
            WriteFile(Path.Combine(Runtime, name), "{\"schemaVersion\":1,\"tuned\":\"" + name + "\"}");
        }

        WriteFile(Path.Combine(Runtime, "jobs", "job.json"), "{}");
        WriteFile(Path.Combine(Runtime, "telemetry", "metrics", "record.json"), "{}");
        WriteFile(Path.Combine(Runtime, "host.json"), "{}");
        WriteFile(Path.Combine(Runtime, RemovalMatrix.SigningKeyDirectoryName, "private.key"), "secret");
        WriteFile(Path.Combine(Runtime, "something-a-later-release-added.json"), "{}");

        PopulatePlainRepository();
        PopulateHuskyRepository();
        WriteManifest("gone", Path.Combine(Root, "deleted-repository", ".git"), []);

        PopulateClaude();
        PopulateCodex();
    }

    /// <summary>A repository with the dispatchers where Git looks by default.</summary>
    private void PopulatePlainRepository()
    {
        WriteFile(Path.Combine(PlainHooks, "post-commit"), ManagedDispatcher);
        WriteFile(Path.Combine(PlainHooks, "post-commit.pre-localai"), ChainedHookBody);
        WriteFile(Path.Combine(PlainHooks, "post-merge"), ManagedDispatcher);
        WriteFile(Path.Combine(PlainHooks, "post-checkout"), ForeignHookBody);
        WriteManifest("plain", Path.Combine(PlainRepository, ".git"), [PlainRepository]);
    }

    /// <summary>
    /// A husky repository: core.hooksPath points at .husky/_, the dispatchers are in .husky,
    /// and .git/info/exclude carries the ignore rules installation added for them.
    /// </summary>
    private void PopulateHuskyRepository()
    {
        WriteFile(Path.Combine(HuskyHooks, "_", "h"), "husky runner\n");
        WriteFile(Path.Combine(HuskyHooks, "post-commit"), ManagedDispatcher);
        WriteFile(
            HuskyExclude,
            "# git ls-files --others --exclude-from=.git/info/exclude\n" +
            "node_modules/\n" +
            "\n" +
            "# LocalAi managed Git hooks\n" +
            "/.husky/post-commit\n" +
            "/.husky/post-commit.pre-localai\n");
        hooksPaths[Path.GetFullPath(HuskyRepository)] = ".husky/_";
        WriteManifest("husky", Path.Combine(HuskyRepository, ".git"), [HuskyRepository]);
    }

    private void PopulateClaude()
    {
        WriteFile(
            Path.Combine(Home, ".claude.json"),
            """
            {
              "theme": "dark",
              "mcpServers": {
                "someone-elses-server": {"command": "other.exe", "args": []},
                "codesearch": {"command": "localai-launcher.exe", "args": ["run", "codesearch-mcp"]},
                "locallm": {"command": "localai-launcher.exe", "args": ["run", "locallm-mcp"]}
              }
            }
            """);
        WriteFile(
            Path.Combine(Home, ".claude", "CLAUDE.md"),
            ClaudeInstructionsPreamble + ManagedInstructionBlock.Block + "\r\n");
    }

    private void PopulateCodex()
    {
        WriteFile(
            Path.Combine(Home, ".codex", "config.toml"),
            "model = \"gpt-5\"\n\n" +
            "[mcp_servers.other]\n" +
            "command = \"other.exe\"\n" +
            "args = []\n\n" +
            "[mcp_servers.codesearch]\n" +
            "command = \"localai-launcher.exe\"\n" +
            "args = [\"run\", \"codesearch-mcp\"]\n" +
            "default_tools_approval_mode = \"prompt\"\n\n" +
            "[mcp_servers.codesearch.tools.search_code]\n" +
            "approval_mode = \"approve\"\n\n" +
            "[mcp_servers.locallm]\n" +
            "command = \"localai-launcher.exe\"\n" +
            "args = [\"run\", \"locallm-mcp\"]\n" +
            "default_tools_approval_mode = \"prompt\"\n");
        WriteFile(
            Path.Combine(Home, ".codex", "AGENTS.md"),
            CodexInstructionsPreamble + ManagedInstructionBlock.CodexBlock + "\n");
    }

    private void WriteManifest(
        string repositoryId,
        string commonDirectory,
        IReadOnlyList<string> worktrees) =>
        new RepositoryManifestStore(FsPath.From(Runtime).Combine("repositories", repositoryId)).Save(
            new RepositoryManifest(
                repositoryId,
                commonDirectory,
                "main",
                "generation-1",
                "tree-1",
                "qwen3-embedding:8b-q8_0",
                4096,
                1,
                1,
                RepositoryIndexState.Current,
                worktrees
                    .Select(path => new RepositoryWorktree(path, "head", "main"))
                    .ToArray(),
                new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero)));

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
