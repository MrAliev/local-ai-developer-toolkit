# Local AI Developer Toolkit (LocalAi)

**The full picture, for developers who work with Codex and Claude every day**

[Русская версия](overview.ru.md)

This document describes the current state of `main`. Features land on users' machines with
the next release: delivery goes through GitHub only — change → release → installer — and
hand-made builds are never placed into the runtime.

---

## 1. Why this exists

An agent in the cloud answers "where does logic X live in this repository" the only way it can:
by reading candidate files whole. Five misses in a row and tens of thousands of tokens have
gone into the context, of which hundreds were useful. The same goes for a 600 KB build log, a
screenshot of an error, and a request to "list every TODO in these files".

LocalAi moves exactly that work — search, reading images, triaging logs, mechanical passes over
known files — onto local models running on the developer's graphics card. The cloud model
receives a result rather than raw material.

| Effect | How it shows up |
|---|---|
| Context is spent on reasoning, not on raw material | The whole task fits in the window, rather than half of one file |
| Answers by meaning, not by string match | "Where is payment cancellation handled" finds code that never says "cancel" |
| Private by default | Local models see the code; only what the agent decides to send leaves the machine |
| Predictable GPU load | Every local task goes through one queue instead of competing for video memory |
| One index for every tool | Codex, Claude, the CLI and the Git hooks share one view of the repository |

What this does **not** replace: reading a file before editing it, a literal sweep for a known
token, and architectural decisions.

---

## 2. What gets installed on the machine

![Diagram: what an installation is made of](images/overview-01-arhitektura.png)

Fifteen source projects and ten test projects; day to day, what matters is six executables
from an immutable version directory plus a launcher that stands apart from them.

| Component | Role |
|---|---|
| Broker | The queue, and the only transport to the models |
| `codesearch` server | Code search and navigation over MCP |
| `locallm` server | Local models over MCP |
| `codesearch` CLI | Indexing and search from the command line |
| `localai` CLI | Repository setup, hooks, policies, diagnostics, cleanup, telemetry |
| Launcher | The stable entry point: clients register against it rather than against a version path |

A version directory's path changes with every update; the launcher's path never does. That is
why the MCP registrations in Codex and Claude point at the launcher.

---

## 3. The broker: why everything goes through one queue

Talking to the Ollama endpoint directly is not supported. The reason is not bureaucratic: on a
single 16 GB card, two concurrent tasks against different models mean unloading and reloading in
a circle — both end up several times slower than they would have been in sequence.

![Diagram: the path of one request](images/overview-02-put-zaprosa.png)

The parameters below come from the code, not from the documentation.

| Mechanism | How it actually works |
|---|---|
| Durable queue | A task survives a client crash and a broker restart; a lease with a heartbeat stops two processes taking the same task |
| Watchdog | Separate from the heartbeat: ten minutes of silence before the first probe, then a probe a minute with a ten-second timeout; an attempt fails only after two consecutive confirmed unhealthy probes, and an inconclusive probe resets the counter |
| Residency | Full residency is required by default: a model is accepted only when all of it is in video memory. The relaxations are "allow partial offload" and "allow CPU" |
| Scheduler | Prefers work for an already resident model, gathers related tasks for two seconds, orders the snapshot by predicted duration, and force-includes anything older than fifteen minutes |
| Contexts | Discrete steps from 2K to 256K tokens; a step is used only when preflight proved the runner fits entirely |
| Experiments | A new candidate runs on the first ten completed tasks of a profile, then pauses until the owner decides: promote, continue, keep as fallback only, or disable |

Partial offload to system memory is not an error — it makes the answer several times slower
without failing. The slowdown announces itself only because the report line is made to say so,
beside the model it names. That is precisely why it is forbidden by default, and why relaxing it
stays visible in the run report.

### 3.1 The queue: three properties visible in use

**Deduplication by key.** Before enqueuing, the queue scans everything queued and running. If an
existing task carries the same deduplication key, a new one is **not created** — the caller
receives the identifier of the task already in flight and joins it. Two clients asking for the
same thing at the same time pay for one piece of work, not two.

**Quarantine instead of corruption.** A task record that cannot be read or parsed is neither
deleted nor repaired in place: it moves to quarantine and the queue carries on. One broken
record therefore does not stop the pipeline and remains available for inspection. Diagnostics
report the quarantine count. The quarantine itself is bounded like everything else the runtime
produces — by age, entry count and total size — with a grace period a fresh record cannot be
deleted inside, so there is always time to look at what just went wrong.

**The archive is a handover, not a journal.** The client collects a response on its next poll,
so the response body is short-lived: a minimum protection period applies first, then age and
size limits, and what is removed is the body — the request and the state document remain as a
trace. Cleanup runs continuously but no more than one pass a minute and with a per-pass limit,
so the queue is not left idle behind housekeeping.

### 3.2 The model catalogue and the routes

The routing catalogue is embedded in the broker. In the current version: six models and
seventeen task profiles.

| Model | Status | Context steps | Images |
|---|---|---|---|
| `qwen2.5-coder:14b` | established | 2K–32K | no |
| `qwen3.5:9b` | established | 2K–256K | yes |
| `gpt-oss:20b` | established | 2K–128K | no |
| `qwen3-vl:8b-instruct-q8_0` | established | 2K–256K | yes |
| `qwen3-embedding:8b-q8_0` | established | 2K–32K | no |
| `translategemma:12b` | experimental | 2K–128K | yes |

The catalogue also marks models whose reasoning Ollama can switch off, and the broker sends
`think: false` for exactly those — today, `qwen3.5:9b` alone. A reasoning model on the small
context tiers routing favours can spend the entire generation window thinking and return an
empty answer; gpt-oss cannot have its reasoning disabled at all, which is why the flag is
per-model rather than unconditional.

Routes are ordered lists of eligible models rather than a single binding:

| Task profile | Model order |
|---|---|
| Code analysis, code review | `qwen2.5-coder:14b` → `gpt-oss:20b` |
| Code editing, log triage | `qwen2.5-coder:14b` |
| Code reranking | `qwen2.5-coder:14b` → `qwen3.5:9b` |
| Extraction, classification, short summary | `qwen3.5:9b` → `qwen2.5-coder:14b` |
| Multi-file synthesis, planning | `gpt-oss:20b` → `qwen3.5:9b` → `qwen2.5-coder:14b` |
| OCR, visual analysis | `qwen3-vl:8b-instruct-q8_0` |
| Translation: plain, technical, from an image | `translategemma:12b` |
| Vector embeddings | `qwen3-embedding:8b-q8_0` |
| Literal search | no model at all — deterministic lexical matching |

There is a separate list of models allowed to be installed automatically through the queue; it
currently holds one entry. A model cannot be installed by saying "install this one" — there is
no general download command, and models already installed are never removed.

### 3.3 Experiments and the circuit breaker

A model marked experimental is not assigned silently and permanently.

| Mechanism | Rule |
|---|---|
| Trial batch | A candidate runs on the first **ten** completed logical tasks of the profile, after which the profile/model pair is paused |
| Report before decision | Feedback before that pause is refused: the decision is made on the batch, not on a first impression |
| Circuit breaker | **Two consecutive** technical failures open the circuit and the candidate stops being selected |
| Resetting the breaker | The only early exception is an explicit "continue the experiment" |
| Owner decisions | Promote · continue · fallback only · disable |
| Composite tasks | Fragments of one translation share a stream identifier and spend a single attempt — after the final check, keeping the original failure category |

Technical, structural and context errors trigger the fallback path along the established model
order. `localai telemetry` shows how often that happens on a given machine.

---

## 4. Indexing

### 4.1 What ends up in the index at all

Only allowed extensions are indexed — around fifty: sources (C#, TypeScript/JavaScript, Python,
Go, Java, Kotlin, Rust, Ruby, PHP, C/C++), markup and templates (XAML, XML, Razor, CSHTML, HTML,
CSS/SCSS/LESS), queries and schemas (SQL, GraphQL, Proto), documentation (Markdown, TXT,
AsciiDoc), configuration (YAML, TOML, INI, JSON), scripts (PowerShell, Bash), infrastructure
(Terraform, Bicep) and project files.

Deliberately excluded: package lock files, minified bundles, designer files and generated C#.
They produce tens of thousands of chunks and never answer a question anyone asks. A file with a
zero byte inside is treated as binary and dropped, even with a textual extension.

### 4.2 How it is chunked

![Diagram: how sources become chunks](images/overview-05-chanking.png)

| File type | Rule |
|---|---|
| C# | A chunk for the type as a whole (with its fields and a table of contents of its members) and one for every **executable** member: method, constructor, destructor, operator, conversion operator, indexer, and property **with a real body** |
| C#: what gets no chunk of its own | Fields and auto-properties — they ride inside the type chunk. A DTO with twenty properties must not become twenty-one useless vectors |
| C#: generated code | Not indexed at all — parsing stops before the first chunk |
| TypeScript and Python | One chunk per definition, on the boundaries the external indexer reports: real symbol, signature and exact lines. A definition containing others lists its children rather than repeating their bodies |
| TypeScript: a declaration with no reported boundary | `scip-typescript` reports no body for a declaration whose initialiser is a call — which is exactly `export const X = memo(...)`. Such a declaration still gets a chunk: the boundary is read off the file, as far as the line before the next thing the indexer named |
| TypeScript: what gets no chunk | A declaration inside a function body (the indexer calls it `local N` — no name, no span) and a declaration that fits on one line: one line is not a body, and a vector per line does not pay for itself. Both stay with the window |
| Everything else, and any region no definition covers | A sliding window over lines: **60 lines per window, 12 lines of overlap**, a step of 48 lines. No syntax is parsed, but exact line numbers are kept — and they are what makes a hit actionable |

The overlap exists so that meaning is not lost exactly at a window boundary.

**What symbol chunking gives, and what it does not.** The boundaries are not invented by a
parser — they come from the navigation index that is built before embedding anyway. A hit in a
symbol-chunked language names the type, member or definition it came from; a windowed hit names
the file and line range. Not every region can be covered by a symbol, and for uncovered regions
the window is still correct: imports, module-level code, the gaps between functions, and
anything the indexer gives neither a name nor a boundary for.

The price of symbol chunking is a larger corpus: a definition gets a vector of its own instead
of sharing a window with its neighbours. Changing the chunk format rebuilds the base generation
completely.

What helps regardless of chunking: twelve lines of overlap keep meaning from being lost at
a seam; a header with the path and line range is added to the chunk text, so a vector "knows"
where it came from; the lexical branch of search matches the words of the query against the
symbol, the signature, the chunk body and the file path, in that order of weight; and
precise navigation is a separate layer that does understand TypeScript and Python.

### 4.3 Base, overlay and dirty overlay

![Diagram: base, branch overlay and uncommitted code](images/overview-03-indeks.png)

The order in which the base is chosen was checked against the code: first the ref recorded in
the repository manifest, then a local `dev`, then a local `main`. Promoting a remote branch does
not by itself move the base.

A repository is identified by its normalised Git common directory, so Claude, Codex, the CLI and
every worktree share one repository identity and one index.

### 4.4 Keeping it current

The shared Git hooks — after a commit, a merge, a history rewrite and a branch switch — trigger
synchronization: the hook runs the same `localai sync` the command line runs, in its own
process, and Git waits for it to finish. Nothing is deferred to a queue. The hook itself never
talks to a model. Installing the hooks is an explicit, consented operation. Two
synchronizations of the same repository do not interleave: the second one waits briefly and
then exits with a named "repository busy" outcome before touching any shared state, instead of
stamping its failure over the other run's story.

