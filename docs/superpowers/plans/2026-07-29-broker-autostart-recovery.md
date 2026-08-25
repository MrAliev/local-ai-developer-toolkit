# Broker Auto-start Recovery Implementation Plan

[Русская версия](2026-07-29-broker-autostart-recovery.ru.md)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make LocalAi clients recover automatically when stale broker state points to a missing, reused, or inaccessible Windows process.

**Architecture:** Keep `EnsureRunningAsync` as the single auto-start entry point. Reject stale heartbeat data before probing the recorded PID, convert process-inspection `Win32Exception` failures into an unhealthy result, and retain the named semaphore as the single-start guard.

**Tech Stack:** .NET 10, xUnit v3, Windows process APIs, named semaphores.

---

### Task 1: Reproduce stale and inaccessible process state

**Files:**
- Modify: `tests/LocalAi.Broker.Tests/BrokerProcessTests.cs`

- [x] **Step 1: Add the stale-heartbeat regression test**

Add a test whose process probe throws `Win32Exception`, whose first broker state
has an expired heartbeat, and whose post-start state is healthy. Assert that the
stale state is rejected without probing and that the broker is started once.

- [x] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --no-restore --filter "FullyQualifiedName~Stale_heartbeat"
```

Expected: FAIL because the current implementation probes the stale PID and
propagates `Win32Exception`.

- [x] **Step 3: Add the inaccessible-process regression test**

Add a fresh-state test whose first process probe throws `Win32Exception` and
whose replacement state is healthy. Assert one replacement start and no escaped
exception.

- [x] **Step 4: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --no-restore --filter "FullyQualifiedName~Inaccessible_process"
```

Expected: FAIL because process-inspection failures currently escape.

### Task 2: Make health checks fail safely

**Files:**
- Modify: `src/LocalAi.Broker.Client/BrokerProcess.cs`

- [x] **Step 1: Reorder validation**

Reject an absent state, unsupported schema, or heartbeat older than five seconds
before invoking `_isRunning`.

- [x] **Step 2: Handle process-inspection failures**

Catch `Win32Exception` around `_isRunning` and return `false`, allowing the
existing synchronized startup path to recover.

- [x] **Step 3: Run focused broker tests and verify GREEN**

Run:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --no-restore --filter "FullyQualifiedName~BrokerProcessTests"
```

Expected: all broker-process tests pass with no warnings.

### Task 3: Verify and install

**Files:**
- Verify and publish only; no additional source changes expected.

- [x] **Step 1: Run complete automated verification**

Run:

```powershell
dotnet test LocalAi.slnx --no-restore
```

Expected: all projects build and all tests pass with zero failures and no new
warnings.

- [x] **Step 2: Review the full diff**

Run:

```powershell
git diff --check
git status --short
```

Expected: only the paired design and plan, `BrokerProcess`, and its tests are
changed.

- [x] **Step 3: Install and verify live recovery**

Publish the fixed LocalAi binaries, update Codex and Claude to use them, stop the
broker, leave a stale state pointing to an inaccessible PID, and run discovery.
Expected: one broker starts automatically and discovery succeeds.

- [x] **Step 4: Refresh Jira CodeSearch overlay**

Run the LocalAi CodeSearch synchronization for
`C:\Users\Mr.Aliev\plugins\jira-intelwash` and verify that status reports the
overlay as current.
