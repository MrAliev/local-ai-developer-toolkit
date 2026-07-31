# Windows Interactive Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a self-contained Windows 10/11 x64 WPF installer that diagnoses prerequisites, installs consented dependencies, verifies and activates LocalAi releases, recommends and validates local models through the shared FIFO broker, and safely configures supported agents.

**Architecture:** `LocalAi.Installer.Core` owns immutable plans and every side effect behind narrow interfaces; `LocalAi.Installer` is a thin WPF shell whose view models render plans and collect explicit consent. Existing `VersionActivator`, `BrokerLocalModelClient`, and LocalAi contracts remain authoritative for activation and model work, while installer-owned journals make LocalAi/configuration mutations resumable and reversible.

**Tech Stack:** .NET 10, C# 14, WPF, xUnit v3, `System.Text.Json`, `HttpClient`, Windows DXGI/WinTrust interop, existing LocalAi launcher/broker/contracts.

---

## Scope and invariants

- Work only in `codex/windows-installer`.
- One feature branch and one PR for issue #9.
- Production behavior follows a witnessed RED test.
- Default install root is `%LOCALAPPDATA%\LocalAi`; never overwrite an unrecognized layout.
- Do not invoke Ollama HTTP endpoints or the `ollama` executable for discovery, download, or model work. Detect an Ollama installation from Windows uninstall/file metadata; submit model status, pull, and preflight only through `ILocalModelClient`.
- Preserve the broker singleton, durable FIFO, runtime ACL, protocol/build compatibility, immutable activation, full-VRAM, and zero-offload guarantees.
- Do not read or log credential values. Agent adapters operate on supported structural regions and managed instruction blocks, create byte-for-byte backups, use atomic writes, and verify read-back.
- External dependency installs are consented and journaled but are not automatically uninstalled during rollback.

## File map

### Core project

- `src/LocalAi.Installer.Core/LocalAi.Installer.Core.csproj` — core library and references to launcher/contracts/LocalLm.
- `src/LocalAi.Installer.Core/Abstractions/IProcessRunner.cs` — bounded process execution without shell interpolation.
- `src/LocalAi.Installer.Core/Abstractions/IInstallerFileSystem.cs` — installer-specific atomic file/directory operations.
- `src/LocalAi.Installer.Core/Abstractions/IReleaseClient.cs` — release manifest/package transport.
- `src/LocalAi.Installer.Core/Planning/InstallerPlan.cs` — immutable reviewed plan and explicit consent snapshot.
- `src/LocalAi.Installer.Core/Diagnosis/EnvironmentDiagnosis.cs` — OS, disk, network, dependency, GPU, installation, and agent snapshots.
- `src/LocalAi.Installer.Core/Diagnosis/WindowsEnvironmentDetector.cs` — production Windows detector.
- `src/LocalAi.Installer.Core/Dependencies/DependencyCatalog.cs` — exact supported package IDs and version policy.
- `src/LocalAi.Installer.Core/Dependencies/WingetDependencyInstaller.cs` — exact, consent-gated WinGet invocation.
- `src/LocalAi.Installer.Core/Releases/ReleaseManifest.cs` — strict signed manifest model.
- `src/LocalAi.Installer.Core/Releases/ReleasePackageVerifier.cs` — HTTPS, signature, SHA-256, Authenticode, and layout gates.
- `src/LocalAi.Installer.Core/Activation/LocalAiPackageInstaller.cs` — fresh version staging and existing launcher activation.
- `src/LocalAi.Installer.Core/Models/ModelRecommendationEngine.cs` — deterministic VRAM tiers.
- `src/LocalAi.Installer.Core/Models/BrokerModelInstaller.cs` — broker-only pull and preflight.
- `src/LocalAi.Installer.Core/Agents/ManagedInstructionBlock.cs` — uniquely marked Markdown block editing.
- `src/LocalAi.Installer.Core/Agents/CodexConfigurationAdapter.cs` — supported Codex TOML sections and `~/.codex/AGENTS.md`.
- `src/LocalAi.Installer.Core/Agents/ClaudeConfigurationAdapter.cs` — supported Claude user MCP JSON and `~/.claude/CLAUDE.md`.
- `src/LocalAi.Installer.Core/Transactions/InstallerJournal.cs` — strict append/replace journal snapshots.
- `src/LocalAi.Installer.Core/Transactions/InstallerExecutor.cs` — idempotent plan execution.
- `src/LocalAi.Installer.Core/Transactions/RollbackService.cs` — reverse completed installer-owned steps.
- `src/LocalAi.Installer.Core/Diagnostics/RedactedDiagnosticReport.cs` — metadata-only report.

