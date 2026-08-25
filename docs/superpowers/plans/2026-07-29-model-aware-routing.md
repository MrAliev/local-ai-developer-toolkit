# Model-aware Local AI Routing Implementation Plan

[Русская версия](2026-07-29-model-aware-routing.ru.md)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route local work through a shared task-aware model catalog, keep all Ollama operations behind the durable LocalAi broker, minimize model swaps, validate zero-offload execution on the RTX 5080, and evaluate a new TranslateGemma candidate independently for the first ten tasks of every applicable profile.

**Architecture:** Extend the broker wire contract with task, workload, workflow, and maintenance metadata while preserving the existing explicit-model calls. The broker owns catalog loading, candidate selection, durable model-aware scheduling, Ollama lifecycle operations, preflight validation, circuit breakers, and privacy-safe metrics. LocalLm owns task-specific prompting, translation chunking, validation, and MCP presentation. CodeSearch continues to use the embedding model recorded in each index and never delegates exact lexical search to a language model.

**Tech Stack:** .NET 10, C# 14, xUnit, ModelContextProtocol 1.4.1, durable JSON queue state, Ollama HTTP API, PowerShell installation checks.

---

## Preconditions and execution boundaries

- The approved design is `docs/superpowers/specs/2026-07-29-model-aware-routing-design.md` with its synchronized Russian sibling.
- Use TDD for each behavior-bearing task: add the focused failing test, observe the expected failure, implement the minimum behavior, then refactor with the focused tests green.
- Keep source, identifiers, comments, test names, and Git messages in English.
- Keep documentation as synchronized English and Russian sibling files, UTF-8 without BOM, with CRLF line endings.
- Do not stage, commit, push, or publish to GitHub until the owner gives separate explicit Git authorization. The suggested commit checkpoints below are therefore conditional.
- Model installation through MCP and local binary installation for Codex and Claude are in scope after all automated verification succeeds.
- Never call Ollama directly from Codex, Claude, LocalLm, or CodeSearch. Test fixtures may host a fake HTTP endpoint because the broker transport is the component under test.
- Never pass prompts, answers, file contents, image bytes, paths, or secrets into telemetry.

## Task 1: Add the shared routing catalog and backward-compatible contracts

**Files:**

- Create: `model-routing.json`
- Create: `src/LocalAi.Contracts/ModelRoutingContracts.cs`
- Modify: `src/LocalAi.Contracts/BrokerContracts.cs`
- Create: `src/LocalAi.Broker/ModelRoutingCatalog.cs`
- Modify: `src/LocalAi.Broker/LocalAi.Broker.csproj`
- Modify: `tests/LocalAi.Broker.Tests/BrokerContractTests.cs`
- Modify: `tests/LocalAi.Broker.Tests/BrokerPayloadContractTests.cs`
- Create: `tests/LocalAi.Broker.Tests/ModelRoutingCatalogTests.cs`

- [ ] **Step 1: Write failing serialization and catalog tests**

Add tests proving:

- all task profiles and lifecycle states round-trip as strings;
- old `CreateChat(...)` requests still deserialize as explicit-model overrides;
- routed chat requests carry no concrete model but do carry task/workload/workflow metadata;
- catalog schema version is `1`;
- every route has at least one candidate and one established fallback;
- only allowlisted catalog tags can be maintenance targets;
- contexts use power-of-two tiers from `2048` through each model's official
  maximum, up to `262144`, and remain eligible only after full-VRAM preflight;
- `translategemma:12b` is experimental/recommended for translation profiles;
- `qwen3-embedding:8b-q8_0` is the only indexing model;
- exact code/file search routes are deterministic and have no language-model candidate.

Use these contract shapes:

```csharp
public enum LocalTaskProfile
{
    PlainTranslation,
    TechnicalTranslation,
    ImageTranslation,
    Ocr,
    VisualAnalysis,
    VectorEmbedding,
    ExactSearch,
    CodeRerank,
    CodeAnalysis,
    CodeEditing,
    CodeReview,
    LogTriage,
    Extraction,
    Classification,
    ShortSummary,
    MultiFileSynthesis,
    Planning
}

public sealed record LocalWorkloadMetadata(
    int InputCharacters,
    int ExpectedOutputCharacters,
    int FileCount,
    int ImageCount,
    long TotalImagePixels,
    LocalDurationClass DurationClass);

public sealed record LocalWorkflowHint(
    Guid WorkflowId,
    int StepIndex,
    int ExpectedStepCount,
    IReadOnlyList<LocalTaskProfile> ExpectedProfiles,
    bool IsDependencyReady);
```

