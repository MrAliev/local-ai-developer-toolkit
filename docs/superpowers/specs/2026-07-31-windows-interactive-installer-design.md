# Windows Interactive Installer Design

[Русская версия](2026-07-31-windows-interactive-installer-design.ru.md)

**Date:** 2026-07-31

**Status:** Approved for a separate follow-up issue and PR

**Target:** Windows 10/11 x64

## Goal

Provide a self-contained graphical installer that can prepare a Windows computer
for complete LocalAi use by the current user. The installer detects
dependencies, offers consent-based installation, recommends models from
available VRAM, installs and activates LocalAi, and optionally configures Codex
and Claude. It preserves the machine-wide singleton, durable FIFO, ACL,
activation, full-VRAM, and zero-offload guarantees.

This work follows the broker protocol/build compatibility fix in issue #6 and
is delivered through its own issue, feature branch, and PR.

## Product Shape

The release artifact is a self-contained WPF `win-x64` bootstrapper. It does not
require a preinstalled .NET runtime.

The solution separates:

- `LocalAi.Installer.Core`: immutable plans, detection, package verification,
  dependency execution, hardware/model recommendations, configuration adapters,
  transaction journaling, rollback, and diagnostics;
- `LocalAi.Installer`: WPF pages and view models that render plans and collect
  explicit choices;
- existing launcher, broker client, contracts, and LocalLm APIs reused without
  bypasses.

The UI never contains installation policy. It presents results and invokes core
operations through interfaces that are testable without Windows dialogs,
network access, package installation, or a GPU.

## Installation Layout

Installation is per-user:

- stable launcher and versioned binaries:
  `%LOCALAPPDATA%\LocalAi\bin`;
- broker runtime state:
  `%LOCALAPPDATA%\LocalAi`;
- installer journal and non-secret diagnostics:
  `%LOCALAPPDATA%\LocalAi\installer`.

The existing version-directory/current-pointer layout is retained. An existing
compatible installation is upgraded in place. An unrecognized layout is shown
but not modified.

## Wizard Flow

### 1. Diagnose

Detect and display:

- Windows version and x64 architecture;
- free disk space and network reachability;
- `winget`, Git, and Ollama versions;
- discrete adapters and dedicated VRAM;
- existing LocalAi installation/runtime state;
- Codex and Claude user installations.

Git and Ollama are LocalAi dependencies. `winget` is an optional acquisition
mechanism. Codex and Claude are optional integration targets and are not required
to install LocalAi.

### 2. Dependencies

Show every missing or unsupported dependency with its exact source, package ID,
version requirement, download size when known, and whether elevation is needed.
Each dependency has an independent consent checkbox.

With consent, invoke `winget` non-interactively for the selected exact package
ID. If `winget` is absent, offer the vendor's official installer and re-run
detection after it completes. Elevation is requested only for an operation that
requires it. Cancellation leaves completed external package operations recorded
but does not attempt unsafe automatic uninstallation.

### 3. LocalAi Package

Download the selected GitHub Release manifest and package over TLS. Verify:

- manifest signature against an embedded release public key;
- package SHA-256 from the verified manifest;
- Authenticode signature where the release pipeline provides it;
- expected package structure and compatibility metadata.

Extract into a new version directory, validate it, and activate it with the
existing launcher transaction. Preserve the prior pointer and version for
rollback.

### 4. Models

Read dedicated VRAM without counting shared system memory. For multiple adapters,
select the eligible discrete adapter with the largest dedicated VRAM by default
and allow the user to choose another.

The signed release manifest contains model metadata, supported context tiers,
download sizes, and conservative memory estimates. The installer reserves
runtime/context overhead and offers:

- Minimal;
- Recommended;
- Extended;
- manual selection.

Clearly over-budget models are disabled with an explanation. Estimates never
claim proof. Downloads and runtime checks are submitted only through the LocalAi
FIFO broker. After download, every selected model runs broker preflight. A model
is accepted only when the broker confirms `size_vram == size`; otherwise the
broker unloads it and the wizard offers a smaller context tier or model.

The installer never invokes Ollama HTTP endpoints or `ollama pull` directly.

### 5. Agent Integration

Detect supported user-scope Codex and Claude configurations. For each detected
client, independently offer:

- CodeSearch and LocalLm MCP registration through the stable launcher;
- a managed global-instructions block requiring LocalAi FIFO delegation and
  forbidding direct Ollama use;
- no change.

Parse supported configuration formats structurally. Add global rules only inside
uniquely marked managed blocks. Show an exact diff and destination path before
confirmation. Create timestamped byte-for-byte backups, write atomically, and
read back the result. Unknown, invalid, or concurrently changed formats block
the write instead of being overwritten.

Do not read, display, copy, or log credential values.

### 6. Review, Apply, and Finish

Present one immutable execution plan grouped into:

- externally managed dependencies;
- LocalAi package activation;
- model downloads and preflight;
- per-agent configuration changes.

The user confirms the final plan. Progress is journaled after each idempotent
step. A rerun resumes or safely repeats incomplete work. The completion page
shows installed versions, model residency results, configured clients, required
client restarts, rollback status, and a path to the redacted diagnostic report.

## Transaction and Rollback Model

The installer owns rollback for:

- staged LocalAi files;
- the activated version pointer;
- installer-created configuration changes;
- newly created managed instruction blocks.

It does not silently uninstall a dependency completed by `winget` and does not
delete existing or newly downloaded models. Such non-transactional effects are
identified before confirmation.

Rollback restores byte-for-byte configuration backups and the prior LocalAi
pointer, then verifies both. A rollback failure is a first-class result with
manual recovery instructions.

## Security and Invariants

- Per-user install by default; elevation is scoped to a selected dependency.
- Downloads require a signed manifest and verified digest.
- Runtime ACL enforcement remains broker-owned.
- All model discovery, download, inference, and residency verification pass
  through the one LocalAi FIFO broker.
- The installer cannot weaken full-VRAM or zero-offload policy.
- Existing models, profiles, agent settings, and unrelated processes are
  preserved.
- Diagnostics are redacted and contain no prompt, job, token, or credential
  contents.

## TDD and Verification

Production behavior is implemented only after a failing test demonstrates it.

Core unit tests use fake dependency detectors, command runners, download and
signature verifiers, hardware snapshots, broker clients, filesystems, clocks,
and agent adapters. Required cases include:

- missing, current, unsupported, and concurrently installed dependencies;
- `winget` success, refusal, cancellation, elevation, and failure;
- manifest, signature, digest, package-layout, and compatibility failures;
- atomic activation and rollback;
- multi-GPU and borderline VRAM recommendations;
- preflight success and `size_vram != size` rejection;
- proof that every model operation uses the broker abstraction;
- supported, unknown, malformed, and concurrently changed agent configs;
- exact preview, backup, atomic write, read-back, rollback, resume, and rerun.

Integration tests run in temporary roots with fake `winget`, release endpoint,
broker, and client homes. WPF view-model tests cover navigation and consent
gates without UI automation.

Release gates include:

- clean Windows VM with no LocalAi prerequisites;
- Windows machine with an existing compatible installation;
- representative real-GPU validation across several VRAM classes;
- deterministic CI snapshots for the same decisions when no GPU is available;
- English/Russian documentation parity;
- full `dotnet test LocalAi.slnx -c Release --nologo`.

The installer is not published until the issue #6 compatibility contract is
merged and its installed-vs-development scenario passes.
