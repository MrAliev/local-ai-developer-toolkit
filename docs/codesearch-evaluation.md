# CodeSearch retrieval evaluation

[Русская версия](codesearch-evaluation.ru.md)

## Scope and provenance

This report compares the historical no-floor search mode with the calibrated
`qwen3-embedding:8b-q8_0` profile. It records measured facts separately from token
heuristics and unavailable telemetry.

- Measurement date: 2026-07-31.
- Corpus: schema 1, 24 cases: 8 natural-language, 6 exact-symbol, 4 generic-text,
  and 6 unrelated/no-answer cases.
- Corpus identity:
  `schema1:sha256:d675331cb7008a67a7335c5a1f2aba85e382974b71b1473e34b9e4685f0d7a52`.
- Source commit: `966aae8eda5653897190b4b69f7b5074deef9652`.
- Source tree: `8f1d9458a60bcd4ba04aae1c29b6c500bba0c7e5`.
- Index generation:
  `399fcc0b53b35ede05dc64f1a84cbc3bfc6bf382bdd2de7d71f2f9dc1ae8debc`,
  containing 203 files and 1,529 chunks.
- Embedding model: `qwen3-embedding:8b-q8_0`; calibrated vector floor: `0.43`.
- Every embedding followed
  `SearchService` -> `BrokerEmbeddingClient` -> the shared LocalAi FIFO broker.
  No direct Ollama request was used.

The fixture validator checked every relevant path and symbol against the source tree
before each run. The first profile run was classified as cold because process
inspection found no resident model runner immediately before it. The second profile
run followed it immediately and was classified as warm. The reconstructed no-floor
runs were both warm.

## Commands and raw artifacts

The supported evaluator command is:

```powershell
dotnet run --project src/CodeSearch.Cli/CodeSearch.Cli.csproj -c Release -- evaluate --cases tests/CodeSearch.Tests/Fixtures/SearchEvaluation/cases.json --root C:\Users\Mr.Aliev\tools\LocalAi --profile
dotnet run --project src/CodeSearch.Cli/CodeSearch.Cli.csproj -c Release -- evaluate --cases tests/CodeSearch.Tests/Fixtures/SearchEvaluation/cases.json --root C:\Users\Mr.Aliev\tools\LocalAi --no-floor
```

During this branch measurement, the running broker belonged to the immutable installed
version `966aae8`. Issue #6 tracks the assembly-path affinity that prevents a worktree
client from accepting that already-running broker. The installed version was not
replaced or restarted. An ignored temporary adapter validated the canonical broker
process and then used the normal `SearchService` -> `BrokerEmbeddingClient` -> shared
durable queue path. The exact capture commands were:

```powershell
dotnet run --project artifacts\eval-harness\EvalHarness.csproj -c Release --no-build -- profile cold tests\CodeSearch.Tests\Fixtures\SearchEvaluation\cases.json C:\Users\Mr.Aliev\tools\LocalAi C:\Users\Mr.Aliev\tools\LocalAi\bin\versions\966aae8\LocalAi.Broker.dll C:\Users\Mr.Aliev\AppData\Local\Temp\codesearch-eval-profile-cold-20260731.json
dotnet run --project artifacts\eval-harness\EvalHarness.csproj -c Release --no-build -- profile warm tests\CodeSearch.Tests\Fixtures\SearchEvaluation\cases.json C:\Users\Mr.Aliev\tools\LocalAi C:\Users\Mr.Aliev\tools\LocalAi\bin\versions\966aae8\LocalAi.Broker.dll C:\Users\Mr.Aliev\AppData\Local\Temp\codesearch-eval-profile-warm-20260731.json
dotnet run --project artifacts\eval-harness\EvalHarness.csproj -c Release --no-build -- no-floor warm tests\CodeSearch.Tests\Fixtures\SearchEvaluation\cases.json C:\Users\Mr.Aliev\tools\LocalAi C:\Users\Mr.Aliev\tools\LocalAi\bin\versions\966aae8\LocalAi.Broker.dll C:\Users\Mr.Aliev\AppData\Local\Temp\codesearch-eval-no-floor-run1-20260731.json
dotnet run --project artifacts\eval-harness\EvalHarness.csproj -c Release --no-build -- no-floor warm tests\CodeSearch.Tests\Fixtures\SearchEvaluation\cases.json C:\Users\Mr.Aliev\tools\LocalAi C:\Users\Mr.Aliev\tools\LocalAi\bin\versions\966aae8\LocalAi.Broker.dll C:\Users\Mr.Aliev\AppData\Local\Temp\codesearch-eval-no-floor-run2-20260731.json
```