- [ ] **Step 2: Run the contract tests and confirm RED**

Run:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~BrokerContractTests|FullyQualifiedName~BrokerPayloadContractTests|FullyQualifiedName~ModelRoutingCatalogTests"
```

Expected: compilation fails because routing types, routed factory methods, and catalog loader do not exist.

- [ ] **Step 3: Implement additive contracts**

Keep `CreateChat(...)` unchanged for compatibility and add `CreateRoutedChat(...)`. Add optional routing fields to `ChatJobPayload`; do not reinterpret a legacy concrete `Model` as an automatically selected candidate.

```csharp
public sealed record ChatJobPayload(
    string? Model,
    string Prompt,
    string? System,
    IReadOnlyList<string>? ImagesBase64,
    LocalTaskProfile? TaskProfile = null,
    LocalWorkloadMetadata? Workload = null,
    LocalWorkflowHint? Workflow = null,
    int? RequestedContextTokens = null);
```

Add a dedicated `ModelMaintenance` job kind and typed payload whose model tag is validated against the catalog. Do not expose a generic user-supplied pull payload.

- [ ] **Step 4: Add and embed the catalog**

Make `model-routing.json` the single source file and embed that exact file into `LocalAi.Broker`:

```xml
<ItemGroup>
  <EmbeddedResource Include="..\..\model-routing.json"
                    Link="model-routing.json"
                    LogicalName="LocalAi.model-routing.json" />
</ItemGroup>
```

The loader must reject unknown schema versions, duplicate tags, missing fallbacks, invalid contexts, unsupported capabilities, and routes that reference undefined models.

- [ ] **Step 5: Re-run focused tests and confirm GREEN**

Run the command from Step 2.

Expected: all selected tests pass with no new warnings.

## Task 2: Add broker-only Ollama lifecycle operations and full-VRAM preflight

**Files:**

- Modify: `src/LocalAi.Contracts/BrokerContracts.cs`
- Modify: `src/LocalAi.Broker/OllamaTransport.cs`
- Create: `src/LocalAi.Broker/ModelRuntime.cs`
- Modify: `tests/LocalAi.Broker.Tests/FakeOllamaServer.cs`
- Modify: `tests/LocalAi.Broker.Tests/OllamaTransportTests.cs`
- Create: `tests/LocalAi.Broker.Tests/ModelRuntimeTests.cs`

- [ ] **Step 1: Write failing transport and runtime tests**

Cover:

- `/api/tags` supplies the live installed-model set;
- `/api/ps` maps `size`, `size_vram`, context, and expiry without tolerating malformed numeric values;
- `/api/pull` uses `POST`, `{ "model": tag, "stream": false }`, and only a catalog-approved tag;
- empty `/api/generate` preflight uses the selected context tier and a bounded `keep_alive`;
- `size_vram == size` is required after preflight;
- a missing process entry, partial VRAM residency, or a context above the
  cataloged model maximum fails preflight;
- failure sends an unload request with `keep_alive: 0`;
- successful preflight returns the exact verified model/context pair;
- no request body or image data appears in thrown errors.

Use a typed proof:

```csharp
public sealed record ModelResidencyProof(
    string Model,
    int ContextTokens,
    long SizeBytes,
    long SizeVramBytes,
    bool FullyResident,
    DateTimeOffset VerifiedAtUtc);
```

- [ ] **Step 2: Run the focused tests and confirm RED**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~OllamaTransportTests|FullyQualifiedName~ModelRuntimeTests"
```

Expected: the new lifecycle operations and runtime coordinator are missing.

- [ ] **Step 3: Implement typed broker transport operations**

Add transport methods for tags, processes, catalog-approved pull, preflight generate, and unload. Keep `/api/pull` inaccessible through the free-form native CLI route.

