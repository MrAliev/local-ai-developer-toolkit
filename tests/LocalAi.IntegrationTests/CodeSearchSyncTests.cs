using System.Diagnostics;
using System.Text.Json;
using CodeSearch.Core.Semantics;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

public sealed class CodeSearchSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-sync-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A runtime of this test's own. A sync writes a manifest and a progress file before it can
    /// fail, and doing that in the real %LOCALAPPDATA%\LocalAi puts this test in contention with
    /// whatever else is indexing at the time.
    /// </summary>
    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "localai-sync-runtime-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, "refs/heads/main")]
    [InlineData(true, "refs/heads/dev")]
    public async Task Sync_selects_local_mainline_without_a_mainline_worktree(
        bool createDev,
        string expectedRef)
    {
        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main", ".");
        Git("config", "user.email", "localai-tests@example.invalid");
        Git("config", "user.name", "LocalAi Tests");
        Git("commit", "--allow-empty", "-m", "main");
        if (createDev)
        {
            Git("branch", "dev");
        }

        Git("switch", "-c", "feature");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CodeSearchSyncCommand.ExecuteAsync(
                _root,
                cancellationToken: TestContext.Current.CancellationToken,
                runtimeRoot: _runtimeRoot));
        Assert.Contains("Nothing was embedded", error.Message);

        var commonDirectory = Git("rev-parse", "--path-format=absolute", "--git-common-dir");
        var identity = RepositoryIdentity.FromCommonDirectory(commonDirectory);
        var repositoryRuntimeRoot = Path.Combine(
            _runtimeRoot,
            "repositories",
            identity.Id);
        var manifest = Assert.IsType<LocalAi.Contracts.RepositoryManifest>(
            new RepositoryManifestStore(FsPath.From(repositoryRuntimeRoot)).Read());
        Assert.Equal(expectedRef, manifest.DevRef);
        Assert.Equal(
            LocalAi.Contracts.RepositoryIndexState.Initializing,
            manifest.State);
    }

    [Fact]
    public void Semantic_generation_accepts_succeeded_and_skipped_adapters()
    {
        CodeSearchSyncCommand.EnsureSemanticAdaptersSucceeded(
            [
                Status("typescript", SemanticAdapterState.Succeeded, "indexed"),
                Status("python", SemanticAdapterState.Skipped, "no files"),
            ]);
    }

    [Fact]
    public void Semantic_generation_rejects_failed_adapters_before_publish()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodeSearchSyncCommand.EnsureSemanticAdaptersSucceeded(
                [
                    Status("typescript", SemanticAdapterState.Failed, "bad shim"),
                    Status("python", SemanticAdapterState.Failed, "bad interpreter"),
                ]));

        Assert.Contains("typescript: bad shim", exception.Message);
        Assert.Contains("python: bad interpreter", exception.Message);
        Assert.Contains("not published", exception.Message);
    }

    [Fact]
    public void A_branch_is_not_blocked_by_a_failed_adapter_but_is_told_about_it()
    {
        // The decision, not an oversight: the base generation refuses to publish when an adapter
        // fails, because its boundaries are immutable, and a branch carries on, because refusing
        // would fail the post-commit hook on every commit until someone repairs a Node package.
        // What a branch owes in exchange is saying which worktree it just degraded.
        var written = CaptureError(() =>
            CodeSearchSyncCommand.WarnDegradedSemanticOverlay(
                [
                    Status("typescript", SemanticAdapterState.Failed, "bad shim"),
                    Status("python", SemanticAdapterState.Skipped, "no files"),
                ],
                @"D:\repo\worktree"));

        Assert.Contains(@"D:\repo\worktree", written);
        Assert.Contains("typescript: bad shim", written);
        Assert.Contains("line window", written);
        // Skipped is not failed: a repository with no Python is not a degraded repository.
        Assert.DoesNotContain("python", written);
    }

    /// <summary>
    /// The silent half of the same problem. A workspace whose projects all failed to load still
    /// returns, the fallback writes an index covering nothing, the generation is published, and
    /// sync exits 0 -- so a hook or a CI step is told the repository is indexed while every
    /// definition query answers from bounded text matching.
    /// </summary>
    [Fact]
    public void Csharp_that_produced_no_semantic_document_is_warned_about()
    {
        var source = SourceTree("Widget.cs");

        var written = CaptureError(() =>
            CodeSearchSyncCommand.ReportCsharpSemanticCoverage(
                source,
                SemanticIndexCovering(),
                uncoveredProjects: null,
                requireSemantics: false));

        Assert.Contains("covered no C# document", written);
        Assert.Contains("text matching", written);
    }

    [Fact]
    public void Csharp_that_produced_a_semantic_document_says_nothing()
    {
        var source = SourceTree("Widget.cs");

        var written = CaptureError(() =>
            CodeSearchSyncCommand.ReportCsharpSemanticCoverage(
                source,
                SemanticIndexCovering("src/Widget.cs"),
                uncoveredProjects: null,
                requireSemantics: false));

        Assert.DoesNotContain("covered no C# document", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// A repository with no C# in it has nothing to be missing, and warning there would teach
    /// everyone to ignore the line.
    /// </summary>
    [Fact]
    public void A_repository_without_csharp_is_not_called_degraded()
    {
        var source = SourceTree("README.md");

        var written = CaptureError(() =>
            CodeSearchSyncCommand.ReportCsharpSemanticCoverage(
                source,
                SemanticIndexCovering(),
                uncoveredProjects: null,
                requireSemantics: false));

        Assert.DoesNotContain("covered no C# document", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Counting C# documents rather than all of them: an index carrying TypeScript and no C# is
    /// exactly the case a total count would call healthy.
    /// </summary>
    [Fact]
    public void Documents_from_another_language_do_not_stand_in_for_csharp()
    {
        var source = SourceTree("Widget.cs");

        var written = CaptureError(() =>
            CodeSearchSyncCommand.ReportCsharpSemanticCoverage(
                source,
                SemanticIndexCovering("src/app.ts"),
                uncoveredProjects: null,
                requireSemantics: false));

        Assert.Contains("covered no C# document", written);
    }

    [Fact]
    public void Require_semantics_turns_the_warning_into_a_failure()
    {
        var source = SourceTree("Widget.cs");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodeSearchSyncCommand.ReportCsharpSemanticCoverage(
                source,
                SemanticIndexCovering(),
                uncoveredProjects: null,
                requireSemantics: true));

        Assert.Contains("covered no C# document", exception.Message);
    }

    /// <summary>
    /// A repository with no solution file has one project chosen and the rest left out, and from
    /// outside that reads exactly like full coverage: the index is not empty, the status says
    /// precise, and navigation answers from text for most of the tree.
    /// </summary>
    [Fact]
    public void Projects_left_out_for_want_of_a_solution_are_named()
    {
        var source = SourceTree("Widget.cs");

        var written = CaptureError(() =>
            CodeSearchSyncCommand.ReportCsharpSemanticCoverage(
                source,
                SemanticIndexCovering("src/Legacy/Helper.cs"),
                uncoveredProjects: [@"src\Modern\Modern.csproj", @"src\Other\Other.csproj"],
                requireSemantics: false));

        Assert.Contains("2 more", written, StringComparison.Ordinal);
        Assert.Contains("Modern.csproj", written, StringComparison.Ordinal);
        Assert.Contains("no solution file", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Require_semantics_also_refuses_a_partly_covered_repository()
    {
        var source = SourceTree("Widget.cs");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodeSearchSyncCommand.ReportCsharpSemanticCoverage(
                source,
                SemanticIndexCovering("src/Legacy/Helper.cs"),
                uncoveredProjects: [@"src\Modern\Modern.csproj"],
                requireSemantics: true));

        Assert.Contains("not covered", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Coverage is judged on what the loader says it left out, never by counting .cs files
    /// against indexed ones: this repository keeps C# under tests/Fixtures that no project
    /// compiles, and a check that warned about those would be ignored within a week.
    /// </summary>
    [Fact]
    public void Csharp_that_belongs_to_no_project_is_not_called_missing_coverage()
    {
        var source = SourceTree("src/Widget.cs", "fixtures/NotInAnyProject.cs");

        var written = CaptureError(() =>
            CodeSearchSyncCommand.ReportCsharpSemanticCoverage(
                source,
                SemanticIndexCovering("src/Widget.cs"),
                uncoveredProjects: [],
                requireSemantics: false));

        Assert.DoesNotContain("covered no C# document", written, StringComparison.Ordinal);
        Assert.DoesNotContain("not covered", written, StringComparison.Ordinal);
    }

    private string SourceTree(params string[] relativePaths)
    {
        var source = Path.Combine(_root, "sources-" + Guid.NewGuid().ToString("N"));
        foreach (var relativePath in relativePaths)
        {
            var full = Path.Combine(source, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "// content");
        }

        return source;
    }

    private static SemanticIndex SemanticIndexCovering(params string[] documents) =>
        new()
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            BaseCommit = "commit",
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = documents
                .Select(path => new SemanticDocument { RelPath = path, Hash = new byte[32] })
                .ToList(),
            Symbols = [],
            Occurrences = [],
            Relationships = [],
        };

    [Fact]
    public void A_branch_whose_adapters_worked_says_nothing()
    {
        var written = CaptureError(() =>
            CodeSearchSyncCommand.WarnDegradedSemanticOverlay(
                [
                    Status("typescript", SemanticAdapterState.Succeeded, "indexed"),
                    Status("python", SemanticAdapterState.Skipped, "no files"),
                ],
                @"D:\repo\worktree"));

        Assert.Empty(written);
    }

    /// <summary>
    /// Console.Error belongs to the process, not to one test, and tests in this module run in
    /// parallel -- so this buffer can catch a line another test wrote. Assert on what the code
    /// under test would have said, never on the buffer being empty.
    /// </summary>
    private static string CaptureError(Action action)
    {
        var original = Console.Error;
        var writer = new StringWriter();
        Console.SetError(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetError(original);
        }

        return writer.ToString();
    }

    [Fact]
    public void Synthetic_typescript_project_includes_javascript_without_touching_repository()
    {
        Directory.CreateDirectory(_root);
        var relative = Path.Combine("wwwroot", "app.js");
        var source = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "window.app = true;");

        var workspace = CodeSearchSyncCommand.CreateSyntheticTypeScriptWorkspace(
            _root,
            [relative]);
        try
        {
            Assert.Equal(
                Path.GetTempPath(),
                Path.GetDirectoryName(workspace) + Path.DirectorySeparatorChar);
            var configPath = Path.Combine(workspace, "tsconfig.json");
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            Assert.True(config.RootElement
                .GetProperty("compilerOptions")
                .GetProperty("allowJs")
                .GetBoolean());
            Assert.Equal(
                relative.Replace('\\', '/'),
                Assert.Single(config.RootElement.GetProperty("files").EnumerateArray())
                    .GetString());
            Assert.True(File.Exists(Path.Combine(workspace, relative)));
            Assert.False(File.Exists(Path.Combine(_root, "tsconfig.json")));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_runtimeRoot))
        {
            Directory.Delete(_runtimeRoot, recursive: true);
        }

        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(
                         _root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git exited with {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout.Trim();
    }

    private static SemanticAdapterStatus Status(
        string name,
        SemanticAdapterState state,
        string message) => new(name, state, message, 0);
}
