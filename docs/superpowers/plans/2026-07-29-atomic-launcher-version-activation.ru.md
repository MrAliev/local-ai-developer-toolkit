# План атомарной активации версии через LocalAi Launcher

> **Для агентных исполнителей:** ОБЯЗАТЕЛЬНЫЙ ДОПОЛНИТЕЛЬНЫЙ НАВЫК: использовать superpowers:executing-plans и выполнять план последовательно. Шаги отмечаются checkbox (`- [ ]`).

**Цель:** направить всех потребителей LocalAi через единый стабильный launcher и атомарно активировать полные неизменяемые версии без смешивания binaries клиента и брокера.

**Архитектура:** добавить BCL-only launcher, который удерживает shared file lease всё время работы разрешённого versioned child и требует exclusive lease для активации. Активный неизменяемый каталог хранится в атомарно заменяемом `current.json`, идентичность broker assembly публикуется в `host.json`, а Codex, Claude, Git hooks и Python-wrapper мигрируют на стабильную команду launcher.

**Технологии:** .NET 10, C#, xUnit v3, `System.Diagnostics.Process`, `System.Text.Json`, Windows file sharing и atomic rename, Python 3 `unittest`.

---

## Структура файлов

### Новые production-файлы launcher

- `src/LocalAi.Launcher/LocalAi.Launcher.csproj` — BCL-only executable.
- `src/LocalAi.Launcher/Program.cs` — разбор `run` и `activate`, стабильные ошибки.
- `src/LocalAi.Launcher/LauncherLayout.cs` — канонические пути и allowlist.
- `src/LocalAi.Launcher/VersionPointer.cs` — строгая модель и атомарная запись.
- `src/LocalAi.Launcher/VersionResolver.cs` — confined-разрешение версии.
- `src/LocalAi.Launcher/VersionLease.cs` — shared run и exclusive activation leases.
- `src/LocalAi.Launcher/ToolRunner.cs` — child с наследуемыми stdio.
- `src/LocalAi.Launcher/VersionActivator.cs` — проверка, остановка и commit pointer.
- `src/LocalAi.Launcher/LocalAiProcessController.cs` — выбор процессов по точному пути.

### Новые тесты launcher

- `tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj`
- `tests/LocalAi.Launcher.Tests/GlobalUsings.cs`
- `tests/LocalAi.Launcher.Tests/VersionResolverTests.cs`
- `tests/LocalAi.Launcher.Tests/VersionLeaseTests.cs`
- `tests/LocalAi.Launcher.Tests/VersionActivatorTests.cs`
- `tests/LocalAi.Launcher.Tests/ToolRunnerTests.cs`
- `tests/LocalAi.Launcher.Tests/LocalAiProcessControllerTests.cs`

### Существующие файлы LocalAi

- `LocalAi.slnx` — добавить launcher и его тесты.
- `src/LocalAi.Contracts/BrokerContracts.cs` — identity broker assembly.
- `src/LocalAi.Broker/Program.cs` — публикация identity в `host.json`.
- `src/LocalAi.Broker.Client/BrokerProcess.cs` — reuse только совпадающего broker.
- `tests/LocalAi.Broker.Tests/BrokerProcessTests.cs` — проверки версий.
- `src/LocalAi.Cli/ClientCommand.cs` — стабильная команда и arguments.
- `src/LocalAi.Cli/HookInstaller.cs` — command prefix launcher.
- `src/LocalAi.Cli/Program.cs` — provenance launcher при установке hooks.
- `tests/LocalAi.IntegrationTests/ClientRegistrationTests.cs`
- `tests/LocalAi.IntegrationTests/HookInstallerTests.cs`
- `README.md` и `README.ru.md` — синхронная документация.

### Файлы wrapper делегирования

- `C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\delegate.py`
- `C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\local_models\ollama_client.py`
- `C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\tests\test_ollama_client.py`
- `C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\tests\test_delegate.py`

## Задача 1: Строгое разрешение текущей версии

**Файлы:** создать project/layout/pointer/resolver/tests из списка и изменить `LocalAi.slnx`.

- [ ] **Шаг 1: Добавить тестовый проект и RED-тесты resolver**

