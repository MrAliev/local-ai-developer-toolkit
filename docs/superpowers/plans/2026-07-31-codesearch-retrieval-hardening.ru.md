# План реализации усиления retrieval в CodeSearch

[English version](2026-07-31-codesearch-retrieval-hardening.md)

> **Для agentic workers:** ОБЯЗАТЕЛЬНЫЙ SUB-SKILL: использовать superpowers:subagent-driven-development (рекомендуется) или superpowers:executing-plans и выполнять план по задачам. Для отслеживания используются checkbox (`- [ ]`).

**Цель:** Добавить в существующий pipeline CodeSearch измеренный model-specific порог релевантности, получение полного чанка точного snapshot, nonce-границы недоверенного содержимого и воспроизводимый A/B-отчёт о качестве/токенах.

**Архитектура:** Сохранить текущую неизменяемую generation и точный overlay рабочего дерева как единственный источник searchable state. Получать откалиброванный порог по модели индекса до RRF, кодировать точный snapshot и composite ordinal в каждом результате, проверять все компоненты identity до чтения чанка и оборачивать только source-derived MCP output. Для baseline/final использовать закоммиченный fixture и существующий broker-backed путь поиска.

**Стек:** .NET 10, C# 14, xUnit v3, ModelContextProtocol 1.4.1, JSON fixtures, `qwen3-embedding:8b-q8_0` через `BrokerEmbeddingClient`.

---

## Предусловия

- Утверждённый проект: `docs/superpowers/specs/2026-07-31-codesearch-retrieval-hardening-design.md` и синхронная русская версия.
- Baseline на `966aae8`: проходят 478 тестов; generation CodeSearch `399fcc0b...` актуальна и содержит 203 файла и 1 529 чанков.
- Никогда не обращаться к Ollama напрямую. Все live embeddings идут через `BrokerEmbeddingClient` и общий FIFO broker.
- Сначала focused RED и ожидаемая ошибка, затем минимальная реализация и focused GREEN.
- После каждой задачи запускать `dotnet test LocalAi.slnx -c Release --nologo`.

## Задача 1: Добавить evaluation-корпус и детерминированные метрики

**Файлы:**

- Создать: `tests/CodeSearch.Tests/Fixtures/SearchEvaluation/cases.json`
- Создать: `tests/CodeSearch.Tests/SearchEvaluationTests.cs`
- Создать: `src/CodeSearch.Core/Search/SearchQualityProfile.cs`
- Изменить: `src/CodeSearch.Cli/Program.cs`

- [ ] Добавить RED-тесты: десериализация 20–30 случаев, запрет duplicate ID и пустых relevance targets, проверка каждого path/symbol по исходникам, расчёт precision@5, recall@10, first relevant rank, no-answer false-positive rate, characters, lines, reads и token proxy.
- [ ] Запустить:

  ```powershell
  dotnet test tests/CodeSearch.Tests/CodeSearch.Tests.csproj -c Release --filter FullyQualifiedName~SearchEvaluationTests
  ```

  Ожидается ошибка compilation/fixture из-за отсутствующих контрактов и корпуса.
- [ ] Добавить `SearchEvaluationCase`, `SearchEvaluationTarget`, `SearchEvaluationMetrics` и чистый расчёт метрик в `SearchQualityProfile.cs`; использовать `ceil(chars/4)` и документированный интервал `ceil(chars/6)..ceil(chars/3)`.
- [ ] Добавить 24 проверенных по исходникам случая: восемь natural-language intent, шесть точных символов, четыре generic-text/document и шесть unrelated/no-answer.
- [ ] Добавить CLI `evaluate --cases <json> --root <repo> [--profile|--no-floor]`, последовательно выполняющий случаи через `SearchService` и выдающий детерминированный JSON.
- [ ] Получить focused GREEN, затем запустить полную solution suite.
- [ ] Дважды выполнить live baseline без порога (cold/warm), сохранить сырой JSON в `work/` и использовать его только как вход измерения до итогового двуязычного отчёта.

## Задача 2: Откалибровать и применить порог релевантности до RRF

**Файлы:**

- Изменить: `src/CodeSearch.Core/Search/SearchQualityProfile.cs`
- Изменить: `src/CodeSearch.Core/Search/SearchEngine.cs`
- Изменить: `src/CodeSearch.Core/Search/SearchService.cs`
- Изменить: `src/CodeSearch.Cli/Program.cs`
- Изменить: `tests/CodeSearch.Tests/SearchEngineTests.cs`
- Изменить: `tests/CodeSearch.Tests/SearchEvaluationTests.cs`

