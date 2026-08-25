# План публикации в личном GitHub

[English version](2026-07-29-personal-github-publication.md)

> **Для агентных исполнителей:** ОБЯЗАТЕЛЬНЫЙ ДОПОЛНИТЕЛЬНЫЙ НАВЫК: используйте superpowers:subagent-driven-development (рекомендуется) или superpowers:executing-plans для пошагового выполнения этого плана. Для отслеживания шагов используются флажки (`- [ ]`).

**Цель:** Опубликовать полное решение LocalAi как приватный личный репозиторий `MrAliev/local-ai-developer-toolkit` с синхронизированными README на английском и русском языках.

**Архитектура:** Сохранить все имена в исходном коде и runtime-контракты. Изменить только представление продукта во вводной документации на Local AI Developer Toolkit, проверить существующее решение, затем создать и отправить приватный GitHub-репозиторий из чистой локальной ветки `main`.

**Технологии:** Markdown, Git, GitHub CLI, PowerShell, .NET 10

---

### Задача 1: Переписать парные вводные документы репозитория

**Файлы:**
- Изменить: `README.md`
- Изменить: `README.ru.md`

- [ ] **Шаг 1: Переписать английский README**

Использовать `Local AI Developer Toolkit` как заголовок документа и добавить ссылку
на язык `README.ru.md`. Сохранить проверенные команды и гарантии runtime, расположив
содержимое в следующем точном порядке разделов:

```text
Overview
Core capabilities
How the components fit together
Prerequisites
Quick start
Build and test
Publishing executables
Runtime and security
Projects
Development rules
```

Раздел возможностей должен явно охватывать:

```text
Надёжный общий FIFO-брокер уровня машины и единственный транспорт Ollama
Гибридный семантический и лексический поиск CodeSearch по репозиториям
Базовые индексы на основе поколений с точными overlays рабочих деревьев
Stdio MCP-серверы CodeSearch и LocalLm
Явно включаемые Git hooks и синхронизацию репозитория
```

- [ ] **Шаг 2: Переписать русский README как полный перевод**

Использовать `Local AI Developer Toolkit` как заголовок документа и добавить ссылку
на `README.md`. Повторить каждый английский раздел и содержательный пункт в том же
порядке. Сохранить команды, идентификаторы, пути проектов, имена компонентов и строки
ошибок в исходном виде.

- [ ] **Шаг 3: Проверить парную структуру и кодировку**

Выполнить:

```powershell
rg -n "^## " README.md README.ru.md
```

Ожидается: оба файла содержат десять соответствующих разделов второго уровня в
одинаковом порядке.

Нормализовать оба файла в Windows CRLF и UTF-8 без BOM, затем проверить, что ни один
файл не содержит одиночных LF или UTF-8 BOM.

- [ ] **Шаг 4: Проверить diff документации**

Выполнить:

```powershell
git diff --check
git diff -- README.md README.ru.md
```

Ожидается: нет ошибок пробелов, изменений вне парных README и утверждений, которые
обходят брокер или изменяют имена продукта на уровне исходного кода.

- [ ] **Шаг 5: Закоммитить синхронизированное обновление README**

Выполнить:

```powershell
git add -- README.md README.ru.md
git commit -m "docs: introduce Local AI Developer Toolkit"
```

Ожидается: один коммит, содержащий ровно два парных вводных документа.

### Задача 2: Проверить кандидата на публикацию

**Файлы:**
- Проверить: `LocalAi.slnx`
- Проверить: всё отслеживаемое состояние репозитория

- [ ] **Шаг 1: Запустить полный набор тестов решения**

Выполнить:

```powershell
dotnet test LocalAi.slnx --no-restore
```

Ожидается: все тестовые проекты проходят без упавших тестов.

- [ ] **Шаг 2: Проверить ветку и состояние рабочей копии**

Выполнить:

```powershell
git status -sb
git branch --show-current
git log -2 --oneline
```

Ожидается: ветка `main`, нет staged, unstaged и untracked файлов, а коммит README
находится на вершине над одобренной историей дизайна публикации.

### Задача 3: Создать и опубликовать приватный личный репозиторий

**Внешнее состояние:**
- Создать: `https://github.com/MrAliev/local-ai-developer-toolkit`
- Настроить: локальный remote `origin`
- Отправить: локальную `main` в `origin/main`

- [ ] **Шаг 1: Повторно подтвердить активную учётную запись GitHub**

Выполнить:

```powershell
gh auth status
gh api user --jq '.login'
```

Ожидается: активная учётная запись и возвращённый login равны `MrAliev`.

- [ ] **Шаг 2: Подтвердить, что целевой репозиторий всё ещё отсутствует**

Выполнить:

```powershell
gh repo view MrAliev/local-ai-developer-toolkit --json nameWithOwner
```

Ожидается: GitHub сообщает, что репозиторий не найден. Если он уже существует,
проверить владельца, видимость и содержимое до любой записи.

- [ ] **Шаг 3: Создать приватный репозиторий и отправить `main`**

Выполнить:

```powershell
gh repo create MrAliev/local-ai-developer-toolkit --private --source . --remote origin --push
```

Ожидается: GitHub создаёт приватный репозиторий, добавляет `origin`, отправляет `main`
и настраивает отслеживание upstream.

- [ ] **Шаг 4: Проверить публикацию**

Выполнить:

```powershell
gh repo view MrAliev/local-ai-developer-toolkit --json nameWithOwner,visibility,defaultBranchRef,url
git remote -v
git status -sb
git rev-parse HEAD
git rev-parse origin/main
```

Ожидается:

```text
nameWithOwner: MrAliev/local-ai-developer-toolkit
visibility: PRIVATE
default branch: main
local HEAD равен origin/main
рабочая копия: чистая
```

Не создавать pull request: это первоначальная публикация одобренной локальной истории
`main` в новый пустой приватный репозиторий.
