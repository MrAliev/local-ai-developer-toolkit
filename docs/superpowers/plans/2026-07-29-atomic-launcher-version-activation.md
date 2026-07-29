# Atomic Launcher Version Activation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route every LocalAi consumer through one stable launcher and atomically activate complete immutable versions without allowing mixed client/broker binaries.

**Architecture:** Add a BCL-only launcher that holds a shared file lease while a resolved versioned child runs and requires an exclusive lease for activation. Store the active immutable directory in an atomically replaced `current.json`, publish broker assembly identity in `host.json`, and migrate Codex, Claude, Git hooks, and the Python delegation wrapper to the stable launcher command.

**Tech Stack:** .NET 10, C#, xUnit v3, `System.Diagnostics.Process`, `System.Text.Json`, Windows file sharing and atomic rename, Python 3 `unittest`.

---

## File structure

### New launcher production files

- `src/LocalAi.Launcher/LocalAi.Launcher.csproj` — BCL-only executable project.
- `src/LocalAi.Launcher/Program.cs` — parse `run` and `activate`, print stable errors.
- `src/LocalAi.Launcher/LauncherLayout.cs` — canonical install paths and tool allowlist.
- `src/LocalAi.Launcher/VersionPointer.cs` — strict pointer model and atomic persistence.
- `src/LocalAi.Launcher/VersionResolver.cs` — confined immutable-version resolution.
- `src/LocalAi.Launcher/VersionLease.cs` — shared run and exclusive activation leases.
- `src/LocalAi.Launcher/ToolRunner.cs` — inherited-stdio child execution.
- `src/LocalAi.Launcher/VersionActivator.cs` — validation, process shutdown, and pointer commit.
- `src/LocalAi.Launcher/LocalAiProcessController.cs` — exact-path process selection.

### New launcher tests

- `tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj`
- `tests/LocalAi.Launcher.Tests/GlobalUsings.cs`
- `tests/LocalAi.Launcher.Tests/VersionResolverTests.cs`
- `tests/LocalAi.Launcher.Tests/VersionLeaseTests.cs`
- `tests/LocalAi.Launcher.Tests/VersionActivatorTests.cs`
- `tests/LocalAi.Launcher.Tests/ToolRunnerTests.cs`
- `tests/LocalAi.Launcher.Tests/LocalAiProcessControllerTests.cs`

### Existing LocalAi files

- `LocalAi.slnx` — include launcher and tests.
- `src/LocalAi.Contracts/BrokerContracts.cs` — broker assembly identity in process state.
- `src/LocalAi.Broker/Program.cs` — publish identity in `host.json`.
- `src/LocalAi.Broker.Client/BrokerProcess.cs` — reuse only a matching broker assembly.
- `tests/LocalAi.Broker.Tests/BrokerProcessTests.cs` — matching/mismatched-version coverage.
- `src/LocalAi.Cli/ClientCommand.cs` — stable launcher command and arguments.
- `src/LocalAi.Cli/HookInstaller.cs` — write launcher command prefix.
- `src/LocalAi.Cli/Program.cs` — require launcher provenance for hook installation.
- `tests/LocalAi.IntegrationTests/ClientRegistrationTests.cs`
- `tests/LocalAi.IntegrationTests/HookInstallerTests.cs`
- `README.md` and `README.ru.md` — synchronized publication/activation guidance.

### Delegation wrapper files

- `C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\delegate.py`
- `C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\local_models\ollama_client.py`
- `C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\tests\test_ollama_client.py`
- `C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\tests\test_delegate.py`

## Task 1: Add strict current-version resolution

**Files:**

- Create the launcher project, layout, pointer, resolver, and resolver tests listed above.
- Modify `LocalAi.slnx`.

- [ ] **Step 1: Add the test project and failing resolver tests**

Use a temporary install root with:

```text
bin/
  current.json
  versions/
    v1/
      localai.exe
      codesearch.exe
      codesearch-mcp.exe
      locallm-mcp.exe
      LocalAi.Broker.dll
      LocalAi.Contracts.dll
```

