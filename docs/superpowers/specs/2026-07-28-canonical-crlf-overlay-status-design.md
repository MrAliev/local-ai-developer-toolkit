# Canonical CRLF Indexing and Exact Overlay Status

[Русская версия](2026-07-28-canonical-crlf-overlay-status-design.ru.md)

## Context

LocalAi builds an immutable CodeSearch base from a Git snapshot and an exact overlay for each
non-mainline or dirty worktree. On Windows, a snapshot commonly contains LF while the checkout
contains CRLF or mixed endings. Byte-sensitive hashing therefore duplicates unchanged files in
the overlay. The legacy status presentation also treats a matching filesystem root as sufficient
to skip the overlay, even when the current worktree has a dirty-content identity.

## Goals

- Make index identity independent of source line-ending style.
- Use Windows CRLF as the canonical in-memory representation for indexed text.
- Apply the same canonical representation to hashes, chunks, embeddings, and dirty-content
  identity.
- Never rewrite repository files.
- Report whether an exact overlay is required from Git tree and dirty-content identity.
- Force a new base generation so existing indexes with the previous normalization cannot mix
  with the new representation.

## Design

### Canonical text

A single CodeSearch text-normalization helper converts every `CRLF`, lone `CR`, and lone `LF`
line ending to `CRLF`. It operates on strings already read from indexable text files.

IndexBuilder uses canonical text for:

- SHA-256 per-file hashes;
- chunker input;
- embedding input derived from those chunks.

DirtyCorpusPolicy uses the same canonical text when hashing working content. Paths and file
boundaries remain part of the dirty-content digest, so different files cannot collide merely
because their content is equal.

The helper never writes canonical text back to disk. Search snippets continue to come from the
current worktree and retain its physical line endings. Line numbers are unchanged by
normalization.

### Generation compatibility

The CodeSearch normalization version in `GenerationIdentity` is incremented. The generation ID
therefore changes and LocalAi builds a fresh base plus exact overlays. Existing generations remain
immutable and are not reused under the new normalization.

### Overlay requirement and status

For generation-backed indexes, a worktree requires an overlay when either:

- its Git tree differs from the base Git tree; or
- its dirty-content hash is present.

`SearchService.Status` exposes this decision explicitly. The CLI renders an existing exact overlay
before considering any legacy base-root shortcut. A required but missing overlay is reported as
`NOT BUILT`. A clean worktree at the base tree reports that no overlay is needed.

Legacy indexes without a generation ID keep their existing root-based behavior.

## Error handling

- File read failures retain the existing behavior: the file is skipped for that indexing pass.
- A missing or mismatched exact overlay continues to block search rather than silently mixing
  stale data.
- Canonicalization is deterministic and does not depend on Git configuration or host culture.

## Testing

- LF base and equivalent CRLF worktree produce an empty overlay.
- CR, LF, CRLF, and mixed input produce the same canonical hash and chunk text.
- A real text edit still produces an overlay entry.
- Dirty content at the same commit/tree requires an overlay.
- A clean worktree at the base tree does not require an overlay.
- Status reports an existing exact overlay even when base and worktree paths match.
- Full LocalAi build and test suite remains clean.
- Jira indexing is rebuilt and verified with an exact overlay and a semantic query.

## Out of scope

- Rewriting repository files or changing `.gitattributes`.
- Changing Git `core.autocrlf`.
- Installing hooks, replacing global binaries, committing, or pushing.
