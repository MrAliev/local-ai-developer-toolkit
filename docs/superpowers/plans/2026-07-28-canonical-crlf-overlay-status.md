# Canonical CRLF and Exact Overlay Status Implementation Plan

[Русская версия](2026-07-28-canonical-crlf-overlay-status.ru.md)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Canonicalize every indexed text file to Windows CRLF in memory and make status report the exact overlay requirement for generation-backed indexes.

**Architecture:** A focused `CanonicalIndexText` helper owns deterministic line-ending normalization and file reads. IndexBuilder and DirtyCorpusPolicy consume that helper, while SearchService computes overlay necessity from generation/tree/dirty identity and exposes it through IndexStatus. The generation normalization version is incremented so old and new vector corpora cannot mix.

**Tech Stack:** .NET 10, C#, xUnit v3, Git, LocalAi durable FIFO broker, CodeSearch binary index.

---

## File map

- Create `src/CodeSearch.Core/Indexing/CanonicalIndexText.cs`: canonical in-memory CRLF representation.
- Create `tests/CodeSearch.Tests/IndexBuilderNormalizationTests.cs`: base/overlay EOL regression tests.
- Create `tests/CodeSearch.Tests/SearchServiceStatusTests.cs`: exact generation overlay status tests.
- Modify `src/CodeSearch.Core/Indexing/IndexBuilder.cs`: canonical hashes and chunker input.
- Modify `src/CodeSearch.Core/Indexing/DirtyCorpusPolicy.cs`: canonical dirty-content hashes.
- Modify `src/CodeSearch.Core/Search/SearchService.cs`: explicit `RequiresOverlay`.
- Modify `src/CodeSearch.Cli/Program.cs`: render exact overlay before legacy path shortcut.
- Modify `src/LocalAi.Cli/CodeSearchSyncCommand.cs`: bump normalization version.
- Modify `tests/CodeSearch.Tests/DirtyCorpusPolicyTests.cs`: canonical dirty hash coverage.
- Modify `tests/CodeSearch.Tests/GenerationStoreTests.cs`: generation identity coverage.
- Modify `README.md` and `README.ru.md`: document CRLF canonicalization.

### Task 1: Canonical CRLF hashing and chunking

**Files:**
- Create: `src/CodeSearch.Core/Indexing/CanonicalIndexText.cs`
- Create: `tests/CodeSearch.Tests/IndexBuilderNormalizationTests.cs`
- Modify: `src/CodeSearch.Core/Indexing/IndexBuilder.cs`
- Modify: `src/CodeSearch.Core/Indexing/DirtyCorpusPolicy.cs`
- Modify: `tests/CodeSearch.Tests/DirtyCorpusPolicyTests.cs`

- [ ] **Step 1: Write failing content-hash tests**

Add tests proving LF, CRLF, lone CR, and mixed endings have the same hash, while a real text edit
changes it:

```csharp
[Fact]
public void Line_ending_styles_have_one_canonical_content_hash()
{
    var variants = new[] { "one\ntwo\n", "one\r\ntwo\r\n", "one\rtwo\r", "one\r\ntwo\n" };
    var hashes = variants.Select(ContentHash).Distinct().ToArray();
    Assert.Single(hashes);
}

[Fact]
public void Canonical_hash_still_detects_text_changes()
{
    Assert.NotEqual(ContentHash("one\ntwo\n"), ContentHash("one\nchanged\n"));
}
```

- [ ] **Step 2: Write a failing overlay test**

Build a base from LF text with a deterministic fake embedder, rewrite only EOL to CRLF, and build
an overlay:

```csharp
var baseResult = await builder.BuildAsync(_baseRoot, _basePath, cancellationToken: token);
File.WriteAllText(sourcePath, "line one\r\nline two\r\n");
var overlayResult = await builder.BuildOverlayAsync(_baseRoot, _basePath, _overlayPath, token);
Assert.Equal(0, overlayResult.FileCount);
Assert.Equal(0, overlayResult.FilesEmbedded);
```

