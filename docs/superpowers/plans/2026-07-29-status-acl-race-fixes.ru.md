# План реализации исправлений статуса и ACL-гонки

[English version](2026-07-29-status-acl-race-fixes.md)

> **Для агентных исполнителей:** ОБЯЗАТЕЛЬНЫЙ ДОПОЛНИТЕЛЬНЫЙ НАВЫК: используйте superpowers:subagent-driven-development (рекомендуется) или superpowers:executing-plans для пошагового выполнения этого плана. Шаги отслеживаются флажками (`- [ ]`).

**Цель:** Сделать статус генерации зависимым от настроенной mainline-ссылки и обеспечить устойчивость Windows ACL-проверки к атомарным перемещениям путей, которыми управляет брокер.

**Архитектура:** Статус генерации будет разрешать `DevRef` из манифеста репозитория через запрошенный worktree и сравнивать базовый индекс с этой ссылкой, поэтому отдельный mainline-worktree не потребуется. ACL-проверка сохранит строгую валидацию существующих путей, но пропустит узел только тогда, когда ошибка применения/чтения сопровождается подтверждённым исчезновением пути; обход будет перечислять по одному каталогу, чтобы перемещённый каталог не ломал рекурсивный итератор.

**Технологии:** .NET 10, xUnit v3, Git worktrees, Windows filesystem ACL APIs.

---

### Задача 1: Определять устаревание базы по настроенной mainline-ссылке

**Файлы:**
- Изменить: `tests/CodeSearch.Tests/SearchServiceStatusTests.cs`
- Изменить: `src/CodeSearch.Core/Search/SearchService.cs`

- [x] **Шаг 1: Написать падающий тест статуса**

Добавить тест, который создаёт ссылки `dev` и `feature` без отдельного worktree для `dev`, публикует генерацию из commit `dev`, пока `Index.Root` указывает на checkout feature-ветки, и сохраняет манифест репозитория с `DevRef = "refs/heads/dev"`.

```csharp
[Fact]
public void Generation_status_tracks_the_manifest_mainline_ref()
{
    // Публикуем базу из dev, пока единственный checkout находится на feature.
    // HEAD feature отличается, но настроенная ссылка dev всё ещё равна базе.
    Assert.False(new SearchService().Status(_root).CommitDrifted);

    // Продвигаем настроенную ссылку dev без создания worktree для dev.
    Git("branch", "-f", "dev", "feature");
    Assert.True(new SearchService().Status(_root).CommitDrifted);
}
```

- [x] **Шаг 2: Запустить целевой тест и подтвердить RED**

Запустить:

```powershell
dotnet test tests/CodeSearch.Tests/CodeSearch.Tests.csproj --no-restore --filter "FullyQualifiedName~Generation_status_tracks_the_manifest_mainline_ref"
```

Ожидается: FAIL, потому что `Status` сравнивает базу с HEAD feature-checkout вместо `manifest.DevRef`.

- [x] **Шаг 3: Реализовать разрешение commit mainline**

Добавить `CurrentBaseCommit` в `SearchService` и использовать результат для `IndexStatus.CurrentCommit`.

```csharp
private static string CurrentBaseCommit(
    string workingRoot,
    CodeIndex index,
    string workingCommit)
{
    if (string.IsNullOrWhiteSpace(index.GenerationId))
    {
        return SameRoot(index.Root, workingRoot)
            ? workingCommit
            : RepoLocator.GitCommit(index.Root);
    }

    var identity = RuntimeIndexLayout.Inspect(workingRoot);
    var manifest = new RepositoryManifestStore(identity.RepositoryRuntimeRoot).Read();
    if (manifest is null ||
        !string.Equals(manifest.RepositoryId, index.RepositoryId, StringComparison.Ordinal))
    {
        return index.GitCommit;
    }

    return RepoLocator.GitOutput(
        workingRoot,
        $"rev-parse --verify {manifest.DevRef}^{{commit}}")
        ?? index.GitCommit;
}
```

