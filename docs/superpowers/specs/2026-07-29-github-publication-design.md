# Local AI Developer Toolkit Publication Design

[Русская версия](2026-07-29-github-publication-design.ru.md)

**Date:** 2026-07-29

## Goal

Publish the existing LocalAi solution as a private personal GitHub repository named
`MrAliev/local-ai-developer-toolkit`, with synchronized English and Russian landing
documentation.

## Scope

- Keep the solution name, project names, namespaces, commands, and runtime paths unchanged.
- Present the product in documentation as **Local AI Developer Toolkit**.
- Rewrite `README.md` as the English original.
- Rewrite `README.ru.md` as its complete Russian translation.
- Create the private GitHub repository under the verified personal account `MrAliev`.
- Push the existing local `main` branch and configure it to track `origin/main`.

Renaming source projects or publishing binaries is outside this change.

## README Structure

Both README files will use the same section order:

1. Product name, one-sentence purpose, and language switch.
2. Core capabilities: the durable FIFO broker, CodeSearch, MCP integration, and
   generation plus worktree-overlay indexing.
3. Component overview describing the broker, repository synchronization, CodeSearch,
   and LocalLm boundaries.
4. Prerequisites and a concise quick start.
5. Build, test, and executable publication commands.
6. Runtime and security guarantees.
7. Project inventory and development rules.

The English file is the source document. The Russian file is a complete synchronized
translation rather than a shortened summary. Commands, identifiers, file paths, and
product component names remain unchanged between languages.

## Publication Flow

1. Update and compare the paired README files.
2. Preserve Windows CRLF line endings and UTF-8 without BOM.
3. Run the full solution test suite and inspect the Git diff.
4. Commit only the documentation and approved publication artifacts.
5. Create `MrAliev/local-ai-developer-toolkit` as a private repository.
6. Add it as `origin` and push local `main`.
7. Verify the repository owner, visibility, default branch, remote tracking, and clean
   local working tree.

If repository creation succeeds but a later push fails, retain the private repository
and report the exact recovery command instead of deleting external state.

## Acceptance Criteria

- The repository URL is `https://github.com/MrAliev/local-ai-developer-toolkit`.
- GitHub reports the repository as private and owned by `MrAliev`.
- `README.md` and `README.ru.md` are complete, synchronized language variants.
- The solution still passes its full test suite.
- Local `main` tracks `origin/main`, and the working tree is clean after publication.
