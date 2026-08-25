# Проект точной навигации по коду

[English version](2026-08-06-precise-code-navigation-design.md)

## Назначение

Добавить в LocalAi точные `Go to definition`, `Find references` и позднее
`Find implementations` для C#, XAML, TypeScript/JavaScript и Python. Точная
навигация должна использовать результаты компилятора или language-specific
indexer, быть привязана к конкретному snapshot репозитория и сосуществовать с
текущим векторным CodeSearch без изменения его retrieval-семантики.

## Ключевое решение

Текущий `CIDX` остаётся индексом retrieval-чанков и embedding-векторов. Данные
definitions/references не добавляются в `ChunkMeta` и не кодируются как
embedding-чанки. Для них вводится отдельный `SemanticIndex` в той же immutable
generation и с той же snapshot identity:

```text
repository generation
├── base.cidx       vector and lexical retrieval
├── semantic.sidx  definitions, references and relationships
└── manifest.json  generation metadata
```

SCIP используется как язык-независимый формат импорта и экспорта. Внутреннее
хранилище LocalAi может быть компактнее SCIP, но обязано сохранять его основные
понятия: document, occurrence, symbol information, symbol role и relationship.

## Границы первой версии

- Первый вертикальный срез: C# в одном solution/project graph.
- Первый XAML-диалект: WPF; остальные подключаются адаптерами.
- TypeScript/JavaScript: импорт результата `scip-typescript`.
- Python: импорт результата `scip-python`.
- Постоянный индекс обслуживает сохранённый snapshot; live LSP overlay будет
  добавлен после стабильного on-disk API.
- Tree-sitter и текстовый поиск являются fallback и никогда не маркируются как
  точный результат.
- Не обращаться к Ollama напрямую и не использовать embedding-модель для
  разрешения символов.
- Не изменять формат CIDX и порядок его composite ordinals в первой версии.

## Модель данных

`SemanticIndex` содержит:

- `RepositoryId`, `GenerationId`, `GitTree`, `DirtyHash`, `BaseCommit`;
- документы с нормализованным относительным путём и content hash;
- canonical symbol ID и сведения о символе;
- occurrences с точным UTF-16 range и ролями definition/reference;
- relationships `implementation`, `typeDefinition`, `override`;
- происхождение результата: compiler, SCIP, LSP или heuristic fallback.

Позиции хранятся как zero-based line/UTF-16 column, совместимо с LSP и SCIP.
Для публичного API возвращается явная система координат; неявное смешивание с
1-based диапазонами CodeSearch запрещено.

Canonical ID должен быть детерминированным и стабильным внутри package/version.
Для импортированных данных авторитетен SCIP symbol string. Для собственного C#
индексатора используется SCIP-совместимый ID с package manager, package name,
version и цепочкой descriptors. Локальные символы имеют document-local ID и не
сопоставляются между независимыми сборками.

## Snapshot identity и overlays

Semantic query принимается только для snapshot, совпадающего с текущей
repository/generation/tree/dirty identity. Нельзя разрешать occurrence из одной
generation по ordinal или path другой generation.

Базовый semantic index неизменяем. Ветка или dirty worktree получает overlay,
который заменяет изменённые документы и содержит tombstones удалённых файлов.
До реализации semantic overlay dirty snapshot закрывается явной диагностикой
или обслуживается live LSP; тихо использовать устаревшую базу запрещено.

## Query API

Минимальный сервис предоставляет:

```text
ResolveOccurrence(path, line, utf16Column)
GoToDefinition(path, line, utf16Column)
FindReferences(path, line, utf16Column, includeDefinition)
```

Алгоритм `GoToDefinition`:

```text
position -> narrowest containing occurrence -> canonical symbol
         -> definition occurrences -> source locations
```

Алгоритм `FindReferences`:

```text
position -> canonical symbol -> all matching occurrences
         -> role filter -> stable path/range ordering
```

Результат содержит `Precision`:

- `Precise`: compiler/SCIP/LSP подтвердил symbol identity;
- `Inferred`: тип или контекст выведен статически, но не гарантирован языком;
- `Heuristic`: syntax/text fallback.

