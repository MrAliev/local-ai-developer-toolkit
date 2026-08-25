# CodeSearch Retrieval Hardening Implementation Plan

[Русская версия](2026-07-31-codesearch-retrieval-hardening.ru.md)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a measured model-specific relevance floor, exact-snapshot full-chunk retrieval, nonce-based untrusted-content boundaries, and a repeatable A/B quality/token report to the existing CodeSearch pipeline.

**Architecture:** Keep the current immutable generation and exact worktree overlay as the single source of searchable state. Resolve the calibrated floor from the index model before RRF, encode the exact snapshot plus composite ordinal in every hit, validate all identity components before reading a chunk, and wrap only source-derived MCP output. Use the committed fixture and existing broker-backed search path for baseline/final measurement.

**Tech Stack:** .NET 10, C# 14, xUnit v3, ModelContextProtocol 1.4.1, JSON fixtures, `qwen3-embedding:8b-q8_0` through `BrokerEmbeddingClient`.

---

## Preconditions

- Approved design: `docs/superpowers/specs/2026-07-31-codesearch-retrieval-hardening-design.md` and synchronized Russian sibling.
- Baseline at `966aae8`: 478 tests pass; CodeSearch generation `399fcc0b...` is current with 203 files and 1,529 chunks.
- Never call Ollama directly. All live embeddings use `BrokerEmbeddingClient` and the shared FIFO broker.
- Run a focused RED test, confirm the expected failure, implement the minimum behavior, then confirm GREEN.
- After each task, run `dotnet test LocalAi.slnx -c Release --nologo` before continuing.

## Task 1: Add the evaluation corpus and deterministic metrics

**Files:**

- Create: `tests/CodeSearch.Tests/Fixtures/SearchEvaluation/cases.json`
- Create: `tests/CodeSearch.Tests/SearchEvaluationTests.cs`
- Create: `src/CodeSearch.Core/Search/SearchQualityProfile.cs`
- Modify: `src/CodeSearch.Cli/Program.cs`

- [ ] Add RED tests that deserialize 20–30 cases, reject duplicate IDs and empty relevance targets, verify every referenced path/symbol against source, and calculate precision@5, recall@10, first relevant rank, no-answer false-positive rate, characters, lines, reads, and token proxy.
- [ ] Run:

  ```powershell
  dotnet test tests/CodeSearch.Tests/CodeSearch.Tests.csproj -c Release --filter FullyQualifiedName~SearchEvaluationTests
  ```

  Expected: compilation/fixture failure because evaluation contracts and corpus do not exist.
- [ ] Add `SearchEvaluationCase`, `SearchEvaluationTarget`, `SearchEvaluationMetrics`, and pure metric calculation in `SearchQualityProfile.cs`. Use `ceil(chars/4)` with the documented `ceil(chars/6)..ceil(chars/3)` interval.
- [ ] Add 24 source-verified cases: eight natural-language intent, six exact symbols, four generic-text/document cases, and six unrelated/no-answer cases.
- [ ] Add CLI `evaluate --cases <json> --root <repo> [--profile|--no-floor]` that runs cases sequentially through `SearchService` and emits deterministic JSON.
- [ ] Confirm focused GREEN, then run the full solution test command.
- [ ] Run the live no-floor baseline twice (cold/warm), save raw JSON under `work/`, and use it only as measurement input until the final bilingual report is written.

## Task 2: Calibrate and apply the relevance floor before RRF

**Files:**

- Modify: `src/CodeSearch.Core/Search/SearchQualityProfile.cs`
- Modify: `src/CodeSearch.Core/Search/SearchEngine.cs`
- Modify: `src/CodeSearch.Core/Search/SearchService.cs`
- Modify: `src/CodeSearch.Cli/Program.cs`
- Modify: `tests/CodeSearch.Tests/SearchEngineTests.cs`
- Modify: `tests/CodeSearch.Tests/SearchEvaluationTests.cs`

- [ ] Add RED tests for non-finite/out-of-range floors, below-threshold exclusion, at-threshold inclusion, lexical-only fallback, zero-result no-answer behavior, and unknown-model fail-closed diagnostics.
- [ ] Run focused tests and verify failures are caused by missing floor/profile behavior.
- [ ] Select the measured `qwen3-embedding:8b-q8_0` threshold using the design rule and commit its corpus/generation provenance in `SearchQualityProfile`.
- [ ] Add nullable `SearchOptions.MinVectorScore` plus `AllowUncalibratedModelForEvaluation`; only evaluation may explicitly use null/no-floor.
- [ ] Resolve production options through `SearchQualityProfile`; throw `SearchNotReadyException` containing `threshold not calibrated` for unknown models.
- [ ] Filter vector candidates below the floor before vector rank assignment. Build the fused set from eligible vector candidates plus all positive lexical candidates.
- [ ] Confirm focused GREEN and full-solution GREEN.

