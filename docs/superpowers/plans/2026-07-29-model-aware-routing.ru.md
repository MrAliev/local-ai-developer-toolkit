# План реализации маршрутизации локального ИИ с учётом моделей

[English version](2026-07-29-model-aware-routing.md)

> **Для агентных исполнителей:** план выполняется последовательно с помощью
> `superpowers:executing-plans` либо `superpowers:subagent-driven-development`.
> Состояние шагов отмечается флажками.

**Цель:** направлять локальные задачи через общий каталог с учётом типа задачи,
сохранять все обращения к Ollama за устойчивым брокером LocalAi, минимизировать
смену моделей, проверять полное размещение на RTX 5080 без CPU/RAM offload и
независимо оценивать нового кандидата TranslateGemma на первых десяти задачах
каждого подходящего профиля.

**Архитектура:** контракт брокера расширяется метаданными задачи, нагрузки,
workflow и обслуживания без нарушения старых вызовов с явно заданной моделью.
Брокер отвечает за каталог, выбор кандидата, устойчивое планирование с учётом
модели, жизненный цикл Ollama, preflight, circuit breaker и безопасную
телеметрию. LocalLm отвечает за специализированные запросы, разбиение и
валидацию перевода и MCP-представление. CodeSearch продолжает использовать
embedding-модель из заголовка индекса и не передаёт точный лексический поиск
языковой модели.

**Технологии:** .NET 10, C# 14, xUnit, ModelContextProtocol 1.4.1, устойчивая
JSON-очередь, Ollama HTTP API и проверки установки PowerShell.

---

## Предварительные условия и границы выполнения

- Утверждённый дизайн находится в
  `docs/superpowers/specs/2026-07-29-model-aware-routing-design.md` и
  синхронизированном русском sibling-файле.
- Для каждого изменения поведения применяется TDD: красный тест, ожидаемый
  сбой, минимальная реализация, рефакторинг при зелёных целевых тестах.
- Исходники, идентификаторы, комментарии, названия тестов и Git-сообщения
  остаются на английском.
- Английские и русские документы синхронизируются, сохраняются в UTF-8 без BOM
  и с окончаниями строк CRLF.
- До отдельного разрешения владельца запрещены stage, commit, push и публикация
  в GitHub. Указанные ниже контрольные коммиты условны.
- После автоматической проверки разрешены установка локальных бинарников для
  Codex и Claude и установка моделей через MCP.
- Codex, Claude, LocalLm и CodeSearch не обращаются к Ollama напрямую.
  Фальшивый HTTP endpoint допустим только в тестах транспорта брокера.
- В телеметрию не попадают запросы, ответы, содержимое файлов, изображения,
  пути и секреты.

## Задача 1. Общий каталог маршрутизации и обратно совместимые контракты

**Файлы:**

- создать `model-routing.json`;
- создать `src/LocalAi.Contracts/ModelRoutingContracts.cs`;
- изменить `src/LocalAi.Contracts/BrokerContracts.cs`;
- создать `src/LocalAi.Broker/ModelRoutingCatalog.cs`;
- изменить `src/LocalAi.Broker/LocalAi.Broker.csproj`;
- изменить тесты контрактов и создать `ModelRoutingCatalogTests.cs`.

- [ ] **Шаг 1. Написать красные тесты сериализации и каталога**

Проверить строковую сериализацию профилей и lifecycle-состояний, совместимость
старого `CreateChat(...)`, отсутствие конкретной модели в routed-запросе,
наличие workload/workflow, версию схемы `1`, кандидата и established fallback
для каждого модельного маршрута, allowlist обслуживания, поддерживаемые
степени двойки от 2048 до официального максимума модели (не более 262144),
экспериментальный `translategemma:12b`, единственную indexing-модель
`qwen3-embedding:8b-q8_0` и полностью детерминированный `ExactSearch`.

Используются типы `LocalTaskProfile`, `LocalWorkloadMetadata` и
`LocalWorkflowHint` из английского оригинала с теми же именами и полями.

