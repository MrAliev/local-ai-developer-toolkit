# Model-aware Local AI Routing Design

[Русская версия](2026-07-29-model-aware-routing-design.ru.md)

## Purpose

Route every local-model task to one or more suitable models, prefer work that
can reuse a model already resident in VRAM, and learn which model is best for
each task profile without removing the established fallbacks.

All model discovery, installation, inference, and residency checks must pass
through LocalLm MCP and the durable LocalAi broker. Clients must never call
Ollama directly.

## Hardware and Safety Boundary

- The only supported inference device is one RTX 5080 with 16 GB VRAM.
- Model weights, KV cache, context, images, and runtime overhead must fit fully
  in VRAM.
- CPU or system-RAM offload is prohibited.
- Context is selected from power-of-two tiers between 2K and the model's official
  maximum, up to 256K, and is accepted only when the full runner fits in VRAM.
- A new model is loaded with an empty preflight request before it receives task
  content. `/api/ps` must report `size_vram == size`.
- A failed preflight unloads and disables that model/context combination.
- Before a cold switch, every other catalog-managed runner is unloaded.
  Unknown external Ollama processes are not modified.

## Versioned Model Catalog

Add one shared, versioned `model-routing.json`. It defines:

- trusted model tag and source;
- lifecycle status: `established`, `experimental`, `recommended`, or `disabled`;
- supported capabilities and task profiles;
- context and image constraints;
- ordered candidates and established fallbacks;
- installation policy;
- output validator;
- experiment and circuit-breaker policy.

LocalLm sends a task profile, input-size metadata, output requirements, and
workflow hints. The broker selects the concrete model immediately before
execution. An explicit model override remains possible but cannot bypass
installation, capability, context, or VRAM checks.

## Initial Task Routes

- Plain translation: experimental `translategemma:12b`, fallback
  `qwen3.5:9b`.
- Structured Markdown and technical translation: experimental
  `translategemma:12b`, then `qwen2.5-coder:14b`, then `qwen3.5:9b`.
- Image translation: experimental `translategemma:12b`; fallback
  `qwen3-vl:8b-instruct-q8_0` OCR followed by a text translator.
- OCR, screenshots, diagrams, and scanned PDF pages:
  `qwen3-vl:8b-instruct-q8_0`; `qwen3.5:9b` is allowed only for simple images.
- Vector indexing and semantic queries: `qwen3-embedding:8b-q8_0`. The index
  header remains authoritative, so embeddings from different models are never
  mixed.
- Exact code and file search: deterministic filename, lexical index, and `rg`
  paths; no language model.
- Optional deep code-search reranking: an already resident
  `qwen2.5-coder:14b` or `qwen3.5:9b`; the existing hybrid rank is the fallback.
- Code analysis, repair, review, and build-log triage:
  `qwen2.5-coder:14b`, with `qwen3.5:9b` for smaller mechanical work and
  `gpt-oss:20b` for deep critique when fully resident.
- Extraction, classification, and short summaries: `qwen3.5:9b`, or the coder
  model for code-heavy input.
- Multi-file synthesis, hypotheses, and planning: `gpt-oss:20b`, falling back
  to `qwen3.5:9b` or the coder model according to content.
- Arbitrary local-file search: deterministic filename and text search first;
  authorized semantic indexes use `qwen3-embedding:8b-q8_0`.

Only `translategemma:12b` is a new recommended installation in the first
release. Existing installed models remain available as established fallbacks.
An unofficial reranker is not installed.

## Experimental Selection

Experiment counters are independent for every
`task_profile × experimental_model` pair.

- The experimental candidate runs first for its first 10 tasks in that profile.
- A technical, structural, or context failure runs the established fallback.
- The candidate is not promoted by results from another task profile.
- After task 10, that profile pauses the experiment and produces a report.
- Owner feedback is rejected until that ten-task report gate pauses the pair.
  `continue_experiment` is the only early exception and may reset a circuit
  opened by two consecutive technical failures.
- The owner rates the result as better, the same, or worse and chooses
  `promote`, `continue_experiment`, `fallback_only`, or `disable`.
- After promotion, an already resident suitable model is preferred.

Any CPU offload disables the exact model/context combination immediately. Two
consecutive technical failures open a circuit breaker. A structural failure
always falls back for the current task.
An execution exception is classified as a technical failure, recorded without
task content, and routed to the established fallback. Multi-step workflows
preserve that experimental failure category while incrementing the experiment
only once at logical completion.