Then change `line two` to `changed` and assert one overlay file is embedded.

- [ ] **Step 3: Run RED tests**

Run:

```powershell
dotnet test tests\CodeSearch.Tests\CodeSearch.Tests.csproj --configuration Release --filter "FullyQualifiedName~IndexBuilderNormalizationTests|FullyQualifiedName~DirtyCorpusPolicyTests"
```

Expected: FAIL because EOL-only variants currently produce different SHA-256 hashes and a non-empty
overlay.

- [ ] **Step 4: Implement the canonical text helper**

Create:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace CodeSearch.Core.Indexing;

internal static class CanonicalIndexText
{
    public static string Read(string path) => Normalize(File.ReadAllText(path));

    public static string Normalize(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    public static byte[] Hash(string content) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(content)));
}
```

Use `CanonicalIndexText.Read` in `IndexBuilder.HashFile` and `ChunkFiles`. Hash the canonical UTF-8
text in `DirtyCorpusPolicy.ComputeContentHash` and `ComputeWorkingContentHash`; keep the existing
path and file-boundary separators and the `<deleted>` marker.

- [ ] **Step 5: Run GREEN tests**

Run the command from Step 3.

Expected: PASS, with EOL-only overlay containing zero files and the real edit containing one.

### Task 2: Exact overlay requirement and status

**Files:**
- Create: `tests/CodeSearch.Tests/SearchServiceStatusTests.cs`
- Modify: `src/CodeSearch.Core/Search/SearchService.cs`
- Modify: `src/CodeSearch.Cli/Program.cs`

- [ ] **Step 1: Write failing status tests**

Create a temporary Git repository and a generation-backed base at its current tree. Verify these
states:

```csharp
var clean = service.Status(_root);
Assert.False(clean.RequiresOverlay);

File.WriteAllText(Path.Combine(_root, "A.cs"), "class Changed {}\r\n");
var dirty = service.Status(_root);
Assert.True(dirty.RequiresOverlay);
Assert.False(dirty.WorkingRootIsBase);
```

Save an exact overlay at `RuntimeIndexLayout.OverlayPath` and assert:

```csharp
var indexed = service.Status(_root);
Assert.True(indexed.RequiresOverlay);
Assert.True(indexed.Overlay.Exists);
```

- [ ] **Step 2: Run the RED test**

Run:

```powershell
dotnet test tests\CodeSearch.Tests\CodeSearch.Tests.csproj --configuration Release --filter FullyQualifiedName~SearchServiceStatusTests
```

Expected: FAIL because IndexStatus does not expose `RequiresOverlay` and same-root dirty worktrees
are classified as the base checkout.

- [ ] **Step 3: Implement explicit overlay necessity**

Add `bool RequiresOverlay` to `IndexStatus`. For generation-backed indexes compute it from the
exact identity:

```csharp
var identity = RuntimeIndexLayout.Inspect(workingRoot);
var requiresOverlay =
    !string.Equals(index.GitTree, identity.HeadTree, StringComparison.Ordinal) ||
    identity.DirtyHash is not null;
```

For legacy indexes retain `!SameRoot(index.Root, workingRoot)`. Make `WorkingRootIsBase` require
`!RequiresOverlay`.

In the CLI status renderer use:

```csharp
if (!status.RequiresOverlay)
{
    Console.WriteLine("Overlay:      not needed - worktree matches the clean base");
    return 0;
}

if (!status.Overlay.Exists)
{
    // Existing NOT BUILT output.
}

