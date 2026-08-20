namespace LocalAi.Installer.Core.Agents;

public sealed record ManagedInstructionBlockResult(bool Changed, string Content);

public static class ManagedInstructionBlock
{
    public const string BeginMarker = "<!-- BEGIN LOCALAI MANAGED INSTRUCTIONS -->";
    public const string EndMarker = "<!-- END LOCALAI MANAGED INSTRUCTIONS -->";

    /// <summary>
    /// The operating rules an assistant needs in order to actually use this installation.
    ///
    /// Three lines used to live here — broker only, no direct Ollama, full VRAM — which
    /// describe how the transport must behave but never say when to reach for a local model
    /// at all. An assistant that reads only that keeps grepping the repository and keeps
    /// sending screenshots to the cloud, so the installed tools sit unused and the machine
    /// buys nothing. The rules below are therefore written as routing decisions first and
    /// transport invariants second.
    ///
    /// Everything sits between the markers so the next install replaces it wholesale, and
    /// anything the user writes outside them survives untouched.
    /// </summary>
    public static readonly string Block = BuildBlock();

    public static ManagedInstructionBlockResult Upsert(string? content)
    {
        content ??= string.Empty;
        var beginIndexes = AllIndexesOf(content, BeginMarker);
        var endIndexes = AllIndexesOf(content, EndMarker);
        if (beginIndexes.Count > 1 || endIndexes.Count > 1 ||
            beginIndexes.Count != endIndexes.Count ||
            (beginIndexes.Count == 1 && beginIndexes[0] > endIndexes[0]))
        {
            throw new InvalidOperationException("Malformed managed instruction markers.");
        }

        string updated;
        if (beginIndexes.Count == 0)
        {
            var prefix = content.Length == 0 || content.EndsWith('\n')
                ? content
                : content + Environment.NewLine;
            updated = prefix + Block + Environment.NewLine;
        }
        else
        {
            var begin = beginIndexes[0];
            var end = endIndexes[0] + EndMarker.Length;
            updated = content[..begin] + Block + content[end..];
        }

        return new(!string.Equals(content, updated, StringComparison.Ordinal), updated);
    }

    private static string BuildBlock() =>
        (BeginMarker + "\n" + Body + "\n" + EndMarker).ReplaceLineEndings();

    private const string Body =
        """
        ## LocalAi local models

        Managed by the LocalAi installer. Anything between these markers is replaced on the
        next install; keep your own guidance outside them.

        ### Transport

        Use only the shared LocalAi FIFO broker for local-model work. Never access Ollama
        directly — no `localhost:11434`, no `ollama` binary from a launcher. CodeSearch,
        LocalLm, delegation wrappers and Git hooks share that single queue, and it is what
        keeps ordering, deduplication, leases, heartbeat and recovery correct across several
        clients running at once.

        Require full-VRAM, zero-offload validation. A model that spills into system memory
        does not fail; it just becomes several times slower and says nothing about it. Relax
        that on purpose or not at all: `localai policy set --residency AllowPartialOffload`
        or `AllowCpu`, and expect degraded answers to be labelled as such.

        ### Finding code

        Begin every "where does X live", "what handles Y", "is there already something like
        Z" with `search_code` from the `codesearch` MCP server rather than a text search. It
        matches by meaning and by exact symbol name, and costs a fraction of reading the
        candidate files. C#, TypeScript and Python are chunked by symbol, so a hit there names
        the type, member or definition it came from. Every other language is chunked by a
        sliding window over lines, and so is any region no definition covers — imports,
        module-level statements, the gap between two functions — where a hit names the file and
        its line range instead. `index_status` says whether the index is behind HEAD;
        `index_refresh` is for repair, not for routine use.

        A literal sweep for one exact token, once the target is already known, is still a job
        for grep. Reading a file before editing it is never delegated to anything.

        ### Any repository, not just the ones already set up

        Indexing is opt-in per repository and costs two commands, so "this repository is new"
        is a step to take, not a reason to stop using local tools. On opening one, run the
        read-only check first:

        ```
        localai-launcher.exe run localai repo status --root <repository>
        ```

        If it is not connected, offer the whole kit — the immutable base generation, branch
        overlays and the shared Git hooks that keep them current — and set nothing up without
        an explicit yes:

        ```
        localai-launcher.exe run localai sync --root <repository>
        localai-launcher.exe run localai hooks install --root <repository>
        ```

        Nothing here is tied to one repository or one path. A repository is identified by its
        normalised Git common directory, so every worktree, client and CLI share a single
        identity for it, and the same two commands work wherever it was cloned.

        While a repository is still building its first generation the status is INITIALIZING.
        A partial index is not a fast index: do not answer from it, and say the repository is
        still indexing rather than quietly falling back to a text search.

        ### Images, logs, routine file work

        | Tool | What it is for |
        | --- | --- |
        | `read_image` | any image on disk: screenshot, scanned page, diagram, photographed table |
        | `triage_log` | machine output of any length, supplied as a file or direct text; it probes real VRAM capacity and processes bounded fragments sequentially |
        | `ask_local` | mechanical work over known files: list, summarise, extract TODOs, translate, check a convention |

        The saving only exists for data that has not entered the conversation yet: an image
        pasted straight into chat has already been paid for, and handing it to a local model
        afterwards buys nothing. Ask for a file path instead.

        Local models are markedly weaker than cloud ones. They are good at "list this" and
        "summarise that", and unreliable on architecture and subtle bugs. Verify anything a
        decision depends on.

        ### Reporting

        After every local tool call, say in the reply which tool and model ran, roughly how
        long it took, and the estimated cloud tokens avoided — as a range, because there is no
        live token counter and false precision is worse than an honest estimate. LocalLm tools
        return that line themselves, computed from what they actually processed; carry it
        through instead of inventing a number. Using a local tool silently and showing only
        the result defeats the point of having one.

        After CodeSearch work, always include the exact `index_unload` tool name so the user
        can release cached index memory immediately; explain that it leaves the on-disk index
        intact and that idle indexes are also evicted automatically. While an index is still
        building, use `index_status` and report processed, total and remaining chunks together
        with the current ETA instead of saying only that indexing is in progress.
        """;

    private static List<int> AllIndexesOf(string content, string marker)
    {
        var indexes = new List<int>();
        var start = 0;
        while (true)
        {
            var index = content.IndexOf(marker, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return indexes;
            }

            indexes.Add(index);
            start = index + marker.Length;
        }
    }
}
