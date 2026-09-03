# Working in this repository

[Русская версия](CLAUDE.ru.md)

Notes for an agent working here. The rules themselves live in
[CONTRIBUTING.md](CONTRIBUTING.md) and most of them are enforced by CI. This file exists
because reading them is a step that gets skipped, and one of them has now been skipped more
than once.

## Read this before opening a pull request

**A pull request that changes `src/` either updates the documentation or says why it does
not.** `.github/workflows/documentation.yml` fails otherwise. It reads the pull request body,
so a green build is not the whole answer, and it re-runs when the body is edited.

In this order:

1. **Find what the change made inaccurate.** Search `docs/`, `README.md`, `SECURITY.md` and
   `CONTRIBUTING.md` for the command, string, procedure or behaviour you touched. These
   documents state the current state of the product as fact; a sentence that stopped being true
   is a defect, and the moment to catch it is now rather than a release later.
2. **Update what you find, and its pair.** Every document exists twice, `name.md` and
   `name.ru.md`, each linking to the other in its opening lines. A document in one language only
   is unfinished.
3. **Only if nothing needed updating**, put a line of its own in the pull request body:

   ```
   Docs: none — internal refactor, no described behaviour changed
   ```

Step 3 is not a way around step 1. "The documentation was considered and needs nothing" and
"the documentation was forgotten" look identical from outside; that line is the only thing that
tells them apart, so it has to be true when you write it.

## CI is never left red

A red check is a defect in the change, including when it is the documentation check. Do not
open the next pull request on top of a red one, and do not treat a failing check as somebody
else's to look at.

Both checks matter and they fail for different reasons:

| Check | Runs on | Fails when |
| --- | --- | --- |
| `build-and-test` | Windows | the solution does not build, or a test fails |
| `documentation-considered` | Linux | `src/` changed, no documentation changed, and the body does not say why |

## Branches and pull requests

One subject per branch, taken from `main`. Two subjects in one branch cannot be reviewed apart
or reverted apart. Do not stack a pull request on another: GitHub closes the second when the
first branch is deleted on merge rather than moving it to `main`. When two changes touch the
same file, land the first and rebase the second.

Name a branch by the kind of work: `fix/`, `feature/`, `test/`, `docs/`, `refactor/`, `chore/`,
then a short name in words — `fix/version-line-holds`, not an issue number.

## Running the suite

```powershell
dotnet test LocalAi.slnx --configuration Release --max-parallel-test-modules 1 --timeout 20m
```

That is what CI runs, and it is the answer before a pull request. While iterating, a single
test project is faster — `dotnet test tests/<name>/<name>.csproj --configuration Release`, or
its built executable with `-class <full.type.name>`. Building and testing never start Ollama,
so a machine with no GPU still runs the suite; a few tests skip themselves where the platform
cannot support them.

A test never asserts how fast the machine is. Wait for the condition, not for a duration — a
deadline a correct run can miss proves nothing and reports itself as a fault in the code under
test.

## What the product prints

Every string the software prints is chosen by the reader's machine: English by default, Russian
on a Russian system, English again where a language has no translation. Strings live in `.resx`
pairs under `Resources/` in the assembly that prints them, reached through a `TextCatalogue`,
and a parity test refuses a language carrying only some of the keys.

Never translated, in any language: commands, option names, identifiers, enum values, status
tokens, wire formats, paths, and the MCP `[Description]` attributes. Numbers, durations and
dates stay invariant everywhere — those lines are quoted verbatim by agents and parsed by
tests.

Wording, layout and what should not be translated are decided by the `ux-designer` agent in
[`.claude/agents/ux-designer.md`](.claude/agents/ux-designer.md), not by the implementer. Give
it the file and the rules already settled, and ask it to flag what should stay as it is.

## Encodings

Repository documentation is UTF-8 without BOM with Windows CRLF line endings. So is source.
