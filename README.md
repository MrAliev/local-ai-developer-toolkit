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
- A backend watchdog is separate from the lease heartbeat. Long-running jobs have no total
  duration limit; after ten minutes without completion the broker probes backend/model residency and only
  fails an attempt after two consecutive confirmed unhealthy results. Probe timeouts and
  inconclusive results never classify a healthy multi-hour operation as failed.
- Repository synchronization persists a safe progress snapshot. `index_status` reports the
  phase, processed/total/remaining chunks, measured rate, and ETA while a generation builds,
  without exposing source chunks or job prompts.
- Task-aware routing selects an eligible model by capability, current VRAM residency,
  context limit, experiment state, and established fallback order.
- Model-aware snapshots group work for the same model, run shorter compatible tasks
  first, and prevent starvation after 15 minutes.
- CodeSearch combines vector similarity with literal matching for repository-aware
  semantic and lexical code search.
- Immutable base generations represent the selected local mainline, while exact
  per-worktree overlays contain branch and dirty-content differences.
- CodeSearch and LocalLm expose stdio MCP servers for integration with compatible AI
  clients.
- Repository synchronization and shared Git hooks are explicit opt-in operations.
- Recommended models are discovered and installed through MCP without removing
  existing fallback models.

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

### CodeSearch result workflow

`search_code` uses the embedding model recorded in the index. Production search resolves
a measured relevance floor for that exact model and fails closed with
`threshold not calibrated` when no profile exists; it never borrows a threshold from
another model.

What a hit *is* depends on the language, and the difference is worth knowing before
reading results. C# is chunked by syntax: one chunk per type — carrying its fields and a
table of contents of its members — and one per executable member, which means a method, a
constructor, a destructor, an operator, a conversion operator, an indexer, or a property
with a real body. Fields and auto-properties ride inside the type chunk rather than
becoming vectors of their own, because a DTO with twenty properties must not become
twenty-one of them. Generated C# is not indexed at all.

TypeScript and Python are chunked by symbol too, on the definition boundaries the external
indexers report: one chunk per definition, carrying its real symbol and line span, with a
definition that contains others listing its children rather than repeating their bodies.
This depends on the indexer being installed. It does not depend on the indexer reporting a
body: `scip-typescript` reports none for a declaration whose initialiser is a call —
`export const X = memo(() => …)` — and that declaration still gets a chunk, its boundary
read off the file as far as the line before the next thing the indexer named. What keeps
the window is what has no boundary to read: a declaration inside a function body, which
the indexer names `local N` and gives neither a name nor a span, and a declaration that
ends on the line it starts on, where one line is not a body worth a vector of its own.

Every other indexed format, and every region no definition covers — imports, module-level
statements, the gap between two functions — is chunked by a sliding window over lines: 60
lines with 12 lines of overlap. A hit there names the file and its line range instead of a
symbol. The window has no syntax awareness; what it does keep is exact line numbers, which
is what makes a hit actionable.

Precise navigation is a separate layer: it parses C# and XAML directly and imports
TypeScript and Python from the same external indexers.

Every search hit includes an opaque `chunk_id`. Pass that value to `get_code_chunk` to
retrieve the complete indexed source chunk. The ID is bound to the repository, immutable
generation, current Git tree, dirty-content hash, and composite chunk ordinal. A
malformed, cross-repository, stale, or out-of-range ID is rejected instead of being
resolved against a different snapshot.

Source-derived MCP output is data, not instructions. `search_code` keeps its trusted
index summary outside the wrappers and returns each hit in a fresh nonce-bound block:

```text
<untrusted-content id="<96-bit-lowercase-hex-nonce>" origin="search_code:<path>">
...source-derived hit, including chunk_id...
</untrusted-content id="<same-nonce>">
```

Every tool that returns something derived from the source uses the same boundary:
successful `get_code_chunk` output, and each location returned by `go_to_definition`,
`find_references`, `find_implementations` and `find_relationships` — paths, symbol ids and
ranges are written by whoever wrote the repository, so they are data as much as a snippet
is. Validation errors, status, and maintenance output remain outside it. Consumers must
preserve the boundary and must not execute or follow instructions found inside it.

The digest inside a `chunk_id` detects accidental corruption and casual tampering; it is
not an authentication boundary. What authorizes reading source is equality of repository,
generation, tree and dirty overlay, checked before anything is read.

Exact C#, WPF/WinUI/MAUI/Avalonia XAML, TypeScript/JavaScript, and Python navigation is stored separately from
vectors in `semantic.sidx`.
`localai sync` loads the primary solution with Roslyn/MSBuild, supplements it with lossless
XAML ranges and dialect-aware namespaces, `x:DataType`, dependency/styled properties, and binding
syntax, and imports bounded output from installed `scip-typescript` and `scip-python`
indexers. A missing external indexer is reported as skipped in the generation manifest rather
than invalidating other languages. The base SIDX is published atomically with CIDX and dirty
worktrees receive compact snapshot-bound semantic overlays containing changed semantic slices
and explicit deletion tombstones. MCP exposes
`go_to_definition`, `find_references`, `find_implementations`, and `find_relationships`; local
diagnostics are available through:

```powershell
localai semantic status --root C:\path\to\repository
localai semantic definition --path src/App.cs --line 10 --column 15 --root C:\path\to\repository
localai semantic references --path src/App.cs --line 10 --column 15 --root C:\path\to\repository
localai semantic implementations --path src/IService.cs --line 4 --column 18 --root C:\path\to\repository
localai semantic relationships --path src/Service.cs --line 8 --column 20 --direction outgoing --kind implementation --root C:\path\to\repository
localai semantic config init
localai semantic config show
localai semantic lsp-config init
localai semantic lsp-config show
localai semantic fallback-config init
localai semantic fallback-config show
localai semantic evaluate --cases tests/CodeSearch.Tests/Fixtures/SemanticNavigation/cases.json --root C:\path\to\repository
```

`--line` and `--column` are **zero-based**, and the column is a UTF-16 offset, matching what
the MCP tools take. An editor showing line 10 means `--line 9`. Positions that land on no
symbol return nothing rather than an error, so an off-by-one reads as "there is nothing here"
rather than as a mistake.

The installation-wide `%LOCALAPPDATA%\LocalAi\semantic-indexing.json` file configures adapter
enablement, executable paths and arguments, output paths, timeout, captured process output, and
all SCIP parser limits. The trusted adapter policy also makes legacy position fallback explicit:
`Utf16` for `scip-typescript` and `Utf32` for `scip-python`; direct SCIP imports without that
policy still reject ambiguous indexes. It is read on every sync, so changes do not require
rebuilding LocalAi. On Windows, npm `.cmd` shims are resolved through PATH/PATHEXT while retaining
bounded output and process-tree timeout handling.

Live navigation is opt-in through `%LOCALAPPDATA%\LocalAi\language-servers.json`; changing this
file does not require rebuilding LocalAi. It configures the global enable switch, per-language
executables and arguments, request/shutdown timeouts, maximum JSON-RPC message size, and bounded
stderr capture. Defaults for TypeScript/JavaScript, Python, HTML, and C# are written disabled by
`localai semantic lsp-config init`. MCP clients use `lsp_open_document` with monotonically
increasing versions and `lsp_close_document`; while open, LSP definition/reference/implementation results are
authoritative and precise, then navigation falls back to the exact snapshot-bound SIDX. A server
that drops its transport is restarted once and receives every open document with its latest
version and full text before the failed query is retried. Repository-relative paths remain `/`
normalized on every OS; platform-specific absolute paths exist only at the `file://` LSP boundary.
Persistent implementation and relationship queries use the exact SIDX graph. Incoming queries
find implementations, overrides, and derived types; outgoing queries expose the corresponding
base or interface symbols. Relationship queries never use heuristic text matching.

When neither LSP nor the snapshot-bound SIDX can resolve a symbol, LocalAi can perform a bounded
syntax/text scan. `%LOCALAPPDATA%\LocalAi\semantic-navigation.json` controls whether this fallback
is enabled and limits scanned files, file size, result count, identifier length, and case
sensitivity. The file is read for every request, so edits require no rebuild. Fallback results are
always marked `Heuristic`; they are never promoted to precise navigation.

A fallback also says why it happened. `go_to_definition` and `find_references` prefix a heuristic
answer with `semantic_navigation_degraded:` naming the cause, and the two causes are worded apart
because only one is repaired by re-syncing: a generation published before semantic indexing
existed carries no `semantic.sidx` at all, whereas a current index may simply have no symbol at
that position. `index_status` reports the same state on its `Navigation:` line, because nothing
else there moves — a repository that has not been re-synced since semantic indexing shipped still
reports the right model, an undrifted commit and `Status: current` while every navigation query
answers from text matching.
The marker-based `semantic evaluate` command reports correctness, process-cold load time,
first/warm query percentiles, observed memory deltas, and combined base/overlay SIDX size.
The current baseline is documented in
[semantic-navigation-evaluation.md](docs/semantic-navigation-evaluation.md).

CLI equivalents are:

```powershell
dotnet run --project src/CodeSearch.Cli -- search --query "where is the broker queue" --root C:\path\to\repository
dotnet run --project src/CodeSearch.Cli -- get-chunk --id "<chunk_id>" --root C:\path\to\repository
```

See the measured [CodeSearch retrieval evaluation](docs/codesearch-evaluation.md) for
the calibrated profile, corpus provenance, A/B results, token heuristic, and
limitations.

The complete inventory of all twenty MCP tools — what exists, what it is for, and the
constraint that matters — is in the [tool reference](docs/mcp-tools.md).

A connected walkthrough for the people who use this daily — what it does, how it works,
what protects it, and what it saves — is in the developer overview
([Russian only for now](docs/overview.ru.md)).

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

After installing the MCP binaries, inspect and synchronize the model catalog through
LocalLm:

```text
local_models_status
local_models_sync
```

The sync command submits allowlisted maintenance jobs to the same durable queue. It
does not expose a generic pull command and does not remove existing models.