Создать временную структуру `bin/current.json` и полный
`bin/versions/v1` с `localai.exe`, `codesearch.exe`, двумя MCP executables,
`LocalAi.Broker.dll` и `LocalAi.Contracts.dll`.

Тесты должны:

- разрешать `localai`, `codesearch`, `codesearch-mcp`, `locallm-mcp` только из
  `v1`;
- отвергать schema `2`, `..`, separator и rooted version;
- возвращать `version_incomplete` при отсутствии обязательного файла.

Добавить точные assertions:

```csharp
[Theory]
[InlineData("localai", "localai.exe")]
[InlineData("codesearch", "codesearch.exe")]
[InlineData("codesearch-mcp", "codesearch-mcp.exe")]
[InlineData("locallm-mcp", "locallm-mcp.exe")]
public void Resolves_every_allowlisted_tool_from_one_version(
    string tool,
    string executable)
{
    var layout = TestInstall.CreateComplete("v1");
    layout.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

    var resolved = new VersionResolver(layout.BinRoot).Resolve(tool);

    Assert.Equal("v1", resolved.Version);
    Assert.Equal(
        Path.Combine(layout.VersionsRoot, "v1", executable),
        resolved.ExecutablePath);
}

[Theory]
[InlineData("""{"schemaVersion":2,"version":"v1"}""")]
[InlineData("""{"schemaVersion":1,"version":".."}""")]
[InlineData("""{"schemaVersion":1,"version":"sub\\v1"}""")]
[InlineData("""{"schemaVersion":1,"version":"C:\\escape"}""")]
public void Rejects_unsupported_or_escaping_pointer(string json)
{
    var layout = TestInstall.CreateComplete("v1");
    layout.WriteCurrent(json);

    var error = Assert.Throws<LauncherException>(
        () => new VersionResolver(layout.BinRoot).Resolve("localai"));

    Assert.Contains(
        error.Code,
        new[] { "current_pointer_invalid", "version_path_invalid" });
}
```

- [ ] **Шаг 2: Подтвердить RED**

```powershell
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~VersionResolverTests"
```

Ожидается ошибка компиляции: launcher types ещё отсутствуют.

- [ ] **Шаг 3: Реализовать минимальные contracts и resolver**

Добавить `VersionPointer(int SchemaVersion, string Version)`,
`ResolvedTool(string Version, string VersionDirectory, string ExecutablePath)`
и `LauncherException` с `Code`.

Allowlist обязательных файлов:

```csharp
[
    "localai.exe",
    "codesearch.exe",
    "codesearch-mcp.exe",
    "locallm-mcp.exe",
    "LocalAi.Broker.dll",
    "LocalAi.Contracts.dll"
]
```

Использовать `Path.GetFullPath`, `Path.GetRelativePath`,
`ResolveLinkTarget(returnFinalTarget: true)`, strict JSON без duplicate/unknown
members и только schema `1`.
Существующий broker test загрузки catalog остаётся доказательством, что routing
catalog встроен в `LocalAi.Broker.dll`.

- [ ] **Шаг 4: Подтвердить GREEN**

Повторить команду шага 2; все `VersionResolverTests` должны пройти.

- [ ] **Шаг 5: Commit**

```powershell
git add LocalAi.slnx src/LocalAi.Launcher tests/LocalAi.Launcher.Tests
git commit -m "feat(launcher): resolve immutable LocalAi versions"
```

## Задача 2: Version lease на всё время жизни child

**Файлы:** создать `VersionLease.cs`, `ToolRunner.cs`, `Program.cs` и два набора тестов.

- [ ] **Шаг 1: Написать RED-тесты lease/runner**

Доказать:

- несколько shared leases сосуществуют;
- exclusive lease возвращает `version_in_use`, пока жив shared lease;
- runner передаёт `["native", "tags"]`, не redirect-ит stdio и возвращает
  injected exit code `17`;
- shared lease освобождается только после завершения child.

Основные assertions:

