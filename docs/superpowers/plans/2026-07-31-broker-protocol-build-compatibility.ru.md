# План реализации совместимости протокола и сборки broker

> **Для agentic workers:** ОБЯЗАТЕЛЬНЫЙ SUB-SKILL: используйте superpowers:subagent-driven-development (рекомендуется) или superpowers:executing-plans для пошагового выполнения. Шаги отмечены checkbox (`- [ ]`).

**Цель:** заменить привязку к пути assembly явным protocol/build compatibility contract и выдавать точные результаты запуска без ослабления гарантий LocalAi.

**Архитектура:** `LocalAi.Contracts` владеет schema состояния host и текущим семейством совместимости. `BrokerProcess` классифицирует каждый свежий живой host до запуска, наблюдает отделённый дочерний процесс через небольшую abstraction и выдаёт типизированные bootstrap errors. Assembly path остаётся только для определения владения версией launcher и диагностики.

**Технологии:** .NET 10, C# records, строгий `System.Text.Json`, Windows process APIs, xUnit v3, существующая инфраструктура тестов launcher/version и broker.

---

## Структура файлов

- `src/LocalAi.Contracts/BrokerContracts.cs`: compatibility value, constants и schema 3.
- `src/LocalAi.Broker/Program.cs`: публикация schema 3 и текущей совместимости.
- Новый `src/LocalAi.Broker.Client/BrokerBootstrap.cs`: типизированная ошибка, observation status и start-attempt abstraction.
- `src/LocalAi.Broker.Client/BrokerProcess.cs`: классификация, переиспользование, наблюдение запуска и диагностика.
- Новый `src/LocalAi.Launcher/BrokerHostStateReader.cs`: строгое чтение schema-3 ownership metadata.
- Новый `src/LocalAi.Launcher/Properties/AssemblyInfo.cs`: доступ к internals только test assembly.
- `src/LocalAi.Launcher/LocalAiProcessController.cs`: использование reader с сохранением path ownership.
- `tests/LocalAi.Broker.Tests/BrokerRuntimeStateStoreTests.cs`: сериализация compatibility contract.
- `tests/LocalAi.Broker.Tests/BrokerProcessTests.cs`: compatibility, lock owner, failed start и timeout.
- `tests/LocalAi.Launcher.Tests/LocalAiProcessControllerTests.cs`: path ownership версии.
- `README.md` и `README.ru.md`: совместимость и диагностика.

### Задача 1. Публикация явного compatibility contract

**Файлы:** `BrokerRuntimeStateStoreTests.cs`, `BrokerContracts.cs`, `Program.cs`.

- [ ] Добавить падающий тест `Publish_includes_current_protocol_and_build_compatibility`, создающий состояние через:

```csharp
private static BrokerProcessState CurrentState(int processId = 42) =>
    new(
        processId,
        DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow,
        BrokerCompatibilityContract.HostStateSchemaVersion,
        Path.GetFullPath("LocalAi.Broker.dll"),
        BrokerCompatibilityContract.Current);
```

Проверить `Compatibility` и `SchemaVersion == 3`.

- [ ] Запустить:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~BrokerRuntimeStateStoreTests"
```

Ожидание: RED на отсутствующих типах.

- [ ] Добавить в `BrokerContracts.cs` точные типы:

```csharp
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerCompatibility(
    int ProtocolVersion,
    string BuildCompatibilityId);

public static class BrokerCompatibilityContract
{
    public const int HostStateSchemaVersion = 3;
    public const int ProtocolVersion = 1;
    public const string BuildCompatibilityId = "localai-broker-v1";

    public static BrokerCompatibility Current { get; } =
        new(ProtocolVersion, BuildCompatibilityId);