Install the shared chained Git hooks only after approving that external mutation:

```powershell
C:\path\to\LocalAi\bin\launcher\localai-launcher.exe run localai hooks install --root C:\path\to\repository
```

The dispatchers go wherever Git actually looks for hooks. A repository that sets
`core.hooksPath` — husky, lefthook and simple-git-hooks all do — gets them there instead of
in `$GIT_DIR/hooks`, where Git would never read them. For husky that means `.husky`, not the
`.husky/_` it rewrites on every `npm install`. Dispatchers that land in the working tree are
added to `.git/info/exclude`, so they stay out of `git status` without touching a `.gitignore`
that belongs to the repository rather than to us.

## What the installer sets up

A finished installation is expected to leave a machine that can actually be used, so the
wizard applies three things beyond copying binaries. Each is shown on the review page and
none is applied before the run is confirmed.

**Client integration.** For every AI client the wizard detects, it registers the `codesearch`
and `locallm` MCP servers against the stable launcher path and writes a managed instruction
block into that client's global instructions file — `~/.claude/CLAUDE.md` for Claude,
`~/.codex/AGENTS.md` for Codex. A detected client defaults to both; a client that was not
found is left alone, and an explicit choice always wins.

For Codex, each managed server also gets `default_tools_approval_mode = "approve"`, and every
tool the two servers expose gets a `[mcp_servers.<server>.tools.<tool>]` row with the same
value. `approve` is the value that skips the prompt — Codex maps it to "approval not required";
the value that asks is `prompt`.

Both are needed. Codex resolves a tool as per-tool override, then server default, then `auto`,
and under `auto` a tool that declares no annotations is prompted for, which is every tool here.
The server default covers tools added by a later release the moment they exist; the rows make
the current set explicit.

Only what is missing is written. An existing row or server default is left exactly as the user
wrote it, including one that refuses the tool, because that is a decision to preserve rather
than an omission to correct. Without this a machine accumulates rows only for the tools it
happened to reach for — one long-lived installation carried rows for eleven of the twenty. The
list is held to the servers by tests that reflect over their tool attributes, so a new tool
cannot ship without its row.

The block is repository-agnostic on purpose. Indexing stays opt-in per repository, so rather
than assuming the repositories that happened to be set up on the machine it was installed on,
it states the check (`localai repo status`) and the two commands that connect any repository
wherever it was cloned. Without that, a client on a fresh machine can use the local models but
never offers to index the first repository it is pointed at.

The instruction block is delimited by `<!-- BEGIN LOCALAI MANAGED INSTRUCTIONS -->` and its
matching end marker. Everything between them is replaced wholesale on the next install and
everything outside them is preserved, so upgrading the rules never costs a user their own
notes. The block covers routing — when to reach for `search_code` instead of a text search,
what belongs to `read_image`, `triage_log` and `ask_local` — as well as the transport
invariants: the shared broker only, never Ollama directly, full-VRAM validation. Transport
rules alone proved insufficient in practice: they describe how a call must travel without
ever saying when to make one, and an assistant reading only those keeps working in the cloud
while the installed tools sit idle.

**Models.** The wizard installs the models the machine can hold and skips the ones already
present; the broker is asked what is installed rather than a list being assumed, and only
missing models are pulled. Candidates come from the signed release manifest, never from the
routing catalogue or the registry, because the broker installer refuses any model it cannot
weigh against the signature. Automatic selection takes the largest context each model fits
in; a model too large for the adapter at every context size is reported by name instead of
being downloaded and left unusable. Anything downloaded is then preflighted, so a model that
cannot load fully resident is reported rather than silently accepted.

Because the selection is manifest-driven, a release published without a model list installs
no models at all — and that is exactly what shipped from 0.1.29 to 0.1.44, because `--models`
was optional and the publish script never passed it. The list is now generated per release
from the routing catalogue and the public registry, and signing demands it:

```powershell
localai-release-signer models --out publish\release\release-models.json
localai-release-signer sign --models publish\release\release-models.json ...
```

`sign` refuses to run without `--models` (or an explicit `--no-models`), `release --publish`
refuses a manifest whose model list is empty, and the wizard now reports a run that installed
no model as a failure rather than as a line in the log. Sizes are read from the registry at
signing time rather than committed here: a tag republished with different quantisation keeps
its name, so a size in the source tree goes stale without anyone noticing.

**Residency policy.** The video-memory page writes `%LOCALAPPDATA%\LocalAi\policy.json` —
but only when LocalAi is installed. That write creates its parent directory with inherited
permissions, and the layout lease refuses such a root without mutating it, so a run that
failed before installing anything used to leave behind a directory that made every later
installation refuse itself with "the directory still inherits access rules". Relaxing the
policy is deliberate and stays visible in the run report.