```csharp
internal interface IModelRuntimeTransport
{
    Task<IReadOnlyList<OllamaModelInfo>> ListInstalledAsync(CancellationToken ct);
    Task<IReadOnlyList<OllamaProcessInfo>> ListProcessesAsync(CancellationToken ct);
    Task PullAsync(string allowlistedModel, CancellationToken ct);
    Task PreflightAsync(string model, int contextTokens, CancellationToken ct);
    Task UnloadAsync(string model, CancellationToken ct);
}
```

- [ ] **Step 4: Implement preflight and disablement**

`ModelRuntime.EnsureReadyAsync` must:

1. validate the catalog entry and requested context;
2. issue an empty preflight;
3. read `/api/ps`;
4. require exact ordinal model match and `SizeVramBytes == SizeBytes`;
5. unload and mark the exact `model × context` combination unavailable on failure;
6. never accept CPU/system-RAM offload.

- [ ] **Step 5: Re-run focused tests and confirm GREEN**

Run the command from Step 2.

Expected: all selected tests pass.

## Task 3: Implement deterministic candidate routing, experiments, and circuit breakers

**Files:**

- Create: `src/LocalAi.Broker/ModelRouter.cs`
- Create: `src/LocalAi.Broker/ExperimentStateStore.cs`
- Create: `tests/LocalAi.Broker.Tests/ModelRouterTests.cs`
- Create: `tests/LocalAi.Broker.Tests/ExperimentStateStoreTests.cs`

- [ ] **Step 1: Write failing router tests**

Prove:

- candidate order matches the approved design for every task profile;
- an installed and eligible experimental candidate is selected first for attempts 1–10 of that exact profile;
- task 10 completes and then pauses only that `profile × model` experiment;
- another profile begins with its own counter at zero;
- explicit override is accepted only when installed, capable, context-safe, and fully resident after preflight;
- structural/context/technical failure selects the established fallback for the current task;
- two consecutive technical failures open the pair's circuit breaker;
- success resets the consecutive technical-failure count;
- CPU offload immediately disables only the exact model/context pair;
- owner actions map exactly to `promote`, `continue_experiment`, `fallback_only`, and `disable`;
- after promotion, a suitable resident model may win over a cold model;
- `ExactSearch` never selects a language model.

Represent routing as a pure decision before execution:

```csharp
public sealed record ModelSelection(
    LocalTaskProfile Profile,
    string Model,
    int ContextTokens,
    bool IsExperimentalAttempt,
    string CatalogVersion,
    string Reason);
```

- [ ] **Step 2: Run router tests and confirm RED**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~ModelRouterTests|FullyQualifiedName~ExperimentStateStoreTests"
```

Expected: selection and experiment state types are absent.

- [ ] **Step 3: Implement the pure router**

Selection inputs are catalog, live installed set, current resident set, workload metadata, explicit override, and content-free experiment state. Prompt or answer text must never be an input to catalog eligibility decisions.

- [ ] **Step 4: Implement atomic experiment persistence**

Store state below `%LOCALAPPDATA%\LocalAi\experiments\` with atomic replace semantics. Persist only profile, model, attempt counts, outcome categories, consecutive technical failures, pause state, and owner decision.

- [ ] **Step 5: Re-run focused tests and confirm GREEN**

Run the command from Step 2.

Expected: all selected tests pass.

## Task 4: Replace strict FIFO leasing with durable model-aware snapshots

**Files:**

- Modify: `src/LocalAi.Broker/DurableQueue.cs`
- Create: `src/LocalAi.Broker/ModelAwareScheduler.cs`
- Create: `src/LocalAi.Broker/DurationEstimator.cs`
- Modify: `src/LocalAi.Broker/BrokerHost.cs`
- Modify: `tests/LocalAi.Broker.Tests/DurableQueueTests.cs`
- Create: `tests/LocalAi.Broker.Tests/ModelAwareSchedulerTests.cs`
- Create: `tests/LocalAi.Broker.Tests/DurationEstimatorTests.cs`
- Modify: `tests/LocalAi.Broker.Tests/BrokerRecoveryTests.cs`

- [ ] **Step 1: Write failing durable-selection tests**

Cover:

- the durable queue remains the source of queued/running/terminal state;
- a scheduler can inspect content-free candidate metadata then atomically lease a chosen job ID;
- only one running lease exists;
- a frozen model-group snapshot excludes later arrivals;
- jobs in a snapshot run predicted shortest to longest;
- model reuse wins before load cost and duration when starvation is not active;
- a switch or long job opens at most a two-second related-work window;
- workflow hints expose expected compatible work without inventing jobs;
- dependent steps are not leased before `IsDependencyReady`;
- a 15-minute wait forces the job into the next compatible snapshot;
- priority and original sequence break otherwise equal decisions;
- broker recovery clears an in-memory snapshot and safely reschedules durable queued jobs;
- a running snapshot is never interrupted.

- [ ] **Step 2: Run scheduler tests and confirm RED**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~DurableQueueTests|FullyQualifiedName~ModelAwareSchedulerTests|FullyQualifiedName~DurationEstimatorTests|FullyQualifiedName~BrokerRecoveryTests"
```

