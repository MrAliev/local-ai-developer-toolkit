# Оценка семантической навигации

[English version](semantic-navigation-evaluation.md)

## Область измерения

Измерено 2026-08-06 на Windows для dirty snapshot ветки `release/0.1.15`
`771aaa1f0ad8cde0f41e29a6034394f08baaacbaaf7ff4aa977e86efca0de79c`.
Marker-based suite находится в
`tests/CodeSearch.Tests/Fixtures/SemanticNavigation/cases.json`; markers не зависят от сдвига
несвязанных строк.

```powershell
localai semantic evaluate `
  --cases tests/CodeSearch.Tests/Fixtures/SemanticNavigation/cases.json `
  --root D:\Documents\ChatGPT\LocalAi
```

## Результат

| Метрика | Значение |
| --- | ---: |
| Корректные cases | 4 / 4 (100%) |
| Process-cold загрузка SIDX | 1030,1215 мс |
| SIDX base плюс overlay | 17 879 390 байт |
| Прирост managed memory при загрузке | 101 628 672 байта |
| Прирост working set при загрузке | 125 571 072 байта |
| Документы | 348 |
| Символы | 24 181 |
| Вхождения | 125 772 |
| Отношения | 2 087 |

| Case | Первый запрос | Warm p50 | Warm p95 | Warm max |
| --- | ---: | ---: | ---: | ---: |
| C# definition | 3,4748 мс | 0,0065 мс | 0,0137 мс | 0,0421 мс |
| C# references | 0,2233 мс | 0,0110 мс | 0,0118 мс | 0,0203 мс |
| C# implementations | 1,1517 мс | 0,0141 мс | 0,0155 мс | 0,0651 мс |
| C# outgoing relationship | 1,7896 мс | 0,0147 мс | 0,0158 мс | 0,0320 мс |

Каждый warm-результат содержит 200 итераций внутри одного процесса. Correctness требует все
ожидаемые markers; разрешение дополнительных результатов задаётся отдельно для каждого case.

## Ограничения

- Cold-значение измеряет загрузку в новом процессе, но не гарантирует холодный файловый cache.
- Разница памяти зависит от GC и учёта памяти операционной системой.
- Репозиторный набор из четырёх cases покрывает точные C# SIDX-запросы. WPF, WinUI, MAUI и
  Avalonia покрыты детерминированными fixtures индексатора; реальные TypeScript/Python SCIP и LSP
  fixtures остаются opt-in, так как требуют внешних npm-инструментов.
- Это датированный baseline, а не жёсткие CI thresholds.