**GitHub sign-in.** The system check reports whether `gh` is signed in as a line of its own,
separate from whether the executable exists. The wizard can install the CLI and cannot sign
in for anyone — `gh auth login` is interactive and the installer never handles a token — so a
machine that is merely signed out would otherwise pass every prerequisite and fail at the
package step with a message about resolving releases.

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
dotnet publish src/LocalAi.Launcher/LocalAi.Launcher.csproj --configuration Release --output publish/LocalAi.Launcher
dotnet publish src/LocalAi.Broker/LocalAi.Broker.csproj --configuration Release --output publish/LocalAi.Broker
```

> The queue and the runtime ACL bootstrap live in `LocalAi.Broker.Core`, a library, and
> `LocalAi.Broker.Client` references that rather than the broker executable. Keep it that
> way. While the client referenced the `OutputType=Exe` project, every dependent publish
> also emitted the broker's `apphost.exe`, `deps.json` and `runtimeconfig.json`, and a
> RID-specific framework-dependent publish then failed with `NETSDK1152` — cleaning
> `bin`/`obj` did not help, because the conflict was produced during the build.
>
> That reference also placed `LocalAi.Broker.exe` next to its dependants by accident, which
> is how development builds could start a broker at all. The copy is now explicit, in
> `src/BrokerBinary.props`, and applies to the projects that need it.

For an installer that does not depend on preinstalled .NET runtime:

```powershell
dotnet publish src/CodeSearch.Cli/CodeSearch.Cli.csproj --configuration Release --runtime win-x64 --self-contained true --property:PublishSingleFile=true --output publish/CodeSearch.Cli
dotnet publish src/CodeSearch.Mcp/CodeSearch.Mcp.csproj --configuration Release --runtime win-x64 --self-contained true --property:PublishSingleFile=true --output publish/CodeSearch.Mcp
dotnet publish src/LocalLm.Mcp/LocalLm.Mcp.csproj --configuration Release --runtime win-x64 --self-contained true --property:PublishSingleFile=true --output publish/LocalLm.Mcp
dotnet publish src/LocalAi.Cli/LocalAi.Cli.csproj --configuration Release --runtime win-x64 --self-contained true --property:PublishSingleFile=true --output publish/LocalAi.Cli
dotnet publish src/LocalAi.Launcher/LocalAi.Launcher.csproj --configuration Release --runtime win-x64 --self-contained true --property:PublishSingleFile=true --output publish/LocalAi.Launcher
dotnet publish src/LocalAi.Installer/LocalAi.Installer.csproj --configuration Release --runtime win-x64 --self-contained true --property:PublishSingleFile=true --output publish/LocalAi.Installer
```

The `publish/` directory is ignored. Publishing does not register executables with an
AI client or install Git hooks.

### Cutting a release

```powershell
dotnet run --project src/LocalAi.ReleaseSigner/LocalAi.ReleaseSigner.csproj -c Release -- `
    release --version 0.1.36 --root .
```

The first run scaffolds `docs/releases/0.1.36.md` and `.ru.md` and stops. Write them — the notes
are connected prose about what changed and why, and a list of commit subjects is a different and
much less useful thing. Run it again and it opens the pull request that carries them.

Once that pull request is merged, from `main`:

```powershell
dotnet run --project src/LocalAi.ReleaseSigner/LocalAi.ReleaseSigner.csproj -c Release -- `
    release --version 0.1.36 --root . --publish
```

That builds, signs locally, checks the manifest against the release being published, and only
then tags and uploads. It refuses a dirty tree, a branch other than `main`, a commit `origin`
does not have, a commit without a green `build-and-test` run, a version already tagged, and notes
still carrying the scaffold's TODO.

The split is deliberate. Building and signing are repeatable; tagging and publishing are what
other people's installers resolve, so they are a separate, explicit invocation rather than a
later stage of the same one. Merging the pull request stays a human decision throughout.

The two halves exist because the same version used to be typed into four places — both note
filenames, the publish script, the tag — and the commit into two, the manifest's version
directory and whatever the tag ended up pointing at. Nothing compared them, and a disagreement
produced a package that verifies, installs, and is not what the tag names.

### Signing a release manifest

An installed machine never needs a system-wide .NET runtime. Every component — the broker
included — ships as a self-contained executable, and the broker is started as
`LocalAi.Broker.exe` from the directory of whichever component needs it. That path is
resolved through `AppContext.BaseDirectory`; **`Assembly.Location` must not be used**,
because it returns an empty string in a single-file publish and the process dies with
"The path is empty" before the broker is ever reached.

The installer refuses any release whose manifest is not signed with the ECDSA P-256 key it
trusts. This is a self-generated key pair, not a certificate: no certificate authority and
no cost are involved. Generate it once and keep the private key outside the repository:

```powershell
$dir = "$env:LOCALAPPDATA\LocalAi\release-signing"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$ec = [System.Security.Cryptography.ECDsa]::Create(
    [System.Security.Cryptography.ECCurve]::CreateFromFriendlyName("nistP256"))
