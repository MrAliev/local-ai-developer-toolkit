# План реализации канонического CRLF и точного статуса overlay

[English version](2026-07-28-canonical-crlf-overlay-status.md)

> **Для agentic workers:** ОБЯЗАТЕЛЬНЫЙ SUB-SKILL: использовать superpowers:executing-plans для пошагового выполнения этого плана. Для отслеживания используются checkbox-шаги (`- [ ]`).

**Цель:** Канонизировать каждый индексируемый текстовый файл в Windows CRLF в памяти и корректно показывать необходимость exact overlay для индексов с generation.

**Архитектура:** Отдельный helper `CanonicalIndexText` отвечает за детерминированную нормализацию окончаний строк и чтение файлов. IndexBuilder и DirtyCorpusPolicy используют этот helper, а SearchService рассчитывает необходимость overlay по generation/tree/dirty identity и возвращает её через IndexStatus. Версия нормализации generation увеличивается, чтобы старый и новый векторные корпуса не смешивались.

**Стек:** .NET 10, C#, xUnit v3, Git, надёжный FIFO broker LocalAi, бинарный индекс CodeSearch.

---

## Карта файлов

- Создать `src/CodeSearch.Core/Indexing/CanonicalIndexText.cs`: каноническое CRLF-представление в памяти.
- Создать `tests/CodeSearch.Tests/IndexBuilderNormalizationTests.cs`: regression-тесты EOL для base/overlay.
- Создать `tests/CodeSearch.Tests/SearchServiceStatusTests.cs`: тесты exact generation overlay status.
- Изменить `src/CodeSearch.Core/Indexing/IndexBuilder.cs`: канонические hash и вход chunker.
- Изменить `src/CodeSearch.Core/Indexing/DirtyCorpusPolicy.cs`: канонические dirty-content hash.
- Изменить `src/CodeSearch.Core/Search/SearchService.cs`: явный `RequiresOverlay`.
- Изменить `src/CodeSearch.Cli/Program.cs`: показывать exact overlay до legacy-проверки пути.
- Изменить `src/LocalAi.Cli/CodeSearchSyncCommand.cs`: увеличить версию нормализации.
- Изменить `tests/CodeSearch.Tests/DirtyCorpusPolicyTests.cs`: покрыть канонический dirty hash.
- Изменить `tests/CodeSearch.Tests/GenerationStoreTests.cs`: покрыть identity поколения.
- Изменить `README.md` и `README.ru.md`: описать канонизацию CRLF.

### Задача 1: Канонические CRLF hash и chunking

**Файлы:**
- Создать: `src/CodeSearch.Core/Indexing/CanonicalIndexText.cs`
- Создать: `tests/CodeSearch.Tests/IndexBuilderNormalizationTests.cs`
- Изменить: `src/CodeSearch.Core/Indexing/IndexBuilder.cs`
- Изменить: `src/CodeSearch.Core/Indexing/DirtyCorpusPolicy.cs`
- Изменить: `tests/CodeSearch.Tests/DirtyCorpusPolicyTests.cs`

- [ ] **Шаг 1: Написать падающие тесты content hash**

Добавить тесты, доказывающие, что LF, CRLF, одиночный CR и mixed дают один hash, а реальное
изменение текста меняет его:

```csharp
[Fact]
public void Line_ending_styles_have_one_canonical_content_hash()
{
    var variants = new[] { "one\ntwo\n", "one\r\ntwo\r\n", "one\rtwo\r", "one\r\ntwo\n" };
    var hashes = variants.Select(ContentHash).Distinct().ToArray();
    Assert.Single(hashes);
}

[Fact]
public void Canonical_hash_still_detects_text_changes()
{
    Assert.NotEqual(ContentHash("one\ntwo\n"), ContentHash("one\nchanged\n"));
}
```

- [ ] **Шаг 2: Написать падающий тест overlay**

Построить базу из LF-текста с детерминированным fake embedder, заменить только EOL на CRLF и
построить overlay:

```csharp
var baseResult = await builder.BuildAsync(_baseRoot, _basePath, cancellationToken: token);
File.WriteAllText(sourcePath, "line one\r\nline two\r\n");
var overlayResult = await builder.BuildOverlayAsync(_baseRoot, _basePath, _overlayPath, token);
Assert.Equal(0, overlayResult.FileCount);
Assert.Equal(0, overlayResult.FilesEmbedded);
```

