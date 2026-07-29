# Atomic Launcher Version Activation Design

## Problem

LocalAi is installed as immutable version directories below
`C:\Users\Mr.Aliev\tools\LocalAi\bin\versions`, but its consumers do not share
one source of truth for the active version. Codex and Claude currently launch
CodeSearch and LocalLm from `versions\caed45c`, while
`delegate-to-local-models` launches the stale flat `bin\localai.exe`.

The version mismatch is observable through the broker-only path:

- the current client successfully executes `native tags`;
- the stale client receives a response containing
  `LocalUsageReceipt.Routing`;
- strict JSON deserialization rejects that additive field because the stale
  `LocalUsageReceipt` contract does not contain it;
- the Python compatibility wrapper collapses the broker protocol failure into
  `OllamaUnavailable`.

Updating several absolute executable paths is not atomic. A reboot or client
startup between those updates can therefore start a client and broker from
different versions.

## Goals

1. Give Codex, Claude, the delegation wrapper, Git hooks, and manual CLI use one
   stable launcher contract.
2. Resolve every invocation to one complete immutable LocalAi version.
3. Activate a fully published version with one atomic pointer replacement.
4. Prevent activation while an invocation from the previous version is alive.
5. Prevent a live broker from surviving a version activation.
6. Preserve the single machine-wide durable FIFO broker and its existing
   `%LOCALAPPDATA%\LocalAi` runtime.
7. Report launcher, activation, and broker protocol failures accurately.
8. Migrate the installed clients and verify the live broker-only workflow.

## Non-goals

- Calling Ollama directly or adding a second broker/runtime queue.
- Silently tolerating arbitrary incompatible broker contracts.
- Overwriting an existing version directory in place.
- Automatically changing unrelated Codex, Claude, Git, or model settings.
- Removing historical version directories during activation.
- Making normal version activation update the stable launcher itself.

## Architecture

### Stable launcher

Add a small `LocalAi.Launcher` executable that depends only on the .NET base
class library. It must not reference LocalAi contracts, broker, repository, MCP,
or model-routing assemblies.

The stable installation lives below:

```text
bin\launcher\
  localai-launcher.exe
  localai-launcher.dll
  localai-launcher.deps.json
  localai-launcher.runtimeconfig.json
```

All long-lived registrations use this launcher:

```text
localai-launcher run localai [arguments...]
localai-launcher run codesearch [arguments...]
localai-launcher run codesearch-mcp [arguments...]
localai-launcher run locallm-mcp [arguments...]
```

The launcher maps only these allowlisted tool names to executables with the same
name under the selected immutable version directory. It forwards arguments,
standard input, standard output, standard error, cancellation, and the child
exit code without interpreting the tool protocol.

### Current-version pointer

The active version is stored in `bin\current.json`:

```json
{
  "schemaVersion": 1,
  "version": "caed45c"
}
```

`version` is a single safe directory name, not a path. Resolution rejects rooted
paths, separators, `.`/`..`, blank values, unknown schema versions, reparse
escapes, missing directories, and missing required executables.

The resolved directory must remain below the canonical
`bin\versions` directory. A version directory is immutable after publication.

### Version lease

`bin\current.lock` coordinates launch and activation:

1. `run` opens the lease for shared reading before reading `current.json`.
2. It reads and validates one pointer snapshot while holding that lease.
3. It starts the exact executable from `bin\versions\<version>`.
4. It retains the shared lease until the child exits.
5. `activate` requires exclusive access to the same lease.

Consequently, activation cannot complete while an old CLI or MCP process
launched through the stable entry point remains alive. Every process started
after activation sees the new pointer.

### Atomic activation

Version publication and version activation are separate operations. Publication
first creates and completely verifies a new immutable directory.

Activation uses:

```text
localai-launcher activate <version> [--stop-running]
```

The operation:

1. Acquires a machine-wide activation mutex to serialize activators.
2. Resolves the candidate beneath `bin\versions`.
3. Verifies the required CLI, broker, MCP, dependency, runtime, and routing
   artifacts without changing current state.
4. With `--stop-running`, stops only processes whose exact executable or broker
   assembly path belongs to the currently active LocalAi version. It never
   stops Ollama or an unrelated `dotnet` process.