### WPF project

- `src/LocalAi.Installer/LocalAi.Installer.csproj` — `net10.0-windows`, WPF, self-contained `win-x64`.
- `src/LocalAi.Installer/App.xaml`, `App.xaml.cs` — composition root.
- `src/LocalAi.Installer/MainWindow.xaml`, `MainWindow.xaml.cs` — wizard host.
- `src/LocalAi.Installer/ViewModels/ObservableObject.cs` — minimal binding base.
- `src/LocalAi.Installer/ViewModels/InstallerWizardViewModel.cs` — navigation and immutable plan assembly.
- `src/LocalAi.Installer/ViewModels/*PageViewModel.cs` — one view model per approved wizard page.
- `src/LocalAi.Installer/Views/*Page.xaml` — view-only pages.
- `src/LocalAi.Installer/Resources/Strings.xaml`, `Strings.ru.xaml` — English/Russian UI strings.

### Tests and release

- `tests/LocalAi.Installer.Core.Tests/*` — core unit tests and fakes.
- `tests/LocalAi.Installer.Tests/*` — WPF view-model tests without UI automation.
- `tests/LocalAi.Installer.IntegrationTests/*` — temporary-root fake-process/fake-release/fake-broker scenarios.
- `.github/workflows/windows-installer.yml` — build/test/self-contained publish and signed-manifest gates.
- `docs/windows-installer.md`, `docs/windows-installer.ru.md` — paired operator/user documentation.

## Task 1: Add installer projects to the solution

**Files:**
- Create: `src/LocalAi.Installer.Core/LocalAi.Installer.Core.csproj`
- Create: `src/LocalAi.Installer/LocalAi.Installer.csproj`
- Create: `src/LocalAi.Installer/Program.cs`
- Create: `tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj`
- Create: `tests/LocalAi.Installer.Tests/LocalAi.Installer.Tests.csproj`
- Create: `tests/LocalAi.Installer.IntegrationTests/LocalAi.Installer.IntegrationTests.csproj`
- Modify: `LocalAi.slnx`

- [ ] **Step 1: Add a failing solution-membership test**

Create `tests/LocalAi.Installer.Core.Tests/SolutionShapeTests.cs` that loads `LocalAi.slnx` from the repository root and asserts all five new project paths are present.

- [ ] **Step 2: Run RED**

Run:

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj -c Release --nologo
```

Expected: FAIL because the projects/solution entries do not exist.

- [ ] **Step 3: Add minimal projects**

Core references:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\LocalAi.Contracts\LocalAi.Contracts.csproj" />
    <ProjectReference Include="..\LocalAi.Launcher\LocalAi.Launcher.csproj" />
    <ProjectReference Include="..\LocalLm.Core\LocalLm.Core.csproj" />
  </ItemGroup>
</Project>
```

WPF properties:

```xml
<TargetFramework>net10.0-windows</TargetFramework>
<OutputType>WinExe</OutputType>
<UseWPF>true</UseWPF>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<SelfContained>true</SelfContained>
```

Use the repository's existing xUnit v3 versions in both test projects.
Add a minimal `[STAThread] static void Main()` in `Program.cs` so the `WinExe`
scaffold builds before Task 11 replaces it with the WPF composition root.

- [ ] **Step 4: Run GREEN and solution build**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj -c Release --nologo
dotnet build LocalAi.slnx -c Release --nologo
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add LocalAi.slnx src/LocalAi.Installer.Core src/LocalAi.Installer tests/LocalAi.Installer.Core.Tests tests/LocalAi.Installer.Tests tests/LocalAi.Installer.IntegrationTests
git commit -m "build(installer): add Windows installer projects"
```

## Task 2: Define immutable plans and consent

**Files:**
- Create: `src/LocalAi.Installer.Core/Planning/InstallerPlan.cs`
- Create: `src/LocalAi.Installer.Core/Planning/InstallerPlanBuilder.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/InstallerPlanTests.cs`

- [ ] **Step 1: Write RED tests**

Cover:

```csharp
[Fact]
public void Build_requires_explicit_consent_for_every_selected_external_change();

