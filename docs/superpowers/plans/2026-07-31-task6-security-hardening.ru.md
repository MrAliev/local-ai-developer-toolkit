# План реализации: усиление безопасности задачи 6

[English version](2026-07-31-task6-security-hardening.md)

> **Для агентов:** ОБЯЗАТЕЛЬНЫЙ ПОДНАВЫК: выполняйте этот план задача за задачей через
> superpowers:subagent-driven-development (рекомендуется) или superpowers:executing-plans. Шаги
> размечены флажками (`- [ ]`) для отслеживания.

**Цель:** закрыть находки задачи 6 по активации, раскладке каталогов, конкурентному доступу к
указателю, доверенному launcher и безопасности неизменяемых версий — не создавая рантайм-зависимости
от проекта исполняемого launcher.

**Архитектура:** канонический мьютекс активации, аренда `current.lock` и строгий ограниченный
контракт снимка указателя и CAS переезжают в `LocalAi.Contracts`, на который Launcher и
Installer.Core уже ссылаются. `current.json` меняет только Launcher; Installer.Core берёт атомарные
защищённые аренды раскладки, публикует неизменяемые версии ровно один раз, удерживает проверенный
стабильный launcher и его родительские каталоги на время выполнения процесса и выполняет откат через
CAS новым launcher — прежде чем восстановить старый.

**Стек:** C#/.NET 10, Windows handle API, xUnit v3, SHA-256, `IProcessRunner`.

---

### Задача 1: общая аренда активации и CAS в launcher

**Файлы:**
- Создать: `src/LocalAi.Contracts/Activation/ActivationCoordinator.cs`
- Создать: `src/LocalAi.Contracts/Activation/CurrentPointerSnapshot.cs`
- Изменить: `src/LocalAi.Launcher/VersionActivator.cs`
- Изменить: `src/LocalAi.Launcher/LauncherProgram.cs`
- Тест: `tests/LocalAi.Launcher.Tests/VersionActivatorTests.cs`
- Тест: `tests/LocalAi.Launcher.Tests/LauncherProgramTests.cs`

- [ ] **Шаг 1: написать падающие тесты общего CAS**

```csharp
var expected = CurrentPointerExpectation.ExactHash(SHA256.HashData(before));
CreateActivator(install).Activate("v2", stopRunning: true, expected);
Assert.Equal("v2", new VersionResolver(install.BinRoot).ReadCurrent().Version);
```

Добавьте случаи: отсутствующий указатель, неверный хеш, сырую перезапись той же версии,
продублированный и неизвестный ключ CLI, взаимоисключающие ожидания.

- [ ] **Шаг 2: прогнать RED**

```powershell
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj --no-restore --filter "FullyQualifiedName~VersionActivatorTests|FullyQualifiedName~LauncherProgramTests"
```

Ожидается: ошибки компиляции из-за отсутствующих типов ожидания и координатора.

- [ ] **Шаг 3: реализовать общую аренду со снимком и CAS в launcher**

```csharp
using var lease = ActivationCoordinator.AcquireExclusive(binRoot, timeout);
var actual = CurrentPointerSnapshot.ReadLocked(lease, maximumBytes: 4096);
expectation.Validate(actual);
WritePointerAtomically(version);
```

CLI принимает ровно один из ключей `--if-current-missing` или `--if-current-sha256 <64 символа hex
в верхнем регистре>` плюс необязательный `--stop-running`; несовпадение бросает устойчивый
`current_pointer_changed`.

- [ ] **Шаг 4: прогнать GREEN и закоммитить**

```powershell
dotnet test tests/LocalAi.Launcher.Tests/LocalAi.Launcher.Tests.csproj --no-restore
git add src/LocalAi.Contracts/Activation src/LocalAi.Launcher tests/LocalAi.Launcher.Tests
git commit -m "fix(installer): coordinate pointer activation with CAS"
```

### Задача 2: атомарная аренда защищённой раскладки установки

**Файлы:**
- Заменить: `src/LocalAi.Installer.Core/Activation/InstallationLayout.cs`
- Создать: `src/LocalAi.Installer.Core/Activation/InstallationLayoutLease.cs`
- Тест: `tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs`

- [ ] **Шаг 1: написать падающие тесты на гонку раскладки**

```csharp
using var lease = InstallationLayoutLease.Acquire(layout);
Assert.ThrowsAny<IOException>(() => Directory.Move(layout.LauncherDirectory, racedPath));
```

Покройте: столкновение с тем, кто создаёт каталог первым; reparse у родительского каталога; файлы
внутри `versions`; зарезервированные имена, оказавшиеся файлами; небезопасные имена версий;
конкурентный дрейф идентичности.

- [ ] **Шаг 2: прогнать RED**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --no-restore --filter FullyQualifiedName~LocalAiPackageInstallerTests
```

Ожидается: типа аренды нет либо небезопасная гонка проходит успешно.

- [ ] **Шаг 3: реализовать захват раскладки относительно дескрипторов**

```csharp
using var lease = InstallationLayoutLease.Acquire(layout);
lease.Revalidate();
using var temporary = lease.CreateVersionTemporary();
temporary.PublishAbsent(version);
```

Используйте нативные create/open без прохода по reparse-точкам, защищённые ACL, удержанные
идентичности, каноническую вложенность и точно распознаваемые формы `bin` и `installer/backups`, —
допуская при этом посторонние рантайм-каталоги внутри `LocalAi`.

- [ ] **Шаг 4: прогнать GREEN и закоммитить**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --no-restore --filter FullyQualifiedName~LocalAiPackageInstallerTests
git add src/LocalAi.Installer.Core/Activation tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs
git commit -m "fix(installer): lease protected installation layout"
```

