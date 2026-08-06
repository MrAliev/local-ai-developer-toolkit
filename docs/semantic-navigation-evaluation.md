# Semantic navigation evaluation

[Русская версия](semantic-navigation-evaluation.ru.md)

## Scope

Measured on 2026-08-06 on Windows against the dirty `release/0.1.15` snapshot
`771aaa1f0ad8cde0f41e29a6034394f08baaacbaaf7ff4aa977e86efca0de79c`.
The marker-based suite is
`tests/CodeSearch.Tests/Fixtures/SemanticNavigation/cases.json`; markers make cases stable when
unrelated lines move.

```powershell
localai semantic evaluate `
  --cases tests/CodeSearch.Tests/Fixtures/SemanticNavigation/cases.json `
  --root D:\Documents\ChatGPT\LocalAi
```

## Result

| Metric | Measured value |
| --- | ---: |
| Correct cases | 4 / 4 (100%) |
| Process-cold SIDX load | 1030.1215 ms |
| SIDX base plus overlay | 17,879,390 bytes |
| Managed-memory delta while loading | 101,628,672 bytes |
| Working-set delta while loading | 125,571,072 bytes |
| Documents | 348 |
| Symbols | 24,181 |
| Occurrences | 125,772 |
| Relationships | 2,087 |

| Case | First query | Warm p50 | Warm p95 | Warm max |
| --- | ---: | ---: | ---: | ---: |
| C# definition | 3.4748 ms | 0.0065 ms | 0.0137 ms | 0.0421 ms |
| C# references | 0.2233 ms | 0.0110 ms | 0.0118 ms | 0.0203 ms |
| C# implementations | 1.1517 ms | 0.0141 ms | 0.0155 ms | 0.0651 ms |
| C# outgoing relationship | 1.7896 ms | 0.0147 ms | 0.0158 ms | 0.0320 ms |

Each warm result contains 200 in-process iterations. Correctness requires every expected marker;
cases that permit additional results state that explicitly.

## Limits

- The cold figure is a new-process load, not a guaranteed cold filesystem-cache measurement.
- Memory deltas are process observations and may vary with GC and operating-system accounting.
- The 4-case repository suite covers exact C# SIDX query paths. WPF, WinUI, MAUI, and Avalonia
  are covered by deterministic indexer fixtures; real TypeScript/Python SCIP and LSP fixtures
  remain opt-in because they require external npm tools.
- These numbers are a dated baseline, not hard CI thresholds.