// Existing exact overlay output.
```

- [ ] **Step 4: Run the GREEN test**

Run the command from Step 2.

Expected: PASS for clean, dirty-missing, and dirty-exact-overlay states.

### Task 3: Generation compatibility and documentation

**Files:**
- Modify: `src/LocalAi.Cli/CodeSearchSyncCommand.cs`
- Modify: `tests/CodeSearch.Tests/GenerationStoreTests.cs`
- Modify: `README.md`
- Modify: `README.ru.md`

- [ ] **Step 1: Strengthen generation identity coverage**

```csharp
[Fact]
public void Normalization_version_changes_generation_identity()
{
    var previous = Identity() with { NormalizationVersion = 3 };
    var canonicalCrlf = previous with { NormalizationVersion = 4 };
    Assert.NotEqual(previous.Id, canonicalCrlf.Id);
}
```

This is characterization coverage for the existing identity contract. The version increment itself
is a configuration-only change and is verified by the new live generation ID in Task 4.

- [ ] **Step 2: Run generation identity tests**

Run:

```powershell
dotnet test tests\CodeSearch.Tests\CodeSearch.Tests.csproj --configuration Release --filter FullyQualifiedName~GenerationStoreTests
```

Expected: PASS, proving normalization version participates in the generation ID.

- [ ] **Step 3: Bump normalization and update paired documentation**

Add `CodeSearchSyncCommand.CurrentNormalizationVersion = 4` and use it when constructing
`GenerationIdentity`. Version 3 was invalidated during pre-publication validation because its
generation reused vectors from the previous normalization contract. Guard corpus reuse with
matching indexing-contract versions before publishing version 4. Update both README files with:

- canonical in-memory Windows CRLF;
- no repository file rewriting;
- generation rebuild after normalization changes.

- [ ] **Step 4: Run focused tests**

```powershell
dotnet test tests\CodeSearch.Tests\CodeSearch.Tests.csproj --configuration Release
dotnet test tests\LocalAi.IntegrationTests\LocalAi.IntegrationTests.csproj --configuration Release
```

Expected: all CodeSearch and LocalAi integration tests pass with no warnings.

### Task 4: Full verification and Jira reindex

**Files:**
- Runtime only: `%LOCALAPPDATA%\LocalAi\repositories\<jira-repository-id>`
- Stage all approved LocalAi source, test, and documentation changes.

- [ ] **Step 1: Run the full LocalAi baseline**

```powershell
dotnet restore LocalAi.slnx
dotnet build LocalAi.slnx --configuration Release --no-restore
dotnet test LocalAi.slnx --configuration Release --no-build
```

Expected: zero build warnings, zero build errors, all tests pass.

- [ ] **Step 2: Stage and inspect**

```powershell
git add -A
git diff --cached --check
git status --short
```

Expected: source, tests, EN/RU docs, and plans are staged; build outputs remain ignored. Do not
commit or push without separate owner authorization.

- [ ] **Step 3: Rebuild Jira generation through the shared broker**

Run the verified Release CLI:

```powershell
src\LocalAi.Cli\bin\Release\net10.0\localai.exe sync --root C:\Users\Mr.Aliev\plugins\jira-intelwash
```

Expected:

- a new generation ID because normalization version is 3;
- base ref is `refs/heads/main`;
- base contains 62 files;
- exact dirty overlay contains only the seven actual Git-changed files;
- broker remains a single FIFO worker.

- [ ] **Step 4: Verify GPU placement and search**

Use `localai native Processes` through the broker and verify `size == size_vram` and
`context_length == 16384`. Run:

```powershell
codesearch status --root C:\Users\Mr.Aliev\plugins\jira-intelwash
codesearch search --query "validate project and issue type fields before creating a Jira issue" --root C:\Users\Mr.Aliev\plugins\jira-intelwash --top 5
```

Expected: status reports the exact seven-file dirty overlay and the semantic query returns relevant
Jira implementation and test locations.

- [ ] **Step 5: Preserve unpublished state**

Confirm global `bin`, hooks, remote branches, and Jira source files were not mutated by the
verification. Leave all LocalAi changes staged and report the remaining publication decision.