[Fact]
public void Plan_snapshots_collections_and_cannot_change_after_review();

[Fact]
public void Model_and_agent_choices_are_independent();
```

The desired API is:

```csharp
public sealed record InstallerPlan(
    Guid PlanId,
    DateTimeOffset CreatedAtUtc,
    EnvironmentDiagnosis Diagnosis,
    IReadOnlyList<DependencyAction> Dependencies,
    LocalAiPackageAction Package,
    IReadOnlyList<ModelInstallAction> Models,
    IReadOnlyList<AgentConfigurationAction> Agents,
    IReadOnlyList<NonTransactionalEffect> NonTransactionalEffects);
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj -c Release --filter FullyQualifiedName~InstallerPlanTests
```

Expected: compile failure because planning types are absent.

- [ ] **Step 3: Implement immutable snapshots and validation**

Copy incoming collections to read-only arrays; reject duplicate action IDs, unconsented selected actions, blank package versions, and a plan with an unsupported diagnosis.

- [ ] **Step 4: Run GREEN**

Run the focused tests and the core test project.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Installer.Core/Planning tests/LocalAi.Installer.Core.Tests/InstallerPlanTests.cs
git commit -m "feat(installer): define immutable execution plans"
```

## Task 3: Diagnose the Windows environment without model bypasses

**Files:**
- Create: `src/LocalAi.Installer.Core/Abstractions/IProcessRunner.cs`
- Create: `src/LocalAi.Installer.Core/Diagnosis/EnvironmentDiagnosis.cs`
- Create: `src/LocalAi.Installer.Core/Diagnosis/WindowsEnvironmentDetector.cs`
- Create: `src/LocalAi.Installer.Core/Diagnosis/WindowsGpuProbe.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/WindowsEnvironmentDetectorTests.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/WindowsGpuProbeTests.cs`

- [ ] **Step 1: Write RED tests**

Required cases:

- Windows 10/11 x64 accepted; other OS/architecture rejected.
- Free disk and network status are represented, not guessed.
- WinGet and Git versions come from bounded process results.
- Ollama is detected only through uninstall registry/file-version metadata; the process runner never receives `ollama`.
- Existing `%LOCALAPPDATA%\LocalAi\bin\current.json` is classified as compatible, absent, or unrecognized.
- Codex and Claude detection records only executable/config paths and versions, never file contents.
- Multi-adapter snapshots retain dedicated local memory and exclude software adapters.

Use:

```csharp
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Run RED**

Run focused detector tests; expected compile failure.

- [ ] **Step 3: Implement minimal detectors**

Use `Environment.OSVersion`, `RuntimeInformation.OSArchitecture`, `DriveInfo`, explicit process argument lists, registry/file metadata, strict current-pointer JSON, and DXGI adapter enumeration. Do not use `Win32_VideoController.AdapterRAM` and do not invoke Ollama.

- [ ] **Step 4: Run GREEN on Windows**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj -c Release --filter "FullyQualifiedName~WindowsEnvironmentDetectorTests|FullyQualifiedName~WindowsGpuProbeTests"
```

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Installer.Core/Abstractions src/LocalAi.Installer.Core/Diagnosis tests/LocalAi.Installer.Core.Tests
git commit -m "feat(installer): diagnose Windows prerequisites"
```

## Task 4: Plan and execute consented dependency installation

**Files:**
- Create: `src/LocalAi.Installer.Core/Dependencies/DependencyCatalog.cs`
- Create: `src/LocalAi.Installer.Core/Dependencies/WingetDependencyInstaller.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/WingetDependencyInstallerTests.cs`

- [ ] **Step 1: Write RED tests**

Verify:

- exact IDs `Git.Git` and the approved Ollama package ID;
- `--exact`, `--source winget`, `--silent`, architecture, and both agreement switches;
- no command without explicit action consent;
- refusal/cancellation/elevation/failure are distinct results;
- completed external packages are recorded as non-transactional and never automatically uninstalled;
- missing WinGet yields an official-installer offer rather than an invented command.

- [ ] **Step 2: Run RED**

Expected: missing dependency types.

- [ ] **Step 3: Implement minimal catalog and runner**

Construct argument arrays, never shell command strings. Re-run detection after a successful external installer.

- [ ] **Step 4: Run GREEN**

Run focused tests and all core tests.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Installer.Core/Dependencies tests/LocalAi.Installer.Core.Tests/WingetDependencyInstallerTests.cs
git commit -m "feat(installer): install consented dependencies"
```

