# Precise Code Navigation Implementation Plan

[Русская версия](2026-08-06-precise-code-navigation.ru.md)

**Goal:** add a snapshot-bound semantic index and precise definition/reference
queries without changing the current CIDX retrieval pipeline.

**Architecture:** a separate `SIDX` in the existing immutable generation, SCIP
as interchange, Roslyn/XAML/scip-typescript/scip-python adapters, followed by a
live LSP overlay after the persistent contract is stable.

## Task 1: Contracts and deterministic SIDX

- [x] RED tests for round trip, byte determinism, corruption/version rejection,
  UTF-16 ranges, duplicate symbols, and invalid paths.
- [x] Add documents, symbols, occurrences, relationships, roles, precision, and
  snapshot identity contracts.
- [x] Implement atomic vector-free `SemanticIndex.Save/Load`.
- [x] Build deterministic lookup indexes and run focused/full tests.

## Task 2: Query service and MCP

- [x] RED tests for occurrence selection, definitions, references, ordering,
  missing symbols, and snapshot mismatches.
- [x] Implement `SemanticNavigationService`.
- [x] Add `go_to_definition` and `find_references` MCP commands.
- [x] Apply path containment and nonce-bound untrusted output.
- [x] Add CLI commands for local diagnostics.

## Task 3: C# Roslyn indexer

- [x] Add Workspaces/MSBuild dependencies and a solution loader.
- [ ] Cover overloads, partials, aliases, generics, overrides, interface
  implementations, and cross-project references.
- [x] Emit deterministic canonical symbols and occurrences.
- [x] Publish SIDX atomically beside CIDX.

## Task 4: WPF XAML supplement

- [x] Add lossless XAML ranges and a WPF namespace resolver.
- [x] Cover CLR types/members, events, classes, names, references, resources,
  dictionaries, and typed bindings.
- [x] Reuse C# canonical IDs and remap generated fields to XAML definitions.

## Task 5: SCIP and external indexers

- [x] Pin the SCIP schema and enforce protobuf parser limits.
- [x] Implement a validated SCIP-to-SIDX importer.
- [x] Add a bounded process runner and per-adapter manifest status.
- [x] Integrate `scip-typescript` and `scip-python` fixtures.

## Task 6: Overlays and LSP

- [x] Add changed-document semantic overlays and deletion tombstones.
- [x] Add an LSP session manager for open and dirty documents.
- [x] Restart crashed LSP processes once and replay authoritative open documents.
- [x] Validate TypeScript and Python navigation through real Windows npm command shims.
- [x] Route authoritative LSP overlays to snapshot-bound SIDX fallback.
- [x] Add syntax/text fallback with explicit non-precise provenance.

## Task 7: Expansion and acceptance

- [x] Add WinUI, MAUI, and Avalonia XAML adapters.
- [x] Add Find implementations and relationship queries.
- [x] Measure correctness, latency, memory, and index size.
- [x] Update paired README, installer manifest, and 0.1.15 release notes.
- [x] Run full tests, `git diff --check`, and a clean install test.