[IO.File]::WriteAllBytes("$dir\release-signing-private.pkcs8.der", $ec.ExportPkcs8PrivateKey())
[IO.File]::WriteAllBytes("$dir\release-signing-public.spki.der", $ec.ExportSubjectPublicKeyInfo())
$ec.Dispose()
icacls $dir /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F"
```

Build the package first. The verifier compares the archive contents against
`LocalAiPackageLayout.PackageArtifactFiles` with `SetEquals`, so it must hold exactly six
artifacts plus `localai-package.json`, flat and with nothing extra. **That is why the
executables must be published self-contained**: the format has no room for sibling
dependency assemblies, so a framework-dependent build cannot be shipped as a release.

```powershell
localai-release-signer pack `
    --input publish\artifacts `
    --release-version 0.1.22 `
    --version-directory d9c52d2 `
    --out publish\release\localai-package.zip
```

Build the model list the manifest will carry. Sizes come from the public model registry,
so this step needs the network, and it fails rather than skipping a tag it could not
size:

```powershell
localai-release-signer models --out publish\release\release-models.json
```

Then sign it with `localai-release-signer`. It builds the manifest through the same
canonical serializer the verifier uses, normalises the signature to the canonical low-S
form the verifier requires, and re-verifies the result before it can be published:

```powershell
localai-release-signer sign `
    --package publish\release\localai-package.zip `
    --package-uri https://github.com/MrAliev/local-ai-developer-toolkit/releases/download/0.1.22/localai-package.zip `
    --release-version 0.1.22 `
    --version-directory d9c52d2 `
    --models publish\release\release-models.json `
    --out publish\release
```

`--models` is not optional. A release signed without it installs no model at all, so
the signer refuses to run unless the list is supplied or `--no-models` states that the
omission is deliberate.

Before publishing, run the finished artifacts through the installer's own package verifier.
This exercises the archive inspection, extraction and metadata comparison an installer
performs, so a structural mistake fails here rather than on someone else's machine:

```powershell
localai-release-signer verify-package `
    --package publish\release\localai-package.zip `
    --manifest publish\release\release-manifest.json `
    --signature publish\release\release-manifest.sig
```

Verification uses the public key embedded in `LocalAi.Installer.Core`
(`ReleaseTrustAnchor`), not a file next to the tool, so signing with a key the shipped
installer does not trust fails immediately instead of on a user's machine. Nothing is
written when verification fails. Commit the public half at
`src/LocalAi.Installer.Core/Releases/release-signing-public.spki.der`; the private half
must never enter the repository.

Publish `release-manifest.json` and `release-manifest.sig` as release assets alongside the
package. Field rules worth knowing before a release fails validation: `ReleaseVersion` is
strict semver with **no leading `v`**, `PackageSha256` is **upper-case** hex, and
`PackageUri` must be `https` without user info or a fragment.

### Authenticode: two separate things

Authenticode appears twice in this project, at different layers, and they are easy to
confuse.

**The installer executable.** `LocalAi.Installer.exe` is published unsigned. It is the file
a person downloads and double-clicks, so it is the one Windows judges: an unsigned
executable carrying the mark of the web produces the full-screen "Windows protected your
PC" prompt, which is dismissed through *More info -> Run anyway*. Signing this file is the
only thing that removes that prompt; nothing inside the release affects it. Until there is
a certificate, `release --publish` prints the installer's SHA-256 so whoever passes the file
on has something for the recipient to compare against.

**The files inside the package.** `RequiresAuthenticode` in the signed manifest is a
different check, performed by the installer rather than by Windows: with it set, every
`.exe` and `.dll` extracted from the package must be signed by the publisher named in
`AuthenticodePublisherPolicy`, and any file that is not fails the installation. That policy
is currently a placeholder — subject `CN=LocalAi` and a SHA-256 of sixty-four zeroes — so
passing `--require-authenticode` today would refuse every install rather than harden one.
Replace both values with the real signer's distinguished name and the SHA-256 of its
SubjectPublicKeyInfo (not the certificate thumbprint) before turning the flag on.

**Order, when a certificate exists.** Sign the artifacts before packing, never after:
`pack` hashes the archive and `sign` hashes the package, so signing a binary afterwards
invalidates both. The sequence is publish, `signtool` over `publish\artifacts` and over
`LocalAi.Installer.exe`, then `pack`, `models`, `sign`, `verify-package`. Always
countersign with a timestamp (`signtool /tr`), or every signature stops validating the day
the certificate expires.

### Immutable versions and atomic activation

Publish the CLI, MCP servers, broker, contracts, and their runtime dependencies into a
fresh staging directory. Merge every per-project publish output into that one directory
**including the `runtimes\` subtree** — a framework-dependent publish without an explicit
RID places Windows-only assemblies such as `System.Diagnostics.EventLog.dll` under
`runtimes\win\lib\net10.0\`, and `deps.json` resolves them from exactly that path. A flat
copy of the top-level files alone produces a directory that fails at startup with
`FileNotFoundException`.

Verify the complete output before activating: the staging directory must contain every
component executable (`codesearch.exe`, `codesearch-mcp.exe`, `locallm-mcp.exe`,
`localai.exe`, `localai-launcher.exe`, `LocalAi.Broker.exe`) and every dependency
(`CodeSearch.Core.dll`, `LocalLm.Core.dll`, `LocalAi.Broker.Client.dll`,
`LocalAi.Repository.dll`, `ModelContextProtocol.dll`, the `Microsoft.Extensions.*` set).
A directory holding only a couple of dozen files is a partial publish, not a release —
launching a tool from it fails with a `System.Runtime` load error that comes from the
child process, not from the launcher.

Then copy it once to
`bin\versions\<version>`. A published version directory is immutable: activation never
updates or deletes it, and historical versions remain available for rollback.

Install the independently published launcher at
`bin\launcher\localai-launcher.exe`. Codex, Claude, Git hooks, and delegation wrappers
must register that stable executable plus a tool prefix, for example:

```text
localai-launcher.exe run codesearch-mcp
localai-launcher.exe run locallm-mcp
localai-launcher.exe run localai
```

The active version is the atomically replaced `bin\current.json` document:

```json
{"schemaVersion":1,"version":"<version>"}
```

After the candidate directory has been verified, activate it. Activation always requires
an explicit expectation about the pointer you are replacing, so a concurrent activation
cannot be overwritten silently:

```powershell
# Replacing a pointer that already exists: state its current SHA-256.
$expected = (Get-FileHash bin\current.json -Algorithm SHA256).Hash
bin\launcher\localai-launcher.exe activate <version> --if-current-sha256 $expected