- [ ] **Шаг 2. Подтвердить RED**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~BrokerContractTests|FullyQualifiedName~BrokerPayloadContractTests|FullyQualifiedName~ModelRoutingCatalogTests"
```

Ожидается ошибка компиляции из-за отсутствующих routing-типов, фабрики и
загрузчика каталога.

- [ ] **Шаг 3. Реализовать аддитивные контракты**

Сохранить `CreateChat(...)` без изменений, добавить `CreateRoutedChat(...)` и
не трактовать старую явно заданную модель как автоматически выбранного
кандидата. Добавить типизированный `ModelMaintenance`; произвольный model tag
пользователя для pull не предоставлять.

- [ ] **Шаг 4. Добавить и встроить каталог**

`model-routing.json` — единственный исходный файл и встраивается в
`LocalAi.Broker` под логическим именем `LocalAi.model-routing.json`.
Загрузчик отклоняет неизвестную схему, дубли тегов, отсутствующие fallback,
недопустимые контексты/способности и ссылки на неизвестные модели.

- [ ] **Шаг 5. Повторить целевые тесты и подтвердить GREEN**

Все выбранные тесты должны пройти без новых предупреждений.

## Задача 2. Жизненный цикл Ollama только через брокер и full-VRAM preflight

**Файлы:** контракты брокера, `OllamaTransport.cs`, новый `ModelRuntime.cs`,
fake Ollama server и тесты транспорта/runtime.

- [ ] **Шаг 1. Написать красные тесты**

Проверить `/api/tags`, строгий разбор `/api/ps`, allowlisted `POST /api/pull`
с `{ "model": tag, "stream": false }`, пустой `/api/generate` с выбранным
контекстом и ограниченным `keep_alive`, обязательное `size_vram == size`,
ошибку при отсутствии процесса/частичном VRAM/слишком большом контексте,
выгрузку через `keep_alive: 0`, точный `ModelResidencyProof` и отсутствие
чувствительного тела запроса в исключениях.

- [ ] **Шаг 2. Подтвердить RED**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~OllamaTransportTests|FullyQualifiedName~ModelRuntimeTests"
```

- [ ] **Шаг 3. Реализовать типизированные операции транспорта**

Добавить получение установленных моделей и процессов, allowlisted pull,
preflight и unload. `/api/pull` не должен быть доступен через свободный native
CLI-маршрут.

- [ ] **Шаг 4. Реализовать preflight и отключение**

`ModelRuntime.EnsureReadyAsync` проверяет каталог и контекст, выполняет пустой
preflight, читает `/api/ps`, требует точного ordinal-совпадения модели и полного
VRAM, а при сбое выгружает и отключает только сочетание `model × context`.
CPU/system-RAM offload никогда не принимается.

- [ ] **Шаг 5. Подтвердить GREEN**

## Задача 3. Детерминированный выбор, эксперименты и circuit breaker

**Файлы:** новые `ModelRouter.cs`, `ExperimentStateStore.cs` и их тесты.

- [ ] **Шаг 1. Написать красные тесты**

Проверить утверждённый порядок кандидатов, выбор экспериментальной модели для
первых 10 задач конкретного профиля, паузу после десятой задачи только для этой
пары, независимый счётчик другого профиля, безопасный explicit override,
fallback при structural/context/technical failure, circuit breaker после двух
последовательных технических ошибок, сброс серии после успеха, точечное
отключение при CPU offload, действия `promote`, `continue_experiment`,
`fallback_only`, `disable`, предпочтение подходящей resident-модели после
продвижения и отсутствие LLM у `ExactSearch`.