## Task 5: Verify signed LocalAi release packages

**Files:**
- Create: `src/LocalAi.Installer.Core/Abstractions/IReleaseClient.cs`
- Create: `src/LocalAi.Installer.Core/Releases/ReleaseManifest.cs`
- Create: `src/LocalAi.Installer.Core/Releases/ReleaseManifestVerifier.cs`
- Create: `src/LocalAi.Installer.Core/Releases/AuthenticodeVerifier.cs`
- Create: `src/LocalAi.Installer.Core/Releases/ReleasePackageVerifier.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/ReleasePackageVerifierTests.cs`

- [ ] **Step 1: Write RED tests**

Test strict JSON, duplicate/unknown fields, HTTPS-only URLs, ECDSA P-256 signature verification against an injected public key, SHA-256 mismatch, missing/invalid Authenticode when required, path traversal, symlinks/reparse points, duplicate ZIP entries, missing launcher/version files, and incompatible protocol/build metadata.

Desired manifest:

```csharp
public sealed record ReleaseManifest(
    int SchemaVersion,
    string ReleaseVersion,
    string VersionDirectory,
    string ProtocolBuildCompatibilityId,
    Uri PackageUri,
    long PackageSize,
    string PackageSha256,
    bool RequiresAuthenticode,
    IReadOnlyList<ManifestModel> Models);
```

- [ ] **Step 2: Run RED**

Expected: missing release verification types.

- [ ] **Step 3: Implement strict verification**

Canonicalize the unsigned manifest payload before signature verification, use fixed-time digest comparison, cap download/extraction sizes, reject entries outside the staging root, and validate `LauncherLayout.RequiredFiles`.

- [ ] **Step 4: Run GREEN**

Run focused tests and all core tests.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Installer.Core/Releases src/LocalAi.Installer.Core/Abstractions/IReleaseClient.cs tests/LocalAi.Installer.Core.Tests/ReleasePackageVerifierTests.cs
git commit -m "feat(installer): verify signed release packages"
```

## Task 6: Stage and atomically activate LocalAi

**Files:**
- Create: `src/LocalAi.Installer.Core/Activation/LocalAiPackageInstaller.cs`
- Create: `src/LocalAi.Installer.Core/Activation/InstallationLayout.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs`

- [ ] **Step 1: Write RED tests**

Cover absent install, compatible upgrade, existing immutable target, unrecognized layout refusal, copy failure before activation, launcher backup, `VersionActivator` invocation, pointer read-back, and rollback to the previous pointer.

- [ ] **Step 2: Run RED**

Expected: missing activation types.

- [ ] **Step 3: Implement minimal installer**

Copy a verified staging tree exactly once into `bin\versions\<version>`, validate it with `VersionResolver`, back up the stable launcher, and call the existing `VersionActivator`. Never update files inside an existing version directory.

- [ ] **Step 4: Run GREEN**

Run focused tests against temporary roots.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Installer.Core/Activation tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs
git commit -m "feat(installer): activate immutable LocalAi packages"
```

## Task 7: Recommend models from dedicated VRAM

**Files:**
- Create: `src/LocalAi.Installer.Core/Models/ModelRecommendationEngine.cs`
- Create: `src/LocalAi.Installer.Core/Models/ModelRecommendation.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/ModelRecommendationEngineTests.cs`

- [ ] **Step 1: Write RED tests**

Cover no GPU, one GPU, multi-GPU default selection, manual adapter selection, shared memory exclusion, runtime/context reserve, exact boundary behavior, Minimal/Recommended/Extended tiers, manual selection, and disabled over-budget choices with explanations.

- [ ] **Step 2: Run RED**

Expected: missing recommendation types.

