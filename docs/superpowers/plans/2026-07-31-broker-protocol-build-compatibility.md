# Broker Protocol/Build Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace broker assembly-path affinity with an explicit protocol/build compatibility contract and produce precise startup outcomes without weakening existing LocalAi guarantees.

**Architecture:** `LocalAi.Contracts` owns the host-state schema and current compatibility family. `BrokerProcess` classifies every fresh live host before starting anything, observes a detached child through a small start-attempt abstraction, and raises typed bootstrap errors. The assembly path remains in host state solely for launcher version ownership and diagnostics.

**Tech Stack:** .NET 10, C# records, strict `System.Text.Json`, Windows process APIs, xUnit v3, existing launcher/version and broker test infrastructure.

---

## File Structure

- Modify `src/LocalAi.Contracts/BrokerContracts.cs`: compatibility value, current contract constants, and host-state schema 3.
- Modify `src/LocalAi.Broker/Program.cs`: publish schema 3 and the current compatibility value.
- Create `src/LocalAi.Broker.Client/BrokerBootstrap.cs`: typed error, observation status, and start-attempt abstraction.
- Modify `src/LocalAi.Broker.Client/BrokerProcess.cs`: classify host states, reuse compatible paths, observe startup, and report precise failures.
- Create `src/LocalAi.Launcher/BrokerHostStateReader.cs`: strictly read fresh schema-3 host ownership metadata.
- Create `src/LocalAi.Launcher/Properties/AssemblyInfo.cs`: expose launcher internals only to its test assembly.
- Modify `src/LocalAi.Launcher/LocalAiProcessController.cs`: consume the reader while continuing path-based version ownership.
- Modify `tests/LocalAi.Broker.Tests/BrokerRuntimeStateStoreTests.cs`: verify the serialized compatibility contract.
- Modify `tests/LocalAi.Broker.Tests/BrokerProcessTests.cs`: all compatibility, lock-owner, failed-start, and timeout behavior.
- Modify `tests/LocalAi.Launcher.Tests/LocalAiProcessControllerTests.cs`: prove version ownership still uses assembly paths.
- Modify `README.md` and `README.ru.md`: explain compatibility and diagnostics.

### Task 1: Publish an Explicit Compatibility Contract

**Files:**
- Modify: `tests/LocalAi.Broker.Tests/BrokerRuntimeStateStoreTests.cs`
- Modify: `src/LocalAi.Contracts/BrokerContracts.cs`
- Modify: `src/LocalAi.Broker/Program.cs`

- [ ] **Step 1: Write the failing host-state contract test**

Replace schema-2 fixture construction with a helper and assert the new fields:

```csharp
private static BrokerProcessState CurrentState(int processId = 42) =>
    new(
        processId,
        DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow,
        BrokerCompatibilityContract.HostStateSchemaVersion,
        Path.GetFullPath("LocalAi.Broker.dll"),
        BrokerCompatibilityContract.Current);

[Fact]
public void Publish_includes_current_protocol_and_build_compatibility()
{
    using var fixture = new RuntimeStateFixture();
    var state = CurrentState();

    fixture.Store.Publish(state);

    var actual = JsonSerializer.Deserialize<BrokerProcessState>(
        File.ReadAllText(fixture.StatePath),
        LocalAiJson.Strict);
    Assert.Equal(BrokerCompatibilityContract.Current, actual!.Compatibility);
    Assert.Equal(3, actual.SchemaVersion);
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~BrokerRuntimeStateStoreTests"
```

Expected: compilation fails because `BrokerCompatibilityContract` and
`BrokerProcessState.Compatibility` do not exist.

- [ ] **Step 3: Add the minimal shared contract**

In `BrokerContracts.cs`, add:

```csharp
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerCompatibility(
    int ProtocolVersion,
    string BuildCompatibilityId);

public static class BrokerCompatibilityContract
{
    public const int HostStateSchemaVersion = 3;
    public const int ProtocolVersion = 1;
    public const string BuildCompatibilityId = "localai-broker-v1";

    public static BrokerCompatibility Current { get; } =
        new(ProtocolVersion, BuildCompatibilityId);

    public static bool IsCurrent(BrokerCompatibility? value) =>
        value is
        {
            ProtocolVersion: ProtocolVersion,
            BuildCompatibilityId: BuildCompatibilityId
        };
}
```

Extend the positional state record without removing the diagnostic path:

```csharp
public sealed record BrokerProcessState(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset HeartbeatAtUtc,
    int SchemaVersion,
    string BrokerAssemblyPath,
    BrokerCompatibility? Compatibility = null);
```

In `LocalAi.Broker/Program.cs`, construct the owner with schema 3 and
`BrokerCompatibilityContract.Current`.

- [ ] **Step 4: Update existing state-store fixtures**

Use `CurrentState()` in all four state-store tests. Preserve the existing atomic
replace, retry, ownership, and cleanup assertions.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the Task 1 command again. Expected: all `BrokerRuntimeStateStoreTests` pass.

- [ ] **Step 6: Commit**

```powershell
git add src/LocalAi.Contracts/BrokerContracts.cs src/LocalAi.Broker/Program.cs tests/LocalAi.Broker.Tests/BrokerRuntimeStateStoreTests.cs
git commit -m "feat(broker): publish compatibility contract"
```

### Task 2: Reuse Compatible Brokers Across Assembly Paths

**Files:**
- Modify: `tests/LocalAi.Broker.Tests/BrokerProcessTests.cs`
- Create: `src/LocalAi.Broker.Client/BrokerBootstrap.cs`
- Modify: `src/LocalAi.Broker.Client/BrokerProcess.cs`

- [ ] **Step 1: Add a failing different-path compatibility test**

```csharp
[Fact]
public async Task Compatible_broker_at_another_assembly_path_is_reused()
{
    var now = DateTimeOffset.UtcNow;
    var starts = 0;
    var process = CreateProcess(
        _ => State(
            42,
            now,
            Path.GetFullPath("installed/LocalAi.Broker.dll"),
            BrokerCompatibilityContract.Current),
        _ => true,
        () => { starts++; return 99; },
        now);

    await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

    Assert.Equal(0, starts);
}
```

The test helper must pass a development assembly path in startup arguments, so
the assertion proves that state-path equality is no longer consulted.

- [ ] **Step 2: Run the test and verify RED**

Run:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~Compatible_broker_at_another_assembly_path"
```

Expected: FAIL because the current health check rejects the different path and
starts a replacement.

- [ ] **Step 3: Introduce bootstrap result types**

Create `BrokerBootstrap.cs`:

```csharp
namespace LocalAi.Broker.Client;