They are installed where git actually looks. Usually that is `$GIT_DIR/hooks`, but when the
repository sets `core.hooksPath` — and husky, lefthook and simple-git-hooks all set it, husky
from `npm install` — they go there instead. For husky that means the `.husky` directory rather
than `.husky/_`, which husky recreates on every run. An existing hook of the same name is not
overwritten: it is kept alongside and called first, and a non-zero exit from it stops the chain.
One case installs nothing at all: a hook LocalAi did not write, with a copy from an
earlier install already saved beside it. Keeping the first would overwrite the second, so
the command stops, names both files, and leaves the choice where it belongs.
Files that land in the working tree are added to `.git/info/exclude` so they never show up in
`git status`.

### 4.5 Search

Hybrid: vector similarity plus lexical matching. The vector branch is cut off by a floor
**calibrated for the specific embedding model**; with no profile for that model, search fails
rather than borrowing somebody else's floor. Candidates with a positive lexical score are
admitted even with a low vector score — that is how a query by exact symbol name works.

Before results are returned, the repository identity, generation, tree, model, dimension, format
and checksum are all checked. Mixing a new base with an old overlay, or quietly answering from
stale code, is not possible.

A loaded index frees its memory automatically after ten minutes without queries; a separate tool
releases it immediately, leaving the file on disk untouched.

When no embedding model answers at all, the vector branch cannot run and the search falls back
to literal matching rather than failing — lexical hits beat an exception. The answer says so, in
those words, and stops naming the embedding model: the model comes out of the index header, so
printing it after nothing was embedded reports a comparison that was never made. The empty
result carries the same line, and that is the case that matters most — a plain-language query
has almost nothing literal to match, so "no matches" in this state means the half of the tool
that answers such questions did not run, not that the repository has no such code.

### 4.6 Precise navigation and its honest degradation

Go-to-definition has to be different from a text search, so navigation has three sources and a
strict order between them.

| Source | When it works | Precision |
|---|---|---|
| Live language server | While the document is open through the matching tool. Off by default, enabled by its own settings file | Authoritative |
| Precise navigation snapshot | Built with the index generation: C# and XAML are parsed directly, TypeScript and Python are imported from external indexers | Exact |
| Bounded text search | Only when the first two did not answer | Marked as heuristic |

Separately, what ties search to navigation. A search hit names a path and a line range but not
the column of the identifier within the line, so the natural call passes column 0. A position
that names nothing itself resolves to the line's outermost declaration — for a single-line
method signature, the method rather than its parameters, which is what makes the hit's start
line usable for navigation as it is. A line of sibling declarations
(`const a = f(), b = g()`), where neither contains the other, has no unambiguous answer, and
there the degradation notice is returned rather than whichever of the two came first.

Two exceptions: finding implementations and finding relationships have no text approximation at
all — a plausible answer there is worse than an empty one. When an answer did come from a
heuristic, it says why, and the two possible reasons are worded differently because only one of
them is curable: a generation built before precise navigation existed is fixed by resyncing,
while a symbol absent at the given position is not.

---

## 5. Twenty tools

### The codesearch server — 11

| Tool | Purpose | When to call it |
|---|---|---|
| `search_code` | Code search | The first step for "where does X live", "what handles Y", "is there already something like Z" |
| `get_code_chunk` | Fetch a chunk | The full body of a hit, by the identifier from the search results |
| `go_to_definition` | Go to definition | The definition of a symbol at a position |
| `find_references` | Find references | Every use of a symbol |
| `find_implementations` | Find implementations | Implementations, overrides, derived types — deliberately with no text approximation |
| `find_relationships` | Find relationships | The snapshot's relationship graph, incoming and outgoing |
| `index_status` | Index state | Whether an index exists, which model built it, whether it is behind HEAD, the synchronization phase |
| `index_refresh` | Refresh the index | Targeted; refuses to do large work inside the call and returns the command to run in the background |
| `index_unload` | Unload the index | Release memory immediately |
| `lsp_open_document`, `lsp_close_document` | Live language server | The authoritative navigation source while a document is open |

### The locallm server — 9

| Tool | Purpose | When to call it |
|---|---|---|
| `read_image` | Read an image | Screenshot, scan, diagram, PDF page — as a file on disk |
| `triage_log` | Triage a log | Build and test logs, dependency dumps, query plans, long stack traces |
| `ask_local` | Ask about known files | Mechanical work: list, summarise, extract |
| `translate_local` | Local translation | With a check that the structure survived |
| `local_models_status` | Model state | Installed, resident, recommended-but-missing |
| `local_model_preflight` | Model preflight | Proof of full residency with no payload |
| `local_models_sync` | Sync models | Install the missing ones through the queue |
| `local_model_experiment_report` | Experiment report | Timings, errors, fallbacks, cold and warm loads |
| `local_model_feedback` | Model feedback | The owner's decision on one task/model pair |

Every tool on the second server returns a line naming the model it used and its estimate of the
cloud tokens avoided. That line belongs in the reply: the point is for it to be visible when
work went downstairs, not merely that it got done.

### How a long log is triaged

A log does not have to fit in the model's context — nor, in whole, in memory.

1. **Choosing a step.** The broker checks the configured context steps from largest to smallest
   and takes the largest one proven fully resident on this machine.
2. **Slicing.** The file is read as a stream and cut into fragments sized for the chosen
   context, with overlap, so an error split by a fragment boundary is not lost.
3. **Sequential triage.** Each fragment goes through the shared queue, strictly in order.
4. **Bounded folding.** Partial results are merged hierarchically, so neither the whole log nor
   an unbounded list of summaries is ever held in memory; repeated evidence collapses.

The behaviour is configured by a policy file read **before every call** — no rebuild, no
restart. It sets the context ceiling, the reserve for the answer, the characters-per-token
estimate, and maximum fragment, overlap and partial-summary sizes. Defaults: a 256K token
ceiling, a reserve of 4096, a conservative 2.0 characters per token, fragments up to a million
characters, overlap up to 2048.

Note: the configured context is only a ceiling. It does not override the residency check, and if
a larger step is not confirmed the pipeline steps down rather than spilling into system memory.

---

## 6. Protections

### 6.1 The untrusted boundary around source

Repository code is data, not instructions. But a comment may say "ignore previous instructions",
and a vendored directory may contain anything at all. So everything derived from source is
returned inside its own block, marked with a random 96-bit nonce.

