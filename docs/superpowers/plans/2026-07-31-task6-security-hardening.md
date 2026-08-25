# Task 6 Security Hardening Implementation Plan

[Русская версия](2026-07-31-task6-security-hardening.ru.md)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the Task 6 activation, layout, pointer-concurrency, trusted-launcher, and immutable-version safety findings without introducing a runtime dependency on the launcher executable project.

**Architecture:** Put the canonical activation mutex, `current.lock` lease, and strict bounded pointer snapshot/CAS contract in `LocalAi.Contracts`, which is already referenced by Launcher and Installer.Core. Launcher alone mutates `current.json`; Installer.Core obtains atomic protected layout leases, publishes immutable versions once, locks the verified stable launcher and ancestors during process execution, and performs CAS rollback through the new launcher before restoring the old launcher.

**Tech Stack:** C#/.NET 10, Windows handle APIs, xUnit v3, SHA-256, `IProcessRunner`.

---

### Task 1: Shared activation lease and launcher CAS

**Files:**
- Create: `src/LocalAi.Contracts/Activation/ActivationCoordinator.cs`
- Create: `src/LocalAi.Contracts/Activation/CurrentPointerSnapshot.cs`
- Modify: `src/LocalAi.Launcher/VersionActivator.cs`
- Modify: `src/LocalAi.Launcher/LauncherProgram.cs`
- Test: `tests/LocalAi.Launcher.Tests/VersionActivatorTests.cs`
- Test: `tests/LocalAi.Launcher.Tests/LauncherProgramTests.cs`

- [ ] **Step 1: Write failing shared-CAS tests**

```csharp
var expected = CurrentPointerExpectation.ExactHash(SHA256.HashData(before));
CreateActivator(install).Activate("v2", stopRunning: true, expected);
Assert.Equal("v2", new VersionResolver(install.BinRoot).ReadCurrent().Version);
```

Add missing-pointer, wrong-hash, same-version raw rewrite, duplicate/unknown CLI option, and mutually-exclusive expectation cases.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj --no-restore --filter "FullyQualifiedName~VersionActivatorTests|FullyQualifiedName~LauncherProgramTests"
```

Expected: compile failures for missing expectation/coordinator types.

- [ ] **Step 3: Implement shared lease/snapshot and launcher CAS**

```csharp
using var lease = ActivationCoordinator.AcquireExclusive(binRoot, timeout);
var actual = CurrentPointerSnapshot.ReadLocked(lease, maximumBytes: 4096);
expectation.Validate(actual);
WritePointerAtomically(version);
```

CLI accepts exactly one of `--if-current-missing` or `--if-current-sha256 <64 uppercase hex>` plus optional `--stop-running`; mismatch throws stable `current_pointer_changed`.

- [ ] **Step 4: Run GREEN and commit**

```powershell
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj --no-restore
git add src/LocalAi.Contracts/Activation src/LocalAi.Launcher tests/LocalAi.Launcher.Tests
git commit -m "fix(installer): coordinate pointer activation with CAS"
```

### Task 2: Atomic protected installation layout lease

**Files:**
- Replace: `src/LocalAi.Installer.Core/Activation/InstallationLayout.cs`
- Create: `src/LocalAi.Installer.Core/Activation/InstallationLayoutLease.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs`

- [ ] **Step 1: Write failing layout-race tests**

```csharp
using var lease = InstallationLayoutLease.Acquire(layout);
Assert.ThrowsAny<IOException>(() => Directory.Move(layout.LauncherDirectory, racedPath));
```

Cover fresh creator collision, ancestor reparse, files under `versions`, file-shaped reserved entries, unsafe version names, and concurrent identity drift.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --no-restore --filter FullyQualifiedName~LocalAiPackageInstallerTests
```

Expected: missing lease type or unsafe race succeeds.

- [ ] **Step 3: Implement handle-relative layout acquisition**

```csharp
using var lease = InstallationLayoutLease.Acquire(layout);
lease.Revalidate();
using var temporary = lease.CreateVersionTemporary();
temporary.PublishAbsent(version);
```

Use native create/open with no reparse traversal, protected ACLs, retained identities, canonical containment, and exact recognized `bin`/`installer/backups` shapes while allowing unrelated runtime directories under `LocalAi`.