Add tests with these assertions:

```csharp
[Theory]
[InlineData("localai", "localai.exe")]
[InlineData("codesearch", "codesearch.exe")]
[InlineData("codesearch-mcp", "codesearch-mcp.exe")]
[InlineData("locallm-mcp", "locallm-mcp.exe")]
public void Resolves_every_allowlisted_tool_from_one_version(
    string tool,
    string executable)
{
    var layout = TestInstall.CreateComplete("v1");
    layout.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

    var resolved = new VersionResolver(layout.BinRoot).Resolve(tool);

    Assert.Equal("v1", resolved.Version);
    Assert.Equal(
        Path.Combine(layout.VersionsRoot, "v1", executable),
        resolved.ExecutablePath);
}

[Theory]
[InlineData("""{"schemaVersion":2,"version":"v1"}""")]
[InlineData("""{"schemaVersion":1,"version":".."}""")]
[InlineData("""{"schemaVersion":1,"version":"sub\\v1"}""")]
[InlineData("""{"schemaVersion":1,"version":"C:\\escape"}""")]
public void Rejects_unsupported_or_escaping_pointer(string json)
{
    var layout = TestInstall.CreateComplete("v1");
    layout.WriteCurrent(json);

    var error = Assert.Throws<LauncherException>(
        () => new VersionResolver(layout.BinRoot).Resolve("localai"));

    Assert.Contains(
        error.Code,
        new[] { "current_pointer_invalid", "version_path_invalid" });
}

[Fact]
public void Rejects_incomplete_version()
{
    var layout = TestInstall.CreateComplete("v1");
    File.Delete(Path.Combine(layout.VersionsRoot, "v1", "LocalAi.Broker.dll"));
    layout.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

    var error = Assert.Throws<LauncherException>(
        () => new VersionResolver(layout.BinRoot).Resolve("localai"));

    Assert.Equal("version_incomplete", error.Code);
}
```

- [ ] **Step 2: Run the focused tests and confirm RED**

```powershell
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~VersionResolverTests"
```

Expected: compilation fails because the launcher types do not exist.

- [ ] **Step 3: Implement the minimal strict model and resolver**

Use this public contract:

```csharp
public sealed record VersionPointer(int SchemaVersion, string Version);

public sealed record ResolvedTool(
    string Version,
    string VersionDirectory,
    string ExecutablePath);

public sealed class LauncherException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
```

`LauncherLayout.RequiredFiles` must contain exactly:

```csharp
[
    "localai.exe",
    "codesearch.exe",
    "codesearch-mcp.exe",
    "locallm-mcp.exe",
    "LocalAi.Broker.dll",
    "LocalAi.Contracts.dll"
]
```

Resolve with `Path.GetFullPath`, `Path.GetRelativePath`, and
`FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)` where a reparse point
exists. Reject any final path outside `versions`. Deserialize with
`UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow`,
`AllowDuplicateProperties = false`, and require schema `1`.
Retain the existing broker catalog-loading test as the proof that
`LocalAi.Broker.dll` contains the embedded routing catalog.

- [ ] **Step 4: Verify GREEN**

Run the command from Step 2.

Expected: all `VersionResolverTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add LocalAi.slnx src/LocalAi.Launcher tests/LocalAi.Launcher.Tests
git commit -m "feat(launcher): resolve immutable LocalAi versions"
```

## Task 2: Hold a version lease for the full child lifetime

**Files:**

- Create `VersionLease.cs`, `ToolRunner.cs`, `Program.cs`.
- Add `VersionLeaseTests.cs` and `ToolRunnerTests.cs`.

- [ ] **Step 1: Write failing lease and runner tests**

Prove that multiple shared leases coexist, an exclusive lease cannot be
obtained while a shared lease is held, and the runner retains the lease until
the injected child completes:

```csharp
[Fact]
public void Shared_run_lease_blocks_exclusive_activation_lease()
{
    var path = Path.Combine(_root, "current.lock");
    using var first = VersionLease.AcquireShared(path);
    using var second = VersionLease.AcquireShared(path);

    var error = Assert.Throws<LauncherException>(
        () => VersionLease.AcquireExclusive(
            path,
            TimeSpan.Zero,
            TimeProvider.System));

    Assert.Equal("version_in_use", error.Code);
}

[Fact]
public async Task Runner_forwards_exact_tool_arguments_and_exit_code()
{
    var child = new FakeChildProcess(exitCode: 17);
    var runner = new ToolRunner(child.Start);

    var exitCode = await runner.RunAsync(
        @"C:\LocalAi\bin\versions\v1\localai.exe",
        ["native", "tags"],
        TestContext.Current.CancellationToken);

    Assert.Equal(17, exitCode);
    Assert.Equal(["native", "tags"], child.Arguments);
    Assert.False(child.RedirectedStandardIo);
}
```

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~VersionLeaseTests|FullyQualifiedName~ToolRunnerTests"
```

Expected: tests fail because lease and runner behavior is missing.

- [ ] **Step 3: Implement shared/exclusive leases**

Create the lock file once, then open shared handles with:

```csharp
new FileStream(
    lockPath,
    FileMode.OpenOrCreate,
    FileAccess.ReadWrite,
    FileShare.ReadWrite);
```

Acquire an exclusive handle with the same mode and `FileShare.None`, retrying
until the supplied timeout expires. Map exhaustion to `version_in_use`.

- [ ] **Step 4: Implement inherited-stdio child execution**

Use:

```csharp
var startInfo = new ProcessStartInfo(executablePath)
{
    UseShellExecute = false,
    RedirectStandardInput = false,
    RedirectStandardOutput = false,
    RedirectStandardError = false,
    CreateNoWindow = true
};
foreach (var argument in arguments)
{
    startInfo.ArgumentList.Add(argument);
}
startInfo.Environment["LOCALAI_LAUNCHER_PATH"] = Environment.ProcessPath;
startInfo.Environment["LOCALAI_ACTIVE_VERSION"] = version;
```

Wait for exit, kill only that child tree when cancellation is requested, return
its exit code, and release the shared lease only after `WaitForExitAsync`.

- [ ] **Step 5: Wire the `run` command**

`Program.cs` accepts only:

```text
localai-launcher run <allowlisted-tool> [arguments...]
```

It resolves the launcher directory from `AppContext.BaseDirectory`, treats its
parent as `bin`, acquires the shared lease before pointer resolution, and writes
errors only to stderr as `<code>: <message>`.

- [ ] **Step 6: Verify GREEN and commit**

Run the command from Step 2, then:

```powershell
git add src/LocalAi.Launcher tests/LocalAi.Launcher.Tests
git commit -m "feat(launcher): lease active versions during execution"
```

## Task 3: Activate atomically and stop only matching LocalAi processes

**Files:**

- Create `VersionActivator.cs`, `LocalAiProcessController.cs`.
- Add `VersionActivatorTests.cs`, `LocalAiProcessControllerTests.cs`.
- Modify broker state files and tests listed in the file structure.

- [ ] **Step 1: Write RED tests for activation rollback and serialization**

Add:

```csharp
[Fact]
public void Incomplete_candidate_leaves_pointer_byte_for_byte_unchanged()
{
    var layout = TestInstall.CreateComplete("v1");
    layout.CreateIncomplete("v2");
    layout.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
    var before = File.ReadAllBytes(layout.CurrentPath);

    var error = Assert.Throws<LauncherException>(
        () => layout.Activator().Activate("v2", stopRunning: false));

    Assert.Equal("version_incomplete", error.Code);
    Assert.Equal(before, File.ReadAllBytes(layout.CurrentPath));
}