MCP-инструменты `go_to_definition` и `find_references` возвращают только
source-derived блоки в существующих nonce-bound untrusted markers.

## C# индексатор

`MSBuildWorkspace` загружает solution/project graph после restore. Для каждого
`SyntaxTree` используется `Compilation.GetSemanticModel`. Индексируются:

- namespaces, named types, methods, constructors, properties, events, fields;
- declarations и все source references, разрешённые через `ISymbol`;
- partial declarations, overrides и interface implementations;
- aliases и reduced extension methods с нормализацией к исходному символу;
- generated documents только как промежуточный источник связей.

Переходы по generated code должны remap-иться к пользовательскому source, когда
доступна связь с XAML или source generator output.

## XAML supplement

XAML индексируется отдельным framework adapter поверх MSBuild evaluation и
Roslyn compilation. Общий parser отвечает за точные ranges элементов,
атрибутов и markup extensions; адаптер диалекта отвечает за семантику.

WPF MVP разрешает:

- XML namespace -> CLR namespace/assembly;
- element/type, property, attached property и event;
- `x:Class`, event handler, `x:Name`, `x:Reference`;
- `StaticResource`, `DynamicResource`, `BasedOn`, `TargetType`;
- `ResourceDictionary.Source`;
- binding path, только когда тип source доказан.

Ссылки XAML на CLR members используют те же canonical IDs, что и C# indexer.
Определением generated field от `x:Name` считается исходный XAML range, а не
файл под `obj/`.

Compiled bindings (`x:Bind`, MAUI/Avalonia `x:DataType`) могут быть precise.
Обычный WPF `Binding` без доказуемого DataContext получает `Inferred` или
`Heuristic`, но не `Precise`.

## SCIP adapters

External indexer запускается в отдельном процессе из project root после
dependency restore. Его stdout/stderr ограничиваются, timeout и cancellation
обязательны. Импортируемый `index.scip` проверяется на размер, количество
documents/occurrences, нормализованные paths и допустимые ranges до публикации
generation.

Первоначальные адаптеры:

- `scip-typescript` для TS/JS;
- `scip-python` для Python;
- собственный Roslyn exporter/importer для C# и XAML.

Сбой одного language adapter не повреждает предыдущую generation. Manifest
фиксирует successful/failed/skipped статус каждого языка.

## Хранилище

Первая реализация использует отдельный versioned binary container `SIDX` с
атомарной записью sibling temp + move, как CIDX. Векторы отсутствуют. Таблицы
сортируются детерминированно:

1. documents по ordinal path;
2. symbols по canonical ID;
3. occurrences по document, start, end, symbol;
4. relationships по source, kind, target.

После загрузки строятся in-memory индексы `position -> occurrence`, `symbol ->
definitions` и `symbol -> references`. Для больших репозиториев формат может
получить memory mapping или SQLite projection без изменения query contracts.

## Безопасность и корректность

- Paths проходят текущую repository-root containment проверку.
- Protobuf и external process output считаются недоверенными.
- Ни один импортёр не загружает анализируемые assemblies в основной процесс.
- MSBuild/Roslyn анализ выполняется без исполнения пользовательских build
  targets, когда это возможно; режим, требующий build, должен быть явным.
- Все результаты привязаны к content hash и snapshot identity.
- Публикация generation атомарна только после полной валидации индексов.

## Проверка

Используются RED -> GREEN -> REFACTOR и golden fixtures. Обязательные наборы:

- deterministic `SIDX` round-trip и corruption handling;
- exact position boundary, Unicode и UTF-16 columns;
- C# overloads, aliases, partials, overrides, generics и cross-project refs;
- XAML type/member/event/name/resource/binding cases;
- malformed SCIP, path traversal и out-of-range occurrences;
- snapshot/generation/tree/dirty mismatch;
- MCP untrusted-content boundaries;
- cold/warm latency и memory measurements на репозитории LocalAi.

Каждый инкремент заканчивается focused tests, полной
`dotnet test LocalAi.slnx -c Release --nologo`, `git diff --check` и обновлением
английской/русской документации.
