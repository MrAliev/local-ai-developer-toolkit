# Post-0.1.45 Issue Backlog Plan

[Русская версия](2026-08-28-post-0-1-45-issue-backlog.ru.md)

**Goal:** close the twelve issues open after 0.1.45, one pull request per issue,
each branched from `main`.

**Order:** the issue that let a broken product reach `main` on a green build goes
first. Then the two defects that ship a silent failure to a user's machine. Then
the `p2` set, then `p3`. Issues #139-#142 arrived from a benchmark run against
the released build and are triaged into the same list rather than appended to it.

## What the triage found

Two of the untriaged issues are not what their reports assume, and one pair
shares a root cause. Both findings change the order.

**#143 and #139 are the same blind spot.** `BuildSemanticIndexAsync` in
`src/LocalAi.Cli/CodeSearchSyncCommand.cs` sends every Roslyn failure to a
console diagnostic and then continues: a null workspace falls back to an empty
semantic index, and a workspace whose projects all failed to load produces an
index with no C# symbols. Both are success paths. Nothing in the exit code, and
nothing in the test suite, separates "indexed C# semantics" from "indexed
nothing" — which is why a split package version passed 1875 tests, and is the
leading explanation for a single-file `localai.exe sync` that prints
"Dll was not found." and exits 0.

**#140 is not a shared libgit2 handle.** There is no `Repository` object to race
on. `RepoLocator.RunGit` spawns a `git` child process under a fixed
`GitTimeoutMs = 10_000` deadline and collapses timeout, spawn failure and
non-zero exit alike into `null`; callers render that `null` as "Git common
directory is unavailable." Six concurrent clients on a loaded machine is how a
ten-second wall-clock deadline gets missed, and a deadline is also why every
retry succeeded. This is the repository's own "never assert how fast the
machine is" rule broken in production code rather than in a test.

## Order and why

| # | Issue | Why here |
| --- | --- | --- |
| 1 | #143 | The only one where a missing test let a broken product merge green. |
| 2 | #142 | Process crash and a Windows dialog on every boot with this ordering. |
| 3 | #139 | The shipped CLI fails and reports success; scripts believe it. |
| 4 | #144 | p2 — installation without GitHub. |
| 5 | #145 | p2 — the tree claims a transactional installer it does not have. |
| 6 | #146 | p2 — a 30-minute cap no setting can raise. |
| 7 | #140 | p2 — intermittent failure of a shipped tool, now diagnosed. |
| 8 | #141 | p2 — a dead tool with no way to discover why. |
| 9 | #147 | p3 — signing and the placeholder policy. |
| 10 | #148 | p3 — prerequisites that block the wizard. |
| 11 | #149 | p3 — narrows once #142 lands; re-scope before starting. |
| 12 | #150 | p3 — community profile. |

#142 and #139 are placed above the `p2` set on severity: one crashes a process
on every boot, the other tells a caller that a failed sync succeeded. #140 and
#141 sit inside the `p2` tail, below the installer work already labelled `p2`.

## Task 1: #143 — a real solution through MSBuildWorkspace

- [ ] RED: load this repository's own `LocalAi.Contracts.csproj` through
  `RoslynSolutionLoader.LoadAsync` and assert a document and a symbol came back.
- [ ] Prove it: split the Roslyn package versions locally, watch the new test go
  red, restore them. A regression test never seen failing is a guess.
- [ ] Decide fail-versus-skip through `FixturePrerequisite` with
  `LOCALAI_STRICT_FIXTURES`; a skip on a runner restores the blindness.
- [ ] Place it in `CodeSearch.Tests` beside the other semantic fixtures.

The existing `RoslynSolutionLoaderTests` already open a synthetic project and
assert only `NotNull`, which a zero-project workspace satisfies. The assertion
that was missing is that something came back, not that loading returned.

## Task 2: #142 — the broker must not die when Ollama is down

- [ ] RED: drive the broker against a refused endpoint and assert the process
  survives and reports a named cause. Wait for the state, never for a duration.
- [ ] Guard the paths that reach the backend outside the executor's own catch —
  scheduling metadata (`PrepareAsync`), the watchdog probe, and the outer body
  of `BrokerProgram.RunAsync`, which today has no general catch at all.
- [ ] Treat backend-unreachable as retryable, and fail queued jobs with
  "Ollama is not reachable at <endpoint>" rather than an exception type.
- [ ] Leave the endpoint flag alone: `serve --ollama <url>` already exists. A
  configurable endpoint is a separate subject and a separate issue.