```csharp
[Fact]
public void Shared_run_lease_blocks_exclusive_activation_lease()
{
    var path = Path.Combine(_root, "current.lock");
    using var first = VersionLease.AcquireShared(path);
    using var second = VersionLease.AcquireShared(path);

    var error = Assert.Throws<LauncherException>(
        () => VersionLease.AcquireExclusive(
            path,
            TimeSpan.Zero,
            TimeProvider.System));

    Assert.Equal("version_in_use", error.Code);
}

[Fact]
public async Task Runner_forwards_exact_tool_arguments_and_exit_code()
{
    var child = new FakeChildProcess(exitCode: 17);
    var runner = new ToolRunner(child.Start);

    var exitCode = await runner.RunAsync(
        @"C:\LocalAi\bin\versions\v1\localai.exe",
        ["native", "tags"],
        TestContext.Current.CancellationToken);

    Assert.Equal(17, exitCode);
    Assert.Equal(["native", "tags"], child.Arguments);
    Assert.False(child.RedirectedStandardIo);
}
```

- [ ] **Шаг 2: Подтвердить RED**

```powershell
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~VersionLeaseTests|FullyQualifiedName~ToolRunnerTests"
```

- [ ] **Шаг 3: Реализовать leases**

Shared handle: `OpenOrCreate`, `ReadWrite`, `FileShare.ReadWrite`.
Exclusive handle: те же mode/access и `FileShare.None`, bounded retry, затем
`version_in_use`.

- [ ] **Шаг 4: Реализовать inherited-stdio execution**

Использовать `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`, все три
redirect-флага `false`, `CreateNoWindow=true`. Передать
`LOCALAI_LAUNCHER_PATH` и `LOCALAI_ACTIVE_VERSION`. При cancellation завершать
только child tree и возвращать точный exit code.

```csharp
var startInfo = new ProcessStartInfo(executablePath)
{
    UseShellExecute = false,
    RedirectStandardInput = false,
    RedirectStandardOutput = false,
    RedirectStandardError = false,
    CreateNoWindow = true
};
foreach (var argument in arguments)
{
    startInfo.ArgumentList.Add(argument);
}
startInfo.Environment["LOCALAI_LAUNCHER_PATH"] = Environment.ProcessPath;
startInfo.Environment["LOCALAI_ACTIVE_VERSION"] = version;
```

- [ ] **Шаг 5: Подключить `run`**

Разрешить только:

```text
localai-launcher run <allowlisted-tool> [arguments...]
```

Shared lease берётся до чтения pointer; ошибки идут только в stderr в формате
`<code>: <message>`.

- [ ] **Шаг 6: GREEN и commit**

Повторить тесты и выполнить:

```powershell
git add src/LocalAi.Launcher tests/LocalAi.Launcher.Tests
git commit -m "feat(launcher): lease active versions during execution"
```

## Задача 3: Атомарная активация и точная остановка процессов

**Файлы:** создать activator/controller/tests и изменить broker state/client/tests.

- [ ] **Шаг 1: RED для rollback и конкурентной активации**

Тест неполного `v2` должен подтвердить `version_incomplete` и байт-в-байт
неизменный `current.json`. Два параллельных activators для `v2`/`v3` должны
сериализоваться, оставить один валидный pointer и не оставить `.tmp`.

```csharp
[Fact]
public void Incomplete_candidate_leaves_pointer_byte_for_byte_unchanged()
{
    var layout = TestInstall.CreateComplete("v1");
    layout.CreateIncomplete("v2");
    layout.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
    var before = File.ReadAllBytes(layout.CurrentPath);

    var error = Assert.Throws<LauncherException>(
        () => layout.Activator().Activate("v2", stopRunning: false));

    Assert.Equal("version_incomplete", error.Code);
    Assert.Equal(before, File.ReadAllBytes(layout.CurrentPath));
}
```

- [ ] **Шаг 2: RED для process selection**

Injected snapshots должны выбрать только versioned MCP PID и schema-2 broker PID
из `v1`; исключить Ollama, посторонний `dotnet` и MCP из `v2`.

```csharp
var snapshots = new[]
{
    new ProcessSnapshot(10, started, v1CodeSearchMcp, null),
    new ProcessSnapshot(11, started, dotnet, v1BrokerDll),
    new ProcessSnapshot(12, started, ollamaExe, null),
    new ProcessSnapshot(13, started, dotnet, unrelatedDll),
    new ProcessSnapshot(14, started, v2LocalLmMcp, null)
};

var selected = controller.SelectOwnedByVersion(v1Directory, snapshots);

Assert.Equal([10, 11], selected.Select(process => process.ProcessId));
```

