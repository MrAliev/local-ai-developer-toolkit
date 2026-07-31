# Broker Protocol/Build Compatibility Design

**Issue:** [#6](https://github.com/MrAliev/local-ai-developer-toolkit/issues/6)

**Date:** 2026-07-31

**Status:** Approved

## Goal

Allow an installed broker and a development client to share the one machine-wide
broker when they implement the same explicit compatibility contract, even when
their assemblies live at different paths. Reject genuinely incompatible hosts
without weakening singleton ownership, durable FIFO ordering, runtime ACLs,
launcher activation safety, or full-VRAM and zero-offload enforcement.

## Scope

This change is limited to broker discovery, compatibility classification,
startup coordination, diagnostics, and the host-state contract. It does not
change queue scheduling, model routing, Ollama transport, residency policy,
translation policy, or launcher version ownership.

The Windows installer is a separate issue, branch, and PR.

## Compatibility Contract

`LocalAi.Contracts` owns one immutable broker compatibility value containing:

- `ProtocolVersion`: the machine-readable broker/client protocol version;
- `BuildCompatibilityId`: a stable compatibility-family identifier.

The build compatibility ID is not a commit SHA, assembly version, or filesystem
path. Compatible builds retain the same value. It changes only when runtime
semantics become incompatible despite the protocol shape remaining parseable.

`BrokerProcessState` advances to host-state schema 3 and publishes:

- process identity and heartbeat;
- protocol version;
- build compatibility ID;
- broker assembly path.

The assembly path remains diagnostic metadata and remains available to
`LocalAiProcessController` for determining whether a running broker belongs to
an activated version. It is not part of broker health compatibility.

## Client State Classification

The client reads host state into a classifier before deciding whether to start
anything:

- `CompatibleHealthy`: fresh heartbeat, matching process identity, and matching
  protocol/build compatibility. Reuse the host.
- `IncompatibleHealthy`: a fresh live host with an unsupported schema,
  protocol, or build compatibility family. Fail immediately with expected and
  actual compatibility details.
- `AbsentOrStale`: missing, malformed, stale, or no-longer-owned state. Enter
  synchronized startup.
- `StartingOrLockOwned`: another process is starting, or the attempted child
  lost `broker.lock` while a live owner is publishing state. Continue bounded
  observation without starting another broker.
- `FailedStart`: the attempted child exits before a compatible state appears,
  or publishes invalid startup state. Report the child exit code and last
  observed host-state classification.
- `TimedOut`: the bounded deadline expires. Report the last classification and
  startup observation rather than a generic readiness timeout.

A live legacy schema-2 broker is classified as incompatible, not absent. This
prevents a client from launching a doomed second process behind an owned lock.

## Startup Coordination

The existing runtime-root-derived named semaphore remains the client-side
single-start guard. The existing exclusive `broker.lock` remains the
machine-wide broker ownership primitive.

The startup abstraction returns an observable attempt rather than only a PID.
It must allow the client to determine whether the child is still running and,
if it has exited, obtain its exit code. Startup continues to use detached,
non-inherited standard streams.

After a start attempt, every observed host state is classified through the same
compatibility path. Losing `broker.lock` is successful only when the active
owner is compatible. An incompatible owner produces an immediate compatibility
error.

## Launcher and Security Invariants

- `LocalAiProcessController` continues to use the diagnostic assembly path to
  stop or block activation of processes owned by a version directory.
- `VersionLease`, activation mutexes, atomic `current.json` replacement, and
  active-version validation remain unchanged.
- `RuntimeAcl.Ensure` still runs before broker lock acquisition and host-state
  publication.
- No additional broker instance or direct Ollama access is introduced.
- FIFO queue semantics and full-VRAM/zero-offload model checks are unchanged.

## Error Model

Compatibility and startup failures use typed error codes suitable for CLI/MCP
diagnostics:

- `broker_incompatible`;
- `broker_start_failed`;
- `broker_start_timeout`.

Messages include safe compatibility values, process/exit information when
available, and the last classified state. They do not expose job contents,
credentials, or unrelated process command lines.

## TDD and Verification

Tests are written and observed failing before production changes. Required
coverage includes:

- compatible protocol/build IDs at different physical DLL paths;
- incompatible IDs at the same path;
- live legacy schema, stale, missing, and malformed `host.json`;
- concurrent compatible clients starting only one process;
- a child losing `broker.lock` to a compatible owner;
- a child losing the lock to an incompatible owner;
- early child exit and exit-code diagnostics;
- bounded timeout with the last observed state;
- launcher ownership by assembly path after compatibility stops using that path;
- unchanged activation lease, singleton, FIFO, ACL, full-VRAM, and zero-offload
  behavior.

Release verification:

```powershell
dotnet test LocalAi.slnx -c Release --nologo
git diff --check
git status --short
```

The work is delivered through one feature branch and one PR for issue #6.
