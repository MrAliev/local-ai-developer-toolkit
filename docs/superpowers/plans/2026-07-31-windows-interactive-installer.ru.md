# План реализации интерактивного установщика Windows

[English version](2026-07-31-windows-interactive-installer.md)

> **Для агентных исполнителей:** ОБЯЗАТЕЛЬНЫЙ ПОДНАВЫК: выполнять этот план по задачам через `superpowers:subagent-driven-development` (рекомендуется) или `superpowers:executing-plans`. Для отслеживания используются шаги с флажками (`- [ ]`).

**Цель:** создать самодостаточный WPF-установщик для Windows 10/11 x64, который диагностирует зависимости, устанавливает выбранные пользователем компоненты, проверяет и активирует релизы LocalAi, рекомендует модели по VRAM, проверяет их только через общий FIFO broker и безопасно настраивает поддерживаемых агентов.

**Архитектура:** вся политика и побочные эффекты находятся в тестируемом
`LocalAi.Installer.Core`; `LocalAi.Installer` остаётся тонкой WPF-оболочкой.
Self-contained installer не получает runtime assembly references на executable
projects: он вызывает packaged stable launcher точными массивами аргументов, а
запущенный LocalAi CLI повторно использует `VersionActivator`,
`BrokerLocalModelClient` и контракты. Журнал обеспечивает resume и rollback.

**Стек:** .NET 10, C# 14, WPF, xUnit v3, `System.Text.Json`, `HttpClient`, Windows DXGI/WinTrust, существующие launcher/broker/contracts.

---

## Обязательные гарантии

- Работа ведётся только в `codex/windows-installer`, одним PR для issue #9.
- Перед каждой production-реализацией наблюдается корректно падающий тест.
- По умолчанию установка выполняется в `%LOCALAPPDATA%\LocalAi`; неизвестная структура не перезаписывается.
- Ollama определяется по реестру установки и метаданным файла. Установщик не вызывает Ollama HTTP или executable для поиска, загрузки и работы с моделями.
- Статус, загрузка и preflight моделей выполняются только через `ILocalModelClient` и общий FIFO broker.
- Сохраняются singleton, FIFO, ACL, protocol/build compatibility, immutable activation, full-VRAM и zero-offload.
- Значения credentials не читаются в диагностические отчёты и не показываются в preview.
- Настройки агентов изменяются только структурно и внутри единственного managed-блока, после точного preview, byte-for-byte backup, атомарной записи и read-back.
- Внешние зависимости фиксируются в журнале, но rollback не удаляет их автоматически.

## Задачи

### 1. Добавить проекты

Создать `LocalAi.Installer.Core`, WPF-проект `LocalAi.Installer`, минимальный
`Program.cs` с `[STAThread] static void Main()`, unit-тесты Core/UI и
integration-тесты; добавить их в `LocalAi.slnx`. Сначала тест проверяет
отсутствие solution entries (RED), затем добавляются минимальные проекты
(GREEN). В Task 11 временная точка входа заменяется WPF composition root.

Коммит:

```powershell
git commit -m "build(installer): add Windows installer projects"
```

### 2. Неизменяемый план и согласия

Создать `Planning/InstallerPlan.cs` и `InstallerPlanBuilder.cs`. Тестами зафиксировать snapshot коллекций, отдельные согласия для каждой внешней операции, независимый выбор моделей и агентов, запрет дубликатов и unsupported diagnosis.

Коммит:

```powershell
git commit -m "feat(installer): define immutable execution plans"
```

### 3. Диагностика Windows

Создать `IProcessRunner`, `EnvironmentDiagnosis`, `WindowsEnvironmentDetector`, `WindowsGpuProbe`. Проверить Windows/x64, диск, сеть, WinGet, Git, Ollama по registry/file metadata, установленный LocalAi, Codex/Claude и DXGI dedicated VRAM. Тест обязан доказать, что process runner никогда не получает `ollama`.

Коммит:

```powershell
git commit -m "feat(installer): diagnose Windows prerequisites"
```

### 4. Зависимости с явным согласием

Создать `DependencyCatalog` и `WingetDependencyInstaller`. Использовать только точные package ID, `--exact`, `--source winget`, silent/architecture/agreement flags. Отдельно тестировать отказ, отмену, elevation и ошибку. Успешная внешняя установка не удаляется rollback.

Коммит:

```powershell
git commit -m "feat(installer): install consented dependencies"
```

### 5. Проверка подписанного релиза

Создать строгий `ReleaseManifest`, ECDSA P-256 verifier, SHA-256/Authenticode/layout verifier и HTTPS release client. Тестировать неизвестные/дублированные поля, неверную подпись/digest, traversal, reparse points, duplicate ZIP entries, размерные лимиты, отсутствующие файлы и несовместимый protocol/build.

Коммит:

```powershell
git commit -m "feat(installer): verify signed release packages"
```

### 6. Immutable staging и activation