## Model-aware Scheduling

The durable queue remains the source of truth, but execution is model-aware.

1. Group compatible jobs by selected model.
2. Before a model switch or a long job, wait up to two seconds for declared
   related work.
3. Freeze a snapshot of the selected model group.
4. Execute that snapshot from predicted shortest job to longest job.
5. Do not allow later arrivals to enter the frozen snapshot.
6. Recalculate all groups after the snapshot completes.

The duration estimator uses task profile, model, input/output buckets, file or
image count, image resolution, and cold/warm state. It learns rolling median
and p90 durations. Unknown work uses conservative `short`, `medium`, or `long`
classes.
Each successful broker execution feeds its actual duration and selected model
back into this estimator.

Selection primarily avoids a model switch, then accounts for observed load
cost, shortest work in the group, task age, original priority, and queue
sequence. Age gradually offsets switch cost. A job waiting 15 minutes enters
the next compatible snapshot, but a running snapshot is never interrupted.

`workflow_id`, expected steps, and candidate models let clients enqueue
independent translation chunks, PDF pages, embedding batches, or analysis
steps together. Dependent steps reserve their expected model class. This makes
future work visible without guessing.

## Model Installation and Residency

At MCP startup, the catalog is compared with `/api/tags`. Missing recommended
or experimental models are enqueued as allowlisted maintenance pulls. A pull:

- is initiated through MCP and the broker;
- uses Ollama `/api/pull` with `stream: false`;
- starts only when no inference work is queued;
- never accepts an arbitrary user-supplied tag.

The new TranslateGemma candidate becomes eligible immediately after a
successful pull and preflight. Models are not all preloaded into VRAM. The
current model remains resident until an incompatible group needs the memory or
the machine has been completely idle for 30 minutes.
Idle unload happens once and only when no queued or running work exists;
dependency-blocked workflow work prevents unloading.

## MCP Contract

Add:

- `local_models_status`
- `local_model_preflight`
- `local_models_sync`
- `local_model_experiment_report`
- `local_model_feedback`
- `translate_local`

`translate_local` is the validated local translation path. The calling agent
owns the policy decision between local and cloud translation.
`local_model_preflight` sends no task content and returns the broker's
model/context residency proof.

Revise:

- `ask_local` accepts a task profile;
- `read_image` distinguishes OCR, visual analysis, and image translation;
- `triage_log` always uses the log-triage profile;
- model discovery reads live broker state instead of a static inventory.

CodeSearch continues to use the embedding model recorded by the index.

## Privacy-safe Observability

Telemetry stores no prompts, answers, file contents, image data, paths, or
secrets. It records:

- task profile and selected model;
- queue, load, execution, and total duration;
- cold/warm state and model switches;
- token-size buckets and selected context;
- full-GPU verification;
- validator result, error category, and fallback;
- local input/output tokens and total local processing;
- estimated avoided cloud generation and net cloud-context reduction;
- final owner rating.

All chunks of one logical task share a workflow ID. Only one validated workflow
completion increments the experiment counter, regardless of chunk count or a
single fallback pass. Experiment telemetry is retained for seven days.

After 10 experimental tasks, the report shows success, error, and fallback
counts; mean, median, and p90 duration; cold/warm results; load/unload count;
automatic quality checks; local tokens processed, avoided cloud generation,
net cloud-context reduction; and comparison with the established fallback.

Low-cost verification is allowed for translations, image/PDF parsing, search
benchmarks, structured extraction, compilation, and tests. Verification cost
is subtracted from the saved-token estimate. Sensitive content is never sent
to a cloud verifier without separate authorization.

## Verification

Implementation uses TDD with fake Ollama responses and deterministic time.
Tests cover routing, per-profile experiments, fallbacks, circuit breakers,
model-affinity snapshots, shortest-job-first ordering, the two-second window,
workflow hints, starvation prevention, duration learning, model pull,
model-specific 2K-through-256K preflight tiers, full-GPU validation, unload,
telemetry sanitization,
translation chunk validation, CodeSearch compatibility, and live catalog
discovery.

After focused tests, the entire solution is built and tested. Updated LocalAi,
LocalLm, and CodeSearch artifacts are installed for Codex and Claude.
`translategemma:12b` is then downloaded through the new MCP route and verified
on the RTX 5080. Git commit and GitHub publication require separate approval.
