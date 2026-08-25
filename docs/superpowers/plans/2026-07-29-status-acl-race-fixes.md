# Status and ACL Race Fixes Implementation Plan

[Русская версия](2026-07-29-status-acl-race-fixes.ru.md)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make generation status follow the configured mainline ref and make Windows runtime ACL enforcement tolerate broker-owned paths that are atomically moved during inspection.

**Architecture:** Generation status will resolve the repository manifest's `DevRef` from the requested worktree and compare the base index against that ref, so no mainline checkout is required. Runtime ACL enforcement will keep strict validation for paths that still exist, but will skip a node only when an apply/read failure is paired with confirmed path disappearance; traversal will enumerate one directory at a time so a moved directory cannot invalidate a recursive iterator.

**Tech Stack:** .NET 10, xUnit v3, Git worktrees, Windows filesystem ACL APIs.

---

### Task 1: Resolve base staleness from the configured mainline ref

**Files:**
- Modify: `tests/CodeSearch.Tests/SearchServiceStatusTests.cs`
- Modify: `src/CodeSearch.Core/Search/SearchService.cs`

- [x] **Step 1: Write the failing status test**

Add a test that creates `dev` and `feature` refs without a dedicated `dev` worktree, publishes a generation from the `dev` commit while `Index.Root` points at the feature checkout, and saves a repository manifest with `DevRef = "refs/heads/dev"`.

```csharp
[Fact]
public void Generation_status_tracks_the_manifest_mainline_ref()
{
    // Publish the base from dev while the only checkout is on feature.
    // The feature HEAD differs, but the configured dev ref still equals the base.
    Assert.False(new SearchService().Status(_root).CommitDrifted);

    // Move the configured dev ref forward without creating a dev worktree.
    Git("branch", "-f", "dev", "feature");
    Assert.True(new SearchService().Status(_root).CommitDrifted);
}
```

- [x] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/CodeSearch.Tests/CodeSearch.Tests.csproj --no-restore --filter "FullyQualifiedName~Generation_status_tracks_the_manifest_mainline_ref"
```

Expected: FAIL because `Status` compares the base against the feature checkout's HEAD instead of `manifest.DevRef`.

- [x] **Step 3: Implement mainline commit resolution**

Add `CurrentBaseCommit` in `SearchService` and use it for `IndexStatus.CurrentCommit`.

```csharp
private static string CurrentBaseCommit(
    string workingRoot,
    CodeIndex index,
    string workingCommit)
{
    if (string.IsNullOrWhiteSpace(index.GenerationId))
    {
        return SameRoot(index.Root, workingRoot)
            ? workingCommit
            : RepoLocator.GitCommit(index.Root);
    }

    var identity = RuntimeIndexLayout.Inspect(workingRoot);
    var manifest = new RepositoryManifestStore(identity.RepositoryRuntimeRoot).Read();
    if (manifest is null ||
        !string.Equals(manifest.RepositoryId, index.RepositoryId, StringComparison.Ordinal))
    {
        return index.GitCommit;
    }

    return RepoLocator.GitOutput(
        workingRoot,
        $"rev-parse --verify {manifest.DevRef}^{{commit}}")
        ?? index.GitCommit;
}
```

- [x] **Step 4: Run the focused and project tests and verify GREEN**

Run:

```powershell
dotnet test tests/CodeSearch.Tests/CodeSearch.Tests.csproj --no-restore
```

Expected: all CodeSearch tests pass with no new warnings.

### Task 2: Tolerate broker moves during ACL enforcement

**Files:**
- Modify: `tests/LocalAi.Broker.Tests/RuntimeAclTests.cs`
- Modify: `src/LocalAi.Broker/RuntimeAcl.cs`

- [x] **Step 1: Write the failing ACL race test**

Add a test that moves a queued job directory to `archive` from the injected ACL apply callback and throws the same `InvalidOperationException` shape produced by Windows error 3.

```csharp
[Fact]
public void Ensure_ignores_a_job_directory_moved_during_acl_application()
{
    // Create jobs/job-1 with a request file.
    // While Ensure applies the ACL to jobs/job-1, move it to archive/job-1 and throw.
    // Ensure must finish because the source path is confirmed gone.
}
```

Also retain strictness with a test proving that the same exception is propagated while the target still exists.

- [x] **Step 2: Run focused tests and verify RED**

Run:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeAclTests"
```

Expected: the moved-directory test fails with `InvalidOperationException`.

- [x] **Step 3: Implement disappearance-aware apply/validate**

Wrap the per-path operation and suppress only `IOException` or `InvalidOperationException` when `File.GetAttributes` confirms the target has disappeared.

```csharp
private void ApplyAndValidate(
    string path,
    string currentUserSid,
    IReadOnlySet<string> expected)
{
    try
    {
        var isDirectory = Directory.Exists(path);
        _applyExactAcl(path, isDirectory, false, currentUserSid, AdministratorsSid);
        ValidateSnapshot(path, _readAclSnapshot(path), expected);
    }
    catch (Exception exception)
        when (exception is IOException or InvalidOperationException &&
              HasDisappeared(path))
    {
    }
}
```

Replace `SearchOption.AllDirectories` with a one-directory-at-a-time iterator that catches only `DirectoryNotFoundException` for a directory that was moved before its children were listed.

- [x] **Step 4: Run focused and project tests and verify GREEN**

Run:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --no-restore
```

Expected: all broker tests pass, including strict propagation for an existing target.

### Task 3: Verify the complete solution and live reproductions

**Files:**
- Verify only; no additional source changes expected.

- [x] **Step 1: Run full automated verification**

Run:

```powershell
dotnet test LocalAi.slnx --no-restore
```

Expected: all projects build and all tests pass with zero failures and no new warnings.

- [x] **Step 2: Publish a temporary build and reproduce both live scenarios**

Run the new `codesearch status` against `R:\IntelWash` and verify `Base status: current` while its exact overlay remains current. Start a sync/query workload and connect a second client repeatedly; no Windows error 3 may occur.

- [x] **Step 3: Review the complete diff**

Run:

```powershell
git diff --check
git diff --stat
git status --short
```

Expected: only the paired plan, two production files, and their two test files are changed; no generated binaries, runtime data, or unrelated formatting are tracked.
