# Local AI Developer Toolkit

[Русская версия](README.ru.md)

## Overview

Local AI Developer Toolkit is a .NET solution for running local-model developer tools
through one durable, machine-wide broker. It combines repository-aware semantic code
search, MCP integrations, local-model task helpers, and Git-aware index synchronization
without changing source projects merely by opening the repository.

The source-level solution, namespaces, commands, and runtime directory remain named
`LocalAi`, `CodeSearch`, and `LocalLm`. The repository name describes the complete
toolkit rather than renaming those stable contracts.

## Core capabilities

- A durable machine-wide FIFO broker is the only supported Ollama transport, keeping
  concurrent Codex, Claude, CodeSearch, and LocalLm workloads sequential.
- CodeSearch combines vector similarity with literal matching for repository-aware
  semantic and lexical code search.
- Immutable base generations represent the selected local mainline, while exact
  per-worktree overlays contain branch and dirty-content differences.
- CodeSearch and LocalLm expose stdio MCP servers for integration with compatible AI
  clients.
- Repository synchronization and shared Git hooks are explicit opt-in operations.

## How the components fit together

```text
Codex / Claude / MCP clients
          |
          +--> CodeSearch MCP/CLI --> repository index + worktree overlay
          |
          +--> LocalLm MCP --------> local task helpers
                          |
                          v
                 LocalAi FIFO broker
                          |
                          v
                        Ollama
```

The broker owns model transport and queue durability. CodeSearch owns chunking,
embeddings, immutable index generations, overlays, and hybrid retrieval. LocalLm owns
local chat/task helpers and token estimates. `LocalAi.Cli` owns repository registration,
synchronization, compatibility transport, and opt-in hook installation.

## Prerequisites

- .NET 10 SDK
- Access to `https://api.nuget.org/v3/index.json` for a clean restore
- Ollama and an installed compatible model only when running model-backed features;
  builds and unit tests do not start Ollama

`NuGet.config` deliberately clears inherited package sources so unrelated private feeds
do not become build dependencies.

## Quick start

Restore and verify the solution:

```powershell
dotnet restore LocalAi.slnx
dotnet test LocalAi.slnx --configuration Release --no-restore
```

Inspect repository synchronization without applying external setup:

```powershell
dotnet run --project src/LocalAi.Cli -- bootstrap --dry-run
dotnet run --project src/LocalAi.Cli -- repo status
```

Synchronize an authorized repository explicitly:

```powershell
dotnet run --project src/LocalAi.Cli -- sync --root C:\path\to\repository
```

Install the shared chained Git hooks only after approving that external mutation:

```powershell
dotnet run --project src/LocalAi.Cli -- hooks install --root C:\path\to\repository
```

## Build and test

From the repository root:

```powershell
dotnet restore LocalAi.slnx
dotnet build LocalAi.slnx --configuration Release --no-restore
dotnet test LocalAi.slnx --configuration Release --no-build
```

The single-command baseline verification is:

```powershell
dotnet test LocalAi.slnx --configuration Release
```

## Publishing executables

Publish only the executable projects that are needed:

```powershell
dotnet publish src/CodeSearch.Cli/CodeSearch.Cli.csproj --configuration Release --output publish/CodeSearch.Cli
dotnet publish src/CodeSearch.Mcp/CodeSearch.Mcp.csproj --configuration Release --output publish/CodeSearch.Mcp
dotnet publish src/LocalLm.Mcp/LocalLm.Mcp.csproj --configuration Release --output publish/LocalLm.Mcp
dotnet publish src/LocalAi.Cli/LocalAi.Cli.csproj --configuration Release --output publish/LocalAi.Cli
```

The `publish/` directory is ignored. Publishing does not register executables with an
AI client or install Git hooks.

## Runtime and security

- CodeSearch and LocalLm submit all model work through the durable LocalAi FIFO broker.
  Direct agent-facing Ollama endpoints are unsupported.
- The immutable base generation is built from local `dev` when present, otherwise local
  `main`. The selected mainline ref remains stable in the repository manifest.
- Each worktree receives an exact generation/tree/dirty-content overlay under
  `%LOCALAPPDATA%\LocalAi`, so a branch stores only its differences from the base.
- CodeSearch canonicalizes indexed text to Windows CRLF in memory for hashing, chunking,
  and embedding. Source files are never rewritten.
- Existing vectors are reused only when the model, dimensions, chunk format, index
  format, and normalization contract all match.
- Shared post-commit, post-merge, post-rewrite, and post-checkout hooks call
  `localai sync`; installation is always explicit.
- The allowlisted `native` compatibility command still routes through the broker.
- MCP projects reserve standard output for stdio protocol messages.
- Generated indexes, logs, process files, credentials, and runtime state stay outside
  Git.
- `RuntimeAcl.Ensure` tolerates broker paths that are atomically moved during traversal
  while still propagating ACL failures for paths that continue to exist.

## Projects

| Project | Purpose |
|---|---|
| `src/LocalAi.Contracts` | Broker, index, and repository wire contracts. |
| `src/LocalAi.Broker` | Durable machine-wide FIFO and exclusive Ollama transport. |
| `src/LocalAi.Broker.Client` | Client and broker-process integration. |
| `src/LocalAi.Repository` | Repository identity, manifest, and worktree state. |
| `src/LocalAi.Cli` | Repository synchronization, compatibility transport, and hook installation. |
| `src/CodeSearch.Core` | Chunking, embeddings, index storage, overlays, and hybrid search. |
| `src/CodeSearch.Cli` | Index, overlay, status, scan, and search commands. |
| `src/CodeSearch.Mcp` | Stdio MCP server exposing CodeSearch operations. |
| `src/LocalLm.Core` | Local-model task helpers, image metadata, and token estimates. |
| `src/LocalLm.Mcp` | Stdio MCP server exposing LocalLm operations. |
| `tests/*` | Unit and integration coverage for all toolkit components. |

## Development rules

- Keep existing namespaces, commands, runtime paths, and package versions unless a
  separate change explicitly approves an update.
- Route Ollama work only through the shared broker.
- Add focused tests alongside behavior changes and run the full solution baseline
  before publishing.
- Keep this English README and `README.ru.md` synchronized.
- Preserve UTF-8 without BOM and Windows CRLF line endings for repository documentation.