- [ ] **Step 4: Run GREEN and commit**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --no-restore --filter FullyQualifiedName~LocalAiPackageInstallerTests
git add src/LocalAi.Installer.Core/Activation tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs
git commit -m "fix(installer): lease protected installation layout"
```

### Task 3: Trusted launcher handoff and immutable orphan policy

**Files:**
- Modify: `src/LocalAi.Installer.Core/Activation/LocalAiPackageInstaller.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs`

- [ ] **Step 1: Write failing handoff and no-delete tests**

```csharp
runner.OnRun = launcherPath => AssertWriteDeleteAndAncestorRenameBlocked(launcherPath);
var result = await installer.InstallAsync(package, layout, cancellationToken);
Assert.True(Directory.Exists(result.VersionPath));
Assert.True(result.InactivePublishedVersionRetained);
```

Cover before-start cancellation, timeout/cancel/termination after start, exact process path/args, locked launcher identity, ancestor rename/reparse attempts, and no recursive delete after publication.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --no-restore --filter FullyQualifiedName~LocalAiPackageInstallerTests
```

Expected: old cleanup deletes the published version or launcher replacement succeeds during runner execution.

- [ ] **Step 3: Implement retained handle handoff**

```csharp
using var trustedLauncher = lease.LockLauncher(expectedMetadata);
trustedLauncher.Revalidate();
var process = await runner.RunAsync(trustedLauncher.CanonicalPath, casArguments, timeout, token);
trustedLauncher.Revalidate();
```

Never recursively delete a published version; only identity-proven unpublished temporaries may be removed.

- [ ] **Step 4: Run GREEN and commit**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --no-restore --filter FullyQualifiedName~LocalAiPackageInstallerTests
git add src/LocalAi.Installer.Core/Activation tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs
git commit -m "fix(installer): lock trusted launcher handoff"
```

### Task 4: CAS recovery ordering and indeterminate outcomes

**Files:**
- Modify: `src/LocalAi.Installer.Core/Activation/LocalAiPackageInstaller.cs`
- Test: `tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs`

- [ ] **Step 1: Write failing recovery-order tests**

```csharp
Assert.Equal(newLauncherBytes, runner.Calls[1].ObservedLauncherBytes);
Assert.Contains("--if-current-sha256", runner.Calls[1].Arguments);
Assert.Equal(LocalAiPackageInstallStatus.Indeterminate, thirdPointerResult.Status);
```

Cover rollback through the new launcher before old-launcher restoration, exact raw prior pointer recovery, unrelated v3 refusal, same-version byte drift, and fresh pointer-created manual recovery.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --no-restore --filter FullyQualifiedName~LocalAiPackageInstallerTests
```

Expected: old launcher is restored too early or unrelated pointers are overwritten.

- [ ] **Step 3: Implement CAS recovery state machine**

```csharp
var actual = activationLease.ReadPointer();
if (!actual.IsExpectedPostFailure) return Indeterminate(actual);
await RunNewLauncherAsync("activate", prior.Version, "--if-current-sha256", actual.Sha256Hex);
VerifyExactPointer(prior);
RestorePriorLauncherAtomically();
```

If the prior pointer never changed, restore only the launcher. Never directly write/delete `current.json`.

- [ ] **Step 4: Run GREEN and commit**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --no-restore
git add src/LocalAi.Installer.Core/Activation tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs
git commit -m "fix(installer): recover activation with pointer CAS"
```

### Task 5: Final gates

**Files:**
- Modify only if a failing gate receives a RED regression test first.

- [ ] **Step 1: Run complete verification**

```powershell
dotnet build LocalAi.slnx -c Release --no-restore --nologo
dotnet test LocalAi.slnx -c Release --no-build --nologo
dotnet publish src/LocalAi.Installer/LocalAi.Installer.csproj -c Release -r win-x64 --self-contained true --no-restore --nologo
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~LocalAiPackageInstallerTests|FullyQualifiedName~StagingRootSecurityTests"
git diff --check
git status --short
```

- [ ] **Step 2: Review spec and commits**

```powershell
git log --oneline 8e9a5a1..HEAD
git diff --check 8e9a5a1..HEAD
```

Expected: separate fix commits, clean worktree, zero failures, and only capability-dependent reparse skips.