![Diagram: the untrusted boundary around source](images/overview-04-granica-nedoveriya.png)

How it actually works:

| Property | Behaviour |
|---|---|
| Where the nonce comes from | A cryptographic generator, 12 bytes |
| Uniqueness check | The nonce is checked for absence from the content itself — up to ten attempts; if none is found the call fails rather than returning a forgeable boundary |
| Escaping the origin | The attribute carrying the path is escaped in full, control characters included — a path cannot close the tag |
| Marker symmetry | The opening and closing markers carry the same nonce, so a "closing" tag cannot be planted in the source |
| What stays outside | Only what is trusted: the index summary, diagnostics, validation errors |

**Ten tools use the boundary.** On the `codesearch` server: search, chunk retrieval,
go-to-definition, find references, find implementations and find relationships — paths, symbol
identifiers and ranges were written by whoever wrote the repository, so they are data as much
as a snippet is. On the `locallm` server: `read_image`, `triage_log`, `ask_local` and
`translate_local` — a local model read files, logs or images, and a weak model can repeat or
amplify an instruction planted in them, so its answer arrives inside the same boundary while
the notice line stays outside as this process's own words.

### 6.2 The result identifier

A chunk identifier is opaque and carries the repository, generation, tree, the hash of
uncommitted content and an ordinal, plus a checksum of the payload.

An important precision, written into the code itself: the checksum is **not an authentication
boundary**. It catches accidental corruption and casual tampering. What authorizes reading
source is something else — equality of repository, generation, tree and dirty overlay, checked
before anything is read. An identifier from another repository, from an old generation, or with
an ordinal out of range is rejected rather than resolved against a different snapshot.

### 6.3 Transport, permissions and delivery

| Boundary | What it guarantees |
|---|---|
| One transport | The only path to the models is the broker; the agent-facing Ollama endpoint is unsupported |
| Runtime permissions | Directories are brought to their expected shape and validated in a single pass. Separate passes left a race window: Claude and Codex running concurrently hit a spurious permission failure |
| Protocol cleanliness | The MCP servers reserve standard output for the protocol |
| Release signature | The public key is embedded in the binaries rather than sitting in a file beside them: a package signed with someone else's key cannot be substituted |
| Immutable versions | A published version directory is never rewritten; rollback is the activation of a previously verified directory |
| Atomic switching | Activation requires naming the pointer being replaced, so a concurrent activation cannot be silently overwritten |
| Graceful stop | The broker is asked to finish: it notices the request on its per-second heartbeat, stops taking new work and finishes what it has. Only what has not left by the deadline is killed |
| Growth limits | Index generations, installed versions, backups, the task archive, response bodies, the quarantine and telemetry are all bounded |

One subtlety about retention: the minimum lifetime of a response body protects correctness
rather than disk — a response younger than that is deleted by nothing, and a manually configured
zero is clamped rather than obeyed.

---

## 7. Cloud tokens saved

### 7.1 Method

Every local call reports what it cost and what it saved: which tool and model ran, how long it
took, and the tokens avoided. The duration was measured all along — the broker records how long
a job waited and how long it ran — and went only into experiment telemetry, so the line that
reports a call could not state it and whoever read that line had to guess. It comes from the
receipt now, with the wait named separately when it is a real share of the total: four seconds
behind another client is a queue to look at, four seconds of inference is a model to look at.
`search_code` does go through that queue — embedding the query is a broker job like any
other — but the receipt does not reach the tool, and the time a search takes is more than the
embedding anyway: loading the index and composing the overlay are the rest of it. So it times
itself around the whole search.

There is no live token counter in the system, so every number is an estimate and is given as a
range. The conversion constants were checked against the code: **4.0 characters per token** for
Latin text, **2.2** for Cyrillic, and for images **the pixel area divided by 750**. The saving
is "what it would have cost to drag this raw material into the cloud context" minus "what was
actually spent".

Not to be confused with another constant: log triage has its own, deliberately conservative
**2.0 characters per token**, and it exists to size fragments for a model's context rather than
to count savings.

### 7.2 Measured on a given machine

`localai telemetry` prints the measured summary for the machine it runs on: how tasks ended,
cold versus warm model loads, fallback share, queue and execution latencies as nearest-rank
percentiles, and the estimated saving — by model and by task profile. When one model with
enough jobs fails at least a quarter of them and owns at least half of all recorded failures,
the summary names it in one `attention` line — a failing model that the fallback quietly
covers is otherwise invisible behind acceptable profile-level success rates. The expensive
thing to delegate is not translation or OCR, but work over large volumes of code and logs.

### 7.3 The arithmetic for typical tasks

An average source file runs to a few thousand tokens, so answering "where does X live" by
reading ten candidate files whole costs tens of thousands of them; a search answer is roughly
one and a half thousand. A build log of hundreds of kilobytes is on the order of 150K tokens
read whole and a few hundred as a triage summary. A full-screen screenshot is a couple of
thousand tokens as pixels and a couple of hundred as extracted text. Surveying an unfamiliar
repository whole runs into millions of tokens — with an index, that task becomes possible at
all.

### 7.4 Honest estimates, and the cases where there is no saving

Search saves context radically, but it produces a good short list of candidates rather than a
guaranteed single right answer.

| Situation | Why there is no saving |
|---|---|
| A screenshot pasted straight into the chat window | It has already been paid for; handing it to a local model afterwards buys nothing. Ask for a file path |
| A very small image | A button-sized screenshot costs a couple of dozen tokens; any useful answer about it is longer |
| A literal search for a known token | Nothing is cheaper than an ordinary text search |

### 7.5 Telemetry: what it collects and what it deliberately never holds

Every delegated task leaves one record in the runtime directory. `localai telemetry` is built
from those records.

