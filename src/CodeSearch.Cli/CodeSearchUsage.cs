namespace CodeSearch.Cli;

/// <summary>
/// Every command this binary answers to, with its syntax and the parts of the index model a
/// reader needs to make sense of them. This is the inventory for a person.
///
/// <see cref="ConsoleJson.Capabilities"/> is the inventory for a program: the same commands minus
/// the syntax, plus the shape each answer arrives in. Two inventories of one binary drift — the
/// `hook` command was missing from `localai`'s block while every installed Git hook invoked it —
/// so a test holds every command the listing names to a line in here.
///
/// It lives out here rather than inside the entry point for that test's sake: a local function in
/// a top-level program is reachable from nothing.
/// </summary>
public static class CodeSearchUsage
{
    public const string Text =
        """
        codesearch - semantic + literal code search over a local repository.

          codesearch index   [--root <dir>] [--model <ollama-model>] [--force] [--index <file>]
          codesearch overlay [--root <dir>] [--index <base>] [--overlay <file>]
          codesearch search   --query "<text>" [--root <dir>] [--top N] [--kind Type|Method|Text|File]
                              [--path <substring>] [--per-file N] [--no-instruct] [--json]
          codesearch get-chunk --id <chunk_id> [--root <dir>] [--json]
          codesearch evaluate --cases <json> [--root <dir>] [--profile|--no-floor] [--no-instruct]
          codesearch status  [--root <dir>] [--json]
          codesearch scan    [--root <dir>]
          codesearch capabilities --json

        One BASE index per repository, and one OVERLAY per worktree holding only what that
        branch changed plus the files it deleted. Searches see the overlay laid over the base,
        so a branch pays for its diff rather than for a second full index.

        For a repository connected to LocalAi both are built and published by `localai sync` -
        the same sync the Git hooks run - and both live under the LocalAi runtime directory.
        Their real paths, whether either has drifted, and whether this repository is connected
        at all are what `codesearch status` prints.

        `index` and `overlay` are the builders underneath. On a connected repository neither is
        the way to refresh anything: run `localai sync`. Use them with explicit `--index` and
        `--overlay` paths, or on a repository LocalAi does not manage.
        """;
}