[Fact]
public async Task Concurrent_activators_commit_one_complete_pointer()
{
    var layout = TestInstall.CreateComplete("v1", "v2", "v3");
    layout.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

    await Task.WhenAll(
        Task.Run(() => layout.Activator().Activate("v2", false)),
        Task.Run(() => layout.Activator().Activate("v3", false)));

    var pointer = VersionPointerStore.Read(layout.CurrentPath);
    Assert.Contains(pointer.Version, new[] { "v2", "v3" });
    Assert.DoesNotContain(".tmp", Directory.GetFiles(layout.BinRoot));
}
```

- [ ] **Step 2: Write RED tests for exact process selection**

Use injected snapshots:

```csharp
var snapshots = new[]
{
    new ProcessSnapshot(10, started, v1CodeSearchMcp, null),
    new ProcessSnapshot(11, started, dotnet, v1BrokerDll),
    new ProcessSnapshot(12, started, ollamaExe, null),
    new ProcessSnapshot(13, started, dotnet, unrelatedDll),
    new ProcessSnapshot(14, started, v2LocalLmMcp, null)
};

var selected = controller.SelectOwnedByVersion(v1Directory, snapshots);

Assert.Equal([10, 11], selected.Select(process => process.ProcessId));
```

- [ ] **Step 3: Write RED broker identity tests**

Extend `BrokerProcessState` to:

```csharp
public sealed record BrokerProcessState(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset HeartbeatAtUtc,
    int SchemaVersion,
    string BrokerAssemblyPath);
```

Add a test where PID/start/heartbeat match but `BrokerAssemblyPath` points to
another version. Assert one replacement start. Add a matching-path test that
asserts zero starts. Existing schema-1 state must be unhealthy.

- [ ] **Step 4: Confirm all new tests are RED**

```powershell
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~VersionActivatorTests|FullyQualifiedName~LocalAiProcessControllerTests"

dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~BrokerProcessTests"
```

Expected: launcher tests do not compile and broker identity assertions fail.

- [ ] **Step 5: Publish and validate broker assembly identity**

Set state schema to `2`. `LocalAi.Broker/Program.cs` publishes:

```csharp
var brokerAssemblyPath = Path.GetFullPath(typeof(BrokerHost).Assembly.Location);
var owner = new BrokerProcessState(
    process.Id,
    startedAt,
    DateTimeOffset.UtcNow,
    2,
    brokerAssemblyPath);
```

`BrokerProcess.CreateDefault` passes its own canonical broker assembly path into
the health check. Reuse requires schema `2`, fresh heartbeat, matching PID/start,
and equal canonical assembly paths using the platform path comparison.

- [ ] **Step 6: Implement exact process control**

The controller may stop only:

- an executable physically below the current immutable version directory; or
- the PID/start-time from fresh schema-2 `host.json` whose
  `BrokerAssemblyPath` is physically below that directory.

Use `Process.Kill(entireProcessTree: true)` and bounded `WaitForExit`. Never
select by process name alone. Reject stale host state before touching its PID.

- [ ] **Step 7: Implement atomic pointer commit**

Serialize to a unique temporary file in `bin`, write with
`FileOptions.WriteThrough`, call `Flush(flushToDisk: true)`, then:

```csharp
File.Move(temporaryPath, currentPath, overwrite: true);
```

Hold a named machine-wide activation mutex across candidate validation, optional
process stop, exclusive-lease acquisition, replacement, and read-back.
Revalidate the candidate after acquiring the lease. Delete only the task's
unique temporary file in `finally`.

- [ ] **Step 8: Wire `activate`**

Accept:

```text
localai-launcher activate <version>
localai-launcher activate <version> --stop-running
```

Use a 15-second process-stop timeout and a 15-second exclusive-lease timeout.
Return `version_in_use`, `broker_still_running`, or `activation_timeout`
without modifying the previous pointer.

- [ ] **Step 9: Verify GREEN and commit**

Run both commands from Step 4.

```powershell
git add src/LocalAi.Launcher tests/LocalAi.Launcher.Tests `
  src/LocalAi.Contracts/BrokerContracts.cs `
  src/LocalAi.Broker/Program.cs `
  src/LocalAi.Broker.Client/BrokerProcess.cs `
  tests/LocalAi.Broker.Tests/BrokerProcessTests.cs