- [x] **Шаг 4: Запустить целевые тесты и тесты проекта и подтвердить GREEN**

Запустить:

```powershell
dotnet test tests/CodeSearch.Tests/CodeSearch.Tests.csproj --no-restore
```

Ожидается: все тесты CodeSearch проходят без новых предупреждений.

### Задача 2: Допускать перемещения брокера во время ACL-проверки

**Файлы:**
- Изменить: `tests/LocalAi.Broker.Tests/RuntimeAclTests.cs`
- Изменить: `src/LocalAi.Broker/RuntimeAcl.cs`

- [x] **Шаг 1: Написать падающий тест ACL-гонки**

Добавить тест, который из внедрённого ACL callback перемещает каталог задания в `archive` и выбрасывает `InvalidOperationException` той же формы, что Windows error 3.

```csharp
[Fact]
public void Ensure_ignores_a_job_directory_moved_during_acl_application()
{
    // Создаём jobs/job-1 с request-файлом.
    // Пока Ensure применяет ACL к jobs/job-1, перемещаем его в archive/job-1 и выбрасываем ошибку.
    // Ensure обязан завершиться, потому что исчезновение исходного пути подтверждено.
}
```

Также сохранить строгость тестом, доказывающим, что та же ошибка пробрасывается, пока целевой путь существует.

- [x] **Шаг 2: Запустить целевые тесты и подтвердить RED**

Запустить:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeAclTests"
```

Ожидается: тест перемещённого каталога падает с `InvalidOperationException`.

- [x] **Шаг 3: Реализовать применение/валидацию с учётом исчезновения**

Обернуть операцию над каждым путём и подавлять только `IOException` или `InvalidOperationException`, когда `File.GetAttributes` подтверждает исчезновение цели.

```csharp
private void ApplyAndValidate(
    string path,
    string currentUserSid,
    IReadOnlySet<string> expected)
{
    try
    {
        var isDirectory = Directory.Exists(path);
        _applyExactAcl(path, isDirectory, false, currentUserSid, AdministratorsSid);
        ValidateSnapshot(path, _readAclSnapshot(path), expected);
    }
    catch (Exception exception)
        when (exception is IOException or InvalidOperationException &&
              HasDisappeared(path))
    {
    }
}
```

Заменить `SearchOption.AllDirectories` на итератор по одному каталогу, который перехватывает только `DirectoryNotFoundException`, если каталог был перемещён до перечисления его детей.

- [x] **Шаг 4: Запустить целевые тесты и тесты проекта и подтвердить GREEN**

Запустить:

```powershell
dotnet test tests/LocalAi.Broker.Tests/LocalAi.Broker.Tests.csproj --no-restore
```

Ожидается: все тесты брокера проходят, включая строгий проброс ошибки для существующей цели.

### Задача 3: Проверить всё решение и живые сценарии

**Файлы:**
- Только проверка; дополнительных изменений исходного кода не ожидается.

- [x] **Шаг 1: Запустить полную автоматическую проверку**

Запустить:

```powershell
dotnet test LocalAi.slnx --no-restore
```

Ожидается: все проекты собираются и все тесты проходят без ошибок и новых предупреждений.

- [x] **Шаг 2: Опубликовать временную сборку и воспроизвести оба живых сценария**

Запустить новый `codesearch status` для `R:\IntelWash` и проверить `Base status: current`, пока exact overlay также остаётся current. Запустить sync/query-нагрузку и многократно подключить второй клиент; Windows error 3 возникать не должен.

- [x] **Шаг 3: Проверить полный diff**

Запустить:

```powershell
git diff --check
git diff --stat
git status --short
```

Ожидается: изменены только парный план, два production-файла и два соответствующих test-файла; сгенерированные бинарники, runtime-данные и постороннее форматирование не отслеживаются.