    public static bool IsCurrent(BrokerCompatibility? value) =>
        value is
        {
            ProtocolVersion: ProtocolVersion,
            BuildCompatibilityId: BuildCompatibilityId
        };
}
```

Добавить nullable `BrokerCompatibility? Compatibility = null` последним
параметром `BrokerProcessState`, не удаляя `BrokerAssemblyPath`.

- [ ] Перевести все state-store fixtures и `LocalAi.Broker/Program.cs` на schema
3 и `BrokerCompatibilityContract.Current`.

- [ ] Повторить focused test; ожидание GREEN.

- [ ] Commit:

```powershell
git add src/LocalAi.Contracts/BrokerContracts.cs src/LocalAi.Broker/Program.cs tests/LocalAi.Broker.Tests/BrokerRuntimeStateStoreTests.cs
git commit -m "feat(broker): publish compatibility contract"
```

### Задача 2. Переиспользование совместимого broker по другому пути

**Файлы:** `BrokerProcessTests.cs`, новый `BrokerBootstrap.cs`, `BrokerProcess.cs`.

- [ ] Добавить `Compatible_broker_at_another_assembly_path_is_reused`: state
указывает `installed/LocalAi.Broker.dll`, startup arguments — development DLL,
compatibility совпадает, `starts == 0`.

- [ ] Запустить тест отдельно и увидеть RED из-за path equality.

- [ ] Создать:

```csharp
public sealed class BrokerBootstrapException(
    string code,
    string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal enum BrokerObservationStatus
{
    CompatibleHealthy,
    IncompatibleHealthy,
    AbsentOrStale,
    StartingOrLockOwned
}

internal sealed record BrokerObservation(
    BrokerObservationStatus Status,
    string Detail);
```

- [ ] Удалить path equality из health check. Порядок `Observe`: null/unreadable,
stale heartbeat, process ownership, schema/compatibility, непустой diagnostic
path, compatible. Path используется только в detail.

- [ ] `EnsureRunningAsync` возвращается для `CompatibleHealthy`, бросает
`broker_incompatible` для `IncompatibleHealthy` и запускает broker только для
`AbsentOrStale`.

- [ ] Повторить focused test; ожидание GREEN.

- [ ] Commit:

```powershell
git add src/LocalAi.Broker.Client/BrokerBootstrap.cs src/LocalAi.Broker.Client/BrokerProcess.cs tests/LocalAi.Broker.Tests/BrokerProcessTests.cs
git commit -m "fix(broker): reuse compatible hosts across paths"
```

### Задача 3. Явное отклонение live incompatible и legacy host

**Файлы:** `BrokerProcessTests.cs`, `BrokerProcess.cs`.

- [ ] Добавить тест с тем же path и `new BrokerCompatibility(2, "other")`, а
также свежий schema 2 с `Compatibility: null`. Оба требуют:

```csharp
var error = await Assert.ThrowsAsync<BrokerBootstrapException>(
    () => process.EnsureRunningAsync(TestContext.Current.CancellationToken));
Assert.Equal("broker_incompatible", error.Code);
Assert.Contains("expected protocol=1", error.Message);
Assert.Equal(0, starts);
```

Legacy message также содержит `schema=2`.

- [ ] Запустить фильтр `Incompatible|Legacy`; увидеть RED на недостаточной
диагностике.

- [ ] Реализовать стабильный detail:

```text
expected schema=3 protocol=1 build=localai-broker-v1; actual schema=<n> protocol=<n|missing> build=<value|missing>; broker path=<path|missing>
```

Не включать command lines, environment, job data или credentials.

- [ ] Запустить весь `BrokerProcessTests`; ожидание GREEN.

- [ ] Commit:

```powershell
git add src/LocalAi.Broker.Client/BrokerProcess.cs tests/LocalAi.Broker.Tests/BrokerProcessTests.cs
git commit -m "fix(broker): reject incompatible live hosts"
```

### Задача 4. Наблюдение startup, lock ownership и раннего failure

**Файлы:** `BrokerProcessTests.cs`, `BrokerBootstrap.cs`, `BrokerProcess.cs`.

- [ ] Добавить в `BrokerBootstrap.cs` production abstraction:

```csharp
internal interface IBrokerStartAttempt : IDisposable
{
    int ProcessId { get; }
    bool TryGetExitCode(out int exitCode);
}
```

Затем добавить в tests `FakeStartAttempt` с `Running`/`Exited`, `ProcessId`,
`TryGetExitCode` и пустым `Dispose`.

- [ ] Добавить RED-тесты:
  - zero-exit child затем compatible owner — success и один start;
  - zero-exit child затем incompatible owner — `broker_incompatible`;
  - exit 17 без host — `broker_start_failed`, message содержит 17, delay не
    вызывается.

- [ ] Запустить фильтр `lock_owner|nonzero_exit`; ожидание RED.

- [ ] Добавить `ProcessStartAttempt`, владеющий `Process`, безопасно читающий
`HasExited`/`ExitCode` и освобождающий handle. Изменить `_start` на:

```csharp
private readonly Func<string, string, IBrokerStartAttempt> _start;
```

Сохранить существующий detached `CreateStartInfo`.

- [ ] В polling loop сначала классифицировать state, затем:
  compatible → return; incompatible → throw; nonzero child exit →
  `broker_start_failed`; zero exit → `StartingOrLockOwned` и bounded
  observation; running child → `starting process <pid>`.

- [ ] Повторить фильтр и весь `BrokerProcessTests`; ожидание GREEN.

- [ ] Commit:

```powershell
git add src/LocalAi.Broker.Client/BrokerBootstrap.cs src/LocalAi.Broker.Client/BrokerProcess.cs tests/LocalAi.Broker.Tests/BrokerProcessTests.cs
git commit -m "fix(broker): expose startup and lock outcomes"
```

### Задача 5. Actionable timeout и invalid-state diagnostics

**Файлы:** `BrokerProcessTests.cs`, `BrokerProcess.cs`.

- [ ] Заменить generic timeout test на проверку `BrokerBootstrapException`,
code `broker_start_timeout`, `last observation:` и последнего detail. Добавить
zero-exit lock-owner test без появившегося host.

- [ ] Запустить фильтр `timeout|lock_owner_did_not_publish`; увидеть RED.

- [ ] Хранить `lastObservation` и бросать:

```csharp
throw new BrokerBootstrapException(
    "broker_start_timeout",
    $"LocalAi broker did not become ready within {_startupTimeout}; " +
    $"last observation: {lastObservation}.");
```

Cancellation имеет приоритет; polling interval остаётся 50 ms.

- [ ] Запустить весь `BrokerProcessTests`; ожидание GREEN.

- [ ] Commit:

```powershell
git add src/LocalAi.Broker.Client/BrokerProcess.cs tests/LocalAi.Broker.Tests/BrokerProcessTests.cs
git commit -m "fix(broker): report bounded startup diagnostics"
```

### Задача 6. Сохранение launcher version ownership при schema 3

**Файлы:** новый `BrokerHostStateReader.cs`, новый `Properties/AssemblyInfo.cs`,
`LocalAiProcessController.cs`, `LocalAiProcessControllerTests.cs`,
`VersionActivatorTests.cs`.

- [ ] Расширить selection test двумя broker snapshots с paths под разными
version directories; выбирается только запрошенный path. Добавить test, который
пишет свежий strict schema-3 `host.json` с compatibility object и проверяет
`BrokerHostStateReader.ReadFreshAssemblyPath` при fixed `TimeProvider`. Сохранить
existing active-broker activation regression.

- [ ] Запустить focused launcher tests; ожидание RED, потому что
`BrokerHostStateReader` отсутствует.

- [ ] Создать strict reader с shapes:

```csharp
internal sealed record BrokerHostState(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset HeartbeatAtUtc,
    int SchemaVersion,
    string BrokerAssemblyPath,
    BrokerHostCompatibility? Compatibility);

internal sealed record BrokerHostCompatibility(
    int ProtocolVersion,
    string BuildCompatibilityId);
```

Reader требует schema 3, свежий heartbeat, non-empty path, positive protocol и
non-empty build ID, но не решает client compatibility. Добавить:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("LocalAi.Launcher.Tests")]
```

`LocalAiProcessController` использует reader, сохраняя process identity и
start-time checks.

- [ ] Запустить все launcher tests; ожидание GREEN.

- [ ] Commit:

```powershell
git add src/LocalAi.Launcher/BrokerHostStateReader.cs src/LocalAi.Launcher/Properties/AssemblyInfo.cs src/LocalAi.Launcher/LocalAiProcessController.cs tests/LocalAi.Launcher.Tests/LocalAiProcessControllerTests.cs tests/LocalAi.Launcher.Tests/VersionActivatorTests.cs
git commit -m "fix(launcher): retain schema-three broker ownership"
```

### Задача 7. Документация и полная проверка

**Файлы:** `README.md`, `README.ru.md`.

- [ ] Синхронно описать explicit protocol/build compatibility, diagnostic path,
installed/development reuse, `broker_incompatible`, typed startup diagnostics и
сохранённый запрет direct Ollama. Сохранить UTF-8 без BOM и CRLF в working tree.

- [ ] Запустить focused suites:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~BrokerProcessTests|FullyQualifiedName~BrokerRuntimeStateStoreTests|FullyQualifiedName~RuntimeAclTests"
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj -c Release --nologo
```

- [ ] Запустить:

```powershell
dotnet test LocalAi.slnx -c Release --nologo
git diff --check
git status --short
git diff --stat main...HEAD
```

Ожидание: ноль failures; известный Windows symlink test может быть skipped;
scope ограничен issue #6.

- [ ] Commit docs:

```powershell
git add README.md README.ru.md
git commit -m "docs: explain broker compatibility diagnostics"
```

- [ ] Через existing launcher workflow опубликовать candidate, запустить
installed broker и development client. Проверить один PID, schema 3, отсутствие
второго lock owner, успешный read-only broker request и при загруженной модели
`size_vram == size`. Ollama напрямую не вызывать.

- [ ] После финальной проверки push только
`codex/issue-6-broker-compatibility` и создать один PR с ссылкой на issue #6.
Installer branch в PR не включать.