- [ ] Добавить RED-тесты для non-finite/out-of-range порога, исключения below-threshold, включения at-threshold, lexical-only fallback, нулевого no-answer и fail-closed неизвестной модели.
- [ ] Запустить focused-тесты и подтвердить, что ошибки вызваны отсутствующим поведением floor/profile.
- [ ] Выбрать измеренный порог `qwen3-embedding:8b-q8_0` по правилу проекта и сохранить provenance корпуса/generation в `SearchQualityProfile`.
- [ ] Добавить nullable `SearchOptions.MinVectorScore` и `AllowUncalibratedModelForEvaluation`; только evaluation может явно использовать null/no-floor.
- [ ] Получать production options через `SearchQualityProfile`; для неизвестных моделей выбрасывать `SearchNotReadyException` с `threshold not calibrated`.
- [ ] Удалять векторные кандидаты ниже порога до назначения vector rank. Fused set строить из допустимых vector candidates и всех положительных lexical candidates.
- [ ] Получить focused GREEN и GREEN полной solution.

## Задача 3: Добавить chunk ID точного snapshot и полный чанк

**Файлы:**

- Создать: `src/CodeSearch.Core/Search/SearchChunkId.cs`
- Создать: `tests/CodeSearch.Tests/SearchChunkIdTests.cs`
- Изменить: `src/CodeSearch.Core/Indexing/CompositeIndex.cs`
- Изменить: `src/CodeSearch.Core/Search/SearchEngine.cs`
- Изменить: `src/CodeSearch.Core/Search/SearchService.cs`
- Изменить: `src/CodeSearch.Mcp/CodeSearchTools.cs`
- Изменить: `src/CodeSearch.Cli/Program.cs`

- [ ] Добавить RED-тесты для round-trip, malformed format, изменения digest, другого репозитория, устаревшей generation, stale HEAD tree, stale dirty hash и ordinal вне диапазона.
- [ ] Добавить snapshot identity в `ISearchableIndex`; `CodeIndex` предоставляет базовую identity, а `CompositeIndex` — активные tree/dirty.
- [ ] Реализовать `cs1.<base64url-payload>.<base64url-sha256>` с ограниченными длинами полей, строгим parser, constant-time сравнением digest и явными диагностиками.
- [ ] Добавить `ChunkId` в `SearchHit` и создавать его из repository, generation, tree, dirty hash и composite ordinal.
- [ ] Добавить `SearchChunk` и `SearchService.GetChunkAsync`; проверять ID/snapshot до чтения `Path.Combine(root, relPath)` и возвращать полный индексированный диапазон.
- [ ] Добавить MCP `get_code_chunk`, IDs в `search_code` и IDs в CLI search.
- [ ] Получить focused GREEN и GREEN полной solution.

## Задача 4: Добавить nonce-обёртки недоверенного содержимого

**Файлы:**

- Создать: `src/CodeSearch.Core/Security/UntrustedContent.cs`
- Создать: `tests/CodeSearch.Tests/UntrustedContentTests.cs`
- Изменить: `src/CodeSearch.Mcp/CodeSearchTools.cs`

- [ ] Добавить RED-тесты для 96-bit lowercase nonce markers, свежих nonce, внедрённых opening/closing tags, вариантов case/whitespace, pre-defused Unicode, враждебных origin, CR/LF/tab escaping и принудительного retry при коллизии.
- [ ] Реализовать криптографический nonce source и injectable test source. Повторять генерацию, пока текст nonce встречается в content при ordinal case-insensitive сравнении.
- [ ] Экранировать metacharacters и control characters атрибутов без нормализации content.
- [ ] Оставить index/status снаружи; оборачивать каждый успешный source-derived hit `search_code` и успешный результат `get_code_chunk`. Ошибки валидации не оборачивать.
- [ ] Получить focused GREEN и GREEN полной solution.

## Задача 5: Зафиксировать итоговый A/B и синхронизировать документацию

**Файлы:**

- Изменить: `tests/CodeSearch.Tests/SearchEvaluationTests.cs`
- Изменить: `tests/CodeSearch.Tests/Fixtures/SearchEvaluation/cases.json`
- Создать: `docs/codesearch-evaluation.md`
- Создать: `docs/codesearch-evaluation.ru.md`
- Изменить: `README.md`
- Изменить: `README.ru.md`

- [ ] Дважды выполнить все случаи с откалиброванным profile и сохранить cold/warm JSON.
- [ ] Сравнить baseline/final precision@5, recall@10, first relevant rank, no-answer false positives, response characters, source lines, reads, token proxy interval, elapsed time и broker queue wait, когда он доступен.
- [ ] Написать синхронные EN/RU-отчёты, разделяющие измеренные факты, heuristic token estimates, недоступную telemetry и ограничения.
- [ ] Документировать chunk IDs `search_code`, `get_code_chunk`, отказ для некалиброванной модели и untrusted-content output в обоих README.
- [ ] Проверить JSON, заголовки/ссылки EN/RU, focused evaluation tests и выполнить:

  ```powershell
  dotnet test LocalAi.slnx -c Release --nologo
  ```

  Ожидается ноль ошибок и отсутствие новых warning.
- [ ] Проверить `git diff --check`, точный scope ветки и отсутствие прямых путей к Ollama.
- [ ] Закоммитить, отправить `codesearch-retrieval-hardening`, создать один PR с `Closes #5` и проверить remote CI.
