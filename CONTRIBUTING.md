# Contributing

[Русская версия](CONTRIBUTING.ru.md)

Thank you for looking. This is a working tool rather than a framework, so the rules below are
short and enforced.

## Getting the solution to build

```powershell
dotnet restore LocalAi.slnx
dotnet test LocalAi.slnx --configuration Release --no-restore
```

You need the .NET 10 SDK. Building and unit-testing never start Ollama, so a machine without a
GPU can still run the suite; a handful of tests skip themselves when the platform cannot
support them, and that is expected.

## Rules that changes are held to

**One subject per pull request, branched from `main`.** Two subjects in one branch cannot be
reviewed apart or reverted apart. Do not stack a pull request on another one: GitHub closes the
second when the first branch is deleted on merge rather than moving it to `main`. When two
changes touch the same file, land the first and rebase the second.

**Tests come with behaviour, and never assert how fast the machine is.** Wait for the condition
rather than for a duration, prefer a purpose-built child process over an interpreter whose
startup cost is unbounded, and leave hang detection to the run timeout CI applies to the whole
solution. A deadline a correct run can miss proves nothing and reports itself as a fault in
the code under test. This is not a style preference — it is the single most common way a test
in this repository has been wrong.

**Ollama is reached only through the shared broker.** No `localhost:11434`, no `ollama` binary
invoked from a tool. The broker is what keeps ordering, deduplication, leases, heartbeat and
recovery correct while several clients run at once.

**Namespaces, commands, runtime paths and package versions stay as they are** unless a change
exists specifically to update one.

**Comments say why, not what.** The code says what it does. A comment earns its place by
recording the reason a thing is the way it is — usually the failure that made it necessary.

## Documentation

**Every pull request that changes `src/` updates the documentation, or says why it does not.**
The documents here describe the current state of the product, so a change to the product
usually makes at least one of them inaccurate — and the moment to notice that is while the
change is being made, not a release later when somebody plans a key rotation around a sentence
that stopped being true.

CI enforces the rule the only way a rule can be enforced without judging prose: a pull request
touching `src/` and nothing under `docs/`, `README*.md`, `SECURITY*.md` or `CONTRIBUTING*.md`
fails unless its body carries a line of its own saying otherwise —

```
Docs: none — internal refactor, no described behaviour changed
```

That line is not a formality. Writing it takes ten seconds and is exactly the thought the rule
exists to force: "the documentation was considered and needs nothing" and "the documentation
was forgotten" are indistinguishable from the outside, and only one of them is acceptable.

Every document exists twice: `name.md` in English and `name.ru.md` in Russian, each linking
to the other in its opening lines. A document in one language only is an unfinished document.

Documentation describes the current state of the product, as fact. What changed and when
belongs to release notes and to Git history, not to the documents that describe how things
work.

Repository documentation is UTF-8 without BOM, with Windows CRLF line endings.

## Releases

Releases are cut by the maintainer with a single command, in two halves:

```powershell
localai-release-signer release --version 0.1.46            # notes, then the pull request
localai-release-signer release --version 0.1.46 --publish  # build, sign, verify, tag, publish
```

The second half refuses to run unless the tree is clean, the commit is `main`, CI is green on
exactly that commit, and the signed manifest agrees with the version being published — including
carrying the model list, without which an installation would set up no models at all.

## What to expect from a review

Correctness first, then whether the change tells the next reader why it exists. Expect
questions about failure modes rather than about formatting.

## Conduct

Disagreement about the work is the point of a review; the rest is in the
[code of conduct](CODE_OF_CONDUCT.md), including how to report something privately without
either party publishing an email address.
