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

The runtime makes one network request of its own, and only after being asked to: an
optional update check, off until it is switched on with `localai policy set --update-check on`
or the checkbox on the installer's review page. It fetches the latest release manifest and
its signature — the same two public documents an installation downloads — and sends nothing
about the machine: no identifier, no account, no usage. The version inside is believed only
after the signature verifies against the embedded release key, and nothing is ever installed
without `localai update` being run.

## What the installer does to a machine

Every change the wizard makes is listed on its review page before anything is applied,
and each run writes a journal of intent and outcome to `%LOCALAPPDATA%\LocalAi-installer-logs`.
A failed or cancelled run offers rollback of everything provably reversible.

Removal is one action, not a checklist. The installation registers itself in **Apps &
features**, where the entry runs a copy of this same installer in uninstall mode; the
wizard is also reachable by starting the installer and choosing Remove. It opens on a
matrix of what to take away — three presets, every row changeable on its own — and lists
every removal on a review page before anything happens: the runtime root, the MCP server
registrations in each client's configuration, the managed instruction block between its
explicit markers, and the Git hook dispatchers of each connected repository, which are
listed from the runtime's own manifests. The user's own text and anybody else's
registrations survive untouched.

Two things it will not do quietly. The release signing key directory is kept unless its
own separate confirmation is given, because removing it makes an offline backup the only
copy in existence. Prerequisites installed through winget and models pulled into Ollama
are machine-wide and shared with other software, so the final page names the
`winget uninstall` and `ollama rm` commands instead of running them. The journal
directory `%LOCALAPPDATA%\LocalAi-installer-logs` also stays, and the uninstall run
writes its own entry there.
