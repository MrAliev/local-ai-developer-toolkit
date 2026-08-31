# Security policy

[Русская версия](SECURITY.ru.md)

## Reporting a vulnerability

Report privately through GitHub:
[open a security advisory](https://github.com/MrAliev/local-ai-developer-toolkit/security/advisories/new).
Please do not open a public issue for something that lets an attacker reach a machine.

Expect a first reply within a week. There is no bounty programme; there is a maintainer who
reads what arrives.

## What is supported

The newest published release. Older version directories stay on disk so a machine can roll
back, but fixes are made on `main` and shipped in the next release rather than backported.

## What the product actually promises

Four of these are worth stating precisely, because they are what a report should measure
against.

**Releases are signed, transports are not trusted.** Every release manifest is signed with an
ECDSA P-256 key whose public half is embedded in the installer binary
(`src/LocalAi.Installer.Core/Releases/release-signing-public.spki.der`). The installer verifies
that signature before acting on anything the manifest says, then checks the package archive
against the SHA-256 recorded inside it. This is why releases can be downloaded anonymously over
plain HTTPS: an anonymous download of a signed document is as trustworthy as an authenticated
one. A manifest that fails verification is never retried through a second transport.
Publishing a release additionally records every asset's digest in GitHub's attestation log
(`gh attestation verify <file> --repo MrAliev/local-ai-developer-toolkit`), a channel the
release page cannot rewrite — the check that covers the one artifact the embedded key cannot:
the installer executable itself, until it carries an Authenticode signature.

**Source-derived output is data, not instructions.** Everything CodeSearch returns from a
repository — snippets, paths, symbol identifiers, line ranges — is wrapped in a nonce-bound
`<untrusted-content>` block. A client that follows instructions found inside that boundary is
doing something the protocol tells it not to do.

**The installation directory is protected.** `%LOCALAPPDATA%\LocalAi` is created with an ACL
granting only the installing user, SYSTEM and Administrators, and the installer refuses to
operate on a root that inherits permissions instead of repairing it. A version directory is
immutable once published; activation swaps a pointer atomically.

**winget is verified before it is run.** The installer executes winget to install prerequisites
machine-wide, and it finds winget by searching a path the user can write to. Before every
invocation it checks that the executable belongs to the registered `Microsoft.DesktopAppInstaller`
package, sits under a path only administrators can write to, and is Authenticode-signed by
Microsoft Corporation — and it runs the path that check resolves, not the one the search
returned. The check is repeated before each install rather than once at detection, because the
two are minutes apart. A winget that fails it is not run, and the installer says which check
failed.

## What is not a vulnerability

- **The SmartScreen prompt.** `LocalAi.Installer.exe` is not Authenticode-signed, so Windows
  shows "Windows protected your PC". This is expected, and the SHA-256 of each installer is
  published in its release notes so a download can be checked.
- **Model output.** Local models are weaker than cloud ones and can be wrong. The tooling says
  so; a wrong answer is a limitation, not a security flaw.
- **Anything requiring an attacker to already be the signed-in user** on the machine, with the
  privileges that implies.

## Keys

The private signing key exists on one machine and has never been in this repository. Its
working copy is DPAPI-wrapped and bound to that machine's signing account, and the signer
refuses to use it while the key directory's ACL grants access beyond SYSTEM, Administrators
and that account. If you believe it has been compromised, say so in the advisory: the
rotation procedure is documented in
[docs/release-signing-runbook.md](docs/release-signing-runbook.md), and it deliberately
requires a release signed with the new key before the old one is retired.