public sealed class BrokerBootstrapException(
    string code,
    string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal enum BrokerObservationStatus
{
    CompatibleHealthy,
    IncompatibleHealthy,
    AbsentOrStale,
    StartingOrLockOwned
}

internal sealed record BrokerObservation(
    BrokerObservationStatus Status,
    string Detail);
```

- [ ] **Step 4: Replace path health with compatibility classification**

In `BrokerProcess`, remove `_brokerAssemblyPath`, `_pathComparison`, and
`CanonicalizePath` from health decisions. Add an `Observe` method with this
order:

```csharp
private BrokerObservation Observe(BrokerProcessState? state)
{
    if (state is null)
        return new(BrokerObservationStatus.AbsentOrStale, "host state is absent or unreadable");
    if (_timeProvider.GetUtcNow() - state.HeartbeatAtUtc > TimeSpan.FromSeconds(5))
        return new(BrokerObservationStatus.AbsentOrStale, "host heartbeat is stale");
    if (!IsRunningSafely(state))
        return new(BrokerObservationStatus.AbsentOrStale, "host process is not the recorded owner");
    if (state.SchemaVersion != BrokerCompatibilityContract.HostStateSchemaVersion ||
        !BrokerCompatibilityContract.IsCurrent(state.Compatibility))
        return new(
            BrokerObservationStatus.IncompatibleHealthy,
            CompatibilityDetail(state));
    if (string.IsNullOrWhiteSpace(state.BrokerAssemblyPath))
        return new(BrokerObservationStatus.IncompatibleHealthy, "host assembly path is missing");
    return new(
        BrokerObservationStatus.CompatibleHealthy,
        $"compatible host at '{state.BrokerAssemblyPath}'");
}
```

`IsRunningSafely` retains the current handled exception set. Keep
`BrokerAssemblyPath` only in safe diagnostic text.

- [ ] **Step 5: Reuse a compatible initial observation**

At the start of `EnsureRunningAsync`, return on `CompatibleHealthy` and throw a
`BrokerBootstrapException("broker_incompatible", detail)` on
`IncompatibleHealthy`. Start only for `AbsentOrStale`.

- [ ] **Step 6: Run the focused test and verify GREEN**

Run the Task 2 command again. Expected: PASS and `starts == 0`.

- [ ] **Step 7: Commit**

```powershell
git add src/LocalAi.Broker.Client/BrokerBootstrap.cs src/LocalAi.Broker.Client/BrokerProcess.cs tests/LocalAi.Broker.Tests/BrokerProcessTests.cs
git commit -m "fix(broker): reuse compatible hosts across paths"
```

### Task 3: Reject Live Incompatible and Legacy Hosts Explicitly

**Files:**
- Modify: `tests/LocalAi.Broker.Tests/BrokerProcessTests.cs`
- Modify: `src/LocalAi.Broker.Client/BrokerProcess.cs`

- [ ] **Step 1: Add failing incompatible-host tests**

Add one test with the same path but `new BrokerCompatibility(2, "other")`, and
one with fresh schema 2 and `Compatibility: null`. Each test must assert:

```csharp
var error = await Assert.ThrowsAsync<BrokerBootstrapException>(
    () => process.EnsureRunningAsync(TestContext.Current.CancellationToken));
Assert.Equal("broker_incompatible", error.Code);
Assert.Contains("expected protocol=1", error.Message);
Assert.Equal(0, starts);
```

For the legacy case also assert the message contains `schema=2`.

- [ ] **Step 2: Run the tests and verify RED**

Run:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~Incompatible|FullyQualifiedName~Legacy"
```

Expected: at least one test fails because current diagnostics do not expose the
expected and actual contract.

- [ ] **Step 3: Implement deterministic compatibility diagnostics**

Implement `CompatibilityDetail` so its format includes:

```text
expected schema=3 protocol=1 build=localai-broker-v1; actual schema=<n> protocol=<n|missing> build=<value|missing>; broker path=<path|missing>
```

Never include command lines, environment variables, job data, or credentials.

- [ ] **Step 4: Run Task 3 tests and the full BrokerProcess test class**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~BrokerProcessTests"
```

Expected: all tests pass with no unexpected process starts.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Broker.Client/BrokerProcess.cs tests/LocalAi.Broker.Tests/BrokerProcessTests.cs
git commit -m "fix(broker): reject incompatible live hosts"
```

### Task 4: Observe Startup, Lock Ownership, and Early Failure

**Files:**
- Modify: `tests/LocalAi.Broker.Tests/BrokerProcessTests.cs`
- Modify: `src/LocalAi.Broker.Client/BrokerBootstrap.cs`
- Modify: `src/LocalAi.Broker.Client/BrokerProcess.cs`

- [ ] **Step 1: Add a reusable fake start attempt**

Add the production abstraction to `BrokerBootstrap.cs`:

```csharp
internal interface IBrokerStartAttempt : IDisposable
{
    int ProcessId { get; }

    bool TryGetExitCode(out int exitCode);
}
```

Then add the fake to `BrokerProcessTests.cs`:

```csharp
private sealed class FakeStartAttempt(int processId) : IBrokerStartAttempt
{
    private int? _exitCode;

    public int ProcessId { get; } = processId;

    public static FakeStartAttempt Running(int processId) => new(processId);

    public static FakeStartAttempt Exited(int processId, int exitCode)
    {
        var attempt = new FakeStartAttempt(processId);
        attempt._exitCode = exitCode;
        return attempt;
    }

    public bool TryGetExitCode(out int exitCode)
    {
        exitCode = _exitCode.GetValueOrDefault();
        return _exitCode.HasValue;
    }

    public void Dispose()
    {
    }
}
```

- [ ] **Step 2: Add failing lock-owner and failed-start tests**

Add:

- `Child_exiting_zero_reuses_compatible_lock_owner`: first read is absent; start
  returns exit 0; next read is a compatible live owner; expect success and one
  start.
- `Child_exiting_zero_rejects_incompatible_lock_owner`: same sequence with an
  incompatible owner; expect `broker_incompatible`.
- `Child_nonzero_exit_fails_without_waiting_for_timeout`: absent state and exit
  code 17; expect `broker_start_failed`, message containing `17`, and no delay.

- [ ] **Step 3: Run and verify RED**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~lock_owner|FullyQualifiedName~nonzero_exit"
```

Expected: compilation or assertion failure because startup returns only an
integer PID and the child cannot be observed.

- [ ] **Step 4: Implement the real process attempt**

In `BrokerBootstrap.cs`, add an internal `ProcessStartAttempt` that owns the
`Process`, returns its ID, safely checks `HasExited`, returns `ExitCode`, and
disposes the process handle. Reuse the existing handled process exception set.

Change `_start` to:

```csharp
private readonly Func<string, string, IBrokerStartAttempt> _start;
```

and make the production `Start` return a `ProcessStartAttempt`. Preserve
`CreateStartInfo` exactly so standard streams remain detached.

- [ ] **Step 5: Implement post-start observation**

Inside the polling loop:

1. classify current host state;
2. return for compatible;
3. throw `broker_incompatible` for incompatible;
4. if the child exited nonzero, throw
   `BrokerBootstrapException("broker_start_failed", ...)`;
5. if it exited zero, record `StartingOrLockOwned` and keep observing until a
   host appears or the deadline expires;
6. otherwise record `starting process <pid>`.

- [ ] **Step 6: Run Task 4 tests and verify GREEN**

Run the Task 4 command again, then the whole `BrokerProcessTests` class. Expected:
all pass.

- [ ] **Step 7: Commit**

```powershell
git add src/LocalAi.Broker.Client/BrokerBootstrap.cs src/LocalAi.Broker.Client/BrokerProcess.cs tests/LocalAi.Broker.Tests/BrokerProcessTests.cs
git commit -m "fix(broker): expose startup and lock outcomes"
```

### Task 5: Make Timeout and Invalid-State Diagnostics Actionable

**Files:**
- Modify: `tests/LocalAi.Broker.Tests/BrokerProcessTests.cs`
- Modify: `src/LocalAi.Broker.Client/BrokerProcess.cs`

- [ ] **Step 1: Replace the generic timeout test**

Assert the typed timeout and last observation:

```csharp
var error = await Assert.ThrowsAsync<BrokerBootstrapException>(
    () => process.EnsureRunningAsync(TestContext.Current.CancellationToken));
Assert.Equal("broker_start_timeout", error.Code);
Assert.Contains("last observation:", error.Message);
Assert.Contains("host state is absent or unreadable", error.Message);
```

Add a second test where a zero-exit child loses the lock but no fresh state ever
appears; the message must contain `lock owner did not publish compatible state`.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~timeout|FullyQualifiedName~lock_owner_did_not_publish"
```

Expected: FAIL because the current code throws `TimeoutException`.

- [ ] **Step 3: Implement typed bounded timeout**

Track a `lastObservation` string throughout the loop and replace the generic
exception with:

```csharp
throw new BrokerBootstrapException(
    "broker_start_timeout",
    $"LocalAi broker did not become ready within {_startupTimeout}; " +
    $"last observation: {lastObservation}.");
```

Keep cancellation precedence and the existing 50 ms polling interval.

- [ ] **Step 4: Run all BrokerProcess tests**

Run the full Task 3 command. Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Broker.Client/BrokerProcess.cs tests/LocalAi.Broker.Tests/BrokerProcessTests.cs
git commit -m "fix(broker): report bounded startup diagnostics"
```

### Task 6: Preserve Launcher Version Ownership with Schema 3

**Files:**
- Create: `src/LocalAi.Launcher/BrokerHostStateReader.cs`
- Create: `src/LocalAi.Launcher/Properties/AssemblyInfo.cs`
- Modify: `src/LocalAi.Launcher/LocalAiProcessController.cs`
- Modify: `tests/LocalAi.Launcher.Tests/LocalAiProcessControllerTests.cs`
- Modify: `tests/LocalAi.Launcher.Tests/VersionActivatorTests.cs`

- [ ] **Step 1: Add failing schema-3 reader and path-ownership tests**

Extend the existing selection test with two broker snapshots whose assembly
paths are under different version directories. Assert only the path below the
requested version is selected.

Add a temporary-runtime test that writes this strict schema-3 `host.json` and
asks `BrokerHostStateReader.ReadFreshAssemblyPath` for its path:

```json
{
  "ProcessId": 42,
  "StartedAtUtc": "2026-07-31T00:00:00+00:00",
  "HeartbeatAtUtc": "2026-07-31T00:00:01+00:00",
  "SchemaVersion": 3,
  "BrokerAssemblyPath": "C:\\LocalAi\\bin\\versions\\v1\\LocalAi.Broker.dll",
  "Compatibility": {
    "ProtocolVersion": 1,
    "BuildCompatibilityId": "localai-broker-v1"
  }
}
```

Inject a fixed `TimeProvider` at `00:00:02Z` and assert the exact assembly path.
Keep `Active_broker_without_run_lease_preserves_previous_pointer` unchanged as a
second regression guard.

- [ ] **Step 2: Run launcher tests and verify RED**

```powershell
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~LocalAiProcessControllerTests|FullyQualifiedName~Active_broker_without_run_lease"
```

Expected: compilation fails because `BrokerHostStateReader` does not exist.

- [ ] **Step 3: Implement the strict launcher host-state reader**

Create `BrokerHostStateReader.cs` with strict JSON options and these private
shapes:

```csharp
internal sealed record BrokerHostState(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset HeartbeatAtUtc,
    int SchemaVersion,
    string BrokerAssemblyPath,
    BrokerHostCompatibility? Compatibility);

internal sealed record BrokerHostCompatibility(
    int ProtocolVersion,
    string BuildCompatibilityId);
```

`ReadFreshAssemblyPath` returns a path only for schema 3, a non-empty assembly
path, non-null compatibility with positive protocol and non-empty build ID, and
a heartbeat no more than five seconds old. It catches only JSON, IO, and access
errors. It does not decide client compatibility.

Create `Properties/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("LocalAi.Launcher.Tests")]
```

Replace the private reader in `LocalAiProcessController` with
`BrokerHostStateReader.ReadFreshAssemblyPath`; process identity/start-time checks
remain in `CaptureSnapshots`.

- [ ] **Step 4: Run all launcher tests**

```powershell
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj -c Release --nologo
```

Expected: all launcher tests pass, including activation lease protections.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Launcher/BrokerHostStateReader.cs src/LocalAi.Launcher/Properties/AssemblyInfo.cs src/LocalAi.Launcher/LocalAiProcessController.cs tests/LocalAi.Launcher.Tests/LocalAiProcessControllerTests.cs tests/LocalAi.Launcher.Tests/VersionActivatorTests.cs
git commit -m "fix(launcher): retain schema-three broker ownership"
```