The raw JSON files are retained at those temporary paths for local review and are not
committed. The adapter is also ignored and is not a product execution path.

## Measured facts

Quality and exposure counts were identical within each two-run mode. The table therefore
uses either run for deterministic metrics and shows latency separately.

| Metric | No floor, runs 1/2 | Profile, cold/warm | Change |
|---|---:|---:|---:|
| Precision@5 | 0.133333 | 0.133333 | 0 |
| Recall@10 | 0.777778 | 0.777778 | 0 |
| Mean first relevant rank | 4.388889 | 4.388889 | 0 |
| No-answer false positives | 6/6 (rate 1.0) | 6/6 (rate 1.0) | 0 |
| Response characters | 151,071 | 147,651 | -3,420 (-2.26%) |
| Source lines | 10,966 | 10,568 | -398 (-3.63%) |
| Chunk reads | 240 | 238 | -2 (-0.83%) |
| Distinct file reads per case, summed | 156 | 152 | -4 (-2.56%) |

| Run | Evaluator elapsed |
|---|---:|
| Profile, cold | 72,246.5 ms |
| Profile, warm | 71,308.5 ms |
| No floor, warm run 1 | 67,625.1 ms |
| No floor, warm run 2 | 68,556.8 ms |

The profile cold run was 938.0 ms slower than its warm repeat. The profile warm run
was 3,217.6 ms (4.73%) slower than the median of the two warm no-floor runs. With only
one cold/warm pair and normal shared-broker scheduling noise, this is a timing
observation, not evidence that the floor causes a latency regression.

The four answerable misses were unchanged:
`intent-runtime-acl`, `text-vector-route`, `text-shared-fifo`, and
`text-russian-install`. The floor removed two returned chunks overall. In the
`none-email-reset` case it reduced the result count from 10 to 8, but the case remained
a false positive because positive lexical candidates are intentionally eligible even
when their vector scores are below the floor.

## Heuristic token estimates

Raw character counts above are authoritative. The token point proxy is
`ceil(response characters / 4)`. The interval
`ceil(characters / 6)..ceil(characters / 3)` is an engineering heuristic, not a
statistical confidence interval and not tokenizer output.

| Metric | No floor | Profile | Change |
|---|---:|---:|---:|
| Point token proxy | 37,768 | 36,913 | -855 (-2.26%) |
| Heuristic interval | 25,179..50,357 | 24,609..49,217 | -570 lower, -1,140 upper |

## Unavailable telemetry

Broker queue wait is unavailable. `BrokerEmbeddingClient` consumes the embedding value
but does not expose the broker receipt to `SearchService`, so the evaluator emits
`null` for `brokerQueueWaitMilliseconds` and an explicit unavailable diagnostic.
Elapsed time includes queueing, model execution, and local search work; those components
cannot be separated from this data.

## Limitations

- The corpus is a small, repository-specific engineering fixture, not a benchmark of
  general code retrieval.
- The relevance floor filters only the vector branch before RRF. Positive lexical
  candidates remain eligible by design, so the observed no-answer false-positive rate
  did not improve even though low-vector candidates were removed.
- `responseCharacters` measures the evaluator's rendered path, metadata, and snippet
  payload. It excludes MCP nonce wrappers and opaque chunk IDs, so it is suitable for
  this A/B comparison but is not an end-to-end MCP transport byte count.
- Only one cold profile run and one immediate warm profile run were captured. Both
  reconstructed no-floor runs were warm.
- A prior manual operator note, whose raw cold/warm JSON is unavailable, recorded the
  same quality metrics and misses but 170,457 response characters, a 42,615 point token
  estimate, and 123,489 ms elapsed. Those memory-derived values used a different output
  capture and are not mixed into the comparable table above.
- Temporary raw JSON paths are machine-local and may be removed by normal temporary-file
  cleanup.
