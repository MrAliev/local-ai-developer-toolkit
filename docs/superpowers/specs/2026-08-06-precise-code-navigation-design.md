# Precise Code Navigation Design

## Purpose

Add precise `Go to definition`, `Find references`, and later
`Find implementations` to LocalAi for C#, XAML, TypeScript/JavaScript, and
Python. Precise navigation uses compiler or language-specific indexer output,
is bound to an exact repository snapshot, and coexists with the current vector
CodeSearch without changing retrieval semantics.

## Key decision

The existing `CIDX` remains the retrieval chunk and embedding vector index.
Definitions and references are not added to `ChunkMeta` or represented as
embedding chunks. A separate `SemanticIndex` is published in the same immutable
generation with the same snapshot identity:

```text
repository generation
├── base.cidx       vector and lexical retrieval
├── semantic.sidx  definitions, references and relationships
└── manifest.json  generation metadata
```

SCIP is the language-neutral import/export format. LocalAi may use a more
compact internal representation, but it preserves the core SCIP concepts:
document, occurrence, symbol information, symbol role, and relationship.

## Initial scope

- First vertical slice: C# within one solution/project graph.
- First XAML dialect: WPF; other dialects plug in through adapters.
- TypeScript/JavaScript imports `scip-typescript` output.
- Python imports `scip-python` output.
- The durable index serves a saved snapshot; live LSP overlays follow after the
  on-disk contract is stable.
- Tree-sitter and text search are fallback sources and are never labelled
  precise.
- Symbol resolution never calls Ollama or an embedding model.
- CIDX layout and composite ordinals do not change in the first release.

## Data model

`SemanticIndex` stores repository/generation/tree/dirty identity, normalized
documents and content hashes, canonical symbols, exact occurrences and roles,
implementation/type-definition/override relationships, and result provenance.

Positions are zero-based line and UTF-16 column, matching LSP and SCIP. Public
APIs state the coordinate system explicitly and never mix it implicitly with
CodeSearch's one-based line ranges.

Imported SCIP symbol strings are authoritative. The native C# indexer emits a
SCIP-compatible package/version/descriptor identity. Local symbols remain
document-local and are not matched across independent builds.

## Snapshot and overlay rules

Queries require an exact repository/generation/tree/dirty match. A base semantic
index is immutable. A branch or dirty worktree eventually gets a replacement
document overlay plus deletion tombstones. Until semantic overlays exist, a
dirty mismatch fails explicitly or is served by live LSP; stale base data is
never returned silently.

## Query API

The minimum service exposes `ResolveOccurrence`, `GoToDefinition`, and
`FindReferences`. It resolves the narrowest containing occurrence, obtains its
canonical symbol, and returns stable path/range-sorted definition or reference
occurrences.

Every result reports precision as `Precise`, `Inferred`, or `Heuristic`. MCP
tools `go_to_definition` and `find_references` wrap all source-derived output in
the existing nonce-bound untrusted-content markers.

## Language adapters

The C# indexer uses `MSBuildWorkspace`, `Compilation`, and `SemanticModel` to
index declarations, source references, partials, overrides, interface
implementations, aliases, extension methods, and cross-project references.
Generated documents are intermediate inputs and are remapped to user source
when a mapping exists.

The WPF XAML supplement resolves CLR namespaces, element types, properties,
attached properties, events, `x:Class`, handlers, names, references, resources,
dictionaries, styles, and binding paths when the source type is provable. XAML
references to CLR members reuse the C# canonical symbol ID. Runtime bindings
without a provable data context are inferred or heuristic, never precise.

External SCIP indexers run as bounded, cancellable child processes after
dependency restoration. Imported protobuf is validated for size, paths,
ranges, and counts before generation publication. Initial adapters are
`scip-typescript`, `scip-python`, and the native Roslyn/XAML producer.

## Storage

The first implementation uses a separate versioned `SIDX` binary container with
atomic sibling-temp publication. It contains no vectors. Documents, symbols,
occurrences, and relationships are deterministically sorted. Load builds
position-to-occurrence, symbol-to-definition, and symbol-to-reference lookup
structures. The query contracts permit a later memory-mapped or SQLite
projection without API changes.

## Safety and verification

Paths must remain under the repository root. Imported protobuf and process
output are untrusted. Analyzable assemblies are not loaded into the main
process. All output is content-hash and snapshot bound, and a generation is
published only after full validation.

Development uses RED -> GREEN -> REFACTOR and golden fixtures covering binary
round trips, UTF-16 positions, C# language features, XAML semantics, malformed
SCIP, path traversal, snapshot mismatches, MCP boundaries, latency, and memory.
Every increment ends with focused tests, full
`dotnet test LocalAi.slnx -c Release --nologo`, `git diff --check`, and paired
English/Russian documentation updates.