Затем заменить `line two` на `changed` и проверить, что встроен один файл overlay.

- [ ] **Шаг 3: Запустить RED-тесты**

Команда:

```powershell
dotnet test tests\CodeSearch.Tests\CodeSearch.Tests.csproj --configuration Release --filter "FullyQualifiedName~IndexBuilderNormalizationTests|FullyQualifiedName~DirtyCorpusPolicyTests"
```

Ожидается: FAIL, потому что варианты EOL сейчас дают разные SHA-256 hash и непустой overlay.

- [ ] **Шаг 4: Реализовать helper канонического текста**

Создать:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace CodeSearch.Core.Indexing;

internal static class CanonicalIndexText
{
    public static string Read(string path) => Normalize(File.ReadAllText(path));

    public static string Normalize(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    public static byte[] Hash(string content) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(content)));
}
```

Использовать `CanonicalIndexText.Read` в `IndexBuilder.HashFile` и `ChunkFiles`. В
`DirtyCorpusPolicy.ComputeContentHash` и `ComputeWorkingContentHash` добавлять в hash канонический
UTF-8-текст, сохранив существующие разделители путей и файлов и маркер `<deleted>`.

- [ ] **Шаг 5: Запустить GREEN-тесты**

Запустить команду из шага 3.

Ожидается: PASS; overlay только с EOL содержит ноль файлов, а реальное изменение — один.

### Задача 2: Exact overlay requirement и status

**Файлы:**
- Создать: `tests/CodeSearch.Tests/SearchServiceStatusTests.cs`
- Изменить: `src/CodeSearch.Core/Search/SearchService.cs`
- Изменить: `src/CodeSearch.Cli/Program.cs`

- [ ] **Шаг 1: Написать падающие status-тесты**

Создать временный Git-репозиторий и generation-backed базу на его текущем tree. Проверить:

```csharp
var clean = service.Status(_root);
Assert.False(clean.RequiresOverlay);

File.WriteAllText(Path.Combine(_root, "A.cs"), "class Changed {}\r\n");
var dirty = service.Status(_root);
Assert.True(dirty.RequiresOverlay);
Assert.False(dirty.WorkingRootIsBase);
```

Сохранить exact overlay по `RuntimeIndexLayout.OverlayPath` и проверить:

```csharp
var indexed = service.Status(_root);
Assert.True(indexed.RequiresOverlay);
Assert.True(indexed.Overlay.Exists);
```

- [ ] **Шаг 2: Запустить RED-тест**

Команда:

```powershell
dotnet test tests\CodeSearch.Tests\CodeSearch.Tests.csproj --configuration Release --filter FullyQualifiedName~SearchServiceStatusTests
```

Ожидается: FAIL, потому что IndexStatus не возвращает `RequiresOverlay`, а dirty worktree с тем же
путём классифицируется как base checkout.

- [ ] **Шаг 3: Реализовать явную необходимость overlay**

Добавить `bool RequiresOverlay` в `IndexStatus`. Для generation-backed индексов рассчитывать его
по exact identity:

```csharp
var identity = RuntimeIndexLayout.Inspect(workingRoot);
var requiresOverlay =
    !string.Equals(index.GitTree, identity.HeadTree, StringComparison.Ordinal) ||
    identity.DirtyHash is not null;
```

Для legacy-индексов сохранить `!SameRoot(index.Root, workingRoot)`. Свойство
`WorkingRootIsBase` должно дополнительно требовать `!RequiresOverlay`.

В CLI status использовать:

```csharp
if (!status.RequiresOverlay)
{
    Console.WriteLine("Overlay:      not needed - worktree matches the clean base");
    return 0;
}

if (!status.Overlay.Exists)
{
    // Существующий вывод NOT BUILT.
}