- [ ] **Step 3: Implement pure deterministic policy**

Select one discrete adapter; never add VRAM across adapters. Compare signed-manifest estimates plus reserve against dedicated local bytes and mark results as estimates until preflight.

- [ ] **Step 4: Run GREEN**

Run focused tests.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Installer.Core/Models tests/LocalAi.Installer.Core.Tests/ModelRecommendationEngineTests.cs
git commit -m "feat(installer): recommend models from dedicated VRAM"
```

## Task 8: Download and preflight models only through the broker

**Files:**
- Create: `src/LocalAi.Installer.Core/Models/BrokerModelInstaller.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/BrokerModelInstallerTests.cs`

- [ ] **Step 1: Write RED tests**

Use a recording fake `ILocalModelClient` and prove:

- status, pull, and preflight are the only model calls;
- pulls use the signed catalog version;
- every selected model is preflighted with its selected context;
- failed full-VRAM/zero-offload proof rejects the model and offers a smaller context/model;
- cancellation stops subsequent work;
- no process or HTTP abstraction is reachable from this service.

- [ ] **Step 2: Run RED**

Expected: missing broker model installer.

- [ ] **Step 3: Implement using existing APIs**

Compose `BrokerLocalModelClient`/`ILocalModelClient`; rely on broker preflight output and existing rejection semantics. Do not add an Ollama transport to the installer.

- [ ] **Step 4: Run GREEN**

Run focused tests and `LocalLm.Tests`.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Installer.Core/Models/BrokerModelInstaller.cs tests/LocalAi.Installer.Core.Tests/BrokerModelInstallerTests.cs
git commit -m "feat(installer): install models through FIFO broker"
```

## Task 9: Preview and apply safe agent integrations

**Files:**
- Create: `src/LocalAi.Installer.Core/Agents/ManagedInstructionBlock.cs`
- Create: `src/LocalAi.Installer.Core/Agents/AgentConfigurationPlan.cs`
- Create: `src/LocalAi.Installer.Core/Agents/CodexConfigurationAdapter.cs`
- Create: `src/LocalAi.Installer.Core/Agents/ClaudeConfigurationAdapter.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/ManagedInstructionBlockTests.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/CodexConfigurationAdapterTests.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/ClaudeConfigurationAdapterTests.cs`

- [ ] **Step 1: Write RED tests**

Cover independently selected MCP/instructions/both/no-change; new and existing managed blocks; duplicate/malformed markers; supported/unknown/malformed TOML and JSON; preservation of unrelated bytes/values; credential-key redaction; exact preview; timestamped byte backup; optimistic concurrency hash; atomic write; read-back; and rollback.

Managed block markers:

```text
<!-- BEGIN LOCALAI MANAGED INSTRUCTIONS -->
Use only the shared LocalAi FIFO broker for local-model work.
Never access Ollama directly.
Require full-VRAM, zero-offload validation.
<!-- END LOCALAI MANAGED INSTRUCTIONS -->
```

Codex targets `~/.codex/config.toml` and `~/.codex/AGENTS.md`; Claude targets its supported user MCP JSON and `~/.claude/CLAUDE.md`. Unsupported layouts block writes.

- [ ] **Step 2: Run RED**

Expected: missing adapter types.

- [ ] **Step 3: Implement strict adapters**

Use `ClientCommand.Plan` for launcher commands/arguments. Parse only supported structural shapes, mutate only `codesearch`/`locallm` and the unique managed block, never emit existing credential values into previews or logs, and verify destination bytes after atomic replacement.

- [ ] **Step 4: Run GREEN**