Expected: chosen-ID lease and scheduler APIs are missing.

- [ ] **Step 3: Add atomic candidate listing and chosen leasing**

Do not let the scheduler mutate queue files directly:

```csharp
public interface IBrokerQueue
{
    Task<IReadOnlyList<QueuedJobCandidate>> ListQueuedAsync(CancellationToken ct = default);
    Task<LeasedJob?> TryLeaseAsync(
        Guid jobId,
        string workerId,
        CancellationToken ct = default);
    // Existing terminal and diagnostic members remain.
}
```

`TryLeaseAsync` must re-check that the selected job is still queued while holding the same queue lock used by enqueue and recovery.

- [ ] **Step 4: Implement frozen snapshots and duration learning**

Use rolling median and p90 keyed only by:

```text
task profile | model | input bucket | output bucket |
file count bucket | image count/pixel bucket | cold-or-warm
```

Unknown jobs use catalog `short`, `medium`, or `long` estimates. The scheduler comparison order is:

1. starvation eligibility;
2. compatible resident model / avoided switch;
3. observed model load cost;
4. shortest predicted snapshot work;
5. accumulated age offset;
6. original priority;
7. durable sequence.

- [ ] **Step 5: Integrate the scheduler into BrokerHost**

`BrokerHost` asks for a schedule decision, waits only until a returned two-second deadline when needed, freezes the selected IDs, and leases each by ID. It recalculates after the snapshot completes.

- [ ] **Step 6: Re-run focused tests and confirm GREEN**

Run the command from Step 2.

Expected: all selected tests pass, including existing durable recovery cases.

## Task 5: Coordinate routing, preflight, fallback, and privacy-safe telemetry

**Files:**

- Create: `src/LocalAi.Broker/ModelExecutionCoordinator.cs`
- Create: `src/LocalAi.Broker/ModelTelemetryStore.cs`
- Modify: `src/LocalAi.Broker/BrokerHost.cs`
- Modify: `src/LocalAi.Broker/ReceiptFactory.cs`
- Modify: `src/LocalAi.Contracts/BrokerContracts.cs`
- Create: `tests/LocalAi.Broker.Tests/ModelExecutionCoordinatorTests.cs`
- Create: `tests/LocalAi.Broker.Tests/ModelTelemetryStoreTests.cs`
- Modify: `tests/LocalAi.Broker.Tests/BrokerReceiptTests.cs`

- [ ] **Step 1: Write failing coordinator and telemetry tests**

Prove:

- scheduler and executor use the same immutable `ModelSelection`;
- cold execution preflights before task content is sent;
- warm execution reuses a still-valid residency proof;
- failed validation records a structural outcome and executes the fallback once;
- technical failure records its category and executes the fallback when eligible;
- partial VRAM residency records zero-offload failure and never sends task content;
- receipts expose selected profile/model/context, queue/load/execution/total duration, cold/warm state, fallback, validator result, and net estimated savings;
- telemetry rejects objects containing members named prompt, answer, content, image, path, or secret;
- telemetry stores only buckets, counts, durations, enums, booleans, model tags, and catalog version;
- idle unload is considered only after 30 minutes with no queued or running work.