## Task 3: #139 — the single-file CLI reports its failures

- [ ] Reproduce from a single-file publish and capture the real exception, its
  type and the DLL it names; the report's message alone does not identify it.
- [ ] Fix the bundling defect the repro identifies.
- [ ] Make a degraded sync observable: an empty C# semantic index on a
  repository that has C# is a failure, not a quiet fallback.
- [ ] If the bundling fix and the reporting fix turn out to be two subjects,
  raise it before splitting into two pull requests rather than merging both.

## Task 4: #144 — install from a folder

- [ ] Add a `DirectoryReleaseFeed` beside `GitHubReleaseFeed` and
  `AnonymousReleaseFeed`, implementing the existing `IReleaseFeed`.
- [ ] Choose it on the package page: an explicit path, or the installer's own
  directory.
- [ ] Verify exactly as before — manifest against the embedded key, package
  against the SHA-256 in the manifest. Where bytes came from is not evidence.
- [ ] Say in the documentation that models still come from the Ollama registry.

## Task 5: #145 — decide what the installer is

- [ ] Choose between wiring `InstallerJournal` and `RollbackService` into the
  wizard, and deleting them with their tests. Put the reasoning in the pull
  request; this is the one issue whose answer is a decision, not a defect.
- [ ] Whichever way it goes, `RollbackNotes` is renamed to what it holds.
- [ ] Leave no class referenced only by its own tests.

## Task 6: #146 — a download is not a command

- [ ] Give the model pull its own ceiling, or liveness based on progress rather
  than a deadline. `MaximumCommandTimeout` stays where it belongs, on commands.
- [ ] Cover a pull that outlives the command ceiling. Drive it with a fake
  runner and a controllable clock; do not make the suite wait.

## Task 7: #140 — git access under concurrent clients

- [ ] RED: concurrent `Inspect` calls against one working tree, and a `git`
  invocation that exceeds the deadline, asserting neither is reported as a
  missing common directory.
- [ ] Replace the fixed deadline with something that does not measure the
  machine, and carry the underlying cause — exit code, stderr, exception — into
  the message instead of collapsing everything to `null`.
- [ ] Confirm the diagnosis against the report before changing behaviour: the
  reporter suspected a shared libgit2 handle, and there is none.

## Task 8: #141 — read_image without a vision model

- [ ] Fail closed with an explanation naming a model to install, in the shape
  the calibration path already uses.
- [ ] Surface a vision model through `RecommendedMissingModels`; the catalog
  already carries `Vision` capabilities, so a fresh install can discover it.

## Task 9: #147 — signing and the placeholder policy

- [ ] Replace the `CN=LocalAi` subject and the sixty-four-zero hash, or make
  `--require-authenticode` refuse to run against a placeholder. Today the flag
  would reject every installation.
- [ ] Write down the order — sign before `pack`, always countersign with a
  timestamp — where the person cutting a release will read it.
- [ ] The certificate itself is a purchase and a maintainer decision; the code
  and the documentation are what this pull request can close.

## Task 10: #148 — prerequisites that are actually required

- [ ] Separate what semantic search over C# needs from what the TypeScript and
  Python indexers need.
- [ ] Let the rest be skipped, with the consequence stated on the page.
- [ ] Cover each prerequisite's blocking decision.

## Task 11: #149 — starting Ollama

- [ ] Re-scope first: #142 already makes an unreachable backend explain itself,
  so what remains here is starting it on demand.
- [ ] Whatever starts it goes through the shared broker; no tool invokes the
  `ollama` binary and nothing talks to port 11434 directly.

## Task 12: #150 — code of conduct

- [ ] Add `CODE_OF_CONDUCT.md` with a contact route that publishes no personal
  address — a private security advisory, or a dedicated address.
- [ ] Add `CODE_OF_CONDUCT.ru.md` linking back, as `DocumentationShapeTests`
  requires of every document outside `.github/` and `Fixtures/`.

## Rules every one of these is held to

- One subject per pull request, branched from `main`, never stacked.
- Tests wait for a condition, never for a duration; hang detection is the CI
  run timeout's job.
- Ollama only through the shared broker.
- Every document exists in English and Russian and links to its pair.
- Documentation is UTF-8 without BOM, CRLF.
- Comments record why, not what.
- `dotnet test LocalAi.slnx --configuration Release --max-parallel-test-modules 1
  --timeout 20m`; the modules run one at a time because they share named mutexes
  and one runtime directory.