Run all three focused test classes.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Installer.Core/Agents tests/LocalAi.Installer.Core.Tests/*ConfigurationAdapterTests.cs tests/LocalAi.Installer.Core.Tests/ManagedInstructionBlockTests.cs
git commit -m "feat(installer): configure supported agents safely"
```

## Task 10: Journal, resume, diagnostics, and rollback

**Files:**
- Create: `src/LocalAi.Installer.Core/Transactions/InstallerJournal.cs`
- Create: `src/LocalAi.Installer.Core/Transactions/InstallerExecutor.cs`
- Create: `src/LocalAi.Installer.Core/Transactions/RollbackService.cs`
- Create: `src/LocalAi.Installer.Core/Diagnostics/RedactedDiagnosticReport.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/InstallerExecutorTests.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/RollbackServiceTests.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/RedactedDiagnosticReportTests.cs`

- [ ] **Step 1: Write RED tests**

Cover strict journal schema, atomic journal updates, idempotent resume/rerun, completed-step skipping, failed-step retry policy, reverse-order rollback, pointer/config restoration verification, dependency/model non-transactional effects, rollback failure instructions, and diagnostics that omit prompts/jobs/tokens/credentials/config values.

- [ ] **Step 2: Run RED**

Expected: missing transaction types.

- [ ] **Step 3: Implement executor and rollback**

Persist a snapshot after every state transition under `%LOCALAPPDATA%\LocalAi\installer`; identify steps by stable IDs; store hashes and backup paths rather than sensitive contents.

- [ ] **Step 4: Run GREEN**

Run focused tests and all core tests.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Installer.Core/Transactions src/LocalAi.Installer.Core/Diagnostics tests/LocalAi.Installer.Core.Tests
git commit -m "feat(installer): add resumable transactions and rollback"
```

## Task 11: Build the WPF wizard and consent gates

**Files:**
- Create: `src/LocalAi.Installer/App.xaml`
- Create: `src/LocalAi.Installer/App.xaml.cs`
- Create: `src/LocalAi.Installer/MainWindow.xaml`
- Create: `src/LocalAi.Installer/MainWindow.xaml.cs`
- Create: `src/LocalAi.Installer/ViewModels/ObservableObject.cs`
- Create: `src/LocalAi.Installer/ViewModels/InstallerWizardViewModel.cs`
- Create: `src/LocalAi.Installer/ViewModels/DiagnosePageViewModel.cs`
- Create: `src/LocalAi.Installer/ViewModels/DependenciesPageViewModel.cs`
- Create: `src/LocalAi.Installer/ViewModels/PackagePageViewModel.cs`
- Create: `src/LocalAi.Installer/ViewModels/ModelsPageViewModel.cs`
- Create: `src/LocalAi.Installer/ViewModels/AgentIntegrationPageViewModel.cs`
- Create: `src/LocalAi.Installer/ViewModels/ReviewApplyPageViewModel.cs`
- Create: `src/LocalAi.Installer/ViewModels/FinishPageViewModel.cs`
- Create: `src/LocalAi.Installer/Views/*Page.xaml`
- Create: `src/LocalAi.Installer/Resources/Strings.xaml`
- Create: `src/LocalAi.Installer/Resources/Strings.ru.xaml`
- Test: `tests/LocalAi.Installer.Tests/InstallerWizardViewModelTests.cs`

- [ ] **Step 1: Write RED view-model tests**

Cover navigation order, blocking unsupported diagnosis, independent dependency consent, model-tier/manual selection, per-agent four-way choices, exact review rendering, final confirmation, cancellation, progress, rollback result, restart notice, and language switching. Do not instantiate dialogs or run UI automation.

- [ ] **Step 2: Run RED**

Expected: missing WPF/view-model types.

- [ ] **Step 3: Implement thin view models and views**

All policy remains in Core. Views use bindings and commands only; code-behind hosts navigation/window concerns. Present external/non-transactional effects before final confirmation.

- [ ] **Step 4: Run GREEN and build WPF**

```powershell
dotnet test tests/LocalAi.Installer.Tests/LocalAi.Installer.Tests.csproj -c Release --nologo
dotnet build src/LocalAi.Installer/LocalAi.Installer.csproj -c Release --nologo
```

- [ ] **Step 5: Commit**

```powershell
git add src/LocalAi.Installer tests/LocalAi.Installer.Tests
git commit -m "feat(installer): add interactive WPF wizard"
```

## Task 12: Add end-to-end fake integration scenarios

**Files:**
- Create: `tests/LocalAi.Installer.IntegrationTests/FakeProcessRunner.cs`
- Create: `tests/LocalAi.Installer.IntegrationTests/FakeReleaseServer.cs`
- Create: `tests/LocalAi.Installer.IntegrationTests/FakeLocalModelClient.cs`
- Create: `tests/LocalAi.Installer.IntegrationTests/InstallerScenarioTests.cs`

- [ ] **Step 1: Write RED scenarios**

Scenarios:

1. Clean temporary home with missing dependencies, consent, verified package, recommended models, agent choices, and successful finish.
2. Compatible upgrade with preserved historical version and rollback.
3. Invalid signature/digest/layout blocks before install mutation.
4. Model preflight rejection records no false success.
5. Concurrent config change blocks write.
6. Crash after activation resumes agent configuration without repeating dependencies/model pulls.

- [ ] **Step 2: Run RED**

Expected: orchestration gaps exposed by scenario assertions.

- [ ] **Step 3: Add only missing orchestration**

Wire production composition through injectable roots/endpoints/clients. Do not add test-only production methods.

- [ ] **Step 4: Run GREEN**

```powershell
dotnet test tests/LocalAi.Installer.IntegrationTests/LocalAi.Installer.IntegrationTests.csproj -c Release --nologo
```

- [ ] **Step 5: Commit**

```powershell
git add tests/LocalAi.Installer.IntegrationTests src/LocalAi.Installer.Core src/LocalAi.Installer
git commit -m "test(installer): cover complete installation scenarios"
```

## Task 13: Package, sign, and document the installer

**Files:**
- Create: `.github/workflows/windows-installer.yml`
- Create: `docs/windows-installer.md`
- Create: `docs/windows-installer.ru.md`
- Modify: `README.md`
- Modify: `README.ru.md`

- [ ] **Step 1: Add failing release-layout tests**

Add tests that validate self-contained `win-x64` output, required launcher payload, manifest schema, package digest, and absence of private signing material.

- [ ] **Step 2: Run RED**

Expected: workflow/package metadata absent.

- [ ] **Step 3: Implement release workflow and paired docs**

Workflow stages:

1. restore/build/test;
2. `dotnet publish src/LocalAi.Installer/LocalAi.Installer.csproj -c Release -r win-x64 --self-contained true`;
3. assemble versioned LocalAi payload;
4. generate canonical manifest;
5. sign through a repository secret or trusted signing service;
6. compute SHA-256 and Authenticode-sign Windows executables when credentials are configured;
7. upload unsigned CI artifacts only for pull requests and signed release assets only for protected tags.

Document prerequisites, choices, backup/rollback, diagnostics, broker-only model path, and manual recovery in English and Russian.

- [ ] **Step 4: Run GREEN and formatting checks**

```powershell
dotnet test LocalAi.slnx -c Release --nologo
dotnet publish src/LocalAi.Installer/LocalAi.Installer.csproj -c Release -r win-x64 --self-contained true --nologo
git diff --check
```

- [ ] **Step 5: Commit**

```powershell
git add .github/workflows/windows-installer.yml docs/windows-installer.md docs/windows-installer.ru.md README.md README.ru.md tests
git commit -m "docs(installer): add packaging and operating guide"
```

## Task 14: Final verification and release gates

**Files:**
- Modify only when a failing verification requires a TDD fix.

- [ ] **Step 1: Run the full clean gate**

```powershell
dotnet restore LocalAi.slnx
dotnet build LocalAi.slnx -c Release --no-restore --nologo
dotnet test LocalAi.slnx -c Release --no-build --nologo
dotnet publish src/LocalAi.Installer/LocalAi.Installer.csproj -c Release -r win-x64 --self-contained true --no-restore --nologo
git diff --check
git status --short
```

- [ ] **Step 2: Run controlled Windows smoke tests**

- clean VM without LocalAi prerequisites;
- existing compatible installation;
- representative GPU VRAM classes or deterministic snapshots;
- broker-only model pull/preflight with `size_vram == size`;
- Codex/Claude exact preview, backup, apply, read-back, and rollback using disposable test homes.

Do not write real user configs during automated validation.

- [ ] **Step 3: Review**

Check issue #9 acceptance criteria, singleton/FIFO/ACL/activation/full-VRAM/zero-offload guarantees, secrets/redaction, English/Russian parity, and the exact branch diff.

- [ ] **Step 4: Prepare one PR**

Push `codex/windows-installer` and open a single PR closing #9 only after all release gates pass. Do not publish a signed installer release until signing credentials and protected-tag workflow succeed.
