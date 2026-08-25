# CodeSearch Retrieval Hardening Design

[Русская версия](2026-07-31-codesearch-retrieval-hardening-design.ru.md)

## Purpose

Implement GitHub issue #5 in one feature branch and one pull request. The change
adds a calibrated semantic relevance floor, exact-snapshot chunk retrieval,
nonce-based untrusted-content boundaries, and a repeatable quality/token
evaluation without replacing the existing indexer or bypassing the LocalAi
broker.

## Boundaries

- Keep the immutable base generation plus exact worktree overlay architecture.
- Keep all embedding requests behind `BrokerEmbeddingClient` and the durable,
  machine-wide LocalAi FIFO broker.
- Do not add direct Ollama access, another index, cross-repository search, or
  changes to broker routing, VRAM, offload, or translation policy.
- Deliver the five increments sequentially. Every increment must pass focused
  tests and `dotnet test LocalAi.slnx -c Release --nologo` before the next one.
- Keep English and Russian documentation synchronized in the same pull request.

## Evaluation and Calibration

`tests/CodeSearch.Tests/Fixtures/SearchEvaluation/cases.json` is the versioned,
non-sensitive corpus. It contains 20–30 cases split across natural-language
intent, exact symbols, C#, generic text, and unrelated/no-answer queries. Each
answerable case names one or more relevant path/symbol pairs that are verified
against the committed source.

The deterministic evaluator runs the existing `SearchService` pipeline and
reports:

- precision@5 and recall@10;
- first relevant rank;
- no-answer false-positive rate;
- response characters and source lines;
- estimated response tokens;
- elapsed time, cold/warm classification, and broker queue wait when available;
- number of returned file/chunk reads.

The token estimator uses `ceil(characters / 4)` as its point proxy and reports
the explicit heuristic interval `ceil(characters / 6)` through
`ceil(characters / 3)`. This interval is an engineering bound, not a statistical
confidence interval. Raw character and line counts remain authoritative.

The first run records the current no-floor baseline. The calibrated
`qwen3-embedding:8b-q8_0` floor is selected from the measured relevant and
irrelevant score distributions: choose the lowest threshold that removes every
observed no-answer vector false positive without reducing recall@10 on the
answerable corpus. The selected numeric value, corpus version, generation ID,
case count, run timestamp, and rule are committed in `SearchQualityProfile`.
No value is copied from another model or corpus.

## Relevance Floor

`SearchOptions.MinVectorScore` is nullable only so the evaluator can explicitly
run the historical no-floor baseline. A supplied value must be finite and in
`[-1, 1]`. Normal production search resolves the floor from
`SearchQualityProfile` using the model recorded in the index.

Unknown models fail closed with a `threshold not calibrated` diagnostic.
Only the evaluator can opt out explicitly. Vector candidates below the floor
are removed before vector ranks are assigned for reciprocal rank fusion.
Lexical candidates remain eligible independently, so exact symbols survive an
empty embedding branch. A query with no eligible vector or lexical candidate
returns fewer than `TopK`, including zero.

## Exact-snapshot Chunk IDs

Every `SearchHit` carries a versioned opaque `SearchChunkId` containing:

- repository identity;
- immutable base generation ID;
- exact worktree HEAD tree;
- dirty-content hash when present;
- composite chunk ordinal.

The serialized payload includes a SHA-256 integrity digest. The digest detects
mutation and corruption; it is not an authorization token. Repository-root
authorization and the exact current snapshot checks remain the security
boundary.

`ISearchableIndex` exposes the snapshot identity used by both `CodeIndex` and
`CompositeIndex`. Composite ordinals remain deterministic because overlay
chunks precede surviving base chunks and the ID binds to every input that can
change that ordering.

`SearchService.GetChunkAsync` parses and validates the ID, resolves the current
repository and base generation, recomposes the exact searchable snapshot,
compares repository/generation/tree/dirty identities, validates the ordinal,
and only then reads the current source line range. It returns path, start/end
lines, kind, symbol, signature, and the full chunk body.

Malformed, integrity-invalid, wrong-repository, stale-generation,
stale-worktree, and out-of-range IDs have distinct diagnostics. An old ordinal
is never resolved against a new snapshot.

## Untrusted-content Boundaries

`UntrustedContent.Wrap` generates a fresh 96-bit cryptographic nonce per
response block and renders it as lowercase hexadecimal. If that representation
already occurs anywhere in the content using an ordinal case-insensitive
comparison, generation retries.

Opening and closing markers both carry the nonce. Origin attributes escape
`&`, `<`, `>`, quotes, apostrophes, carriage return, line feed, and tab.
Content is preserved byte-for-character; Unicode normalization is not applied.

`search_code` keeps trusted index/status text outside wrappers and wraps every
source-derived hit block. `get_code_chunk` wraps its successful source-derived
result. Validation errors, status, and maintenance output remain trusted and
unwrapped.

## CLI and MCP Flow

- `search_code` returns chunk IDs and wrapped hit blocks.
- `get_code_chunk` accepts an ID and optional root, then returns the wrapped full
  chunk.
- The CLI search output includes chunk IDs.
- The CLI evaluation command loads the committed fixture, runs baseline or
  profile mode, emits deterministic JSON metrics, and never calls Ollama
  directly.

## Verification

Use strict RED → GREEN → REFACTOR cycles. Tests cover threshold validation and
filter order, lexical fallback, zero-result queries, unknown models, every
chunk-ID rejection path, exact round-trip retrieval, adversarial wrapper
content/origins, forced nonce collision retry, fixture validation, metric
calculation, and CLI/MCP formatting boundaries.

The final A/B report records measured baseline/final quality, exposed source,
token proxy, cold/warm latency, and broker queue wait where emitted. Facts,
heuristics, and unavailable measurements are labeled separately.
