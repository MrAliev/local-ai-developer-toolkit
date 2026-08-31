# Release signing: backup, restore, rotation

[Русская версия](release-signing-runbook.ru.md)

The installer refuses any release whose manifest is not signed by the trusted ECDSA P-256 key.
`README.md` covers generating that key. This file covers the three things that only matter after
it exists: keeping a copy, proving the copy works, and replacing the key on purpose.

## What actually depends on the key

The trust anchor is the public key, embedded as a resource in `LocalAi.Contracts`
(`ReleaseTrustAnchor`) and therefore carried by every component built from it — the installer,
the CLI and the broker.

Three things verify against it, all of them reading the same anchor:

| Who | When | What a failure means |
| --- | --- | --- |
| The installer | Before fetching a package on a manifest's authority | The release is not installed |
| `localai update` | The same check, from the command line | The update does not happen |
| The broker's update check | Only when release lookups are switched on, at most once per interval | No update information — never an error, never an install |

The broker's check downloads nothing but the manifest and its signature, and installs nothing
under any circumstance; what it produces is a version number that a person may act on. That is
precisely why it verifies: an unsigned answer would let whoever answered the request invent a
version and send somebody looking for it.

That shape decides how bad the failure modes are.

| Event | Consequence |
| --- | --- |
| Private key lost | Recoverable. Generate a new pair, rebuild the installer with the new anchor, publish. Users download the installer from the release as they already do. |
| Private key leaked | Anyone can sign a package that every existing installer accepts. Rotate immediately and say so publicly. |
| Key silently replaced | Indistinguishable from a compromise for anyone who recorded the old fingerprint. This is the case the fingerprint record below exists for. |

Losing the key is therefore an inconvenience with a day of work behind it, not the end of the
distribution channel. It is worth avoiding anyway, and worth *not being surprised by*.

## Keep a copy

A freshly generated private key starts as:

```
%LOCALAPPDATA%\LocalAi\release-signing\release-signing-private.pkcs8.der
```

Make the backup below **first**, then run `localai-release-signer protect-key`: it replaces
the working copy with `release-signing-private.pkcs8.dpapi` — the same PKCS#8 wrapped with
DPAPI, bound to this Windows account on this machine — and destroys the raw file. Signing
prefers the wrapped copy and accepts a raw one only with a warning. It also refuses to
touch the key while the directory's ACL grants access to anyone beyond SYSTEM,
Administrators and the current user; the refusal names the offender and the `icacls`
command that fixes it.

One machine, one copy, no history. Put a second copy of the **raw PKCS#8** — never the
`.dpapi` file, which does not unprotect anywhere else — somewhere that does not share a
failure domain with that disk: a password manager attachment or an encrypted archive on
separate media. Not the repository: `publish/` and the runtime are ignored precisely so key
material never reaches git.

## Record the fingerprint out of band

Rotation and compromise look identical unless the old key was written down somewhere the attacker
does not control. Record the public key's SHA-256 alongside the release notes for the version that
introduced it:

```powershell
$pub = "$env:LOCALAPPDATA\LocalAi\release-signing\release-signing-public.spki.der"
(Get-FileHash -Algorithm SHA256 $pub).Hash
```

A reader who has that value can tell "the maintainer rotated the key" from "someone else is
signing now". Without it, they cannot.

## Prove the copy works — before you need it

A backup that has never been restored is a belief, not a backup. Verify it without touching the
live key:

```powershell
$restored = Join-Path $env:TEMP "release-signing-restore-check"
New-Item -ItemType Directory -Force -Path $restored | Out-Null
# Copy the backup private key into $restored, then:
dotnet run --project src/LocalAi.ReleaseSigner/LocalAi.ReleaseSigner.csproj -c Release -- sign `
    --package publish/release/localai-package.zip `
    --package-uri "https://example.invalid/probe" `
    --release-version 0.0.0-probe --version-directory probe `
    --no-models `
    --private-key "$restored\release-signing-private.pkcs8.der" `
    --out $restored
```

`--no-models` is what makes this a probe rather than a release: signing refuses to run without
a model list unless the omission is stated, and this manifest is never published. An explicit
`--private-key` path ending in `.der` is read as raw PKCS#8, which is exactly what the backup
holds — restoring never requires DPAPI-wrapping first.

Signing validates the private key against the anchor the installer ships, so a copy that does not
match fails here rather than on a user's machine. Delete `$restored` afterwards.

## Rotate on purpose

Rotation is a release, not an emergency procedure — as long as you still hold the old key, or have
accepted that you do not.

1. Generate a new pair into a fresh directory, keeping the current one in place.
2. Replace the embedded public key resource so `ReleaseTrustAnchor` carries the new anchor.
3. Publish a release built and signed with the **new** private key.
4. Publish the new public key fingerprint next to the release notes, and say that the key changed
   and why.
5. Retire the old private key only once a release signed by the new one has been installed
   successfully.
6. Back up the new raw key out of band, then run `protect-key` so the working copy is
   DPAPI-wrapped again.

The order matters in one place: step 3 and step 2 ship together, in the same installer. An
installer carrying the new anchor cannot verify a package signed by the old key, and the reverse
is equally true — mixing them produces a release nobody can install.

## Do not put the key in CI

CI builds and tests; signing and publishing stay on the machine that holds the key. Copying it
into a secrets store adds a second place it can leak from and undoes the point of keeping a
controlled copy. The release script is deliberately runnable locally for this reason, and so is
`localai-release-signer release --publish`, which drives it: the whole release runs on the machine
that holds the key, and CI is asked only whether the commit is green.