Создать `LocalAiPackageInstaller` и `InstallationLayout`. Копировать проверенный
пакет ровно один раз в новый `bin\versions\<version>`, проверять required layout,
сохранять launcher и вызывать
`localai-launcher.exe activate <version> --stop-running` через `IProcessRunner`.
`VersionActivator` остаётся внутри launcher; после команды обязательно прочитать
`current.json`. Существующий version-directory не изменять.

Коммит:

```powershell
git commit -m "feat(installer): activate immutable LocalAi packages"
```

### 7. Рекомендации моделей

Создать чистый `ModelRecommendationEngine`. Проверить no-GPU, single/multi-GPU, ручной выбор адаптера, запрет суммирования VRAM, исключение shared memory, reserve для runtime/context, граничные случаи и уровни Minimal/Recommended/Extended/manual. До preflight результат всегда называется оценкой.

Коммит:

```powershell
git commit -m "feat(installer): recommend models from dedicated VRAM"
```

### 8. Модели только через broker

Добавить в LocalAi CLI строгие JSON-команды
`model status|pull|preflight`, которые внутри используют существующий
`ILocalModelClient`. `BrokerModelInstaller` вызывает их только через stable
launcher (`run localai model ...`). Recording fakes должны доказать точные
аргументы, сохранение catalog version, preflight каждого выбора и отклонение
модели без full-VRAM/zero-offload proof. Installer не получает executable
assembly reference и не имеет Ollama transport.

Коммит:

```powershell
git commit -m "feat(installer): install models through FIFO broker"
```

### 9. Безопасная настройка Codex и Claude

Создать `ManagedInstructionBlock`, `CodexConfigurationAdapter`, `ClaudeConfigurationAdapter` и точный preview. Поддержать четыре независимых варианта на агент: MCP, инструкции, оба, ничего. Тестировать TOML/JSON, malformed/unknown layouts, duplicate markers, byte backup, optimistic concurrency hash, atomic write, read-back и rollback.

Целевые инструкции:

```text
<!-- BEGIN LOCALAI MANAGED INSTRUCTIONS -->
Use only the shared LocalAi FIFO broker for local-model work.
Never access Ollama directly.
Require full-VRAM, zero-offload validation.
<!-- END LOCALAI MANAGED INSTRUCTIONS -->
```

Коммит:

```powershell
git commit -m "feat(installer): configure supported agents safely"
```

### 10. Журнал, resume, diagnostics и rollback

Создать `InstallerJournal`, `InstallerExecutor`, `RollbackService`, `RedactedDiagnosticReport`. После каждого перехода атомарно сохранять strict snapshot. Повторный запуск пропускает completed steps. Rollback идёт в обратном порядке и проверяет pointer/config restoration. Diagnostics не содержит prompts/jobs/tokens/credentials/config values.

Коммит:

```powershell
git commit -m "feat(installer): add resumable transactions and rollback"
```

### 11. WPF wizard

Создать страницы Diagnose, Dependencies, Package, Models, Agent Integration, Review/Apply и Finish, тонкие view models, английские/русские resources. Unit-тесты проверяют навигацию, consent gates, model/agent choices, точный review, final confirmation, cancellation/progress/rollback и переключение языка без UI automation.

Коммит:

```powershell
git commit -m "feat(installer): add interactive WPF wizard"
```

### 12. Интеграционные сценарии

На временных home/root использовать fake WinGet/process runner, fake release endpoint и fake `ILocalModelClient`. Проверить чистую установку, upgrade/rollback, invalid package, preflight rejection, concurrent config change и resume после сбоя.

Коммит:

```powershell
git commit -m "test(installer): cover complete installation scenarios"
```

### 13. Packaging и парная документация

Добавить `.github/workflows/windows-installer.yml`, `docs/windows-installer.md`, `docs/windows-installer.ru.md` и синхронные README. Workflow выполняет restore/build/test, self-contained `win-x64` publish, сборку payload, canonical manifest, подпись через secret/trusted service, SHA-256 и Authenticode. В PR публикуются только unsigned CI artifacts; подписанный installer разрешён только protected tag.

Коммит:

```powershell
git commit -m "docs(installer): add packaging and operating guide"
```

### 14. Финальный gate

```powershell
dotnet restore LocalAi.slnx
dotnet build LocalAi.slnx -c Release --no-restore --nologo
dotnet test LocalAi.slnx -c Release --no-build --nologo
dotnet publish src/LocalAi.Installer/LocalAi.Installer.csproj -c Release -r win-x64 --self-contained true --no-restore --nologo
git diff --check
git status --short
```

Дополнительно проверить clean VM, compatible upgrade, VRAM snapshots/real GPU, broker-only pull/preflight, disposable Codex/Claude homes, rollback, redaction и паритет EN/RU. После review создать один PR, закрывающий #9. Подписанный installer release не публиковать, пока signing credentials и protected-tag workflow не пройдут полностью.