Extend receipts additively:

```csharp
public sealed record LocalRoutingReceipt(
    LocalTaskProfile? TaskProfile,
    string SelectedModel,
    int? ContextTokens,
    bool WasCold,
    bool UsedFallback,
    string? ValidatorResult,
    long EstimatedVerificationTokens,
    long EstimatedNetCloudTokensSaved);
```

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~ModelExecutionCoordinatorTests|FullyQualifiedName~ModelTelemetryStoreTests|FullyQualifiedName~BrokerReceiptTests"
```

Expected: coordinator, routing receipt, and telemetry store are missing.

- [ ] **Step 3: Implement execution coordination**

The execution order must be:

```text
select -> preflight if needed -> execute -> validate ->
record outcome -> fallback if required -> finalize receipt
```

Never preflight with real task content. Never retry ambiguous successful text as a transport retry.

- [ ] **Step 4: Implement content-free metrics and reports**

Use atomic JSON state below `%LOCALAPPDATA%\LocalAi\telemetry\`. Calculate success/error/fallback counts; mean/median/p90 duration; cold/warm comparisons; load/unload count; automatic validation results; gross verification cost; and net estimated cloud-token savings.

- [ ] **Step 5: Re-run focused tests and confirm GREEN**

Run the command from Step 2.

Expected: all selected tests pass.

## Task 6: Make LocalLm task-aware and implement validated translation

**Files:**

- Modify: `src/LocalLm.Core/ILocalModelClient.cs`
- Modify: `src/LocalLm.Core/BrokerLocalModelClient.cs`
- Modify: `src/LocalLm.Core/LocalModels.cs`
- Modify: `src/LocalLm.Core/LocalTasks.cs`
- Create: `src/LocalLm.Core/TranslationChunker.cs`
- Create: `src/LocalLm.Core/TranslationValidator.cs`
- Create: `src/LocalLm.Core/TranslationAttribution.cs`
- Modify: `tests/LocalLm.Tests/BrokerLocalModelClientTests.cs`
- Create: `tests/LocalLm.Tests/LocalTasksTests.cs`
- Create: `tests/LocalLm.Tests/TranslationChunkerTests.cs`
- Create: `tests/LocalLm.Tests/TranslationValidatorTests.cs`

- [ ] **Step 1: Write failing LocalLm tests**

Cover:

- default tasks submit a profile instead of hard-coding `qwen3.6:27b`;
- explicit model remains an override, not a default;
- log triage always sends `LogTriage`;
- image work distinguishes `Ocr`, `VisualAnalysis`, and `ImageTranslation`;
- text translation distinguishes plain and technical/Markdown profiles;
- translation chunks stay within 48,000 characters and select a cataloged
  context tier large enough for the prompt plus expected output;
- Markdown headings, fenced code, inline code, links, placeholders, and list structure survive validation;
- a structural mismatch triggers fallback, not silent acceptance;
- independent chunks share a workflow ID and deterministic step indexes;
- the final translated document contains exactly one attribution naming the actual model;
- saved-token estimates subtract local verification cost;
- input selects a cataloged context tier up to the model maximum and proceeds only
  when the complete runner fits in VRAM.

Use an explicit routed client call:

```csharp
Task<LocalJobResult<string>> ChatAsync(
    LocalTaskProfile profile,
    string prompt,
    string? system,
    IReadOnlyList<string>? imagesBase64,
    LocalWorkloadMetadata workload,
    LocalWorkflowHint? workflow,
    string? modelOverride,
    int? requestedContextTokens,
    LocalJobPriority priority,
    CancellationToken cancellationToken = default);
