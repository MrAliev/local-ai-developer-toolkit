# План реализации точной навигации по коду

[English version](2026-08-06-precise-code-navigation.md)

**Цель:** добавить snapshot-bound semantic index и точные definition/reference
запросы, не изменяя текущий CIDX retrieval pipeline.

**Архитектура:** отдельный `SIDX` внутри существующей immutable generation,
SCIP как interchange, language adapters для Roslyn/XAML/scip-typescript/
scip-python и live LSP overlay после стабильного persistent API.

**Стек:** .NET 10, C# 14, Roslyn Workspaces/MSBuild, SCIP Protobuf, xUnit v3,
существующие repository identity, generation store и MCP security boundaries.

## Задача 1: Контракты и детерминированный SIDX

- [x] RED: round-trip, byte determinism, corruption/version rejection, UTF-16
  ranges, duplicate symbols и invalid paths.
- [x] Добавить `SemanticDocument`, `SemanticSymbol`, `SemanticOccurrence`,
  `SemanticRelationship`, roles, precision и snapshot identity.
- [x] Реализовать atomic `SemanticIndex.Save/Load` без векторов.
- [x] Добавить lookup indexes и deterministic ordering.
- [x] Focused и полная test suite.

## Задача 2: Query service и MCP

- [x] RED: narrowest occurrence, definition, references, includeDefinition,
  stable ordering, absent symbol и snapshot mismatch.
- [x] Реализовать `SemanticNavigationService`.
- [x] Добавить `go_to_definition` и `find_references` в MCP.
- [x] Применить containment checks и `UntrustedContent.Wrap`.
- [x] Добавить CLI-команды для локальной диагностики.

## Задача 3: C# Roslyn indexer

- [x] Добавить Roslyn Workspaces/MSBuild dependencies и solution loader.
- [ ] RED fixtures: overloads, partials, aliases, generics, overrides,
  interface implementations и cross-project references.
- [x] Генерировать deterministic canonical symbols и occurrences.
- [x] Исключить `bin/obj`, но remap-ить полезные generated symbols.
- [x] Публиковать SIDX атомарно рядом с CIDX.

## Задача 4: WPF XAML supplement

- [x] Добавить lossless XAML ranges и WPF namespace resolver.
- [x] RED fixtures для types, properties, attached properties, events,
  `x:Class`, `x:Name`, `x:Reference`, resources и dictionaries.
- [x] Связать XAML CLR references с C# canonical symbols.
- [x] Добавить binding resolver с явным precision level.
- [x] Remap generated fields к XAML definitions.

## Задача 5: SCIP import и external indexers

- [x] Vendor/pin SCIP schema и добавить protobuf parser limits.
- [x] Реализовать validated SCIP -> SIDX importer.
- [x] Добавить bounded process runner и manifest status per adapter.
- [x] Подключить `scip-typescript` и `scip-python` на fixtures.
- [x] Проверить package/version и cross-repository symbol identities.

## Задача 6: Semantic overlays и LSP

- [x] Добавить changed-document semantic overlay и deletion tombstones.
- [x] Добавить LSP session manager для открытых/dirty документов.
- [x] Перезапускать упавший LSP process один раз и воспроизводить открытые документы.
- [x] Проверить TypeScript и Python через реальные Windows npm command shims.
- [x] Приоритет: authoritative LSP overlay -> snapshot-bound SIDX.
- [x] Добавить syntax/text fallback с явным non-precise provenance.
- [x] Никогда не повышать heuristic result до precise.

## Задача 7: Расширение и приёмка

- [x] Добавить adapters WinUI, MAUI и Avalonia XAML.
- [x] Добавить Find implementations и relationship queries.
- [x] Измерить correctness, cold/warm latency, memory и index size.
- [x] Обновить README EN/RU, installer manifest и release notes 0.1.15.
- [x] Полная solution suite, `git diff --check`, clean install test.
