# Personal GitHub Publication Implementation Plan

[Русская версия](2026-07-29-personal-github-publication.ru.md)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish the complete LocalAi solution as the private personal repository `MrAliev/local-ai-developer-toolkit` with synchronized English and Russian README files.

**Architecture:** Preserve every source-level name and runtime contract. Reframe only the repository landing documentation under the product name Local AI Developer Toolkit, verify the existing solution, then create and push a private GitHub repository from the clean local `main` branch.

**Tech Stack:** Markdown, Git, GitHub CLI, PowerShell, .NET 10

---

### Task 1: Rewrite the paired repository landing documents

**Files:**
- Modify: `README.md`
- Modify: `README.ru.md`

- [ ] **Step 1: Rewrite the English README**

Use `Local AI Developer Toolkit` as the document title and add a language link to
`README.ru.md`. Preserve the verified commands and runtime guarantees while arranging
the content in this exact section order:

```text
Overview
Core capabilities
How the components fit together
Prerequisites
Quick start
Build and test
Publishing executables
Runtime and security
Projects
Development rules
```

The capability section must explicitly cover:

```text
Durable machine-wide FIFO broker and exclusive Ollama transport
CodeSearch hybrid semantic and lexical repository search
Generation-based base indexes with exact worktree overlays
CodeSearch and LocalLm stdio MCP servers
Opt-in Git hook installation and repository synchronization
```

- [ ] **Step 2: Rewrite the Russian README as a complete translation**

Use `Local AI Developer Toolkit` as the document title and add a language link to
`README.md`. Mirror every English section and substantive bullet in the same order.
Keep commands, identifiers, project paths, component names, and error strings in their
original form.

- [ ] **Step 3: Verify paired structure and encoding**

Run:

```powershell
rg -n "^## " README.md README.ru.md
```

Expected: both files contain ten matching second-level sections in the same order.

Normalize both files to Windows CRLF and UTF-8 without BOM, then verify that neither
file contains a bare LF or UTF-8 BOM.

- [ ] **Step 4: Review the documentation diff**

Run:

```powershell
git diff --check
git diff -- README.md README.ru.md
```

Expected: no whitespace errors, no changes outside the paired README files, and no
claim that bypasses the broker or changes the source-level product names.

- [ ] **Step 5: Commit the synchronized README update**

Run:

```powershell
git add -- README.md README.ru.md
git commit -m "docs: introduce Local AI Developer Toolkit"
```

Expected: one commit containing exactly the paired landing documents.

### Task 2: Verify the publication candidate

**Files:**
- Verify: `LocalAi.slnx`
- Verify: entire tracked repository state

- [ ] **Step 1: Run the full solution tests**

Run:

```powershell
dotnet test LocalAi.slnx --no-restore
```

Expected: all test projects pass with zero failed tests.

- [ ] **Step 2: Verify branch and working-tree state**

Run:

```powershell
git status -sb
git branch --show-current
git log -2 --oneline
```

Expected: branch `main`, no staged, unstaged, or untracked files, and the README commit
at the tip above the approved publication-design history.

### Task 3: Create and publish the private personal repository

**External state:**
- Create: `https://github.com/MrAliev/local-ai-developer-toolkit`
- Configure: local remote `origin`
- Push: local `main` to `origin/main`

- [ ] **Step 1: Reconfirm the active GitHub identity**

Run:

```powershell
gh auth status
gh api user --jq '.login'
```

Expected: the active account and returned login are both `MrAliev`.

- [ ] **Step 2: Confirm the target repository is still absent**

Run:

```powershell
gh repo view MrAliev/local-ai-developer-toolkit --json nameWithOwner
```

Expected: GitHub reports that the repository cannot be resolved. If it now exists,
inspect its owner, visibility, and contents before performing any write.

- [ ] **Step 3: Create the private repository and push `main`**

Run:

```powershell
gh repo create MrAliev/local-ai-developer-toolkit --private --source . --remote origin --push
```

Expected: GitHub creates the private repository, adds `origin`, pushes `main`, and
configures upstream tracking.

- [ ] **Step 4: Verify publication**

Run:

```powershell
gh repo view MrAliev/local-ai-developer-toolkit --json nameWithOwner,visibility,defaultBranchRef,url
git remote -v
git status -sb
git rev-parse HEAD
git rev-parse origin/main
```

Expected:

```text
nameWithOwner: MrAliev/local-ai-developer-toolkit
visibility: PRIVATE
default branch: main
local HEAD equals origin/main
working tree: clean
```

Do not create a pull request: this is the initial publication of the approved local
`main` history into a new empty private repository.
