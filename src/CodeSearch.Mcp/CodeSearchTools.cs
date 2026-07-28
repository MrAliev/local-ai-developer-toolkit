using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;
using ModelContextProtocol.Server;

namespace CodeSearch.Mcp;

[McpServerToolType]
public static class CodeSearchTools
{
    /// <summary>
    /// Above this many chunks, an inline refresh would run for minutes and blow the tool-call
    /// budget. A post-commit incremental refresh is far below it; a cold build is far above.
    /// </summary>
    private const int InlineRefreshChunkLimit = 4000;

    /// <summary>
    /// Only used when a repository has no index yet. Quality-first, and it fits a single 16GB
    /// card: the bare `:8b` tag is Q4_K_M, and fp16 only runs split across two GPUs.
    /// </summary>
    private const string DefaultModel = "qwen3-embedding:8b-q8_0";

    [McpServerTool(Name = "search_code")]
    [Description("""
        Semantic + literal search over a repository's code, chunked by symbol (class, method,
        property) rather than by file. Use this INSTEAD of grep/glob as the first step for any
        "where does X live", "which code handles Y", "what already does something like Z"
        question - it answers by meaning, so it finds the right code without knowing its name,
        and it costs a fraction of the tokens that reading candidate files would.
        Falls back gracefully: literal identifiers in the query are matched exactly too, so
        "where is TrustSetFlags" works as well as a plain-language description.
        """)]
    public static async Task<string> SearchCode(
        SearchService service,
        [Description("What you're looking for. A natural-language description works best; exact symbol names also work.")]
        string query,
        [Description("Repository root. Defaults to the git repository containing the working directory; a worktree resolves to its main repository.")]
        string? root = null,
        [Description("How many results to return. Default 10.")]
        int topK = 10,
        [Description("Restrict to a chunk kind: Type, Method, Text, or File. Omit for all.")]
        string? kind = null,
        [Description("Only match files whose path contains this substring, e.g. 'Reports' or '.Domain'.")]
        string? pathContains = null,
        [Description("Max hits from any single file. Default 3, keeps one big class from taking every slot.")]
        int maxPerFile = 3,
        CancellationToken cancellationToken = default)
    {
        var options = new SearchOptions
        {
            TopK = Math.Clamp(topK, 1, 50),
            Kind = Enum.TryParse<ChunkKind>(kind, ignoreCase: true, out var parsed) ? parsed : null,
            PathContains = string.IsNullOrWhiteSpace(pathContains) ? null : pathContains,
            MaxPerFile = Math.Max(0, maxPerFile),
        };

        IReadOnlyList<SearchHit> hits;
        try
        {
            hits = await service.SearchAsync(query, root, options, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            var status = service.Status(root);
            return $"""
                No index exists for {status.RepositoryRoot}.
                Build it (runs for minutes on a large repository, so run it in the background):
                  {IndexCommand(status.RepositoryRoot, DefaultModel)}
                """;
        }
        catch (Exception ex)
        {
            return $"Search failed: {ex.Message}";
        }

        if (hits.Count == 0)
        {
            return "No matches.";
        }

        var report = new StringBuilder();
        var status2 = service.Status(root);
        report.Append("Index: ").Append(status2.ChunkCount).Append(" chunks over ")
            .Append(status2.FileCount).Append(" files, model ").Append(status2.Model);
        if (status2.CommitDrifted)
        {
            report.Append(" (STALE: built at ").Append(Short(status2.IndexedCommit))
                .Append(", HEAD is ").Append(Short(status2.CurrentCommit)).Append(')');
        }

        report.AppendLine().AppendLine();

        var rank = 0;
        foreach (var hit in hits)
        {
            report.Append(++rank).Append(". ").Append(hit.RelPath)
                .Append(':').Append(hit.StartLine).Append('-').Append(hit.EndLine)
                .Append("  [").Append(hit.Kind).Append("]  cos=").Append(hit.VectorScore.ToString("F3"))
                .AppendLine();

            report.Append("   ").AppendLine(hit.Symbol);
            if (hit.Signature.Length > 0 && hit.Signature != hit.Symbol)
            {
                report.Append("   ").AppendLine(hit.Signature);
            }

            report.AppendLine(Indent(hit.Snippet)).AppendLine();
        }

        return report.ToString();
    }

    [McpServerTool(Name = "index_status")]
    [Description("""
        Reports whether a repository has a code-search index, how big it is, which embedding
        model built it, and whether it has drifted behind the current git HEAD. Check this when
        search results look wrong or suspiciously empty.
        """)]
    public static string IndexStatus(
        SearchService service,
        [Description("Repository root. Defaults to the repository containing the working directory.")]
        string? root = null)
    {
        var status = service.Status(root);
        if (!status.Exists)
        {
            return $"""
                Repository: {status.RepositoryRoot}
                Index:      {status.IndexPath}
                Status:     NOT BUILT

                Build it with:
                  {IndexCommand(status.RepositoryRoot, DefaultModel)}
                """;
        }

        var staleness = status.CommitDrifted
            ? $"STALE - built at {Short(status.IndexedCommit)}, HEAD is now {Short(status.CurrentCommit)}"
            : "current";

        return $"""
            Repository: {status.RepositoryRoot}
            Index:      {status.IndexPath}
            Model:      {status.Model} ({status.Dim} dims)
            Files:      {status.FileCount}
            Chunks:     {status.ChunkCount}
            Size:       {status.SizeBytes / 1024.0 / 1024.0:F1} MB
            Built:      {status.IndexedAtUtc:u} at commit {Short(status.IndexedCommit)}
            Status:     {staleness}
            """;
    }

    [McpServerTool(Name = "index_unload")]
    [Description("""
        Frees the memory a loaded index occupies, right now, without stopping the server. The
        index itself is untouched on disk - the next search reloads it in about a second.
        Use when a session will sit idle for a long time and its ~700MB matter. Indexes are also
        evicted automatically after 10 minutes without a search (CODESEARCH_IDLE_MINUTES).
        """)]
    public static string IndexUnload(SearchService service)
    {
        var loaded = service.Loaded();
        if (loaded.Count == 0)
        {
            return "Ничего не загружено — память уже свободна.";
        }

        var names = string.Join(", ", loaded.Select(l => Path.GetFileName(l.Path)));
        var report = service.UnloadAll();
        var trim = report.WorkingSetTrimmed
            ? "рабочий набор процесса сжат"
            : "рабочий набор не сжимался (не Windows или вызов отклонён)";

        return $"""
            Выгружено из памяти: {names}
            Процесс занимал больше на ~{report.FreedMb} МБ, сейчас ~{report.RemainingMb} МБ ({trim}).
            Следующий поиск перезагрузит индекс за ~1 с.
            """;
    }

    [McpServerTool(Name = "index_refresh")]
    [Description("""
        Incrementally refreshes a repository's index: re-embeds only files whose content changed
        since the last build. Run it after committing, so search stops answering for code that no
        longer looks like that. Refuses to run inline when the work is large (a cold build) and
        returns the command to run in the background instead.
        """)]
    public static async Task<string> IndexRefresh(
        SearchService service,
        [Description("Repository root. Defaults to the repository containing the working directory.")]
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedRoot = RepoLocator.ResolveWorkingRoot(root);
        var executable = Path.Combine(AppContext.BaseDirectory, "localai.exe");
        if (!File.Exists(executable))
        {
            return $"LocalAi CLI is unavailable. Expected: {executable}";
        }

        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("sync");
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(resolvedRoot);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start LocalAi CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            return $"LocalAi sync failed with {process.ExitCode}: {(await stderr).Trim()}";
        }

        return (await stdout).Trim();
    }

    private static string IndexCommand(string root, string model) =>
        $""""{Path.Combine(AppContext.BaseDirectory, "localai.exe")}" sync --root "{root}"""";

    private static string Short(string commit) => commit.Length >= 9 ? commit[..9] : commit;

    private static string Indent(string text) =>
        string.Join('\n', SourceLines.Split(text).Select(line => "   | " + line));
}
