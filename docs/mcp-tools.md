# MCP tools: the complete inventory

[Русская версия](mcp-tools.ru.md)

Twenty tools across two stdio servers. This file is the inventory — what exists, what it is for,
and the constraint that matters. Mechanisms are described in `README.md`; this is only the surface
an agent sees.

The list is held by `McpToolInventoryTests` in `tests/CodeSearch.Tests`.

## CodeSearch — 11 tools

The `codesearch` server. The embedding model is recorded in the index header and cannot be
overridden per query.

| Tool | Purpose | The constraint that matters |
| --- | --- | --- |
| `search_code` | Semantic and literal search over indexed chunks. The first step for "where does X live". | C#, TypeScript and Python are chunked by symbol; every other language, and every region no definition covers, by a 60-line window with 12 lines of overlap. Every hit is wrapped in `<untrusted-content>`. |
| `get_code_chunk` | The full body of one result, by `chunk_id`. | The id is bound to repository, generation, git tree and dirty overlay; a stale one is refused. |
| `go_to_definition` | The definition of the symbol at a zero-based line and UTF-16 column. | Source order: live LSP, then snapshot SIDX, then text tagged `Heuristic`. A position that names nothing resolves to the line's outermost declaration — a method rather than its parameters — so the start line of a `search_code` hit navigates as it stands with column 0; sibling declarations stay unresolved. |
| `find_references` | References to that symbol. | Same source order. |
| `find_implementations` | Implementations, overrides, derived types. | No text fallback: an approximate answer is worse than none here. |
| `find_relationships` | The snapshot's relationship graph. | SIDX only. Direction `incoming`/`outgoing`, kind `implementation`/`override`/`type-definition`. |
| `index_status` | Whether an index exists, its model and size, drift behind HEAD, sync phase. | Diagnostic — outside the untrusted boundary. |
| `index_refresh` | Incremental refresh after a commit. | Refuses to run large work inline and returns the background command instead. |
| `index_unload` | Frees a loaded index's memory immediately. | Leaves the file on disk; the next search reloads it in about a second. |
| `lsp_open_document` | Opens a document in its language server, making it authoritative. | Versions must increase monotonically. Live servers are off by default. |
| `lsp_close_document` | Closes it, returning authority to the snapshot. | — |

### Language servers

Configured in `language-servers.json`, disabled by default both globally and per language.

An adapter carries `InitializationOptions` — an arbitrary JSON object passed straight
into the LSP `initialize` request. For `typescript-language-server` it is the only way to name a
tsserver in a workspace that has no `node_modules/typescript` of its own:

```json
"typescript": {
  "Enabled": true,
  "Executable": "typescript-language-server",
  "Arguments": ["--stdio"],
  "InitializationOptions": { "tsserver": { "path": "…/node_modules/typescript/lib/tsserver.js" } }
}
```

The path is configured and never inferred: navigating with a different TypeScript than the project
builds with produces wrong answers that look like right ones. Note that TypeScript 7 no longer
ships `lib/tsserver.js`, so a 5.x installation is what works.

## LocalLm — 9 tools

The `locallm` server. Every model call goes through the shared broker. The four content
tools — `read_image`, `triage_log`, `ask_local`, `translate_local` — return their
model-derived answer inside nonce-bound `<untrusted-content>` markers, with the notice line
outside: a local model read files, logs or images, and its answer is data exactly as a
CodeSearch snippet is.

| Tool | Purpose | The constraint that matters |
| --- | --- | --- |
| `read_image` | An image on disk turned into text: screenshot, PDF page, scan, diagram. | Saves nothing for an image already pasted into the conversation. At most 8 images per call, 60 MB and 80 megapixels in total. |
| `triage_log` | Machine output of any length, supplied as a file or direct text: what failed and why. | Probes the largest full-VRAM context, then streams and reduces bounded fragments sequentially. |
| `ask_local` | A mechanical task over known files: list, summarise, extract. | Not for architecture or subtle bug analysis. At most 64 files sharing a ~720K-character budget; overflow is cut at a visible `TRUNCATED` marker and named in the notice. |
| `translate_local` | Translation with structural validation. | Attributes the model actually used. |
| `local_models_status` | Installed and resident models, recommended missing ones, experiment state. | — |
| `local_model_preflight` | Loads one model and context with no task content. | Returns full-VRAM residency proof. |
| `local_models_sync` | Queues installation of recommended missing models. | — |
| `local_model_experiment_report` | Timings, errors, fallback, warm/cold and estimated saving per task/model pair. | — |
| `local_model_feedback` | The owner's decision: `Promote`, `ContinueExperiment`, `FallbackOnly`, `Disable`. | — |

`triage_log` reads its tuning profile before every invocation from
`%LOCALAPPDATA%\LocalAi\log-triage.json` on Windows or
`$XDG_DATA_HOME/LocalAi/log-triage.json` (normally `~/.local/share/LocalAi/log-triage.json`) on
Linux/macOS. The optional JSON fields are `maximumContextTokens`, `reservedContextTokens`,
`charactersPerToken`, `maximumFragmentCharacters`, `maximumOverlapCharacters`,
`maximumPartialSummaryCharacters`, and `promptOverheadCharacters`, plus `schemaVersion: 1`.
Missing, malformed, and unsupported-version files use safe defaults. Changes apply to the next
call; no rebuild or process restart is required. The context cap does not claim that the requested
size fits: every invocation still proves full-VRAM residency and falls through smaller catalogued
contexts when necessary.

### The notice line

Every LocalLm tool returns a line naming the model it used and the estimated cloud tokens avoided.
Surface it: the point is that it is visible when work went downstairs, not only that it got done.

The estimate is computed from what was actually processed — 4.0 characters per token for Latin,
2.2 for Cyrillic, pixel area over 750 for images — and is reported as a range, because there is no
live token counter here. Zero is a correct answer: a job too small to save anything says so.

## What these tools do not replace

- **Reading a file before editing it.** Never delegated.
- **A literal sweep for one exact token** once the target is known: that is a job for `rg`.
- **Judgement.** A local 9–27B model is good at "list" and "summarise"; verify anything a decision
  depends on.
- **Answering from a partial index.** While a repository is still building, the state is
  `INITIALIZING`, and that is said plainly rather than replaced by a quiet text search.

## Checking an installation

`localai doctor [--root <repo>]` replaces the sequence people run by hand: which version the
pointer names and whether its binaries are all there, the stable entry point, whether the broker
is alive, the queue and quarantine, the policies actually in effect, and the repository index.

Read-only, and it starts nothing — including the broker. The exit code is non-zero only for a
real fault: a stopped broker is a note, because it starts on demand.

## Reading what the local models actually did

`localai telemetry` summarises the per-job records the broker writes. Every delegated job
leaves one under the runtime root, and they are kept for thirty days.

It answers the questions a routing or residency decision turns on: how jobs ended, how often a
model had to be loaded rather than found warm, how often the fallback was taken, what the queue
and the execution actually cost at the median and the p90, and how much cloud spend was avoided —
broken down by model and by task profile. A model that fails at least a quarter of its jobs and
owns at least half of all recorded failures is named in one `attention` line, because the
fallback covers such a model well enough that nothing else makes it visible.

Latencies are nearest-rank percentiles, so every duration printed is one a job really took.
Savings are bands, never totals: the estimator counts characters, there is no live token counter
anywhere in this system, and summing a hundred thousand estimates does not make them exact.