- [ ] **Шаг 3: RED для broker identity**

Расширить ожидаемый state полем `BrokerAssemblyPath`, schema `2`. Совпадающий
PID/start/heartbeat с другим assembly path обязан запустить replacement;
полностью совпадающий — reuse; schema `1` — unhealthy.

```csharp
public sealed record BrokerProcessState(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset HeartbeatAtUtc,
    int SchemaVersion,
    string BrokerAssemblyPath);
```

- [ ] **Шаг 4: Запустить RED**

```powershell
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~VersionActivatorTests|FullyQualifiedName~LocalAiProcessControllerTests"

dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~BrokerProcessTests"
```

- [ ] **Шаг 5: Публиковать и проверять identity**

`LocalAi.Broker/Program.cs` публикует schema `2` и канонический
`typeof(BrokerHost).Assembly.Location`. `BrokerProcess` требует совпадения
schema, heartbeat, PID/start и platform-aware полного пути assembly.

```csharp
var brokerAssemblyPath = Path.GetFullPath(typeof(BrokerHost).Assembly.Location);
var owner = new BrokerProcessState(
    process.Id,
    startedAt,
    DateTimeOffset.UtcNow,
    2,
    brokerAssemblyPath);
```

- [ ] **Шаг 6: Реализовать exact process control**

Останавливать только executable физически внутри current version или свежий
PID/start из `host.json` с broker assembly внутри этой версии. Использовать
`Kill(entireProcessTree:true)` и bounded wait. Stale host state никогда не
трогает PID.

- [ ] **Шаг 7: Реализовать atomic pointer commit**

Unique temp в `bin`, `FileOptions.WriteThrough`, `Flush(true)`,
`File.Move(temp,current,overwrite:true)`. Машинный activation mutex удерживается
на всех этапах; candidate повторно проверяется после exclusive lease.

```csharp
File.Move(temporaryPath, currentPath, overwrite: true);
```

- [ ] **Шаг 8: Подключить `activate`**

Поддержать `activate <version>` и `activate <version> --stop-running`, timeout
15 секунд. Ошибки до commit не меняют pointer.

- [ ] **Шаг 9: GREEN и commit**

Повторить оба test run и создать commit:

```powershell
git add src/LocalAi.Launcher tests/LocalAi.Launcher.Tests `
  src/LocalAi.Contracts/BrokerContracts.cs `
  src/LocalAi.Broker/Program.cs `
  src/LocalAi.Broker.Client/BrokerProcess.cs `
  tests/LocalAi.Broker.Tests/BrokerProcessTests.cs
git commit -m "feat(launcher): activate versions atomically"
```

## Задача 4: Регистрация всех потребителей через launcher

**Файлы:** client/hook/integration tests из структуры.

- [ ] **Шаг 1: RED-тесты регистрации**

`ClientCommand.Plan(@"C:\LocalAi\bin")` должен вернуть launcher path и arguments
`run codesearch-mcp` / `run locallm-mcp`, Codex TOML с `args`, Claude command с
теми же arguments. Hook должен содержать:

```text
"C:/LocalAi/bin/launcher/localai-launcher.exe" run localai hook post-commit
```

Без `LOCALAI_LAUNCHER_PATH` установка hook обязана завершиться до записи.

```csharp
var plan = ClientCommand.Plan(@"C:\LocalAi\bin");

Assert.Equal(
    @"C:\LocalAi\bin\launcher\localai-launcher.exe",
    plan.CodeSearch.Command);
Assert.Equal(["run", "codesearch-mcp"], plan.CodeSearch.Arguments);
Assert.Equal(["run", "locallm-mcp"], plan.LocalLm.Arguments);
Assert.Contains(
    "args = [\"run\", \"codesearch-mcp\"]",
    plan.CodexTomlSections[0]);
```

- [ ] **Шаг 2: Подтвердить RED**