- [ ] **Шаг 2. Подтвердить RED**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~ModelRouterTests|FullyQualifiedName~ExperimentStateStoreTests"
```

- [ ] **Шаг 3. Реализовать чистый router**

Входы решения: каталог, live installed/resident sets, workload, explicit
override и не содержащий контента experiment state. Текст запроса и ответа не
участвует в выборе.

- [ ] **Шаг 4. Реализовать атомарное хранение эксперимента**

Состояние хранится под `%LOCALAPPDATA%\LocalAi\experiments\` атомарной заменой и
содержит только профиль, модель, счётчики, категории результата, серию
технических ошибок, паузу и решение владельца.

- [ ] **Шаг 5. Подтвердить GREEN**

## Задача 4. Устойчивые model-aware snapshots вместо строгого FIFO leasing

**Файлы:** `DurableQueue.cs`, новые `ModelAwareScheduler.cs` и
`DurationEstimator.cs`, `BrokerHost.cs` и соответствующие тесты.

- [ ] **Шаг 1. Написать красные тесты durable selection**

Очередь остаётся источником состояний; scheduler просматривает только
безопасные метаданные и атомарно арендует выбранный ID; одновременно существует
не более одной running lease; frozen snapshot не принимает поздние задачи;
задачи внутри него идут от коротких к длинным; resident affinity важнее
стоимости загрузки и длительности до starvation; окно сбора перед сменой/долгой
задачей не более двух секунд; workflow hints не создают вымышленных задач;
зависимые шаги ждут `IsDependencyReady`; ожидание 15 минут гарантирует
включение; равенства разрешаются priority и sequence; после восстановления
in-memory snapshot пересоздаётся из durable state; выполняющийся snapshot не
прерывается.

- [ ] **Шаг 2. Подтвердить RED**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~DurableQueueTests|FullyQualifiedName~ModelAwareSchedulerTests|FullyQualifiedName~DurationEstimatorTests|FullyQualifiedName~BrokerRecoveryTests"
```

- [ ] **Шаг 3. Добавить атомарный список кандидатов и lease по ID**

Scheduler не изменяет файлы очереди. `TryLeaseAsync` под тем же lock повторно
проверяет состояние выбранной задачи.

- [ ] **Шаг 4. Реализовать frozen snapshots и обучение длительности**

Rolling median и p90 ключуются только по профилю, модели, buckets входа/выхода,
количеству файлов/изображений/пикселей и cold/warm. Неизвестная работа получает
оценку `short`, `medium` или `long`. Порядок сравнения: starvation, resident
совместимость, наблюдаемая стоимость загрузки, кратчайшая snapshot-работа,
накопленный возраст, priority, durable sequence.

- [ ] **Шаг 5. Подключить scheduler к `BrokerHost`**

Host ждёт только возвращённый двухсекундный deadline, замораживает IDs,
арендует их по ID и пересчитывает решение после завершения snapshot.
Фактическая длительность успешного выполнения передаётся estimator. После
30 минут истинного простоя при отсутствии queued/running работы resident-модель
выгружается один раз; заблокированный workflow считается ожидающей работой.

- [ ] **Шаг 6. Подтвердить GREEN**, включая старые recovery-тесты.

## Задача 5. Координация routing, preflight, fallback и безопасной телеметрии

**Файлы:** новые `ModelExecutionCoordinator.cs`, `ModelTelemetryStore.cs`,
изменения host, receipt/contracts и соответствующие тесты.

- [ ] **Шаг 1. Написать красные тесты**

Scheduler и executor используют неизменный `ModelSelection`; cold-запуск
проходит preflight до отправки контента; warm-запуск использует свежий proof;
ошибка валидатора записывает structural outcome и один fallback; техническое
исключение записывает `TechnicalFailure`, безопасную telemetry-запись и
выполняет fallback; workflow переносит исходный experimental outcome без
увеличения счётчика для каждого chunk; частичный VRAM не получает контент;
receipt содержит профиль/модель/контекст, времена, cold/warm, fallback,
валидатор и token estimates; telemetry отклоняет поля prompt/answer/content/
image/path/secret и хранит только buckets, счётчики, длительности, enum,
boolean, model tag и catalog version.

