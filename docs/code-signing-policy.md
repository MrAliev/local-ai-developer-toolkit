# Code signing policy

[Русская версия](code-signing-policy.ru.md)

This policy governs Authenticode signing of LocalAi binaries. It takes effect with the
first signed release and is published in advance as part of the project's application to
the SignPath Foundation open source program.

Free code signing is provided by [SignPath.io](https://signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

## What gets signed

- `LocalAi.Installer.exe` — the download users run;
- the six executables inside the release package: `LocalAi.Broker.exe`,
  `codesearch-mcp.exe`, `locallm-mcp.exe`, `codesearch.exe`, `localai.exe`,
  `localai-launcher.exe`.

Nothing else is signed under this policy. The release manifest keeps its separate ECDSA
P-256 signature, verified by the installer against the key embedded in it — Authenticode
protects the download; the manifest signature protects everything the installer fetches
afterwards.

## Where signed binaries come from

Signed binaries are built by GitHub Actions from the tagged release commit of
[this repository](https://github.com/MrAliev/local-ai-developer-toolkit) and signed
through SignPath, so every signed artifact is traceable to public source. Binaries built
anywhere else — including a maintainer's machine — are never submitted for signing.
Product name and version attributes are set at build time from the release version.

## Team and roles

The project has a single maintainer, who holds all three roles:

| Role | Person |
| --- | --- |
| Committer | Anton Aliev ([@MrAliev](https://github.com/MrAliev)) |
| Reviewer | Anton Aliev ([@MrAliev](https://github.com/MrAliev)) |
| Approver | Anton Aliev ([@MrAliev](https://github.com/MrAliev)) |

Every signing request is approved manually by the approver. Multi-factor authentication
is enabled for both the repository and the SignPath account.

## Privacy

LocalAi runs local models on the user's own machine; code, logs and images handed to
those models never leave it. The runtime records per-job telemetry locally and it never
holds prompts, file contents, paths or secrets — the full statement is in
[SECURITY.md](../SECURITY.md) and the telemetry section of the
[developer overview](overview.md). The installer collects nothing and phones nowhere;
its network use is downloading the release it verifies.

## What the installer does to a machine

Every change the wizard makes is listed on its review page before anything is applied,
and each run writes a journal of intent and outcome to `%LOCALAPPDATA%\LocalAi-installer-logs`.
A failed or cancelled run offers rollback of everything provably reversible. Removal:
delete `%LOCALAPPDATA%\LocalAi`, remove the two MCP server registrations from the AI
clients' configuration files, and remove the managed instruction block those files carry
between its explicit markers; prerequisites installed through winget are removed through
winget, and models through Ollama.