```powershell
dotnet test tests/LocalAi.IntegrationTests/LocalAi.IntegrationTests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~ClientRegistrationTests|FullyQualifiedName~HookInstallerTests"
```

- [ ] **Шаг 3: Реализовать command + arguments**

Добавить `ClientToolRegistration(string Command, IReadOnlyList<string>
Arguments)`, хранить две регистрации в plan, генерировать TOML/Claude без
применения конфигурации (`AppliesClientConfiguration=false`).

- [ ] **Шаг 4: Стабилизировать hooks**

`HookInstaller.Install(common, launcher, ["run","localai"])` отдельно quote-ит
executable и prefix arguments. `Program.cs` требует
`LOCALAI_LAUNCHER_PATH`, иначе stderr + exit `2`, без изменений hooks.

- [ ] **Шаг 5: GREEN и commit**

```powershell
git add src/LocalAi.Cli tests/LocalAi.IntegrationTests
git commit -m "feat(cli): register clients through stable launcher"
```

## Задача 5: Исправить Python resolution и классификацию ошибок

**Файлы:** четыре файла wrapper из структуры.

- [ ] **Шаг 1: RED-тесты Python**

Проверить, что `cli_command=(launcher,"run","localai")` образует команду
`launcher run localai native tags`; non-zero exit с
`current_pointer_invalid` даёт `LocalAiProcessError`, а exit `0` с invalid JSON
даёт `OllamaProtocolError`. `_client()` не должен содержать `bin\localai.exe`.

```python
def test_nonzero_broker_exit_is_not_ollama_unavailable(self) -> None:
    client = OllamaClient(cli_command=("launcher.exe", "run", "localai"))
    with patch("subprocess.run") as run:
        run.return_value = CompletedProcess(
            args=[],
            returncode=1,
            stdout="",
            stderr="current_pointer_invalid: malformed",
        )
        with self.assertRaisesRegex(
            LocalAiProcessError,
            "current_pointer_invalid",
        ):
            client.tags()

def test_invalid_broker_stdout_is_protocol_error(self) -> None:
    client = OllamaClient(cli_command=("launcher.exe", "run", "localai"))
    with patch("subprocess.run") as run:
        run.return_value = CompletedProcess(
            args=[],
            returncode=0,
            stdout="not-json",
            stderr="",
        )
        with self.assertRaises(OllamaProtocolError):
            client.tags()
```

- [ ] **Шаг 2: Подтвердить RED**

```powershell
Push-Location C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts
python -m unittest tests.test_ollama_client tests.test_delegate
Pop-Location
```

- [ ] **Шаг 3: Реализовать command prefix**

Заменить `cli_path` на `cli_command: tuple[str,...]`, строить
`[*self._cli_command,"native",operation]`. Добавить `LocalAiProcessError`.
Нормализовать control characters и ограничить stderr 2048 символами. Invalid
stdout — protocol error; failure запуска executable — `OllamaUnavailable`.

`delegate.py` использует абсолютный stable launcher path и `run localai`.

```python
cli_command=(
    r"C:\Users\Mr.Aliev\tools\LocalAi\bin\launcher\localai-launcher.exe",
    "run",
    "localai",
)
```

- [ ] **Шаг 4: GREEN**

Запустить focused tests и:

```powershell
python -m unittest discover `
  -s C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\tests `
  -t C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts
```

Wrapper находится вне LocalAi repo, поэтому не добавлять его в LocalAi commit;
diff сообщить отдельно.

## Задача 6: Документация и публикация полной candidate version

**Файлы:** `README.md`, `README.ru.md`, ignored install artifacts.

- [ ] **Шаг 1: Синхронно документировать**

Описать immutable publication, stable registrations, schema `current.json`,
`activate`, `--stop-running`, rollback старой проверенной версией, запрет
прямого Ollama и сохранение исторических версий.

- [ ] **Шаг 2: Проверить документацию**

```powershell
git diff --check
rg -n "localai-launcher|current.json|activate" README.md README.ru.md
```

Оба файла: CRLF, UTF-8 без BOM.

- [ ] **Шаг 3: Полная source verification**

```powershell
dotnet test LocalAi.slnx --configuration Release
```

- [ ] **Шаг 4: Опубликовать fresh immutable directory**