| What is recorded | What the record never holds |
|---|---|
| Task identifier, profile, model, context size | Prompts and answers |
| Input and output sizes — **as buckets**, not exact values | File contents and image bytes |
| Cold load, model switch, fallback used | File paths and repository names |
| Validator result and execution outcome | Secrets of any kind |
| Durations: queue, load, execution, total | — |
| Token estimates: gross, spent on verification, net | — |

Retention: task records are kept for thirty days, experiment records for a week. An experiment
report therefore states honestly how many surviving records its timings are built on: the number
of attempts comes from the experiment state and outlives the measurements.

The saving in the summary is given as a range and split into gross, spent on verification, and
net. A sum of a hundred thousand estimates does not become exact by being large.

---

## 8. Everyday recipes

| Situation | What to do |
|---|---|
| "Where is subscription cancellation handled?" | `search_code` — describe it in words, not by symbol name |
| Found a fragment, need the full text | `get_code_chunk` with the identifier from the results |
| "Who calls this method?" | `find_references`, not a text search |
| "What implements this interface?" | `find_implementations` — an approximate answer here is worse than none |
| The build failed, the log is hundreds of kilobytes | `triage_log` with the path to the file |
| A colleague sent a screenshot of an error | Save it as a file, then `read_image` by path |
| "Collect the TODOs across these eight files" | `ask_local`, extraction profile |
| Search is answering oddly | `index_status`: check whether the index is behind HEAD |
| A long pause in the work and memory to spare | `index_unload`; the index stays on disk and the next search rereads it in about a second |

**A new repository.** Start with the read-only check (`localai repo status --root <repository>`).
If it is not connected, synchronize (`localai sync`) and install the hooks
(`localai hooks install`) — both require explicit consent. While the first generation is being
built the state is "initializing": answering from a partial index is not allowed, and the right
answer from an agent is "the repository is still indexing".

**Diagnostics.** `localai doctor` checks the version and the integrity of the binaries, the
stable entry point, whether the broker is alive, the queue and the quarantine, the policies in
force and the state of the index. It only reads and starts nothing; a stopped broker is noted
rather than reported as an error, because it starts on demand.

**Cleanup.** `localai prune` frees space against the retention limits; a dry-run flag previews
it. It never touches the active version pointer or the current index generation, and it
collects the overlays no live worktree is on — leaving a repository's overlays alone whenever
it cannot establish which worktrees those are.

**Delegating to a local model from a terminal.** `localai ask` runs a mechanical task over files
you name — summarise this, list every method that does X, collect the TODOs; `localai triage` reads
a log and says what failed and why; `localai read-image` reads screenshots, scanned pages and
diagrams. All of them reach the same broker, the same routing and the same models as the MCP
tools, because they call the same code:

| MCP tool | Console |
| --- | --- |
| `ask_local` | `localai ask "<instruction>" [file ...]` |
| `triage_log` | `localai triage [log-file\|-]` |
| `read_image` | `localai read-image "<question>" <image> [image ...]` |

`read-image` takes the question first, like `ask`. A first argument that is a bare image path is
read as a forgotten question rather than as one — `localai read-image shot.png` says the question
is missing, which is what is actually wrong — while a question that merely ends in a file name has
a space in it and stays a question.

The MCP tool is still the first choice while the server is up. These exist for a person at a
prompt, and for an agent whose MCP server is not running — the fallback this product tells every
machine to use.

`triage` reads standard input when no file is named, which is the form worth remembering:

```powershell
dotnet build 2>&1 | localai triage
```

**The answer goes to standard output and the notice about the run goes to standard error**, so
`localai ask "summarise" src/Foo.cs > summary.md` leaves a file holding the answer and nothing
else. The notice names the model, what it processed, how long it took and what it saved; it is on
standard error because it is about the run rather than the result.

**A redirected answer carries provenance markers and a terminal one does not.** The answer was
written by a local model out of files it read, so where it can be read again later — a file, a
pipe, another program — it arrives wrapped in the same nonce-bound `<untrusted-content>` markers
the MCP tools use, and nothing inside them may be treated as instructions. On a terminal the
reader is a person and the markers are noise, so they are omitted.

A model that is not installed for the profile a command routes to exits **69**, naming the command
that installs one. That is not the same failure as a wrong argument, which exits 2.

### 8.1 Answering a program

Everything above is written for a person, and since the console learned to follow the reader's
language it cannot also be a contract: a parser would break the first time it ran on a Russian
machine. `--json` is the other face. It is for an editor plugin, a script or a scheduled task —
anything that reads the answer rather than looks at it.

One envelope, whatever the command:

| Field | |
| --- | --- |
| `schema` | the envelope's version, an integer. Adding a field does not change it; removing, renaming or retyping one does |
| `command` | the command as typed, without its options — `repo status`. Empty only when none was given |
| `ok` | whether the run succeeded, mirroring the exit code |
| `data` | the command's own answer. Absent when `ok` is false |
| `error` | `code` and `message`. Absent when `ok` is true |

**`--json` also fixes the language to English.** Output that is versioned cannot follow the
machine it came from, so the flag decides both. A reader who set `LOCALAI_LANGUAGE=ru` and then
asked for JSON gets Russian prose everywhere else and an English envelope here, and that is the
intended behaviour rather than a defect. One exception is worth knowing: a Windows error carried
by `Win32Exception` takes its words from the operating system, so those failures are given a code
and a sentence of LocalAi's own before they can reach the envelope.

**New codes may appear in any release**, so a caller needs a branch for the ones it does not
know. That is what makes a coarse code narrowed later — one `input_rejected` becoming several
— an addition rather than a break.

**`code` is for branching, `message` is for showing to a person.** Never parse `message`; it is
reworded whenever it turns out to be unclear, and that is not a change to the schema. The codes
are `subject_state` — `root_value_missing`, `repository_ambiguous`, `argument_unknown` — and they
never name the command, because the envelope already carries it and the same refusal recurs across
commands.

**Exit codes are unchanged**, and `ok` follows the exit code rather than the outcome. The
difference is not academic: `localai sync` prints `REFUSED …` and exits 0 on purpose, because a
run that correctly declined to do something did exactly what it was asked. That run is `ok: true`
with the refusal inside `data`.