## Task 3: Add exact-snapshot chunk IDs and full-chunk retrieval

**Files:**

- Create: `src/CodeSearch.Core/Search/SearchChunkId.cs`
- Create: `tests/CodeSearch.Tests/SearchChunkIdTests.cs`
- Modify: `src/CodeSearch.Core/Indexing/CompositeIndex.cs`
- Modify: `src/CodeSearch.Core/Search/SearchEngine.cs`
- Modify: `src/CodeSearch.Core/Search/SearchService.cs`
- Modify: `src/CodeSearch.Mcp/CodeSearchTools.cs`
- Modify: `src/CodeSearch.Cli/Program.cs`

- [ ] Add RED tests for ID round-trip, malformed format, digest mutation, wrong repository, stale generation, stale HEAD tree, stale dirty hash, and out-of-range ordinal.
- [ ] Add snapshot identity members to `ISearchableIndex`; expose base identity from `CodeIndex` and active tree/dirty identity from `CompositeIndex`.
- [ ] Implement `cs1.<base64url-payload>.<base64url-sha256>` encoding with bounded field lengths, strict parsing, constant-time digest comparison, and explicit diagnostics.
- [ ] Add `ChunkId` to `SearchHit`; create it from repository, generation, tree, dirty hash, and composite ordinal.
- [ ] Add `SearchChunk` and `SearchService.GetChunkAsync`. Validate all ID and snapshot fields before reading `Path.Combine(root, relPath)` and return the full indexed line range.
- [ ] Add MCP `get_code_chunk`, include IDs in `search_code`, and include IDs in CLI search.
- [ ] Confirm focused GREEN and full-solution GREEN.

## Task 4: Add nonce-based untrusted-content wrappers

**Files:**

- Create: `src/CodeSearch.Core/Security/UntrustedContent.cs`
- Create: `tests/CodeSearch.Tests/UntrustedContentTests.cs`
- Modify: `src/CodeSearch.Mcp/CodeSearchTools.cs`

- [ ] Add RED tests for 96-bit lowercase nonce markers, fresh nonces, injected opening/closing tags, case/whitespace variants, pre-defused Unicode, hostile origin attributes, CR/LF/tab escaping, and forced collision retry.
- [ ] Implement a cryptographic nonce source plus an injectable test source. Retry while the nonce text occurs in content using ordinal case-insensitive comparison.
- [ ] Escape attribute metacharacters and controls without normalizing content.
- [ ] Keep index/status text outside wrappers; wrap each successful `search_code` hit and the successful `get_code_chunk` source result. Keep validation errors unwrapped.
- [ ] Confirm focused GREEN and full-solution GREEN.

## Task 5: Record final A/B results and synchronize documentation

**Files:**

- Modify: `tests/CodeSearch.Tests/SearchEvaluationTests.cs`
- Modify: `tests/CodeSearch.Tests/Fixtures/SearchEvaluation/cases.json`
- Create: `docs/codesearch-evaluation.md`
- Create: `docs/codesearch-evaluation.ru.md`
- Modify: `README.md`
- Modify: `README.ru.md`

- [ ] Re-run every case twice with the calibrated profile and capture cold/warm JSON.
- [ ] Compare baseline/final precision@5, recall@10, first relevant rank, no-answer false positives, response characters, source lines, reads, token proxy interval, elapsed time, and broker queue wait when available.
- [ ] Write synchronized EN/RU reports that separate measured facts, heuristic token estimates, unavailable telemetry, and limitations.
- [ ] Document `search_code` chunk IDs, `get_code_chunk`, calibrated-model failure, and untrusted-content output in both READMEs.
- [ ] Validate JSON, scan EN/RU headings and links, run focused evaluation tests, and run:

  ```powershell
  dotnet test LocalAi.slnx -c Release --nologo
  ```

  Expected: zero failures and no new warnings.
- [ ] Inspect `git diff --check`, exact branch scope, and the absence of direct Ollama paths.
- [ ] Commit, push `codesearch-retrieval-hardening`, create one PR linked with `Closes #5`, and verify remote CI.