### Task 7: Document Compatibility and Verify the Whole Repository

**Files:**
- Modify: `README.md`
- Modify: `README.ru.md`

- [ ] **Step 1: Update paired documentation**

Document:

- `host.json` uses explicit protocol/build compatibility;
- assembly paths are diagnostic and launcher-ownership metadata;
- compatible installed and development clients share one broker;
- incompatible hosts fail with `broker_incompatible`;
- lock/start failures expose typed diagnostics;
- direct Ollama access remains unsupported.

Keep the English and Russian sections semantically identical and preserve UTF-8
without BOM plus Windows CRLF working-tree endings.

- [ ] **Step 2: Run focused broker and launcher suites**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~BrokerProcessTests|FullyQualifiedName~BrokerRuntimeStateStoreTests|FullyQualifiedName~RuntimeAclTests"
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj -c Release --nologo
```

Expected: zero failures.

- [ ] **Step 3: Run full Release verification**

```powershell
dotnet test LocalAi.slnx -c Release --nologo
```

Expected: zero failures; the known Windows symlink test may remain skipped.

- [ ] **Step 4: Verify scope and formatting**

```powershell
git diff --check
git status --short
git diff --stat main...HEAD
git diff main...HEAD -- src/LocalAi.Contracts src/LocalAi.Broker src/LocalAi.Broker.Client src/LocalAi.Launcher tests README.md README.ru.md docs/superpowers
```

Expected: only issue #6 contract, bootstrap, launcher-parser, tests, and paired
documentation changes.

- [ ] **Step 5: Commit documentation**

```powershell
git add README.md README.ru.md
git commit -m "docs: explain broker compatibility diagnostics"
```

- [ ] **Step 6: Perform live installed-vs-development verification**

Publish a candidate version through the existing launcher workflow, start its
broker, then run a development client from this worktree. Verify:

- the same broker PID remains active;
- `host.json` reports schema 3 and the current compatibility contract;
- no second broker owns or waits on `broker.lock`;
- one broker-backed read-only model/status request succeeds;
- `/api/ps` evidence, when a model is loaded, still reports
  `size_vram == size`.

Do not call Ollama directly. Use only the LocalAi launcher/client path.

- [ ] **Step 7: Prepare one PR for issue #6**

Re-run the full suite if the live verification caused any tracked change. Push
only `codex/issue-6-broker-compatibility` and create one PR referencing issue #6.
Do not include the Windows installer branch or files.