Использовать short SHA implementation commit как `<new-version>`. Опубликовать
пять executable projects следующими командами в новый temp,
объединить и проверить required files, затем один раз скопировать в
`bin\versions\<new-version>`. Launcher публикуется отдельно в `bin\launcher`
при отсутствии launcher processes. `current.json` на этом шаге не менять.

```powershell
dotnet publish src/LocalAi.Cli/LocalAi.Cli.csproj -c Release --no-restore
dotnet publish src/CodeSearch.Cli/CodeSearch.Cli.csproj -c Release --no-restore
dotnet publish src/CodeSearch.Mcp/CodeSearch.Mcp.csproj -c Release --no-restore
dotnet publish src/LocalLm.Mcp/LocalLm.Mcp.csproj -c Release --no-restore
dotnet publish src/LocalAi.Launcher/LocalAi.Launcher.csproj -c Release --no-restore
```

- [ ] **Шаг 5: Commit документации**

```powershell
git add README.md README.ru.md
git commit -m "docs: explain atomic LocalAi activation"
```

## Задача 7: Однократная live-миграция и acceptance

**State:** `bin/current.json`, Codex/Claude configs, managed hooks и только
LocalAi broker/MCP processes.

- [ ] **Шаг 1: Точный read-only preview**

Показать old/candidate paths и hashes, required artifacts, `host.json`,
процессы для остановки, before/after Codex/Claude, прямые LocalAi paths в hooks
и неизменяемый список Ollama processes. Любой target вне утверждённых путей
останавливает миграцию.

- [ ] **Шаг 2: Установить и проверить initial pointer на `caed45c`**

Если pointer отсутствует, остановить только exact напрямую зарегистрированные
broker/MCP-процессы `caed45c` из preview, затем выполнить:

```powershell
bin\launcher\localai-launcher.exe activate caed45c
bin\launcher\localai-launcher.exe run localai native tags
```

Первая команда атомарно создаёт pointer, вторая demand-start-ит broker
`caed45c` и завершается с exit `0`.

- [ ] **Шаг 3: Атомарно активировать candidate**

```powershell
bin\launcher\localai-launcher.exe activate <new-version> --stop-running
```

Только старые exact LocalAi broker/MCP завершаются; Ollama остаётся.

- [ ] **Шаг 4: Применить previewed registrations**

Codex и Claude получают stable launcher command и args соответственно
`run codesearch-mcp` и `run locallm-mcp`. Только managed LocalAi hooks
переустанавливаются через launcher; chained hooks сохраняются.

```text
command = C:\Users\Mr.Aliev\tools\LocalAi\bin\launcher\localai-launcher.exe
args = run codesearch-mcp
```

```text
command = C:\Users\Mr.Aliev\tools\LocalAi\bin\launcher\localai-launcher.exe
args = run locallm-mcp
```

- [ ] **Шаг 5: Проверить исходный failure path**

```powershell
python C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\delegate.py discover
```

Ожидается exit `0`, JSON моделей, отсутствие `OllamaUnavailable` и broker
assembly из `<new-version>`.

- [ ] **Шаг 6: Проверить stable entry points**

```powershell
bin\launcher\localai-launcher.exe run localai native tags
bin\launcher\localai-launcher.exe run codesearch status --root C:\Users\Mr.Aliev\tools\LocalAi
```

После restart Codex/Claude
проверить CodeSearch и LocalLm из обоих клиентов; все используют
`<new-version>` и общий `%LOCALAPPDATA%\LocalAi`.

- [ ] **Шаг 7: Свежая финальная проверка**

```powershell
dotnet test LocalAi.slnx --configuration Release
python -m unittest discover `
  -s C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts\tests `
  -t C:\Users\Mr.Aliev\.codex\skills\delegate-to-local-models\scripts
git diff --check
git status --short
```

- [ ] **Шаг 8: Независимый self-review**

Проверить confinement/reparse, lifetime lease, atomic replacement, exact process
ownership, чистоту MCP stdout, отсутствие прямого Ollama, согласованность всех
клиентов и парность документации. Любое замечание исправлять новым RED/GREEN
циклом. Не push-ить и не создавать PR без отдельного разрешения.