**Commands that do not answer `--json` refuse it** rather than printing prose, so the promise
holds without exception: if the flag was passed, standard output is an envelope. The usage block
marks the commands that take it with `[--json]`, and today those are `localai repo status`,
`localai ask`, `localai triage` and `localai read-image`:

```json
{"schema":1,"command":"repo status","ok":true,"data":{"repositoryId":"0ecc9019…","commonDirectory":"R:\\LOCALAI\\.GIT","status":"CONFIGURED"}}
```

| `data` | |
| --- | --- |
| `repositoryId` | the identity every runtime directory is named by, and what `SYNCED repository=` prints |
| `commonDirectory` | which repository this answer is about — the identity spelling: absolute, native separators, upper-cased on Windows. A plugin comparing it against its own workspace path must do so case-insensitively |
| `status` | `CONFIGURED` or `NOT_CONFIGURED`, the same token the prose prints |

`ask` and `triage` fill the same envelope with the answer and what the run cost:

```json
{"schema":1,"command":"ask","ok":true,"data":{"answer":"…","origin":"ask:R:\\repo\\src\\Foo.cs","model":"qwen3.5:9b","residency":"None","queuedMs":2241,"ranMs":5230,"savedTokensEstimate":253,"truncated":false}}
```

| `data` | |
| --- | --- |
| `answer` | what the model replied, bare — the markers of the prose face are a boundary for text with no structure, and here the structure is the boundary |
| `origin` | the command and what it read, the same value the prose face puts in the marker's attribute |
| `model` | the model that actually ran, which is not always the one a profile prefers |
| `residency` | `None`, `PartialOffload` or `Cpu` — how much of the model was in video memory. Anything but `None` means the answer was slower and the run said so |
| `queuedMs`, `ranMs` | waiting and running, separately: four seconds behind another client is a queue to look at, four seconds of inference is a model to look at |
| `savedTokensEstimate` | an estimate, and named so — it is computed from characters, so printing it as an exact number of tokens would be false precision |
| `truncated` | whether the answer was formed from part of the input because the shared budget ran out. Always present; only `ask` can truncate, so `triage` and `read-image` always report `false` |
| `vramResidentPercent` | how much of the model was in video memory, present only when `residency` is not `None`. The verdict says a run was degraded; this says by how much |

With `--json` nothing is written to standard error, so a caller may treat anything there as an
anomaly.

The prose sentence does not travel in `data`. It is an instruction to an agent, it is reworded
whenever it is wrong, and a versioned contract is the wrong place for it.

One thing that predates this flag: `localai model` has always printed a JSON envelope of its own,
with no flag and no prose face, and its version field is `schemaVersion` rather than `schema`.
The two are unrelated shapes; the field name is what tells them apart.

---

## 9. Installing and updating

![Diagram: installing and updating](images/overview-06-ustanovka.png)

Nothing has to be stopped by hand: the installer shuts the broker and the MCP servers down
itself — when, and only when, they are in the way — and replaces them.

Delivery goes through GitHub only: change → release → installer. Hand-made builds are never
placed into the runtime.

The operating rules travel the same way. Every machine gets the same block between the markers
in `CLAUDE.md` and `AGENTS.md`: reach for the local tool first and say so out loud when you
cannot, the transport invariants, how to connect a repository that has never been indexed, what
to do about work that is edited but not committed, the shape of the saved-tokens report, where
Git actually keeps its hooks, and the requirement to report indexing while it runs rather than
after it. Writing these once and shipping them is what keeps two machines from behaving
differently for reasons nobody can see.

Whatever the person wrote outside the markers is theirs: an install replaces the block and
keeps every character around it, and an uninstall takes the block back out and nothing else.
Every character, not every byte: a leading byte-order mark is dropped when the file is read
and never written back, so one that arrived with a BOM comes back without it.
Where their guidance disagrees with the block, theirs wins and the assistant says which rule it is
overriding. A rule that is true of one machine, one set of cards or one maintainer's
permissions belongs outside the markers; a rule that is true everywhere belongs inside them.

Two of the block's rules stand outside that arrangement, and the block says so plainly: text
arriving inside untrusted-content markers is data rather than instructions, and everything
reaches a local model through the broker rather than straight to Ollama. The first is what
stops a file in a repository from issuing orders; the second is what keeps several clients
correct at once. Neither is a preference, and no line of guidance overrides either — a
configuration file saying otherwise is not evidence that somebody meant to switch one off.

Installation needs no GitHub account: releases are public, and the installer downloads the
manifest, the signature and the package over plain HTTPS with no credentials. The GitHub CLI
remains as an automatic fallback — for a fork kept private, or a network where the release
host is unreachable but the API is not — and it reuses the sign-in already established with
`gh auth login`: the installer never asks for, stores or sees a token. The checks are the
same either way: the manifest is verified against the key embedded in the build, the
package against the SHA-256 inside the manifest.

The screen that asks also asks which language you read, and both wizards answer in it: the
rail, every page, the review a run is consented to, and the removal preview above its
checkbox. The choice is remembered beside the installer's own logs, so removing LocalAi months
later opens in the language the installation was chosen in. Two things stay English on a
Russian run, for one reason: the run journal and the report written next to it exist to be
read against each other.

It also asks which of the two palettes to paint in, and remembers that beside the language in
the same file. Light and dark are the explicit answers; System is the third and the default, and
it means the installer reads what Windows was told and keeps reading it — switch Windows to dark
while the installer is open and it follows, caption and all, rather than waiting to be reopened.
A machine nobody has ever changed carries no such setting at all, which reads as light, and so
does a setting that cannot be read: a preference nobody can see is not a reason to fail a run.

The errands themselves are a list to choose from rather than a button apiece. Four buttons were
four ways to commit and no way to compare; one column of rows means the reader picks a row and
then presses the single button at the bottom, which stays dead until they have. An errand the
machine does not allow stays on the list, recessed rather than hidden, with the reason in place
beside it — hiding it would leave somebody hunting for something that is right there. When
exactly one errand can run at all, choosing it is not a question, so the screen answers it and
the button is live on arrival.