git commit -m "feat(launcher): activate versions atomically"
```

## Task 4: Register every LocalAi consumer through the launcher

**Files:**

- Modify client, hook, and integration-test files listed above.

- [ ] **Step 1: Write failing registration tests**

Assert:

```csharp
var plan = ClientCommand.Plan(@"C:\LocalAi\bin");

Assert.Equal(
    @"C:\LocalAi\bin\launcher\localai-launcher.exe",
    plan.CodeSearch.Command);
Assert.Equal(["run", "codesearch-mcp"], plan.CodeSearch.Arguments);
Assert.Equal(["run", "locallm-mcp"], plan.LocalLm.Arguments);
Assert.Contains(
    "args = [\"run\", \"codesearch-mcp\"]",
    plan.CodexTomlSections[0]);
Assert.Contains(
    "-- \"C:\\LocalAi\\bin\\launcher\\localai-launcher.exe\" run codesearch-mcp",
    plan.ClaudeCommands);
```

Update hook tests to assert:

```text
"C:/LocalAi/bin/launcher/localai-launcher.exe" run localai hook post-commit
```

Add a test that hook installation without `LOCALAI_LAUNCHER_PATH` is rejected
instead of recording a versioned `localai.exe`.

- [ ] **Step 2: Confirm RED**

```powershell
dotnet test tests/LocalAi.IntegrationTests/LocalAi.IntegrationTests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~ClientRegistrationTests|FullyQualifiedName~HookInstallerTests"
```

Expected: direct-binary assertions fail.

- [ ] **Step 3: Implement command-plus-arguments registrations**

Introduce:

```csharp
public sealed record ClientToolRegistration(
    string Command,
    IReadOnlyList<string> Arguments);
```

Store `CodeSearch` and `LocalLm` registrations in
`ClientRegistrationPlan`. Produce TOML with `command` plus `args`, and Claude
commands with the same launcher and tool arguments. Preserve
`AppliesClientConfiguration: false`.

- [ ] **Step 4: Make hooks stable**

Change `HookInstaller.Install` to accept:

```csharp
HookInstaller.Install(
    commonDirectory,
    launcherPath,
    ["run", "localai"]);
```

Quote the executable and every fixed prefix argument independently before
appending `hook <event> --root ...`. `LocalAi.Cli/Program.cs` obtains
`LOCALAI_LAUNCHER_PATH`; if it is absent, print an actionable error and exit `2`
without modifying hooks.

- [ ] **Step 5: Verify GREEN and commit**

Run the command from Step 2.

```powershell
git add src/LocalAi.Cli tests/LocalAi.IntegrationTests
git commit -m "feat(cli): register clients through stable launcher"
```

## Task 5: Fix delegation command resolution and error classification

**Files:**

- Modify the four delegation wrapper files listed above.

- [ ] **Step 1: Write failing Python tests**

Add:

```python
def test_broker_command_uses_stable_launcher_prefix(self) -> None:
    client = OllamaClient(
        cli_command=(
            r"C:\LocalAi\bin\launcher\localai-launcher.exe",
            "run",
            "localai",
        )
    )
    with patch("subprocess.run") as run:
        run.return_value = CompletedProcess(
            args=[],
            returncode=0,
            stdout='{"models":[]}',
            stderr="",
        )
        client.tags()

    self.assertEqual(
        run.call_args.args[0][:5],
        [
            r"C:\LocalAi\bin\launcher\localai-launcher.exe",
            "run",
            "localai",
            "native",
            "tags",
        ],
    )

