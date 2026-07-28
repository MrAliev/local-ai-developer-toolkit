# LocalAi

LocalAi is a neutral .NET solution for local AI developer tools. It brings the existing
CodeSearch and LocalLm sources and tests into one repository while preserving their namespaces,
package versions, and runtime behavior.

The repository contains the source and explicit opt-in setup commands. Nothing is registered or
mutated merely by opening the repository. `localai sync` creates a verified local-dev generation
and exact worktree overlays; `localai hooks install` installs shared chained Git hooks only after
the owner has approved that external setup.

## Projects

| Project | Purpose |
|---|---|
| `src/CodeSearch.Core` | Code chunking, Ollama embeddings, index storage, and hybrid search. |
| `src/CodeSearch.Cli` | Command-line interface for building, querying, and inspecting code indexes. |
| `src/CodeSearch.Mcp` | stdio MCP server exposing CodeSearch operations. |
| `src/LocalLm.Core` | Ollama chat client, local task orchestration, image metadata, and token estimates. |
| `src/LocalLm.Mcp` | stdio MCP server exposing LocalLm operations. |
| `src/LocalAi.Broker` | Durable machine-wide FIFO and the only Ollama transport. |
| `src/LocalAi.Cli` | Repository synchronization, compatibility transport, and hook installation. |
| `tests/CodeSearch.Tests` | CodeSearch unit tests. |
| `tests/LocalLm.Tests` | LocalLm unit tests. |

## Prerequisites

- .NET 10 SDK
- Access to `https://api.nuget.org/v3/index.json` for a clean restore
- Ollama only when running features that call local models; builds and unit tests do not start it

`NuGet.config` deliberately clears inherited package sources so unrelated private feeds do not
become build dependencies.

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

## Publishing

Publish only the executable projects that are needed:

```powershell
dotnet publish src/CodeSearch.Cli/CodeSearch.Cli.csproj --configuration Release --output publish/CodeSearch.Cli
dotnet publish src/CodeSearch.Mcp/CodeSearch.Mcp.csproj --configuration Release --output publish/CodeSearch.Mcp
dotnet publish src/LocalLm.Mcp/LocalLm.Mcp.csproj --configuration Release --output publish/LocalLm.Mcp
```

The `publish/` directory is ignored. Publishing does not register the resulting executables with
an AI client.

## Runtime notes

- CodeSearch and LocalLm submit all model work through the LocalAi durable FIFO broker.
- The immutable base generation is built from local `dev`; every worktree gets an exact
  generation/tree/dirty-content overlay under `%LOCALAPPDATA%\LocalAi`.
- Shared post-commit, post-merge, post-rewrite, and post-checkout hooks call `localai sync`.
- Direct agent-facing Ollama endpoints are forbidden; the `native` compatibility command is
  strictly allowlisted and still routes through the broker.
- MCP projects use stdio transport; standard output is reserved for protocol messages.
- Keep generated indexes, logs, process files, and other runtime state out of Git.
- `RuntimeAcl.Ensure` applies and validates each filesystem path in a single pass, so it is safe
  to call concurrently from multiple client processes (e.g. Claude and Codex connecting to the
  broker at the same time) without a false `Runtime ACL verification failed` failure. Earlier it
  walked the runtime tree twice — apply everywhere, then re-walk and validate everywhere — which
  left a window where a path created by a concurrent process between the two walks was validated
  but never had its ACL applied.

## Development rules

- Keep existing namespaces and package versions unless a separate change explicitly approves an
  update.
- Add tests alongside behavior changes and run the full solution baseline before publishing.
- Keep this English README and `README.ru.md` synchronized.