// Существующий вывод exact overlay.
```

- [ ] **Шаг 4: Запустить GREEN-тест**

Запустить команду из шага 2.

Ожидается: PASS для clean, dirty-missing и dirty-exact-overlay.

### Задача 3: Совместимость generation и документация

**Файлы:**
- Изменить: `src/LocalAi.Cli/CodeSearchSyncCommand.cs`
- Изменить: `tests/CodeSearch.Tests/GenerationStoreTests.cs`
- Изменить: `README.md`
- Изменить: `README.ru.md`

- [ ] **Шаг 1: Усилить покрытие identity поколения**

```csharp
[Fact]
public void Normalization_version_changes_generation_identity()
{
    var previous = Identity() with { NormalizationVersion = 3 };
    var canonicalCrlf = previous with { NormalizationVersion = 4 };
    Assert.NotEqual(previous.Id, canonicalCrlf.Id);
}
```

Это characterization-покрытие существующего контракта identity. Само увеличение версии является
конфигурационным изменением и проверяется новым live generation ID в задаче 4.

- [ ] **Шаг 2: Запустить тесты identity поколения**

Команда:

```powershell
dotnet test tests\CodeSearch.Tests\CodeSearch.Tests.csproj --configuration Release --filter FullyQualifiedName~GenerationStoreTests
```

Ожидается: PASS, подтверждающий участие normalization version в generation ID.

- [ ] **Шаг 3: Увеличить версию нормализации и обновить парную документацию**

Добавить `CodeSearchSyncCommand.CurrentNormalizationVersion = 4` и использовать её при создании
`GenerationIdentity`. Версия 3 признана недействительной во время проверки перед публикацией,
поскольку её generation переиспользовала векторы из предыдущего контракта нормализации. До
публикации версии 4 ограничить переиспользование корпуса совпадением версий индексирующего
контракта. Обновить оба README:

- канонический Windows CRLF в памяти;
- отсутствие перезаписи файлов репозитория;
- перестроение generation после изменения нормализации.

- [ ] **Шаг 4: Запустить сфокусированные тесты**

```powershell
dotnet test tests\CodeSearch.Tests\CodeSearch.Tests.csproj --configuration Release
dotnet test tests\LocalAi.IntegrationTests\LocalAi.IntegrationTests.csproj --configuration Release
```

Ожидается: все CodeSearch и LocalAi integration tests проходят без предупреждений.

### Задача 4: Полная проверка и переиндексация Jira

**Файлы:**
- Только runtime: `%LOCALAPPDATA%\LocalAi\repositories\<jira-repository-id>`
- Добавить в staging все одобренные исходники, тесты и документацию LocalAi.

- [ ] **Шаг 1: Запустить полный baseline LocalAi**

```powershell
dotnet restore LocalAi.slnx
dotnet build LocalAi.slnx --configuration Release --no-restore
dotnet test LocalAi.slnx --configuration Release --no-build
```

Ожидается: ноль предупреждений и ошибок сборки, все тесты проходят.

- [ ] **Шаг 2: Добавить изменения в staging и проверить**

```powershell
git add -A
git diff --cached --check
git status --short
```

Ожидается: исходники, тесты и документы EN/RU добавлены в staging; результаты сборки игнорируются.
Не выполнять commit или push без отдельного разрешения владельца.

- [ ] **Шаг 3: Перестроить Jira generation через общий broker**

Запустить проверенный Release CLI:

```powershell
src\LocalAi.Cli\bin\Release\net10.0\localai.exe sync --root C:\Users\Mr.Aliev\plugins\jira-intelwash
```

Ожидается:

- новый generation ID из-за normalization version 4;
- base ref `refs/heads/main`;
- база содержит 62 файла;
- exact dirty overlay содержит только семь реально изменённых Git-файлов;
- broker остаётся единственным FIFO worker.

- [ ] **Шаг 4: Проверить GPU и поиск**

Через broker выполнить `localai native Processes` и проверить `size == size_vram` и
`context_length == 16384`. Запустить:

```powershell
codesearch status --root C:\Users\Mr.Aliev\plugins\jira-intelwash
codesearch search --query "validate project and issue type fields before creating a Jira issue" --root C:\Users\Mr.Aliev\plugins\jira-intelwash --top 5
```

Ожидается: status показывает точный dirty overlay из семи файлов, семантический запрос возвращает
релевантные участки реализации и тестов Jira.

- [ ] **Шаг 5: Сохранить неопубликованное состояние**

Подтвердить, что глобальный `bin`, hooks, remote-ветки и исходники Jira не изменены проверкой.
Оставить все изменения LocalAi в staging и сообщить владельцу оставшееся решение по публикации.