Back on the wizard's first page returns to that list with the choice still on it. Nothing has
been changed by then, and every choice is meant to be revisitable until a run starts. Reached
from Apps and features there is no such list behind the wizard, so Back stays unavailable there
rather than opening a screen the person never saw.

Everything outside the wizards answers the same question without asking it. The CLI and both MCP
servers take the language from this computer, fall back to English wherever there is no
translation, and are overridden by `localai policy set --language <en|ru|system>` for every
process started afterwards. Their strings live in `.resx` pairs rather than in the code, so a
language is added by adding a file and a test refuses one that carries only some of the strings.
The one exception is the update-check disclosure. It is consent, and it has to make the same
promises in every language it is asked in — which a parity test cannot check — so it stays a
pair of constants in a single file, chosen by the language this process resolved to, with a
test of its own that refuses a supported language it has no answer for.
What does not follow the language is anything a machine reads back: commands, option names,
identifiers, the MCP tool descriptions, and every number, duration and date, which stay invariant
so that a line quoted verbatim by an agent means the same thing wherever it was produced.

The same executable is also the uninstaller, the updater and the repair tool, so starting it
asks which of those this run is and offers only what the machine allows, naming the reason for
the rest. An update also stops re-asking what it already has: prerequisites that are installed, models
that are pulled, the residency policy read from disk and the client registrations that exist
are folded away, so the rail shows four steps rather than eight and the counter says so.

The video-memory rule and the model choice are one page, in that order. They were two, and
the recompute a rule change triggers — relaxing it offers more models — landed on a page the
reader had already left, so they chose models under a filter the next page then silently
changed. On one page the rule sits above its consequence, and a line inside the model group
restates it with the count, so the dependency is legible after the radio buttons scroll away. The
release is folded with them — the errand settled which one it is, so resolving it is work
rather than a question, and it happens behind the first page instead of behind a button.
The page is still `System check` and still shows what was found; on this path what the reader
waits for is the release as much as the machine.
Two buttons on the review page bring back what was folded — one for the release, one for the
settings, because they are different questions. What the review says a run will do is what it
will do: when nothing is selected and no release verified, it says nothing will be applied
rather than describing work that is not going to happen, and it names each setting in the words
its page offered rather than in the identifier the code carries — a list somebody reads before
consenting is not the place for an enum member. Their
values still appear on the review page — a folded page must never become an unlisted effect —
and one button there brings all four back for the run where a carried-forward answer is wrong.

That answer travels with the window: the title bar names the errand, and the step rail
carries a version line on every page — `0.1.51` on a computer with nothing installed,
`0.1.50 → 0.1.51` for an upgrade, `0.1.50 → 0.1.50 (repair)` when the release asked for is the
one already there. The line has two halves and they follow different rules. The left half is
what was on this computer before the run: it is read from the version pointer once, behind the
first page, and never read again — an installation writes that pointer, so a later read answers
a different question, and the finish page turned `0.1.50 → 0.1.51` into `0.1.51 → 0.1.51
(repair)` at exactly the moment somebody was reading the outcome. The right half is what this
run is putting there. That is not history, and it is not always known when the window opens:
the release is resolved behind the first page, and a request left at `latest` is checked once
more immediately before installing, in case one was published while the wizard sat open. So it
moves — `checking…` while the feed is being asked, `no release` when the answer never came, the
version itself once there is one — right up to the moment the run settles it, and then stays.

An installation registers itself in Apps & features, whose entry runs a copy of the
installer parked inside the runtime root — so removal is still reachable long after the
downloaded file is gone. Settings live in a directory of their own — `settings` under the runtime root — rather than
loose beside the queue and the indexes. They used to be told apart from everything else by a
list of file names kept in the removal matrix, and the list fell behind: `semantic-navigation.json`
is a real setting the matrix classified as an unrecognised runtime file, so the reinstall that
promised to keep settings deleted it. A directory cannot fall behind, because adding a setting
and having the matrix know about it become the same act. Reading falls back to the old loose path for
installations that predate the split, and the write that follows takes that copy away — so the
old file is a fallback rather than a second copy holding a stale answer for ever. That mattered
more than it sounds: everything still building the old path by hand kept finding it while the
runtime read the other one, which is how the installer came to journal an undo record for a
file it had not written.

What the person chooses for themselves rather than for a machine lives outside the runtime root
altogether, in `%APPDATA%\LocalAi`, which roams with the profile and survives an uninstall.
The split is about what a setting is attached to: a residency policy is a statement about this
computer's graphics card and indexing limits are about its memory, so carrying either to
another machine would be carrying a wrong answer.

Removal is a matrix rather than one hammer: three presets over rows
that change one at a time, every removal listed before anything happens, the broker asked to
finish before the root is touched, and the release signing key kept unless separately
confirmed. It says where it has got to while it works — asking the broker to stop, rewriting
the client configurations, taking out the hook dispatchers, removing files, finishing — because
the stop alone can take two minutes, and a bar that has not moved for two minutes is what
makes people kill an installer mid-run. The page itself is one object rather than a part of
the uninstall window: which boxes a preset ticks is the contract between it and the planner,
and a clean reinstall needs the same page inside the installer, where a second copy would be a
second answer to that.

In both wizards a step is marked done because it was reached, not because it sits left of
where the reader is now. Position stood in for history until pages could be folded away and
revealed out of order — after which a reveal button that inserts three steps before the
current one moves every remembered position onto a different step, and a finish page reached
without the pages before it having run marks work that never took place. Marking a step done
is a claim about what happened to this computer, and it is the one claim a step rail must not
get wrong.

