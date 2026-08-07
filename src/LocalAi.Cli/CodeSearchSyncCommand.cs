using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalAi.Repository;
using System.Text;
using System.Text.Json;

namespace LocalAi.Cli;

public sealed record CodeSearchSyncResult(
    string RepositoryId,
    string GenerationId,
    string BaseIndexPath,
    int OverlaysBuilt,
    bool GenerationChanged,
    // Reported rather than counted silently: fewer overlays than worktrees is otherwise
    // indistinguishable from a sync that decided they were all up to date.
    int WorktreesSkipped = 0);

public static class CodeSearchSyncCommand
{
    public const string DefaultModel = "qwen3-embedding:8b-q8_0";
    public const int DefaultDimension = 4096;
    public const int CurrentNormalizationVersion = 4;
    // Bump whenever semantic extraction changes even if the SIDX binary format does not.
    // Generations are immutable, so changing relationships without changing this value
    // would keep serving the previous semantic graph for an already indexed commit.
    public const int CurrentSemanticGenerationVersion = 10;

    private sealed record SemanticBuildResult(
        SemanticIndex Index,
        IReadOnlyList<SemanticAdapterStatus> AdapterStatuses);

    public static async Task<CodeSearchSyncResult> ExecuteAsync(
        string workingRoot,
        string model = DefaultModel,
        CancellationToken cancellationToken = default,
        bool includeOverlays = true)
    {
        var requested = RuntimeIndexLayout.Inspect(workingRoot);
        var progressStore = new RepositoryIndexProgressStore(
            requested.RepositoryRuntimeRoot);
        var manifestStore = new RepositoryManifestStore(
            requested.RepositoryRuntimeRoot);
        var lastProgress = new RepositoryIndexProgress(
            requested.RepositoryId,
            RepositoryIndexProgressPhase.Planning,
            requested.WorkingRoot,
            0,
            0,
            0,
            null,
            DateTimeOffset.UtcNow);
        progressStore.Save(lastProgress);

        void ReportProgress(
            RepositoryIndexProgressPhase phase,
            string root,
            IndexBuildProgress progress)
        {
            lastProgress = new RepositoryIndexProgress(
                requested.RepositoryId,
                phase,
                root,
                progress.ProcessedChunks,
                progress.TotalChunks,
                progress.ChunksPerSecond,
                progress.EstimatedRemaining,
                DateTimeOffset.UtcNow);
            progressStore.Save(lastProgress);
        }

        try
        {
            var worktrees = ReadWorktrees(requested.WorkingRoot);
            var mainline = ResolveMainline(
                requested,
                manifestStore.Read()?.DevRef);
            var generationIdentity = new GenerationIdentity(
                requested.RepositoryId,
                mainline.Identity.HeadCommit,
                mainline.Identity.HeadTree,
                model,
                DefaultDimension,
                1,
                CodeIndex.CurrentVersion,
                CurrentNormalizationVersion,
                1,
                CurrentSemanticGenerationVersion);
            var commonDirectory = RepositoryIdentity.FromCommonDirectory(
                RepoLocator.GitOutput(
                    requested.WorkingRoot,
                    "rev-parse --path-format=absolute --git-common-dir")
                ?? throw new InvalidOperationException("Git common directory is unavailable."))
                .CommonDirectory;
            manifestStore.Save(new RepositoryManifest(
                requested.RepositoryId,
                commonDirectory,
                mainline.Ref,
                null,
                null,
                model,
                DefaultDimension,
                1,
                CodeIndex.CurrentVersion,
                RepositoryIndexState.Initializing,
                worktrees
                    .Where(item => !item.IsPrunable)
                    .Select(item => new RepositoryWorktree(item.Path, item.Head, item.Branch))
                    .ToArray(),
                DateTimeOffset.UtcNow));
            var store = new GenerationStore(requested.RepositoryRuntimeRoot);
            var current = store.ReadCurrent();
            var generationChanged = !string.Equals(
                current?.GenerationId,
                generationIdentity.Id,
                StringComparison.Ordinal);

            GenerationManifest generation;
            try
            {
                generation = store.ReadManifest(generationIdentity.Id);
            }
            catch (Exception error) when (
                error is DirectoryNotFoundException or FileNotFoundException)
            {
                generation = await BuildGenerationAsync(
                    store,
                    current,
                    generationIdentity,
                    mainline.Identity,
                    progress => ReportProgress(
                        RepositoryIndexProgressPhase.EmbeddingBase,
                        mainline.Identity.WorkingRoot,
                        progress),
                    cancellationToken);
            }

            var targets = includeOverlays
                ? generationChanged
                    ? worktrees
                    : worktrees.Where(
                        item => SamePath(item.Path, requested.WorkingRoot)).ToArray()
                : [];
            var overlaysBuilt = 0;
            var present = SelectPresentWorktrees(
                targets.Where(item => !item.IsPrunable),
                path => Console.Error.WriteLine(
                    $"Worktree {path} no longer exists; skipping its overlay."));
            var worktreesSkipped = present.Skipped;
            foreach (var worktree in present.Worktrees)
            {
                WorkingIndexIdentity identity;
                try
                {
                    identity = RuntimeIndexLayout.Inspect(worktree.Path);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or IOException or
                        UnauthorizedAccessException &&
                    !Directory.Exists(worktree.Path))
                {
                    Console.Error.WriteLine(
                        $"Worktree {worktree.Path} disappeared while it was being inspected; " +
                        "skipping its overlay.");
                    worktreesSkipped++;
                    continue;
                }

                if (string.Equals(
                        identity.HeadTree,
                        generation.Identity.DevTree,
                        StringComparison.Ordinal) &&
                    identity.DirtyHash is null)
                {
                    continue;
                }

                var overlayPath = RuntimeIndexLayout.OverlayPath(
                    identity,
                    generation.Identity.Id);
                var semanticOverlayPath = RuntimeIndexLayout.SemanticOverlayPath(
                    identity,
                    generation.Identity.Id);
                var builtOverlay = false;
                if (!File.Exists(overlayPath))
                {
                    var embeddingCheckpointPath = overlayPath + ".embedding-checkpoint";
                    var embedder = new BrokerEmbeddingClient(
                        generation.Identity.EmbeddingModel,
                        BrokerClientFactory.CreateDefault());
                    var builder = new IndexBuilder(
                        embedder,
                        Console.Error.WriteLine,
                        progress => ReportProgress(
                            RepositoryIndexProgressPhase.EmbeddingOverlay,
                            identity.WorkingRoot,
                            progress));
                    await builder.BuildOverlayAsync(
                        identity.WorkingRoot,
                        store.IndexPath(generation.Identity.Id),
                        overlayPath,
                        cancellationToken,
                        new IndexBuildContext(
                            identity.WorkingRoot,
                            identity.HeadCommit,
                            identity.HeadTree,
                            identity.RepositoryId,
                            generation.Identity.Id,
                            identity.DirtyHash),
                        embeddingCheckpointPath,
                        generation.Identity.EmbeddingDimension);
                    DeleteEmbeddingCheckpoint(embeddingCheckpointPath);
                    builtOverlay = true;
                }

                if (!IsCurrentSemanticOverlay(
                        semanticOverlayPath,
                        identity,
                        generation.Identity))
                {
                    var semanticBuild = await BuildSemanticIndexAsync(
                        identity.WorkingRoot,
                        identity,
                        generation.Identity,
                        cancellationToken);
                    var baseSemanticIndex = SemanticIndex.Load(
                        store.SemanticIndexPath(generation.Identity.Id));
                    var semanticOverlay = SemanticIndexOverlay.Create(
                        baseSemanticIndex,
                        semanticBuild.Index,
                        RuntimeIndexLayout.GetDirtyPaths(identity.WorkingRoot));
                    semanticOverlay.Save(semanticOverlayPath);
                    builtOverlay = true;
                }

                if (builtOverlay)
                {
                    overlaysBuilt++;
                }
            }

            progressStore.Save(lastProgress = lastProgress with
            {
                Phase = RepositoryIndexProgressPhase.Publishing,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            store.SetCurrent(generation);
            var manifest = new RepositoryManifest(
                requested.RepositoryId,
                commonDirectory,
                mainline.Ref,
                generation.Identity.Id,
                generation.Identity.DevTree,
                generation.Identity.EmbeddingModel,
                generation.Identity.EmbeddingDimension,
                generation.Identity.ChunkFormatVersion,
                generation.Identity.IndexFormatVersion,
                RepositoryIndexState.Current,
                worktrees
                    .Where(item => !item.IsPrunable)
                    .Select(item => new RepositoryWorktree(
                        item.Path,
                        item.Head,
                        item.Branch))
                    .ToArray(),
                DateTimeOffset.UtcNow);
            manifestStore.Save(manifest);

            progressStore.Save(lastProgress = lastProgress with
            {
                Phase = RepositoryIndexProgressPhase.Completed,
                ProcessedChunks = lastProgress.TotalChunks,
                EstimatedRemaining = TimeSpan.Zero,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            return new CodeSearchSyncResult(
                requested.RepositoryId,
                generation.Identity.Id,
                store.IndexPath(generation.Identity.Id),
                overlaysBuilt,
                generationChanged,
                worktreesSkipped);
        }
        catch
        {
            try
            {
                progressStore.Save(lastProgress with
                {
                    Phase = RepositoryIndexProgressPhase.Failed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
            catch (Exception progressError) when (
                progressError is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException)
            {
                // Progress is observational and cannot replace the indexing failure.
            }

            throw;
        }
    }

    private static Mainline ResolveMainline(
        WorkingIndexIdentity requested,
        string? configuredRef)
    {
        var candidates = new[]
            {
                configuredRef,
                "refs/heads/dev",
                "refs/heads/main"
            }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var commit = GitValue(
                requested.WorkingRoot,
                "rev-parse",
                "--verify",
                $"{candidate}^{{commit}}");
            var tree = GitValue(
                requested.WorkingRoot,
                "rev-parse",
                "--verify",
                $"{candidate}^{{tree}}");
            if (commit is null || tree is null)
            {
                continue;
            }

            return new Mainline(
                candidate!,
                requested with
                {
                    HeadCommit = commit,
                    HeadTree = tree,
                    DirtyHash = null
                });
        }

        throw new InvalidOperationException(
            "A local mainline branch is required before indexing. " +
            "Expected refs/heads/dev or refs/heads/main.");
    }

    private static async Task<GenerationManifest> BuildGenerationAsync(
        GenerationStore store,
        GenerationPointer? current,
        GenerationIdentity generation,
        WorkingIndexIdentity dev,
        Action<IndexBuildProgress> progress,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(
            dev.RepositoryRuntimeRoot,
            "staging");
        Directory.CreateDirectory(stagingRoot);
        var workIndex = Path.Combine(
            stagingRoot,
            generation.Id + "." + Guid.NewGuid().ToString("N") + ".cidx");
        var workSemanticIndex = Path.Combine(
            stagingRoot,
            generation.Id + "." + Guid.NewGuid().ToString("N") + ".sidx");
        var embeddingCheckpointPath = Path.Combine(
            stagingRoot,
            generation.Id + ".embedding-checkpoint");
        try
        {
            if (current is not null)
            {
                var previous = store.ReadManifest(current.GenerationId);
                var reusable = store.IndexPath(previous.Identity.Id);
                if (generation.CanReuseCorpusFrom(previous.Identity) &&
                    File.Exists(reusable))
                {
                    File.Copy(reusable, workIndex);
                }
            }

            using var snapshot = CommitSnapshot.Create(dev.WorkingRoot, dev.HeadCommit);
            var embedder = new BrokerEmbeddingClient(
                generation.EmbeddingModel,
                BrokerClientFactory.CreateDefault());
            var builder = new IndexBuilder(
                embedder,
                Console.Error.WriteLine,
                progress);
            await builder.BuildAsync(
                snapshot.Root,
                workIndex,
                force: false,
                cancellationToken,
                new IndexBuildContext(
                    dev.WorkingRoot,
                    dev.HeadCommit,
                    dev.HeadTree,
                    dev.RepositoryId,
                    generation.Id),
                embeddingCheckpointPath,
                generation.EmbeddingDimension);
            var header = CodeIndex.Load(workIndex, withVectors: false);
            if (header.Dim != generation.EmbeddingDimension)
            {
                throw new InvalidDataException(
                    $"Embedding dimension {header.Dim} does not match expected " +
                    $"{generation.EmbeddingDimension}.");
            }

            var semanticIndex = await BuildSemanticIndexAsync(
                snapshot.Root,
                dev,
                generation,
                cancellationToken);

            EnsureSemanticAdaptersSucceeded(semanticIndex.AdapterStatuses);
            semanticIndex.Index.Save(workSemanticIndex);
            var published = store.PublishIndex(
                workIndex,
                generation,
                workSemanticIndex,
                semanticAdapterStatuses: semanticIndex.AdapterStatuses);
            DeleteEmbeddingCheckpoint(embeddingCheckpointPath);
            return published;
        }
        finally
        {
            if (File.Exists(workIndex))
            {
                File.Delete(workIndex);
            }

            if (File.Exists(workSemanticIndex))
            {
                File.Delete(workSemanticIndex);
            }
        }
    }

    internal static void EnsureSemanticAdaptersSucceeded(
        IReadOnlyList<SemanticAdapterStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var failures = statuses
            .Where(status => status.State == SemanticAdapterState.Failed)
            .ToArray();
        if (failures.Length == 0)
        {
            return;
        }

        var details = string.Join(
            "; ",
            failures.Select(failure => $"{failure.Name}: {failure.Message}"));
        throw new InvalidOperationException(
            "Semantic generation was not published because required adapters failed: " +
            details);
    }

    private static void DeleteEmbeddingCheckpoint(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Embedding checkpoint '{path}' could not be removed: {exception.Message}");
        }
    }

    private static async Task<SemanticBuildResult> BuildSemanticIndexAsync(
        string sourceRoot,
        WorkingIndexIdentity snapshot,
        GenerationIdentity generation,
        CancellationToken cancellationToken)
    {
        await using var loaded = await RoslynSolutionLoader.LoadAsync(
            sourceRoot,
            message => Console.Error.WriteLine($"Roslyn: {message}"),
            cancellationToken);
        SemanticIndex languageIndex;
        if (loaded is null)
        {
            var empty = EmptySemanticIndex(generation) with
            {
                GitTree = snapshot.HeadTree,
                DirtyHash = snapshot.DirtyHash,
                BaseCommit = snapshot.HeadCommit,
            };
            languageIndex = new XamlSemanticIndexer().Supplement(empty, sourceRoot);
        }
        else
        {
            var csharp = await loaded.BuildIndexAsync(
                sourceRoot,
                new SemanticIndexBuildIdentity(
                    generation.RepositoryId,
                    generation.Id,
                    snapshot.HeadTree,
                    snapshot.DirtyHash,
                    snapshot.HeadCommit,
                    CommitTimestamp(snapshot.WorkingRoot, snapshot.HeadCommit)),
                cancellationToken);
            languageIndex = new XamlSemanticIndexer().Supplement(csharp, sourceRoot);
        }

        return await RunScipAdaptersAsync(
            languageIndex,
            sourceRoot,
            cancellationToken);
    }

    private static bool IsCurrentSemanticOverlay(
        string path,
        WorkingIndexIdentity snapshot,
        GenerationIdentity generation)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var overlay = SemanticIndexOverlay.Load(path);
            return string.Equals(overlay.RepositoryId, snapshot.RepositoryId, StringComparison.Ordinal) &&
                   string.Equals(overlay.GenerationId, generation.Id, StringComparison.Ordinal) &&
                   string.Equals(overlay.BaseGitTree, generation.DevTree, StringComparison.Ordinal) &&
                   string.Equals(overlay.GitTree, snapshot.HeadTree, StringComparison.Ordinal) &&
                   string.Equals(overlay.DirtyHash, snapshot.DirtyHash, StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static async Task<SemanticBuildResult> RunScipAdaptersAsync(
        SemanticIndex index,
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var policy = SemanticIndexingPolicyStore.ReadDefault();
        var statuses = new List<SemanticAdapterStatus>();
        var files = FileScanner.Enumerate(sourceRoot)
            .Select(path => path.Replace('\\', '/'))
            .ToArray();
        if (!policy.Enabled)
        {
            statuses.Add(Skipped("typescript", "Semantic external adapters are disabled."));
            statuses.Add(Skipped("python", "Semantic external adapters are disabled."));
            return new SemanticBuildResult(index, statuses);
        }

        var importer = new ScipImporter(policy.ImportLimits());
        var runner = new ScipAdapterRunner(importer);
        var hasTypeScript = files.Any(path =>
            path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase));
        var typeScript = policy.TypeScript;
        if (!hasTypeScript)
        {
            statuses.Add(Skipped("typescript", "No TypeScript or JavaScript files detected."));
        }
        else if (!typeScript.Enabled)
        {
            statuses.Add(Skipped("typescript", "Adapter is disabled by policy."));
        }
        else
        {
            var arguments = typeScript.Arguments.ToList();
            string? syntheticWorkspace = null;
            var adapterRoot = sourceRoot;
            try
            {
                if (arguments.SequenceEqual(["index"], StringComparer.Ordinal))
                {
                    var projects = files
                        .Where(path => string.Equals(
                            Path.GetFileName(path),
                            "tsconfig.json",
                            StringComparison.OrdinalIgnoreCase))
                        .Select(path => Path.GetDirectoryName(Path.GetFullPath(Path.Combine(
                            sourceRoot,
                            path.Replace('/', Path.DirectorySeparatorChar))))!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (projects.Length == 0)
                    {
                        syntheticWorkspace = CreateSyntheticTypeScriptWorkspace(
                            sourceRoot,
                            files.Where(IsTypeScriptSourceFile).ToArray());
                        adapterRoot = syntheticWorkspace;
                        projects = [syntheticWorkspace];
                    }

                    // scip-typescript 0.4.0 compares its project root with
                    // slash-normalized source paths using ordinal equality. Windows
                    // backslashes make it walk to the drive root and encode the
                    // temporary absolute path into every local symbol.
                    arguments.AddRange(projects.Select(project =>
                        project.Replace('\\', '/')));
                }

                var result = await runner.RunAsync(
                    index,
                    adapterRoot,
                    Spec("typescript", typeScript, arguments, policy),
                    cancellationToken);
                index = result.Index;
                statuses.Add(result.Status);
                ReportAdapter(result.Status);
            }
            finally
            {
                if (syntheticWorkspace is not null &&
                    Directory.Exists(syntheticWorkspace))
                {
                    Directory.Delete(syntheticWorkspace, recursive: true);
                }
            }
        }

        var hasPython = files.Any(path =>
            path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase));
        var python = policy.Python;
        if (!hasPython)
        {
            statuses.Add(Skipped("python", "No Python files detected."));
        }
        else if (!python.Enabled)
        {
            statuses.Add(Skipped("python", "Adapter is disabled by policy."));
        }
        else
        {
            var result = await runner.RunAsync(
                index,
                sourceRoot,
                Spec("python", python, python.Arguments, policy),
                cancellationToken);
            index = result.Index;
            statuses.Add(result.Status);
            ReportAdapter(result.Status);
        }

        return new SemanticBuildResult(index, statuses);
    }

    private static ScipAdapterSpec Spec(
        string name,
        ScipLanguageAdapterPolicy adapter,
        IReadOnlyList<string> arguments,
        SemanticIndexingPolicy policy) =>
        new(
            name,
            adapter.Executable,
            arguments,
            adapter.OutputFile,
            TimeSpan.FromSeconds(policy.TimeoutSeconds),
            policy.MaximumProcessOutputBytes,
            adapter.UnspecifiedPositionEncoding);

    internal static string CreateSyntheticTypeScriptWorkspace(
        string sourceRoot,
        IReadOnlyList<string> sourceFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(sourceFiles);
        if (sourceFiles.Count == 0)
        {
            throw new InvalidOperationException(
                "A synthetic TypeScript project requires at least one source file.");
        }

        var sourceRootFull = Path.GetFullPath(sourceRoot)
            .TrimEnd(Path.DirectorySeparatorChar);
        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"localai-scip-typescript-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var relativeFiles = new List<string>(sourceFiles.Count);
            foreach (var path in sourceFiles)
            {
                var relative = path.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relative) || relative
                    .Split(Path.DirectorySeparatorChar)
                    .Any(segment => segment is "" or "." or ".."))
                {
                    throw new InvalidOperationException(
                        $"Synthetic TypeScript source path is not canonical: '{path}'.");
                }

                var source = Path.GetFullPath(Path.Combine(sourceRootFull, relative));
                var sourcePrefix = sourceRootFull + Path.DirectorySeparatorChar;
                if (!source.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(source))
                {
                    throw new InvalidOperationException(
                        $"Synthetic TypeScript source is unavailable: '{path}'.");
                }

                var target = Path.GetFullPath(Path.Combine(workspace, relative));
                var workspacePrefix = workspace + Path.DirectorySeparatorChar;
                if (!target.StartsWith(
                        workspacePrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Synthetic TypeScript target escapes its workspace: '{path}'.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
                relativeFiles.Add(path.Replace('\\', '/'));
            }

            var config = new
            {
                compilerOptions = new
                {
                    allowJs = true,
                    checkJs = false,
                    noEmit = true,
                    skipLibCheck = true,
                    target = "ES2020",
                    rootDir = ".",
                },
                files = relativeFiles,
            };
            File.WriteAllText(
                Path.Combine(workspace, "tsconfig.json"),
                JsonSerializer.Serialize(config),
                new UTF8Encoding(false));
            return workspace;
        }
        catch
        {
            Directory.Delete(workspace, recursive: true);
            throw;
        }
    }

    private static bool IsTypeScriptSourceFile(string path) =>
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase);

    private static SemanticAdapterStatus Skipped(string name, string message) =>
        new(name, SemanticAdapterState.Skipped, message, 0);

    private static void ReportAdapter(SemanticAdapterStatus status) =>
        Console.Error.WriteLine(
            $"SCIP {status.Name}: {status.State.ToString().ToLowerInvariant()} — {status.Message}");

    private static SemanticIndex EmptySemanticIndex(GenerationIdentity generation) =>
        new()
        {
            RepositoryId = generation.RepositoryId,
            GenerationId = generation.Id,
            GitTree = generation.DevTree,
            DirtyHash = null,
            BaseCommit = generation.DevCommit,
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = [],
            Symbols = [],
            Occurrences = [],
            Relationships = [],
        };

    private static DateTime CommitTimestamp(string root, string commit)
    {
        var value = GitValue(root, "show", "-s", "--format=%cI", commit);
        return DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp.UtcDateTime
            : DateTime.UnixEpoch;
    }

    /// <summary>
    /// Drops the worktrees that are no longer on disk, and says which.
    ///
    /// A worktree can be removed while this runs — it belongs to somebody else, and a sync of a
    /// large repository takes tens of minutes. Losing the whole run to that, after an hour of
    /// embedding, is the wrong answer: that overlay is gone either way, and every other
    /// worktree still deserves its own.
    ///
    /// Only absence is tolerated. A worktree that exists but cannot be read still stops the
    /// run, because publishing a generation with overlays quietly missing is worse than
    /// failing.
    /// </summary>
    internal static (IReadOnlyList<GitWorktree> Worktrees, int Skipped) SelectPresentWorktrees(
        IEnumerable<GitWorktree> targets,
        Action<string> report)
    {
        var present = new List<GitWorktree>();
        var skipped = 0;
        foreach (var worktree in targets)
        {
            if (Directory.Exists(worktree.Path))
            {
                present.Add(worktree);
                continue;
            }

            report(worktree.Path);
            skipped++;
        }

        return (present, skipped);
    }

    private static IReadOnlyList<GitWorktree> ReadWorktrees(string root)
    {
        var output = RepoLocator.GitOutput(root, "worktree list --porcelain")
            ?? throw new InvalidOperationException("Git worktree inventory is unavailable.");
        return WorktreeInventory.ParsePorcelain(output);
    }

    private static string? GitValue(string root, params string[] arguments)
    {
        var output = RepoLocator.GitOutputBytes(root, arguments);
        return output is null
            ? null
            : Encoding.UTF8.GetString(output).Trim();
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private sealed record Mainline(
        string Ref,
        WorkingIndexIdentity Identity);
}
