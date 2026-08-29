using LocalAi.Installer.Core.Abstractions;
using LocalAi.ReleaseSigner;

namespace LocalAi.ReleaseSigner.Tests;

/// <summary>
/// The release command's job is to refuse, so these check the refusals — and each one checks
/// that nothing was published, not merely that the exit code was non-zero. A command that
/// reports failure after creating the tag has still created the tag.
/// </summary>
public sealed class ReleaseCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-release-" + Guid.NewGuid().ToString("N"));

    private readonly StringWriter _output = new();

    /// <summary>
    /// Every git operation this command runs must carry an empty hooks path. The first real
    /// release run died because `git switch --create` fired the repository's post-checkout
    /// hook and the hook outlived the command's two-minute git budget — the budget was
    /// measuring an embedding queue, not git.
    /// </summary>
    [Fact]
    public async Task Every_git_invocation_disables_repository_hooks()
    {
        WriteNotes("0.1.36");
        var runner = Runner().Respond("tag --list", "0.1.35\n");

        await new ReleaseCommand(runner, _root, _output)
            .PrepareAsync(ReleaseVersion.Parse("0.1.36"), TestContext.Current.CancellationToken);

        var directory = Path.Combine(Path.GetTempPath(), "localai-release-signer-no-hooks");
        var git = runner.Invocations
            .Where(line => line.StartsWith("git ", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(git);
        Assert.All(git, line => Assert.Contains(
            $"-c core.hooksPath={directory}",
            line,
            StringComparison.Ordinal));
        Assert.True(Directory.Exists(directory), $"'{directory}' does not exist.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task Preparing_a_version_already_published_is_refused()
    {
        var runner = Runner().Respond("tag --list", "0.1.34\n0.1.35\n");

        var exitCode = await new ReleaseCommand(runner, _root, _output)
            .PrepareAsync(ReleaseVersion.Parse("0.1.35"), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("already published", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Invocations, line => line.Contains("switch"));
    }

    /// <summary>
    /// The first run has nothing to open a pull request about. Scaffolding the notes and then
    /// pushing a branch containing "TODO: describe this release" is worse than stopping.
    /// </summary>
    [Fact]
    public async Task Preparing_stops_at_the_scaffold_rather_than_opening_a_pull_request()
    {
        var runner = Runner().Respond("tag --list", "0.1.35\n");

        var exitCode = await new ReleaseCommand(runner, _root, _output)
            .PrepareAsync(ReleaseVersion.Parse("0.1.36"), TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.True(File.Exists(Path.Combine(_root, "docs", "releases", "0.1.36.md")));
        Assert.True(File.Exists(Path.Combine(_root, "docs", "releases", "0.1.36.ru.md")));
        Assert.DoesNotContain(runner.Invocations, line => line.Contains("pr create"));
        Assert.DoesNotContain(runner.Invocations, line => line.Contains("push"));
    }

    [Fact]
    public async Task Preparing_written_notes_opens_the_pull_request()
    {
        WriteNotes("0.1.36");
        var runner = Runner().Respond("tag --list", "0.1.35\n");

        var exitCode = await new ReleaseCommand(runner, _root, _output)
            .PrepareAsync(ReleaseVersion.Parse("0.1.36"), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(runner.Invocations, line => line.Contains("switch --create release/0.1.36"));
        Assert.Contains(runner.Invocations, line => line.Contains("pr create"));
    }

    [Fact]
    public async Task A_dirty_tree_publishes_nothing()
    {
        WriteNotes("0.1.36");
        var runner = Runner().Respond("status --porcelain", " M src/LocalAi.Cli/Program.cs\n");

        var exitCode = await new ReleaseCommand(runner, _root, _output)
            .PublishAsync(ReleaseVersion.Parse("0.1.36"), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        AssertNothingPublished(runner);
    }

    [Fact]
    public async Task Publishing_from_a_branch_other_than_main_publishes_nothing()
    {
        WriteNotes("0.1.36");
        var runner = Runner().Respond("rev-parse --abbrev-ref HEAD", "release/0.1.36\n");

        var exitCode = await new ReleaseCommand(runner, _root, _output)
            .PublishAsync(ReleaseVersion.Parse("0.1.36"), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        AssertNothingPublished(runner);
    }

    /// <summary>
    /// A local commit that origin has never seen produces a tag pointing at a commit nobody else
    /// can fetch, and the release page then advertises a tree that does not exist upstream.
    /// </summary>
    [Fact]
    public async Task Publishing_a_commit_origin_does_not_have_publishes_nothing()
    {
        WriteNotes("0.1.36");
        var runner = Runner()
            .Respond("rev-parse HEAD", new string('a', 40) + "\n")
            .Respond("rev-parse origin/main", new string('b', 40) + "\n");

        var exitCode = await new ReleaseCommand(runner, _root, _output)
            .PublishAsync(ReleaseVersion.Parse("0.1.36"), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        AssertNothingPublished(runner);
    }

    [Fact]
    public async Task Publishing_a_commit_without_a_green_build_publishes_nothing()
    {
        WriteNotes("0.1.36");
        var runner = Runner()
            .Respond("run list", "[{\"conclusion\":\"failure\",\"status\":\"completed\"}]");

        var exitCode = await new ReleaseCommand(runner, _root, _output)
            .PublishAsync(ReleaseVersion.Parse("0.1.36"), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("No successful build-and-test run", _output.ToString(), StringComparison.Ordinal);
        AssertNothingPublished(runner);
    }

    [Fact]
    public async Task Publishing_a_version_that_is_already_tagged_publishes_nothing()
    {
        WriteNotes("0.1.36");
        var runner = Runner().Respond("tag --list", "0.1.35\n0.1.36\n");

        var exitCode = await new ReleaseCommand(runner, _root, _output)
            .PublishAsync(ReleaseVersion.Parse("0.1.36"), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        AssertNothingPublished(runner);
    }

    /// <summary>
    /// The whole point of the second half: notes that were never written cannot reach the release
    /// page, even if the pull request that carried them was merged anyway.
    /// </summary>
    [Fact]
    public async Task Publishing_unwritten_notes_publishes_nothing()
    {
        ReleaseNotes.Scaffold(_root, ReleaseVersion.Parse("0.1.36"));

        var runner = Runner();
        var exitCode = await new ReleaseCommand(runner, _root, _output)
            .PublishAsync(ReleaseVersion.Parse("0.1.36"), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        AssertNothingPublished(runner);
    }

    /// <summary>
    /// A failed build must not reach the tag, and it must say why. The first real run of this
    /// command failed here and reported only that the build had failed: the locked file that
    /// caused it was on the build's stdout, which was being discarded.
    /// </summary>
    [Fact]
    public async Task A_failed_build_publishes_nothing_and_reports_the_reason()
    {
        WriteNotes("0.1.36");
        var runner = Runner().Respond(
            "publish-localai-release",
            "MSB3027: Could not copy localai-release-signer.exe",
            exitCode: 1);

        var exitCode = await new ReleaseCommand(runner, _root, _output)
            .PublishAsync(ReleaseVersion.Parse("0.1.36"), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("MSB3027", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Invocations, line => line.Contains("release create"));
    }

    /// <summary>
    /// Preparing writes the notes and then commits them, so it cannot also demand that nothing
    /// be written. The first version of this command scaffolded the files on one run and then
    /// refused the next run because of the scaffold it had just produced.
    /// </summary>
    [Fact]
    public void The_notes_this_release_is_about_to_commit_do_not_count_as_a_dirty_tree()
    {
        var unexpected = ReleaseCommand.UnexpectedChanges(
            "?? docs/releases/0.1.36.md\n?? docs/releases/0.1.36.ru.md\n",
            ReleaseVersion.Parse("0.1.36"));

        Assert.Empty(unexpected);
    }

    [Fact]
    public void Anything_else_uncommitted_still_counts()
    {
        var unexpected = ReleaseCommand.UnexpectedChanges(
            "?? docs/releases/0.1.36.md\n M src/LocalAi.Cli/Program.cs\n",
            ReleaseVersion.Parse("0.1.36"));

        Assert.Equal([" M src/LocalAi.Cli/Program.cs"], unexpected);
    }

    /// <summary>
    /// Someone else's half-written release notes are exactly the surprise this check is for.
    /// </summary>
    [Fact]
    public void Notes_for_a_different_version_are_not_excused()
    {
        var unexpected = ReleaseCommand.UnexpectedChanges(
            "?? docs/releases/0.1.35.md\n",
            ReleaseVersion.Parse("0.1.36"));

        Assert.Single(unexpected);
    }

    /// <summary>
    /// Publishing excuses nothing: by then the notes are committed, and anything left in the tree
    /// is a real difference from the commit the tag will name.
    /// </summary>
    [Fact]
    public void Publishing_excuses_no_uncommitted_file_at_all()
    {
        var unexpected = ReleaseCommand.UnexpectedChanges(
            "?? docs/releases/0.1.36.md\n",
            version: null);

        Assert.Single(unexpected);
    }

    /// <summary>
    /// The installer is not Authenticode-signed, so Windows tells whoever downloads it that it
    /// is an unrecognised app and gives them nothing to check it against. Its hash belongs on
    /// the release page, next to the download — not in the console of whoever ran the release.
    /// </summary>
    [Fact]
    public async Task The_release_body_carries_the_installer_hash()
    {
        WriteNotes("0.1.36");
        var installer = Path.Combine(_root, "publish", "LocalAi.Installer");
        Directory.CreateDirectory(installer);
        await File.WriteAllTextAsync(
            Path.Combine(installer, "LocalAi.Installer.exe"),
            "not really an installer",
            TestContext.Current.CancellationToken);
        WriteManifest("0.1.36", new string('a', 12));
        var runner = Runner();

        var exitCode = await new ReleaseCommand(runner, _root, _output)
            .PublishAsync(ReleaseVersion.Parse("0.1.36"), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var create = Assert.Single(
            runner.Invocations,
            line => line.Contains("release create"));
        var body = create.Split(' ').First(part => part.EndsWith(".md", StringComparison.Ordinal));
        var text = await File.ReadAllTextAsync(body, TestContext.Current.CancellationToken);
        Assert.Contains("A real change.", text, StringComparison.Ordinal);
        Assert.Contains("SHA-256", text, StringComparison.Ordinal);
        // The notes themselves stay as they were written: the hash belongs to this release,
        // not to the file a later release would copy from.
        Assert.DoesNotContain(
            "SHA-256",
            await File.ReadAllTextAsync(
                Path.Combine(_root, "docs", "releases", "0.1.36.md"),
                TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    private static void AssertNothingPublished(ScriptedRunner runner)
    {
        Assert.DoesNotContain(runner.Invocations, line => line.Contains("release create"));
        Assert.DoesNotContain(runner.Invocations, line => line.Contains("publish-localai-release"));
    }

    /// <summary>
    /// The signed manifest as the publish script would have left it, so the consistency check
    /// this command runs before tagging has something real to compare against.
    /// </summary>
    private void WriteManifest(string version, string versionDirectory)
    {
        var directory = Path.Combine(_root, "publish", "release");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "release-manifest.json"),
            "{" + Quote("SchemaVersion") + ":1," +
            Quote("ReleaseVersion") + ":" + Quote(version) + "," +
            Quote("VersionDirectory") + ":" + Quote(versionDirectory) + "," +
            Quote("ModelCatalogVersion") + ":" + Quote("1") + "," +
            Quote("ProtocolVersion") + ":1," +
            Quote("BuildCompatibilityId") + ":" + Quote("localai-broker-v1") + "," +
            Quote("PackageUri") + ":" +
            Quote(ReleaseConsistency.PackageUriPrefix + version + "/localai-package.zip") + "," +
            Quote("PackageSize") + ":1024," +
            Quote("PackageSha256") + ":" + Quote(new string('A', 64)) + "," +
            Quote("RequiresAuthenticode") + ":false," +
            Quote("Models") + ":[{" + Quote("Name") + ":" + Quote("qwen3-embedding:8b-q8_0") +
            "," + Quote("ContextTokens") + ":8192," + Quote("DownloadSize") + ":1024," +
            Quote("EstimatedVramBytes") + ":1024}]}");
    }

    private static string Quote(string value) => "\"" + value + "\"";

    private void WriteNotes(string version)
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "releases"));
        File.WriteAllText(
            Path.Combine(_root, "docs", "releases", $"{version}.md"),
            $"# LocalAi {version}\n\n[Русская версия]({version}.ru.md)\n\nA real change.\n");
        File.WriteAllText(
            Path.Combine(_root, "docs", "releases", $"{version}.ru.md"),
            $"# LocalAi {version}\n\n[English version]({version}.md)\n\nНастоящее изменение.\n");
    }

    /// <summary>
    /// A runner whose defaults describe the happy path — clean tree, on main, HEAD equal to
    /// origin/main, one green build, no tags — so each test states only the one thing it is
    /// about. Anything not stated is the case where publishing would be allowed to continue,
    /// which is what makes "nothing was published" mean something.
    /// </summary>
    private static ScriptedRunner Runner() =>
        new ScriptedRunner()
            .Respond("status --porcelain", string.Empty)
            .Respond("rev-parse --abbrev-ref HEAD", "main\n")
            .Respond("rev-parse HEAD", new string('a', 40) + "\n")
            .Respond("rev-parse origin/main", new string('a', 40) + "\n")
            .Respond("tag --list", string.Empty)
            .Respond("run list", "[{\"conclusion\":\"success\",\"status\":\"completed\"}]");

    private sealed class ScriptedRunner : IProcessRunner
    {
        private readonly List<(string Match, ProcessResult Result)> _responses = [];

        public List<string> Invocations { get; } = [];

        /// <summary>
        /// The most recently declared response wins, so a test can override one leg of the happy
        /// path without restating the rest of it. Declared the other way round the defaults
        /// shadowed every override, and each refusal test sailed past the refusal it was written
        /// for and failed later on a missing manifest.
        /// </summary>
        public ScriptedRunner Respond(string match, string standardOutput, int exitCode = 0)
        {
            _responses.Insert(
                0,
                (match, new ProcessResult(exitCode, standardOutput, string.Empty, false, false)));
            return this;
        }

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var line = $"{executable} {string.Join(' ', arguments)}";
            Invocations.Add(line);
            foreach (var (match, result) in _responses)
            {
                if (line.Contains(match, StringComparison.Ordinal))
                {
                    return Task.FromResult(result);
                }
            }

            return Task.FromResult(
                new ProcessResult(0, string.Empty, string.Empty, false, false));
        }
    }

    public void Dispose()
    {
        _output.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