# First ever activation, when bin\current.json does not exist yet.
bin\launcher\localai-launcher.exe activate <version> --if-current-missing

# Add --stop-running when the previous version is still in use.
bin\launcher\localai-launcher.exe activate <version> --if-current-sha256 $expected --stop-running
```

Omitting both guards is a usage error and exits with code 2. Activation also fails when
the observed pointer does not match the stated expectation. Without `--stop-running` it
fails while a launcher-managed version is in use; with it, only processes whose exact
executable or fresh broker assembly identity belongs to the previous version are stopped,
and then the pointer is switched. It does not stop Ollama or unrelated `dotnet`
processes. Roll back by activating a previously verified immutable directory.
All model requests, including compatibility commands, continue to use the shared FIFO
broker; direct Ollama access is unsupported.

Stopping the tools of a version is available on its own:

```powershell
bin\launcher\localai-launcher.exe stop [--version <version>]
```

The broker is asked to finish rather than killed. A shutdown request naming its process id and
start time is written next to `host.json`; the broker notices it on the heartbeat it already
publishes every second, stops leasing new work, lets the job in flight run to completion, and
exits. Only a broker that has not gone by the deadline is terminated. It owns a durable queue
and can be minutes into an inference, so destroying it is the answer of last resort rather than
the mechanism. The request names a start time as well as an id so a stale one cannot shut down
a healthy broker that inherited the id, and it is deleted as soon as it is accepted.

The stdio tools have no durable state and no channel to be asked through, and their client
restarts them on demand, so for those termination is all there is — and all that is needed.

It stops the processes running out of the active version — or the named one — and does not
touch the pointer. Activation stops them too, but only as part of switching, and the step
before that switch is replacing the stable launcher binary. Windows refuses to overwrite a
running executable, and every connected client keeps one launcher process alive per tool it
uses, so an installation could publish a new version, fail to put the new launcher in place,
and roll the whole thing back — with the recovery for it sitting one step further on. The
installer now stops the tools when, and only when, they are in the way: an upgrade on an idle
machine touches nothing.

Reading the pointer takes a **shared** lease; only the swap takes the lock exclusively.
This matters because every launcher-run tool holds a shared lease for its whole lifetime, so
on any machine that is actually using LocalAi a connected client is holding one — and an
installer that took the lock exclusively merely to read the pointer refused the upgrade
before reaching the activation that would have cleared those processes. A read has never
needed exclusivity: the swap stays guarded by `--if-current-sha256`, which catches a pointer
that changed in between rather than overwriting it.

### Model residency policy

By default a model must be **fully resident in video memory**; anything less is refused. This
is not a performance preference. A model that spills into system memory does not fail — it
just becomes several times slower, and nothing in the answer says so.

Machines without a usable discrete adapter can relax it:

```powershell
localai policy show
localai policy set --residency AllowPartialOffload
localai policy set --residency AllowCpu
localai policy set --idle-model-keep-alive-seconds 0
```

| Setting | Admits | Refuses |
| --- | --- | --- |
| `RequireFullVram` (default) | fully resident loads | everything else |
| `AllowPartialOffload` | part of the model on the adapter | pure CPU loads |
| `AllowCpu` | anything that actually loaded | a model reporting no size |

The policy lives in `%LOCALAPPDATA%\LocalAi\policy.json` and is read by the broker, the CLI
and the installer alike. A missing, malformed or unknown-value document falls back to
`RequireFullVram`: a parse error must never silently relax a safety check. A broker that is
already running keeps the previous policy until it is restarted.

`IdleModelKeepAliveSeconds` controls how long the broker retains a resident model after it
becomes idle. Its default is `0`: the model is unloaded immediately unless the queue still
contains work routed to that same model. Jobs for other models do not retain it. Set a larger
number with `localai policy set --idle-model-keep-alive-seconds <seconds>` when the latency of
reloading is preferable to releasing video memory promptly.

Degradation stays visible: every load admitted below full residency carries a warning naming
the share that reached video memory, and `FullyResident` reports the truth rather than a
constant. Note that the agent instruction block still asks for full-VRAM validation, so
relaxing this diverges from what those instructions promise.

An NPU does not help here. Everything runs through Ollama, whose backends are CPU, CUDA,
ROCm, Metal and Vulkan; NPUs are driven by a separate stack (OpenVINO, DirectML, Windows ML),
and the residency numbers this policy is built on do not map onto NPU memory.

### Retention

Everything the runtime produces is bounded, and the bounds live in
`%LOCALAPPDATA%\LocalAi\retention.json`. A missing or malformed document yields the defaults
below.

| Bound | Default | Applies to |
| --- | --- | --- |
| `ResponseGraceMinutes` | 10 | floor: no response body younger than this is ever dropped |
| `ResponseRetentionMinutes` | 60 | age at which an archived response body is dropped |
| `ResponseBudgetBytes` | 512 MB | total retained response bodies, oldest dropped first |
| `ArchiveRetentionDays` | 14 | age at which an archived job is deleted outright |
| `ArchiveEntryLimit` | 2000 | archived jobs kept, oldest deleted first |
| `GenerationsPerRepository` | 3 | index generations kept, including the current one |
| `InstalledVersions` | 3 | version directories kept under `bin\versions` |
| `LauncherBackups` | 3 | launcher backups kept under `installer\backups` |
| `TelemetryRetentionDays` | 30 | age at which a job telemetry record under `telemetry\metrics` is deleted |

The archive is a handover, not a record: a client collects its response on the next poll after
the job turns terminal and never asks again, yet one embedding batch leaves megabytes of
vectors behind. The broker applies the archive bounds continuously — throttled to one pass a
minute and capped per pass, so the queue never stalls behind housekeeping — and drops the body
while keeping the request and state documents as the audit trail.

`ResponseGraceMinutes` is the one value here that protects correctness rather than disk.
Nothing may remove a body inside it, whatever the other bounds ask for, and a hand-edited
document that sets it to zero is clamped rather than obeyed.

To reclaim a backlog, or to reach the parts a background sweep does not own:

```powershell
localai prune --dry-run
localai prune
```

`prune` never removes the generation a repository's pointer names, the version
`bin\current.json` names, or a job that has not been terminal for longer than the response
grace. A version whose files are still mapped by a running tool refuses to be deleted, which is
the right answer — the next prune collects it. It also forgets repository records whose
checkout no longer exists, which is what a throwaway worktree leaves behind.

### Broker compatibility and startup

The broker `host.json` schema 3 publishes an explicit protocol version and stable build
compatibility family. Its assembly path remains a diagnostic and launcher
version-ownership record; client health does not depend on DLL-path affinity. Therefore,
compatible installed and development clients share the one machine-wide broker even when
their DLL paths differ.

A fresh live incompatible or legacy host remains running, but the observing client fails
immediately as `broker_incompatible` and does not attempt to start a second broker. During
startup, an early nonzero child exit is
`broker_start_failed`, while a bounded wait that never observes compatible health is
`broker_start_timeout` and includes the last observation. A zero-exit child likely lost
`broker.lock` to another process, so the client observes that lock owner's host state only
within that same bound. CodeSearch retains its lexical fallback for `broker_start_timeout`.

These rules do not change the singleton FIFO, runtime ACL, immutable-version activation,
full-VRAM/zero-offload, or direct-Ollama prohibition.

## Model-aware routing

The embedded `model-routing.json` catalog maps each local task profile to one or more
eligible models. Profiles cover plain, technical, and image translation; OCR and visual
analysis; vector embedding and deterministic exact search; code analysis, editing,
review, and reranking; log triage; extraction and classification; summaries,
multi-file synthesis, and planning.

The broker enforces these invariants:

- Context tiers range from 2K to each model's official maximum (up to 256K).
  A tier is usable only when live preflight proves the complete runner fits in VRAM.
- A cold model is preflighted with empty content before the real task is sent.
- `/api/ps` must report `size_vram == size`; CPU or system-RAM offload disables that
  exact model/context combination and triggers an established fallback.
- The scheduler prefers compatible work for the resident model, freezes each selected
  snapshot, orders its jobs by predicted duration, waits at most two seconds to collect
  related work before a switch or long task, and forces work older than 15 minutes into
  the next compatible snapshot. Successful execution feeds its actual duration back
  into the content-free rolling estimate.
- Resident models are unloaded after the configurable idle keep-alive (zero seconds by
  default) when no queued work targets that model. A dependency-blocked workflow step for
  the resident model still retains it; work routed to another model does not.
- Before a cold model switch, the broker unloads every other catalog-managed runner.
  Unknown external Ollama processes are left untouched.
- Experiments are tracked independently per task profile and model. A new candidate is
  tried for the first ten completed logical tasks of each applicable profile, then
  paused for owner review; feedback is rejected before that report gate. Technical,
  structural, and context failures run an established fallback. Translation chunks
  share one workflow ID and consume only one attempt after final validation, while
  preserving the candidate's failure category if the broker used a fallback. The sole
  early exception is `continue_experiment`, which may reset a circuit opened by two
  consecutive technical failures.
- Experiment telemetry is retained for thirty days (`TelemetryRetentionDays`) and contains only workflow/task/model
  identifiers, counts, outcomes, timings, and token estimates. It reports local input
  and output, total local processing, avoided cloud generation, and net cloud-context
  reduction separately. Prompts, answers, file contents, image bytes, paths, and
  secrets are excluded.

LocalLm exposes these model-management and translation MCP tools:

| Tool | Purpose |
|---|---|
| `translate_local` | Translate text, validate protected Markdown structure, and append the actual model attribution. |
| `local_models_status` | Show installed, resident, missing recommended, and experiment state. |
| `local_model_preflight` | Load one model/context without task content and return full-VRAM residency proof. |
| `local_models_sync` | Queue allowlisted recommended-model installation. |
| `local_model_experiment_report` | Show logical-task attempts, errors, fallbacks, timing, warm/cold counts, local processing, avoided cloud generation, and net context reduction. |
| `local_model_feedback` | Promote, continue, restrict to fallback, or disable one task/model pair. |

`local_model_experiment_report` takes its `Attempts`, `Successes` and `Errors` from the
experiment state — the same counts `local_models_status` shows and the router uses to decide
when an experiment pauses. Timings and token figures can only be measured over the per-task
telemetry that still exists, which is bounded to a week, so `ObservedTasks` says how many
records they rest on. Reporting one number for both is what made a pair with six completed
attempts answer `Attempts: 1`. Reading the report never discards telemetry; the week-long
bound is applied by the write that records a new task.

`translate_local` is the validated local translation path. The calling agent decides
whether a translation runs locally or in the cloud; LocalAi does not impose that policy.

`read_image` accepts `VisualAnalysis`, `Ocr`, or `ImageTranslation`; `ask_local`
accepts an explicit text task profile. An explicit model is an override subject to the
same capability, installation, context, and full-VRAM checks.

The notice each tool returns states the saving as a sentence, not a number to interpret. A job
small enough to save nothing says so — a 336x52 screenshot is worth about two dozen tokens to
look at directly, and any useful answer about it is longer than that, so delegating it is
honest work with no saving attached. That is advice about what to delegate, not a counter that
failed.

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
- Semantic indexing always uses the embedding model recorded in the index header.
  Exact lexical search remains deterministic and never invokes a chat model.
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
| `src/LocalAi.Broker.Core` | The queue, the runtime ACL bootstrap, and everything the client shares with the broker process. |
| `src/LocalAi.Broker.Client` | Client and broker-process integration. |
| `src/LocalAi.Launcher` | Stable tool dispatch and atomic immutable-version activation. |
| `src/LocalAi.Repository` | Repository identity, manifest, and worktree state. |
| `src/LocalAi.Cli` | Repository synchronization, compatibility transport, and hook installation. |
| `src/CodeSearch.Core` | Chunking, embeddings, index storage, overlays, and hybrid search. |
| `src/CodeSearch.Cli` | Index, overlay, status, scan, and search commands. |
| `src/CodeSearch.Mcp` | Stdio MCP server exposing CodeSearch operations. |
| `src/LocalLm.Core` | Local-model task helpers, image metadata, and token estimates. |
| `src/LocalLm.Mcp` | Stdio MCP server exposing LocalLm operations. |
| `src/LocalAi.Installer` | The GUI installer: system check, prerequisites, package, models, residency, client registration. |
| `src/LocalAi.Installer.Core` | What the installer does rather than shows — signature verification, layout leases, dependencies, agent instruction blocks. |
| `src/LocalAi.ReleaseSigner` | Packing, signing and publishing a release, and verifying a published manifest. |
| `tests/*` | Unit and integration coverage for all toolkit components. |

## Development rules

- Keep existing namespaces, commands, runtime paths, and package versions unless a
  separate change explicitly approves an update.
- Route Ollama work only through the shared broker.
- Add focused tests alongside behavior changes and run the full solution baseline
  before publishing.
- Never let a test assert how fast the machine is. Wait for the condition rather than for
  a duration, prefer a purpose-built child process over an interpreter whose startup cost
  is unbounded, and leave hang detection to `--blame-hang`, which CI already applies to
  every test project. A deadline a correct run can miss proves nothing and reports itself
  as a fault in the code under test.
- Branch a pull request from `main`, and give it one subject. Two subjects in one branch cannot
  be reviewed apart or reverted apart. Stacking a pull request on another one is worse than the
  rebase it saves: GitHub closes it when its base branch is deleted on merge rather than moving
  it to `main` — which is what happened to #91 and cost a recreated pull request. When two
  changes touch the same file, land the first and rebase the second.
- Keep this English README and `README.ru.md` synchronized.
- Preserve UTF-8 without BOM and Windows CRLF line endings for repository documentation.