- [ ] **Шаг 2. Подтвердить RED**

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~ModelExecutionCoordinatorTests|FullyQualifiedName~ModelTelemetryStoreTests|FullyQualifiedName~BrokerReceiptTests"
```

- [ ] **Шаг 3. Реализовать порядок исполнения**

```text
select -> preflight if needed -> execute -> validate ->
record outcome -> fallback if required -> finalize receipt
```

Preflight не использует контент задачи. Неоднозначный успешный текст не
повторяется как transport retry.

- [ ] **Шаг 4. Реализовать безопасные метрики и отчёты**

Атомарные JSON-файлы находятся под `%LOCALAPPDATA%\LocalAi\telemetry\`.
Считаются success/error/fallback, mean/median/p90, cold/warm, load/unload,
автоматические проверки, локальные input/output/total tokens, избегаемая
облачная генерация и чистое уменьшение облачного контекста.

- [ ] **Шаг 5. Подтвердить GREEN**

## Задача 6. Task-aware LocalLm и валидируемый перевод

**Файлы:** интерфейс и broker client LocalLm, `LocalModels.cs`, `LocalTasks.cs`,
новые chunker/validator/attribution и их тесты.

- [ ] **Шаг 1. Написать красные тесты**

Обычные задачи отправляют profile вместо устаревшего `qwen3.6:27b`; explicit
model остаётся override; log triage всегда `LogTriage`; image work различает
`Ocr`, `VisualAnalysis`, `ImageTranslation`; перевод различает plain и
technical/Markdown; chunks не превышают 48 000 символов и выбирают каталоговый
context tier, достаточный для prompt и ожидаемого output; Markdown headings,
fenced и inline code, links, placeholders и list structure проходят проверку;
структурная ошибка вызывает fallback; chunks имеют один workflow ID и
детерминированные индексы; документ получает ровно одну атрибуцию фактической
модели; token estimate вычитает проверку; context tier выбирается из каталога и
исполнение разрешено только при полном VRAM.

- [ ] **Шаг 2. Подтвердить RED**

```powershell
dotnet test tests/LocalLm.Tests/LocalLm.Tests.csproj --configuration Release
```

- [ ] **Шаг 3. Реализовать task-aware calls**

Удалить default `qwen3.6:27b`; routing defaults принадлежат каталогу брокера.

- [ ] **Шаг 4. Реализовать детерминированное разбиение**

Защитить непереводимые участки, делить прозу по абзацам/предложениям,
сохранять однозначную сборку и общий workflow ID.

- [ ] **Шаг 5. Реализовать валидацию и атрибуцию**

Проверить число структурных элементов и точное сохранение защищённых токенов.
После сборки добавить локализованное примечание с фактической успешной моделью,
включая fallback.

- [ ] **Шаг 6. Подтвердить GREEN**

## Задача 7. MCP для статуса, синхронизации, перевода, отчёта и feedback

**Файлы:** `LocalLmTools.cs`, `Program.cs`, startup service и MCP-тесты.

- [ ] **Шаг 1. Написать красные MCP-тесты**

Проверить `local_models_status`, `local_model_preflight`, `local_models_sync`,
`local_model_experiment_report`, `local_model_feedback`, `translate_local`,
task-aware `ask_local`, mode-aware `read_image` и fixed-profile `triage_log`.
Startup сравнивает каталог с live `/api/tags`, дедуплицирует maintenance pull,
не принимает произвольный tag, ставит pull после inference, одинаково работает
при одновременном запуске Codex/Claude и показывает только live state.
Feedback отклоняется до report/pause gate после десяти логических задач.

- [ ] **Шаг 2. Подтвердить RED**

```powershell
dotnet test tests/LocalLm.Tests/LocalLm.Tests.csproj --configuration Release --filter "FullyQualifiedName~LocalLmToolsTests|FullyQualifiedName~ModelCatalogStartupServiceTests"
```

- [ ] **Шаг 3. Реализовать broker-backed MCP methods**

Инструменты возвращают компактные типизированные результаты и русское
уведомление. Sync берёт цели только из embedded catalog.

- [ ] **Шаг 4. Добавить неблокирующую startup-синхронизацию**

Hosted service ставит загрузку в очередь и не блокирует MCP на многогигабайтном
скачивании; статус доступен через `local_models_status`.

- [ ] **Шаг 5. Подтвердить GREEN**

## Задача 8. Сохранить authority CodeSearch и детерминированный поиск

**Файлы:** тесты embedding/search/status; production CodeSearch меняется только
если это требует реально упавший regression-тест.

- [ ] **Шаг 1. Добавить compatibility tests**

Индекс по умолчанию использует `qwen3-embedding:8b-q8_0`; query использует
модель из base header; overlay наследует её; несовпадающая embedding-модель
отклоняется; exact symbol/path/text остаётся hybrid/lexical и не создаёт chat
job; optional deep rerank вызывается явно и имеет fallback на текущий rank.

- [ ] **Шаг 2. Запустить целевые CodeSearch-тесты**

```powershell
dotnet test tests/CodeSearch.Tests/CodeSearch.Tests.csproj --configuration Release --filter "FullyQualifiedName~BrokerEmbeddingClientTests|FullyQualifiedName~SearchEngineTests|FullyQualifiedName~SearchServiceStatusTests"
```

Существующее поведение должно оставаться зелёным; production нельзя менять
только ради искусственной routing-работы.

- [ ] **Шаг 3. Сделать минимальное compatibility-изменение при необходимости**

Explicit rerank использует `CodeRerank`, но не меняет vectors или stored rank.

- [ ] **Шаг 4. Запустить полный проект CodeSearch**

```powershell
dotnet test tests/CodeSearch.Tests/CodeSearch.Tests.csproj --configuration Release
```

## Задача 9. План установки и синхронизированная документация

**Файлы:** `ClientCommand.cs`, `BootstrapCommand.cs`, integration tests,
`README.md`, `README.ru.md`.

- [ ] **Шаг 1. Написать красные тесты installation plan**

Одинаковые `codesearch-mcp.exe` и `locallm-mcp.exe` регистрируются для Codex и
Claude; catalog встроен через broker binary; после замены нужен restart;
старые модели сохраняются; sync идёт через MCP, а не `ollama pull`; публикация
бинарников сама по себе не изменяет конфигурацию клиентов.

- [ ] **Шаг 2. Подтвердить RED**

```powershell
dotnet test tests/LocalAi.IntegrationTests/LocalAi.IntegrationTests.csproj --configuration Release --filter "FullyQualifiedName~ClientRegistrationTests|FullyQualifiedName~BootstrapTests"
```

- [ ] **Шаг 3. Обновить installation planning и английскую документацию**

Описать профили, routing, эксперимент 10 задач, двухсекундную группировку,
starvation 15 минут, idle unload 30 минут, context tiers до 256K, zero-offload
preflight, безопасные метрики и MCP-команды.

- [ ] **Шаг 4. Синхронизировать русский README**

По текущему глобальному правилу перевод выполняет основной агент. Локальный
`translate_local` используется только при явном запросе пользователя на
конкретную задачу. Код, команды, ссылки и структура сохраняются; примечание о
локальной модели добавляется только если локальный перевод действительно
использовался.

- [ ] **Шаг 5. Повторить integration tests и подтвердить GREEN**

## Задача 10. Полная проверка и security/compatibility self-review

- [ ] **Шаг 1. Restore и build**

```powershell
dotnet restore LocalAi.slnx
dotnet build LocalAi.slnx --configuration Release --no-restore
```

Сборка должна пройти без новых compiler/analyzer warnings.

- [ ] **Шаг 2. Запустить всю тестовую матрицу**

```powershell
dotnet test LocalAi.slnx --configuration Release --no-build
```

- [ ] **Шаг 3. Проверить полный diff**

Проверить `git status --short`, `git diff --check`, `git diff --stat` и
`git diff`. Убедиться, что Ollama не используется в обход брокера; telemetry не
принимает чувствительные поля; explicit-model совместимость сохранена; context
не превышает каталог; CPU/RAM offload запрещён; pull allowlisted и стоит после
inference; experiment state разделён по profile/model; late arrivals не
расширяют snapshot; deterministic search и embedding authority сохранены;
документы синхронны и имеют CRLF/UTF-8 без BOM.

- [ ] **Шаг 4. Выполнить publish без установки**

```powershell
dotnet publish src/CodeSearch.Mcp/CodeSearch.Mcp.csproj --configuration Release --no-build --output "$env:TEMP\localai-routing-verify\CodeSearch.Mcp"
dotnet publish src/LocalLm.Mcp/LocalLm.Mcp.csproj --configuration Release --no-build --output "$env:TEMP\localai-routing-verify\LocalLm.Mcp"
dotnet publish src/LocalAi.Cli/LocalAi.Cli.csproj --configuration Release --no-build --output "$env:TEMP\localai-routing-verify\LocalAi.Cli"
```

Все три команды должны пройти; LocalLm publish включает broker/contracts и
embedded catalog.

## Задача 11. Установка для Codex/Claude, sync и live acceptance

**Состояние:** заменить только существующий LocalAi/CodeSearch/LocalLm install,
изменить только текущие MCP-регистрации Codex/Claude, писать runtime state
только под `%LOCALAPPDATA%\LocalAi`, не менять Git.

- [ ] **Шаг 1. Разрешить и показать точные targets**

Показать текущие/новые paths и hashes, точные config entries, runtime path и
процессы для restart. До проверки confinement файлы не заменять.

- [ ] **Шаг 2. Остановить только старые LocalAi broker/MCP processes**

Определять их по точному executable/assembly path и command line; Ollama и
посторонние `dotnet` не останавливать.

- [ ] **Шаг 3. Установить проверенные artifacts и registrations**

Скопировать Task 10 publish в разрешённый install и применить точный план
Codex/Claude. Брокер автоматически запускается первым MCP-запросом.

- [ ] **Шаг 4. Синхронизировать рекомендуемые модели через MCP**

`local_models_status`, затем `local_models_sync`: старые модели сохраняются,
`translategemma:12b` становится дедуплицированной maintenance job, прямого
`ollama pull` нет, pull ждёт отсутствия inference.

- [ ] **Шаг 5. Дождаться pull ограниченным polling**

Не чаще одного `local_models_status` каждые 30 секунд и без молчания более
60 секунд.

- [ ] **Шаг 6. Live preflight RTX 5080**

Через `local_model_preflight` для `translategemma:12b` и 2048 токенов получить proof:

```text
model = translategemma:12b
context = 2048
size_vram = size
fully_resident = true
```

При offload немедленно выгрузить и отключить точное сочетание до отправки
контента.

- [ ] **Шаг 7. Проверить основные пути только через MCP**

Локальный перевод выполняется только если пользователь явно запросил его для
этой проверки. Остальные пути: OCR через `qwen3-vl:8b-instruct-q8_0`,
code/log analysis через `qwen2.5-coder:14b`, semantic CodeSearch по embedding
из header и exact lexical search без chat-модели. Проверить receipt, model,
context, fallback, zero-offload, timings и token estimates.

- [ ] **Шаг 8. Проверить оба клиента после restart**

В Codex и Claude вызвать status и одну read-only задачу; оба должны видеть
общую очередь, catalog version, experiment counters и installed models.

- [ ] **Шаг 9. Отчитаться без commit**

Указать файлы, команды и результаты, live status, residency proof, install
paths, первые experiment counters, риски и out-of-scope findings. Перед любой
Git-операцией запросить отдельное разрешение.

## Условные Git checkpoints

Только после явного разрешения:

1. `feat(broker): add model routing and lifecycle controls`
2. `feat(broker): schedule work by model affinity`
3. `feat(locallm): add task profiles and validated translation`
4. `docs: document model-aware local routing`

Перед каждым commit показывать staged diff и включать только точные файлы
соответствующего checkpoint.