The reinstall-friendly preset keeps the client registrations and the hook dispatchers rather
than asking about them: the installation that follows rewrites all three, and a question put
twice gets two answers that disagree. The review page says so in the words of the arrival —
reached from a clean reinstall, that the installation to come rewrites them; reached by
picking the preset by hand in a plain uninstall, that nothing here disconnects a client and
which rows to tick to change that. The one thing that genuinely breaks in between is stated
the same way: dispatchers left pointing at a launcher that has gone are a permanent state when
nothing follows, and an interval when an installation does, so the kept-item note says which
of the two this run is producing, and the finish page stops calling stopping there free. The
Apps & features entry is left alone on that path for the same reason and one more: taking it
out and writing it back inside one run is churn on the good path, and on the bad one the
uninstaller's parked copy is what a removal launched from Apps & features runs from, so
deleting it falls to a retry loop that keeps trying for a minute after the process exits —
long enough to delete the copy the installation half has by then written, leaving an entry
that points at nothing and no way to uninstall. The entry is also true throughout: the run
ends with LocalAi installed.

A version directory is named after the commit it was built from, so the pointer alone cannot
say which release is installed. `bin\installed-release.json` records that, beside the pointer
rather than inside it — the pointer is written by the launcher under a compare-and-swap over
its exact bytes. The record names the directory it describes, so a rollback performed without
a manifest leaves a record that is detectably stale rather than a confidently wrong answer.

Knowing that a release exists is opt-in and off by default. When it is switched on, the broker
fetches the latest manifest and its signature at most once per interval — nothing about the
machine is sent — and believes the version only after the signature verifies. What it learns
goes into a state file that `localai doctor` and `index_status` read; nothing installs until
`localai update` is run, which refuses while the broker has queued jobs unless asked to wait.
Activation stops everything running out of the outgoing version and does not restart it;
clients start the new one the next time they reach for a tool.

Each run writes a journal of its effects to `%LOCALAPPDATA%\LocalAi-installer-logs` — the
intent before each effect, the outcome after — so a run killed mid-install still leaves a
record. A failed or cancelled run offers a rollback of what can be proven reversible: the
activation of an upgrade, the residency policy, the Ollama launch record and the client
configuration files. What stays — winget/npm installs, pulled models, a first installation's
root — is named plainly rather than implied undone. An interrupted run is offered back at the
next start; a run still installing in another window is recognised by its live lock and left
alone.

### 9.1 Activation, rollback and stopping

| Operation | How it works |
|---|---|
| Activation | Requires naming the pointer being replaced — either its checksum or the assertion "there is no pointer yet". Omitting both guarantees is a usage error; a mismatch is a refusal |
| Version rollback | Not an "undo" but the activation of a previously verified immutable directory: it was never rewritten, so it is available whole |
| Stopping | The broker is asked to finish: a request carrying its identifier and start time is placed beside the state document, the broker notices it on its per-second heartbeat, stops taking new work and finishes what it has |
| Force | Only what has not left by the deadline is killed. The broker owns a durable queue and may be in the middle of a multi-minute generation, so killing is a last resort rather than a mechanism |
| MCP tools | They hold no state of their own and have no channel to be asked, and the client restarts them itself, so for them stopping is simply exiting |

The stop request names the start time as well as the process identifier: otherwise a stale
request could take down a healthy broker that inherited the same identifier.

### 9.2 Settings that are read on the fly

None of them requires a rebuild; some do not even require a restart.

| File | What it sets | When it takes effect |
|---|---|---|
| Model policy | The residency requirement, keeping a model after idle | The broker reads it at start: one already running keeps the previous policy |
| Retention limits | Generations, versions, backups, the archive, response bodies, telemetry | On the next cleanup pass |
| Log triage profile | Contexts, fragments, overlap, characters-per-token estimate | Before every call |
| Language servers | Enablement, executables, timeouts, message size limit | The next time a document is opened |
| External indexers | Enablement, paths, arguments, parsing limits | On every synchronization |
| Update check | Whether releases may be looked up, and how often. Off by default | The broker reads it at start: one already running keeps the previous policy |

The wizard reads these files before it offers them, so a run that does not visit a settings page
writes back what the machine already had. Where a run does change one, the review page says so —
`Update check: off (currently on - this run changes it)` — because listing the value about to be
written reads as *what will be configured* rather than *what is being replaced*.

A corrupt policy file, or one of an unknown version, does **not** weaken the check: safe defaults
apply. A parse error never turns into silent permission. The update check is the sharpest case of
that rule — an unreadable `update-check.json` means *off*, because a file nobody can parse is not
consent to talk to the network.

---

## 10. Boundaries and limitations

| Limitation | What it means in practice |
|---|---|
| The hardware ceiling | The target is a single 16 GB card. A model that survives only by being spread across two cards pays for it over the bus, and simply will not start on one |
| Model tags are misleading | A tag like "8b" can mean a four-bit quantisation. Quality needs an explicit quantisation tag, checked against the registry manifest rather than against the name |
| Changing the embedding model | Requires rebuilding the index: the model is recorded in the header, and querying an index with a different model is impossible by design |
| Live language servers | Off by default, enabled by their own settings file |
| An NPU does not help | Everything goes through Ollama, whose backends are CPU, CUDA, ROCm, Metal and Vulkan |
| Local models are weaker | Good at "list this" and "summarise that", unreliable on subtle analysis; anything a decision depends on is verified |

---

## Appendix. Where things live

| Path | What |
|---|---|
| `bin\versions\<version>` | The immutable binaries of a version |
| `bin\launcher` | The stable entry point |
| `bin\current.json` | The active version pointer |
| `repositories\<id>\generations` | Index generations |
| `policy.json` | Model residency and keeping a model in memory |
| `retention.json` | Retention limits |
| `log-triage.json` | The log triage profile |
| `language-servers.json` | Live language servers, off by default |
| `semantic-indexing.json` | The external indexers for precise navigation |
| `update-check.json` | Whether releases may be looked up, and how often. Off by default |
| `update-state.json` | What the last look-up found; every surface answers from this file |
| `bin\installed-release.json` | Which published release the active version directory came from |
| `telemetry\metrics` | Task records |

Every path is relative to the LocalAi runtime directory in the user's local application data.
The installer's run reports and run journals live beside that directory, in
`LocalAi-installer-logs`, deliberately outside the validated installation root.