def test_nonzero_broker_exit_is_not_ollama_unavailable(self) -> None:
    client = OllamaClient(cli_command=("launcher.exe", "run", "localai"))
    with patch("subprocess.run") as run:
        run.return_value = CompletedProcess(
            args=[],
            returncode=1,
            stdout="",
            stderr="current_pointer_invalid: malformed",
        )
        with self.assertRaisesRegex(
            LocalAiProcessError,
            "current_pointer_invalid",
        ):
            client.tags()

def test_invalid_broker_stdout_is_protocol_error(self) -> None:
    client = OllamaClient(cli_command=("launcher.exe", "run", "localai"))
    with patch("subprocess.run") as run:
        run.return_value = CompletedProcess(
            args=[],
            returncode=0,
            stdout="not-json",
            stderr="",
        )
        with self.assertRaises(OllamaProtocolError):
            client.tags()
```

Add a `delegate.py` test asserting `_client()` contains the stable launcher
prefix and no `bin\localai.exe`.

- [ ] **Step 2: Confirm RED**

```powershell
Push-Location C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts
python -m unittest tests.test_ollama_client tests.test_delegate
Pop-Location
```

Expected: `cli_command` and `LocalAiProcessError` are missing.

- [ ] **Step 3: Implement command-prefix execution**

Change the constructor to:

```python
def __init__(
    self,
    base_url: str = "http://127.0.0.1:11434",
    timeout_seconds: float = 300.0,
    cli_command: tuple[str, ...] | None = None,
) -> None:
    self._cli_command = cli_command
```

Build broker commands with:

```python
command = [*self._cli_command, "native", operation]
```

Add `LocalAiProcessError`. For non-zero exit, normalize control characters,
limit stderr to 2,048 characters, and raise that type. Map invalid UTF-8/JSON
stdout to `OllamaProtocolError`; reserve `OllamaUnavailable` for failure to
start the launcher executable.

`delegate.py` uses:

```python
cli_command=(
    r"C:\Users\Mr.Aliev\tools\LocalAi\bin\launcher\localai-launcher.exe",
    "run",
    "localai",
)
```

- [ ] **Step 4: Verify GREEN**

Run the command from Step 2, then:

```powershell
python -m unittest discover `
  -s C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\tests `
  -t C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts
```

Expected: all delegation wrapper tests pass.

The wrapper is outside the LocalAi repository, so do not include it in a LocalAi
Git commit. Report its exact diff separately.

## Task 6: Document and publish a complete candidate version

**Files:**

- Modify `README.md` and `README.ru.md`.
- Create only ignored artifacts below `bin\versions\<new-version>` and
  `bin\launcher`.

- [ ] **Step 1: Add synchronized documentation**

Document:

- immutable version publication;
- stable launcher registrations;
- `current.json` schema;
- `activate <version>` and `--stop-running`;
- rollback by activating a previously verified directory;
- no direct Ollama access;
- no deletion of historical versions during activation.

- [ ] **Step 2: Verify documentation-only properties**

```powershell
git diff --check
rg -n "localai-launcher|current.json|activate" README.md README.ru.md
```

Expected: both files cover all three terms, remain CRLF, and are UTF-8 without
BOM.

- [ ] **Step 3: Run complete source verification before publishing**

```powershell
dotnet test LocalAi.slnx --configuration Release
```

Expected: every project builds and every test passes with zero failures.

- [ ] **Step 4: Publish into a fresh immutable directory**

Use the implementation commit's short SHA as `<new-version>`. Publish each
executable project into a fresh task-specific temporary directory, combine the
outputs, verify all required files, then copy once into:

```text
C:\Users\Mr.Aliev\tools\LocalAi\bin\versions\<new-version>
```

Publish the launcher separately into a fresh temporary directory and install it
into `bin\launcher` only while no launcher process exists. Do not modify
`current.json` in this step.

Required publish commands:

```powershell
dotnet publish src/LocalAi.Cli/LocalAi.Cli.csproj -c Release --no-restore
dotnet publish src/CodeSearch.Cli/CodeSearch.Cli.csproj -c Release --no-restore
dotnet publish src/CodeSearch.Mcp/CodeSearch.Mcp.csproj -c Release --no-restore
dotnet publish src/LocalLm.Mcp/LocalLm.Mcp.csproj -c Release --no-restore
dotnet publish src/LocalAi.Launcher/LocalAi.Launcher.csproj -c Release --no-restore
```

- [ ] **Step 5: Commit documentation**

```powershell
git add README.md README.ru.md
git commit -m "docs: explain atomic LocalAi activation"
```

## Task 7: Perform the one-time live migration and acceptance

**Files/state:**

- `C:\Users\Mr.Aliev\tools\LocalAi\bin\current.json`
- `C:\Users\Mr.Aliev\.codex\config.toml`
- `C:\Users\Mr.Aliev\.claude.json`
- existing installed LocalAi Git hooks
- LocalAi broker/MCP processes only

- [ ] **Step 1: Print an exact read-only migration preview**

Show:

- old and candidate version directories and SHA-256 hashes;
- required candidate artifacts;
- current `host.json` PID/start/assembly identity;
- exact LocalAi broker and MCP processes to stop;
- exact Codex and Claude entries before/after;
- exact hooks containing a direct LocalAi path;
- the unchanged Ollama process list.

Abort if any resolved mutation target is outside the paths listed above.

- [ ] **Step 2: Install and verify the initial pointer**

If `current.json` is absent, stop only the exact directly registered
`caed45c` broker/MCP processes identified by the preview, then run:

```powershell
bin\launcher\localai-launcher.exe activate caed45c
bin\launcher\localai-launcher.exe run localai native tags
```

Expected: the first command atomically creates the pointer and the second exits
`0` through a newly demand-started `caed45c` broker.

- [ ] **Step 3: Atomically activate the candidate**

```powershell
bin\launcher\localai-launcher.exe activate <new-version> --stop-running
```

Expected: only exact old LocalAi broker/MCP processes stop, `current.json`
contains `<new-version>`, and Ollama remains untouched.

- [ ] **Step 4: Apply only the previewed stable registrations**

Update Codex and Claude LocalAi MCP entries to:

```text
command = C:\Users\Mr.Aliev\tools\LocalAi\bin\launcher\localai-launcher.exe
args = run codesearch-mcp
```

and:

```text
command = C:\Users\Mr.Aliev\tools\LocalAi\bin\launcher\localai-launcher.exe
args = run locallm-mcp
```

Reinstall only LocalAi-managed hooks through
`localai-launcher run localai hooks install`. Preserve chained non-LocalAi hooks.

- [ ] **Step 5: Verify the original failure path**

```powershell
python C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\delegate.py discover
```

Expected: exit `0`, installed models JSON, no `OllamaUnavailable`, and a broker
process whose assembly path belongs to `<new-version>`.

- [ ] **Step 6: Verify every stable entry point**

Run:

```powershell
bin\launcher\localai-launcher.exe run localai native tags
bin\launcher\localai-launcher.exe run codesearch status --root C:\Users\Mr.Aliev\tools\LocalAi
```

After Codex and Claude restart, call CodeSearch status and LocalLm model status
from both clients. Confirm all processes and `host.json` use `<new-version>` and
all requests share `%LOCALAPPDATA%\LocalAi`.

- [ ] **Step 7: Fresh final verification**

```powershell
dotnet test LocalAi.slnx --configuration Release
python -m unittest discover `
  -s C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\tests `
  -t C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts
git diff --check
git status --short
```

Expected: all .NET and Python tests pass, diff checks are clean, repository
changes are intentional, and installed ignored artifacts are reported
separately.

- [ ] **Step 8: Independent self-review**

Review:

- path confinement and reparse-point handling;
- lease lifetime and atomic replacement;
- exact process ownership checks;
- stdout cleanliness for MCP;
- absence of direct Ollama calls;
- Codex/Claude/Python agreement;
- paired English/Russian documentation.

Fix any actionable finding with a new RED/GREEN cycle before declaring
completion. Do not push or create a pull request without separate authorization.
