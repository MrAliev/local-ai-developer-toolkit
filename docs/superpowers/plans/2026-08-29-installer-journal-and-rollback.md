# Installer Run Journal and Rollback

[Русская версия](2026-08-29-installer-journal-and-rollback.ru.md)

**Goal:** close #157 — give the wizard a durable journal of what a run did to the
machine, and a rollback that undoes what can honestly be undone, wired into the
wizard in the same change that introduces it.

**The bar, from the issue:** the previous transactional installer was a complete,
tested design that no installation ever ran — 243 test cases describing an
installer the product had never been. It was removed in `62442cd`. Whatever
replaces it must be reachable from the Install button, or not exist.

## Decisions taken with the maintainer

Four questions were the maintainer's to answer, and were asked before a line was
written:

1. **Rollback runs only on request.** A failed or cancelled run shows a
   "Roll back changes" button on the finish page; nothing is undone
   automatically. Undoing work somebody wanted to keep is its own failure mode.
2. **An interrupted run is offered back at the next start.** A journal whose run
   never wrote an outcome is the trace of a killed process. The first page shows
   what that run recorded and offers to roll back the reversible part — or to
   continue and leave it, which is recorded as `Abandoned` so the question is
   asked once.
3. **The journal lives in `%LOCALAPPDATA%\LocalAi-installer-logs`**, beside the
   run reports. It cannot live inside `%LOCALAPPDATA%\LocalAi`: that tree is
   validated against an exact name list on every install, so a journal inside it
   would make the next installation refuse the layout it is trying to repair.
   The journal file is the only new thing a successful run writes.
4. **A failed run does not block the wizard.** The finish page reports what was
   applied; a partial installation stays unless the user asks for the rollback.

## What the journal records

`InstallerRunJournal` (Core, `Transactions/`) writes the intent of each effect to
disk — atomically, write-through — before the effect runs, and the outcome after
it. A process killed mid-effect leaves a `Running` step, which is exactly the
information "state unknown, check by hand" needs. Steps carry the undo data
collected at the moment the effect completed:

- **Package activation:** the activated and the prior version.
- **Residency policy and the Ollama launch record:** the pre-install file bytes
  inline (they are small JSON files), plus hashes of both sides.
- **Agent configurations:** the path of the `.bak` backup the adapter already
  writes, plus hashes of both sides. A backup exists exactly when the file
  existed before the run, so its presence doubles as the existed-before record.
- **Dependency and model installs:** description only, marked irreversible.

An outcome of null alone cannot tell a killed wizard from one still installing
in another window, so the journal also holds an unshared live lock beside
itself for as long as its process runs, released by the operating system the
moment that process dies. The interrupted-run scan probes the lock: held means
alive and skipped — offering to roll back a run that is mid-install would race
its own effects; released, however the process ended, means the run is offered
back. This is a condition, not a deadline: any elapsed-time rule would call a
slow install dead. A lock file a power loss left behind opens freely, proves
nothing is alive, and is cleaned up.

## What rollback undoes, and what it will not

`InstallerRunRollback` walks completed steps newest first.

Undone when the machine still looks like what the run wrote:

- **Version activation, for an upgrade:** the launcher reactivates the prior
  version through the same guarded swap the installation used
  (`activate <prior> --stop-running --if-current-sha256 <observed>`), so a
  version somebody activated in between is never overwritten. The new version
  directory and the updated launcher stay: versions are immutable, the launcher
  is the stable entry point.
- **The three file effects:** restored from the backup or the inline copy, but
  only after proving the file still hashes to what the run wrote and the copy
  hashes to what was there before. A file edited since the run is left alone and
  said so; a restore that cannot prove its bytes is a mutation, not a rollback.

Left in place, and said plainly in the report and the journal:

- **winget and npm installs** — shared machine software other programs may
  already depend on.
- **Pulled models** — they live in Ollama's store, shared with everything else
  that uses it.
- **A first installation** — there is no prior version to return to, and the
  LocalAi root starts holding runtime data (indexes included) as soon as the
  broker runs, so "undo" would mean deleting things the run did not create.
- **Anything a killed process left mid-flight** — state unknown, reported as
  such.

The package installer's own recovery is unchanged: a failed activation still
restores the prior state itself, and such a step is journalled as failed with
nothing to undo.

## What was reused from the removed code, and what was not

The removed `InstallerJournal` contributed two ideas worth keeping: write the
intent before the effect, and persist atomically via a temporary file. Its shape
was not kept. It carried duplicate record types, a redaction that replaced any
message containing "job" with `<redacted>`, and an executor abstraction whose
only callers were its tests — the shape of code grown against tests instead of a
product. The new journal stores no secrets by construction (hashes, paths and
version names; config content only for the two runtime-owned JSON files), so it
needs no redaction layer.

## Wiring

`InstallerWizardViewModel.RunAsync` creates the journal after the dry-run gate
and before the first effect; a dry run writes no journal at all. Each effect
begins its step just before running. `ReleaseInstallService.InstallAsync` gained
an `activated` callback invoked between activation and the model pulls, so the
activation is recorded as done the moment it is — not after model downloads that
can run for minutes and die. Journal write failures never fail the installation;
they drop the journal and say in the log that rollback will not be available.

The finish page shows the rollback report effect by effect — undone, left in
place, failed — in a field separate from the run log, because collapsing the two
is how the old `RollbackNotes` came to mean nothing.