```

- [ ] **Step 2: Run LocalLm tests and confirm RED**

```powershell
dotnet test tests/LocalLm.Tests/LocalLm.Tests.csproj --configuration Release
```

Expected: task-aware client, translation components, and LocalTasks tests are missing.

- [ ] **Step 3: Implement task-aware LocalLm calls**

Remove the stale `qwen3.6:27b` default. Keep stable model tag constants only for compatibility and user-facing status; routing defaults come from the broker catalog.

- [ ] **Step 4: Implement deterministic translation chunking**

Protect non-translatable spans before chunking. Split prose at paragraph/sentence boundaries, preserve a deterministic mapping back to the source structure, and assign all chunks one workflow ID.

- [ ] **Step 5: Implement validation and attribution**

Validate structural counts and exact protected tokens before accepting output. Append the localized attribution after chunk reassembly:

```text
Translation performed by the local model: translategemma:12b.
```

The Russian form must be:

```text
Перевод выполнен локальной моделью: translategemma:12b.
```

Use the model from the final successful routing receipt, including fallback.

- [ ] **Step 6: Re-run LocalLm tests and confirm GREEN**

Run the command from Step 2.

Expected: all LocalLm tests pass.

## Task 7: Expose model status, sync, translation, experiment reports, and feedback through MCP

**Files:**

- Modify: `src/LocalLm.Mcp/LocalLmTools.cs`
- Modify: `src/LocalLm.Mcp/Program.cs`
- Create: `src/LocalLm.Mcp/ModelCatalogStartupService.cs`
- Create: `tests/LocalLm.Tests/LocalLmToolsTests.cs`
- Create: `tests/LocalLm.Tests/ModelCatalogStartupServiceTests.cs`

- [ ] **Step 1: Write failing MCP-facing tests**

Test the public behavior of:

- `local_models_status`;
- `local_model_preflight`;
- `local_models_sync`;
- `local_model_experiment_report`;
- `local_model_feedback`;
- `translate_local`;
- revised `ask_local(task_profile, ...)`;
- revised `read_image(mode, ...)`;
- fixed-profile `triage_log`.

Also prove:

- MCP startup compares catalog models with live broker `/api/tags`;
- missing recommended/experimental tags enqueue deduplicated maintenance jobs;
- sync cannot accept an arbitrary model tag;
- pulls wait while inference is queued;
- concurrent Codex and Claude startup produces one durable pull job;
- status reports installed, eligible, resident, disabled-context, experiment, and pull state without static invented inventory;
- feedback is rejected before a profile reaches its report/pause gate.

- [ ] **Step 2: Run MCP-facing tests and confirm RED**

```powershell
dotnet test tests/LocalLm.Tests/LocalLm.Tests.csproj --configuration Release --filter "FullyQualifiedName~LocalLmToolsTests|FullyQualifiedName~ModelCatalogStartupServiceTests"
```

Expected: new MCP tools and startup service are absent.

- [ ] **Step 3: Implement broker-backed tool methods**

MCP methods return compact typed records plus the existing Russian local-use notice. `local_models_sync` derives targets only from the embedded catalog.

- [ ] **Step 4: Add startup synchronization**

Register `ModelCatalogStartupService` as an `IHostedService`. It must enqueue work and return without blocking MCP startup on an 8 GB download. Durable job status remains queryable through `local_models_status`.

- [ ] **Step 5: Re-run MCP-facing tests and confirm GREEN**

Run the command from Step 2.

Expected: all selected tests pass.

## Task 8: Preserve CodeSearch model authority and deterministic search boundaries

**Files:**

- Modify: `tests/CodeSearch.Tests/BrokerEmbeddingClientTests.cs`
- Modify: `tests/CodeSearch.Tests/SearchEngineTests.cs`
- Modify: `tests/CodeSearch.Tests/SearchServiceStatusTests.cs`
- Modify only if a failing test requires it: `src/CodeSearch.Core/Search/SearchService.cs`
- Modify only if a failing test requires it: `src/CodeSearch.Cli/Program.cs`
- Modify only if a failing test requires it: `src/CodeSearch.Mcp/CodeSearchTools.cs`

- [ ] **Step 1: Add compatibility tests**

Prove:

- index creation still defaults to `qwen3-embedding:8b-q8_0`;
- search embeds a query with the model in the base index header;
- an overlay inherits the base generation's embedding model;
- a mismatched embedding model is rejected rather than mixed;
- exact symbol/path/text matches remain deterministic hybrid/lexical behavior and enqueue no chat job;
- optional deep rerank is explicit and falls back to the current hybrid rank.

- [ ] **Step 2: Run CodeSearch tests and confirm RED or existing GREEN**

```powershell
dotnet test tests/CodeSearch.Tests/CodeSearch.Tests.csproj --configuration Release --filter "FullyQualifiedName~BrokerEmbeddingClientTests|FullyQualifiedName~SearchEngineTests|FullyQualifiedName~SearchServiceStatusTests"
```

Expected: existing embedding/header behavior remains green; only genuinely missing explicit-rerank coverage may fail. Do not change production CodeSearch merely to manufacture routing work.

- [ ] **Step 3: Make the minimum compatibility change**

If all required invariants already pass, keep production CodeSearch unchanged and retain only the regression tests. If optional rerank is implemented, put it behind an explicit request flag and submit a routed `CodeRerank` profile without changing the index vectors or stored rank.

- [ ] **Step 4: Run the full CodeSearch test project**

```powershell
dotnet test tests/CodeSearch.Tests/CodeSearch.Tests.csproj --configuration Release
```

Expected: all tests pass.

## Task 9: Add installation planning and synchronized documentation

**Files:**

- Modify: `src/LocalAi.Cli/ClientCommand.cs`
- Modify: `src/LocalAi.Cli/BootstrapCommand.cs`
- Modify: `tests/LocalAi.IntegrationTests/ClientRegistrationTests.cs`
- Modify: `tests/LocalAi.IntegrationTests/BootstrapTests.cs`
- Modify: `README.md`
- Modify: `README.ru.md`

- [ ] **Step 1: Write failing installation-plan tests**

Prove the planned installation:

- registers the same `codesearch-mcp.exe` and `locallm-mcp.exe` for Codex and Claude;
- includes the embedded routing catalog through the broker binary;
- requires a client restart after replacing binaries;
- preserves old models and profiles;
- advertises model synchronization through MCP, not `ollama pull`;
- never claims that publishing the binaries automatically edits client configuration.

- [ ] **Step 2: Run integration tests and confirm RED**

```powershell
dotnet test tests/LocalAi.IntegrationTests/LocalAi.IntegrationTests.csproj --configuration Release --filter "FullyQualifiedName~ClientRegistrationTests|FullyQualifiedName~BootstrapTests"
```

Expected: new catalog/sync assertions fail.

- [ ] **Step 3: Update installation planning and English documentation**

Document task profiles, routing rules, the ten-task experiment, the two-second grouping window, 15-minute starvation bound, 30-minute idle residency, model-specific context tiers up to 256K, zero-offload preflight, privacy-safe metrics, and MCP commands.

- [ ] **Step 4: Synchronize the Russian README**

Under the current global policy, the primary agent translates by default.
Use `translate_local` only when the owner explicitly requests local translation
for the current task. Preserve code blocks, links, and structure; append local
model attribution only when a local model was actually used.

- [ ] **Step 5: Re-run integration tests**

Run the command from Step 2.

Expected: all selected tests pass.

## Task 10: Verify the complete solution and perform a security/compatibility self-review

**Files:**

- Review all files changed by Tasks 1–9.
- Modify only focused files needed to resolve failures introduced by this work.

- [ ] **Step 1: Restore and build**

```powershell
dotnet restore LocalAi.slnx
dotnet build LocalAi.slnx --configuration Release --no-restore
```

Expected: build succeeds with zero new compiler or analyzer warnings.

- [ ] **Step 2: Run the entire test suite**

```powershell
dotnet test LocalAi.slnx --configuration Release --no-build
```

Expected: all tests pass.

- [ ] **Step 3: Review the complete diff**

Run:

```powershell
git diff --check
git status --short
git diff --stat
git diff -- src tests model-routing.json README.md README.ru.md docs/superpowers
```

Verify:

- no direct Ollama use escaped the broker;
- no prompt, answer, content, image data, path, or secret can reach telemetry;
- old explicit-model requests remain compatible;
- context cannot exceed the cataloged official maximum for the selected model;
- no CPU/system-RAM offload can be accepted;
- pulls accept only catalog tags and wait behind inference;
- experiments are per profile/model;
- late queue arrivals cannot extend a frozen snapshot;
- deterministic search and index-header embedding authority are preserved;
- documentation siblings are synchronized and CRLF/UTF-8-no-BOM compliant.

- [ ] **Step 4: Run targeted publish builds without installation**

Publish into a fresh task-specific temporary directory:

```powershell
dotnet publish src/CodeSearch.Mcp/CodeSearch.Mcp.csproj --configuration Release --no-build --output "$env:TEMP\localai-routing-verify\CodeSearch.Mcp"
dotnet publish src/LocalLm.Mcp/LocalLm.Mcp.csproj --configuration Release --no-build --output "$env:TEMP\localai-routing-verify\LocalLm.Mcp"
dotnet publish src/LocalAi.Cli/LocalAi.Cli.csproj --configuration Release --no-build --output "$env:TEMP\localai-routing-verify\LocalAi.Cli"
```

Expected: all three publish commands succeed and the published LocalLm tree includes the broker/contracts required to load the embedded catalog.

## Task 11: Install for Codex and Claude, synchronize TranslateGemma, and run live acceptance

**Files/state:**

- Replace only the existing LocalAi/CodeSearch/LocalLm installation directory used by both clients.
- Update only the existing Codex and Claude MCP registrations planned by `ClientCommand`.
- Write runtime state only below `%LOCALAPPDATA%\LocalAi`.
- Do not change Git state.

- [ ] **Step 1: Resolve and preview exact installation targets**

Use the existing client registration/status commands and inspect current Codex and Claude registrations. Print:

- current and replacement binary paths;
- current hashes and replacement hashes;
- exact client configuration entries;
- broker/runtime path;
- processes that must be restarted.

Do not replace files until every target is resolved and confined to the existing LocalAi installation.

- [ ] **Step 2: Stop only the old LocalAi broker and MCP processes**

Resolve processes by exact executable/assembly path and command line. Do not stop Ollama or unrelated `dotnet` processes.

- [ ] **Step 3: Install the verified artifacts and registrations**

Copy the Task 10 publish output into the resolved installation directory, apply the exact Codex/Claude registration plan, and restart the broker automatically through the first MCP request.

- [ ] **Step 4: Synchronize recommended models through MCP**

Call `local_models_status`, then `local_models_sync`. Expected:

- existing models remain installed;
- `translategemma:12b` appears as a deduplicated maintenance pull;
- no direct `ollama pull` command is used;
- the pull begins only when inference work is absent.

- [ ] **Step 5: Wait for pull completion with bounded status polling**

Poll through `local_models_status` at no more than one request every 30 seconds. Continue reporting useful state without blocking user communication for more than 60 seconds.

- [ ] **Step 6: Run live RTX 5080 preflight**

Call `local_model_preflight` with 2K context for `translategemma:12b`, then inspect the broker-provided residency proof. Expected:

```text
model = translategemma:12b
context = 2048
size_vram = size
fully_resident = true
```

If any CPU/system-RAM offload is observed, unload and disable the exact combination immediately. Do not send translation content to it.

- [ ] **Step 7: Run one acceptance task per major path**

Through MCP only:

Run local translation acceptance only if the owner explicitly opts in for this
task. The remaining paths are:

1. OCR with `qwen3-vl:8b-instruct-q8_0`;
2. code/log analysis with `qwen2.5-coder:14b`;
3. semantic CodeSearch query using the index-header embedding model;
4. exact lexical search proving no chat model is invoked.

Check receipts, selected model/context, fallback state, zero-offload proof, timings, and estimated net cloud-token savings.

- [ ] **Step 8: Confirm both clients after restart**

After the owner restarts Codex and Claude, call `local_models_status` and one read-only local task from each client. Confirm both share the same durable queue, catalog version, experiment counters, and installed-model view.

- [ ] **Step 9: Report without committing**

Report changed files, all commands and results, live model status, residency proof, installation locations, first experiment counters, remaining risks, and out-of-scope findings. Request separate authorization before any stage, commit, push, or GitHub publication.

## Conditional Git checkpoints

Only after explicit Git authorization, use small English Conventional Commits:

1. `feat(broker): add model routing and lifecycle controls`
2. `feat(broker): schedule work by model affinity`
3. `feat(locallm): add task profiles and validated translation`
4. `docs: document model-aware local routing`

Before each commit, stage only the exact files belonging to that checkpoint and show the staged diff. Never include unrelated work.