### Задача 3: передача доверенного launcher и политика неизменяемых «сирот»

**Файлы:**
- Изменить: `src/LocalAi.Installer.Core/Activation/LocalAiPackageInstaller.cs`
- Тест: `tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs`

- [ ] **Шаг 1: написать падающие тесты передачи и запрета удаления**

```csharp
runner.OnRun = launcherPath => AssertWriteDeleteAndAncestorRenameBlocked(launcherPath);
var result = await installer.InstallAsync(package, layout, cancellationToken);
Assert.True(Directory.Exists(result.VersionPath));
Assert.True(result.InactivePublishedVersionRetained);
```

Покройте: отмену до старта; таймаут, отмену и принудительное завершение после старта; точные путь и
аргументы процесса; удержанную идентичность launcher; попытки переименования и reparse у родительских
каталогов; отсутствие рекурсивного удаления после публикации.

- [ ] **Шаг 2: прогнать RED**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --no-restore --filter FullyQualifiedName~LocalAiPackageInstallerTests
```

Ожидается: старая уборка удаляет опубликованную версию либо подмена launcher удаётся прямо во время
выполнения процесса.

- [ ] **Шаг 3: реализовать передачу через удержанный дескриптор**

```csharp
using var trustedLauncher = lease.LockLauncher(expectedMetadata);
trustedLauncher.Revalidate();
var process = await runner.RunAsync(trustedLauncher.CanonicalPath, casArguments, timeout, token);
trustedLauncher.Revalidate();
```

Опубликованную версию не удалять рекурсивно никогда; удалять можно только неопубликованные временные
каталоги с доказанной идентичностью.

- [ ] **Шаг 4: прогнать GREEN и закоммитить**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --no-restore --filter FullyQualifiedName~LocalAiPackageInstallerTests
git add src/LocalAi.Installer.Core/Activation tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs
git commit -m "fix(installer): lock trusted launcher handoff"
```

### Задача 4: порядок восстановления через CAS и неопределённые исходы

**Файлы:**
- Изменить: `src/LocalAi.Installer.Core/Activation/LocalAiPackageInstaller.cs`
- Тест: `tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs`

- [ ] **Шаг 1: написать падающие тесты порядка восстановления**

```csharp
Assert.Equal(newLauncherBytes, runner.Calls[1].ObservedLauncherBytes);
Assert.Contains("--if-current-sha256", runner.Calls[1].Arguments);
Assert.Equal(LocalAiPackageInstallStatus.Indeterminate, thirdPointerResult.Status);
```

Покройте: откат через новый launcher до восстановления старого; точное восстановление сырого
предыдущего указателя; отказ трогать посторонний v3; дрейф байтов в пределах той же версии; ручное
восстановление, когда указатель был создан заново.

- [ ] **Шаг 2: прогнать RED**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --no-restore --filter FullyQualifiedName~LocalAiPackageInstallerTests
```

Ожидается: старый launcher восстанавливается слишком рано либо посторонние указатели
перезаписываются.

- [ ] **Шаг 3: реализовать конечный автомат восстановления через CAS**

```csharp
var actual = activationLease.ReadPointer();
if (!actual.IsExpectedPostFailure) return Indeterminate(actual);
await RunNewLauncherAsync("activate", prior.Version, "--if-current-sha256", actual.Sha256Hex);
VerifyExactPointer(prior);
RestorePriorLauncherAtomically();
```

Если предыдущий указатель не менялся — восстанавливать только launcher. Напрямую писать или удалять
`current.json` нельзя никогда.

- [ ] **Шаг 4: прогнать GREEN и закоммитить**

```powershell
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --no-restore
git add src/LocalAi.Installer.Core/Activation tests/LocalAi.Installer.Core.Tests/LocalAiPackageInstallerTests.cs
git commit -m "fix(installer): recover activation with pointer CAS"
```

### Задача 5: финальные проверки

**Файлы:**
- Изменять только если непройденная проверка сначала получила падающий регрессионный тест.

- [ ] **Шаг 1: прогнать полную проверку**

```powershell
dotnet build LocalAi.slnx -c Release --no-restore --nologo
dotnet test LocalAi.slnx -c Release --no-build --nologo
dotnet publish src/LocalAi.Installer/LocalAi.Installer.csproj -c Release -r win-x64 --self-contained true --no-restore --nologo
dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj -c Release --no-build --nologo --filter "FullyQualifiedName~LocalAiPackageInstallerTests|FullyQualifiedName~StagingRootSecurityTests"
git diff --check
git status --short
```

- [ ] **Шаг 2: просмотреть спецификацию и коммиты**

```powershell
git log --oneline 8e9a5a1..HEAD
git diff --check 8e9a5a1..HEAD
```

Ожидается: отдельные коммиты-исправления, чистое рабочее дерево, ноль падений и только те пропуски
тестов, что зависят от поддержки reparse-точек платформой.