5. Acquires the exclusive version lease with a bounded timeout.
6. Rechecks the candidate after acquiring the lease.
7. Writes `current.json.tmp` in the same directory, flushes it, and atomically
   replaces `current.json`.
8. Reopens and validates the committed pointer before reporting success.

Without `--stop-running`, activation fails with a precise `version_in_use`
diagnostic when a launcher child or broker is active. If any step before the
atomic replacement fails, the previous pointer remains intact.

The stable launcher itself is not replaced by normal activation. A future
launcher-contract upgrade is a separate maintenance operation performed only
while no launcher process is running.

### Broker consistency

All new clients start the broker assembly from their own physical immutable
version directory. The live migration stops the old broker before switching the
pointer. After activation, the first client starts the broker from the newly
selected directory under the existing broker startup semaphore.

The broker remains machine-wide and uses the existing runtime root. No
per-version broker or direct Ollama fallback is introduced.

### Client registration

`ClientCommand.Plan` will register the stable launcher plus tool arguments
instead of direct versioned MCP executables:

- Codex `codesearch`: command is the launcher; arguments are
  `run`, `codesearch-mcp`.
- Codex `locallm`: command is the launcher; arguments are
  `run`, `locallm-mcp`.
- Claude uses the equivalent command and arguments.
- Git hooks invoke `localai-launcher run localai`.
- The Python delegation wrapper invokes the same launcher command prefix.

The Python `OllamaClient` accepts an executable command prefix rather than one
hardcoded executable path. A non-zero launcher exit, invalid JSON output, and a
broker protocol error remain distinct failures. Broker stderr is included in a
bounded, sanitized diagnostic instead of being discarded and rewritten as
`OllamaUnavailable`.

## Reboot and update behavior

After the one-time migration, every consumer has a stable registration.
Publishing a future version does not affect running processes. Activation
cannot occur while old launcher children or the old broker remain active, and
the pointer replacement is atomic.

After a reboot there are no old client or broker processes. Codex, Claude,
hooks, and delegation may start in any order, but every launcher reads the same
committed pointer and starts binaries from the same immutable directory. The
existing broker startup semaphore still ensures that only one matching broker
is started.

## Error handling

The launcher uses stable machine-readable error codes and human-readable stderr:

- `current_pointer_missing`
- `current_pointer_invalid`
- `version_path_invalid`
- `version_incomplete`
- `version_in_use`
- `broker_still_running`
- `activation_timeout`
- `child_start_failed`

It exits non-zero without changing the pointer on resolution or activation
failure. Normal child stdout remains untouched because MCP and CLI protocols
depend on it.

## Testing

TDD coverage will prove:

1. A valid pointer resolves every allowlisted tool to the same version.
2. Missing, malformed, escaping, reparse-escaping, or unsupported pointers are
   rejected.
3. An incomplete version cannot be activated.
4. The old pointer remains byte-for-byte intact after every pre-commit failure.
5. Shared run leases block activation.
6. Concurrent activators serialize and publish one complete pointer.
7. A child continues using its resolved immutable version even when a later
   activation succeeds.
8. `--stop-running` targets only exact LocalAi paths and never Ollama or
   unrelated `dotnet` processes.
9. Codex, Claude, Git hook, and Python plans use the stable launcher.
10. Python distinguishes process, protocol, timeout, and JSON failures.
11. Existing broker startup, durable queue, and full solution tests remain
    green.

## Live migration and acceptance

The migration will:

1. Publish the verified source revision into a new immutable version directory.
2. Install the stable launcher without changing the active pointer.
3. Preview exact process and configuration changes.
4. Stop only the current LocalAi broker and MCP processes.
5. Atomically activate the new version.
6. Update only the existing LocalAi entries in Codex, Claude, the delegation
   wrapper, and installed LocalAi hooks.
7. Restart through normal client demand.
8. Verify `delegate.py discover`, CodeSearch status, and LocalLm status through
   the shared broker.
9. Confirm the running broker and MCP executable paths resolve to the activated
   immutable directory.

No local model generation is required for the acceptance test, and Ollama is
never called directly.
