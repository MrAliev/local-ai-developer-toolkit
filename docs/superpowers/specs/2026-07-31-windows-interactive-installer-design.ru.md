# Дизайн интерактивного Windows-установщика

[English version](2026-07-31-windows-interactive-installer-design.md)

**Дата:** 2026-07-31

**Статус:** согласован для отдельного последующего issue и PR

**Целевая система:** Windows 10/11 x64

## Цель

Создать самодостаточный графический установщик, подготавливающий Windows-компьютер
к полноценной работе LocalAi для текущего пользователя. Установщик обнаруживает
зависимости, предлагает их согласованную установку, рекомендует модели по
доступной VRAM, устанавливает и активирует LocalAi и при согласии настраивает
Codex и Claude. Он сохраняет гарантии общесистемного singleton, устойчивого FIFO,
ACL, безопасной активации, full-VRAM и zero-offload.

Работа следует после исправления protocol/build compatibility в issue #6 и
поставляется отдельными issue, feature branch и PR.

## Форма продукта

Release artifact — самодостаточный WPF bootstrapper `win-x64`. Предварительно
установленный .NET runtime не требуется.

Решение разделяется на:

- `LocalAi.Installer.Core` — неизменяемые планы, detection, проверка пакетов,
  запуск зависимостей, рекомендации по hardware/моделям, configuration adapters,
  журнал транзакции, rollback и диагностика;
- `LocalAi.Installer` — WPF-страницы и view models, отображающие планы и
  собирающие явные решения пользователя;
- существующие launcher, broker client, contracts и LocalLm API, используемые
  без обходных путей.

UI не содержит installation policy. Он показывает результаты и вызывает core
через интерфейсы, тестируемые без Windows dialogs, сети, package installation и
GPU.

## Размещение

Установка выполняется для текущего пользователя:

- stable launcher и versioned binaries:
  `%LOCALAPPDATA%\LocalAi\bin`;
- runtime state broker:
  `%LOCALAPPDATA%\LocalAi`;
- journal установщика и безопасная диагностика:
  `%LOCALAPPDATA%\LocalAi\installer`.

Сохраняется существующая схема version directories/current pointer. Совместимая
установка обновляется на месте. Неизвестная структура показывается, но не
изменяется.

## Поток мастера

### 1. Диагностика

Обнаруживаются и показываются:

- версия Windows и x64 architecture;
- свободное место и доступность сети;
- версии `winget`, Git и Ollama;
- дискретные adapters и dedicated VRAM;
- существующие LocalAi installation/runtime state;
- пользовательские установки Codex и Claude.

Git и Ollama являются зависимостями LocalAi. `winget` — необязательный механизм
получения. Codex и Claude — необязательные integration targets.

### 2. Зависимости

Каждая отсутствующая или неподдерживаемая зависимость показывается с точным
source, package ID, требованием к версии, известным размером и необходимостью
elevation. У каждой зависимости отдельный consent checkbox.

После согласия вызывается неинтерактивный `winget` для выбранного точного package
ID. При отсутствии `winget` предлагается официальный installer поставщика, после
завершения detection повторяется. Elevation запрашивается только для требующего
её действия. Отмена фиксирует уже завершённые внешние операции, но не запускает
опасное автоматическое удаление.

### 3. Пакет LocalAi

Выбранные GitHub Release manifest и пакет скачиваются по TLS. Проверяются:

- подпись manifest встроенным release public key;
- SHA-256 пакета из проверенного manifest;
- Authenticode signature, когда её предоставляет release pipeline;
- структура пакета и compatibility metadata.

Пакет распаковывается в новую version directory, проверяется и активируется
существующей launcher transaction. Предыдущий pointer и версия сохраняются для
rollback.

### 4. Модели

Dedicated VRAM определяется без учёта shared system memory. Для нескольких
adapters по умолчанию выбирается подходящий discrete adapter с максимальной
dedicated VRAM, но пользователь может выбрать другой.

Подписанный manifest содержит model metadata, context tiers, download sizes и
консервативные оценки памяти. Установщик резервирует overhead runtime/context и
предлагает:

- Минимальный;
- Рекомендуемый;
- Расширенный;
- ручной выбор.

Очевидно не помещающиеся модели блокируются с объяснением. Оценки не объявляются
доказательством. Downloads и runtime checks отправляются только через FIFO broker
LocalAi. После загрузки каждая модель проходит broker preflight и принимается
только при `size_vram == size`; иначе broker выгружает её, а мастер предлагает
меньший context tier или модель.

Установщик никогда не вызывает Ollama HTTP endpoints или `ollama pull` напрямую.

### 5. Интеграция агентов

Обнаруживаются поддерживаемые user-scope конфигурации Codex и Claude. Для каждого
клиента независимо предлагаются:

- MCP-регистрация CodeSearch и LocalLm через stable launcher;
- управляемый блок глобальных инструкций, требующий LocalAi FIFO delegation и
  запрещающий прямой доступ к Ollama;
- отсутствие изменений.

Поддерживаемые конфигурации разбираются структурно. Глобальные правила добавляются
только в уникально маркированный managed block. До подтверждения показываются
точный diff и destination path. Создаётся timestamped byte-for-byte backup,
запись выполняется атомарно и перечитывается. Неизвестный, повреждённый или
конкурентно изменённый формат блокирует запись.

Credential values не читаются, не показываются, не копируются и не логируются.

### 6. Проверка, применение и завершение

Показывается один неизменяемый execution plan, сгруппированный по:

- внешним зависимостям;
- активации пакета LocalAi;
- загрузке и preflight моделей;
- изменениям каждого агента.

Пользователь подтверждает итоговый план. После каждого идемпотентного шага
обновляется journal. Повторный запуск продолжает либо безопасно повторяет
незавершённую работу. Финальная страница показывает версии, результаты
резидентности моделей, настроенные клиенты, требуемые перезапуски, rollback status
и путь к очищенному диагностическому отчёту.

## Транзакция и rollback

Установщик отвечает за rollback:

- staged files LocalAi;
- активированного version pointer;
- созданных установщиком изменений конфигурации;
- новых managed instruction blocks.

Он не удаляет автоматически dependency, установленную через `winget`, и не
удаляет существующие или новые модели. Такие нетранзакционные последствия
показываются до подтверждения.

Rollback восстанавливает byte-for-byte backups и предыдущий pointer, затем
проверяет оба результата. Ошибка rollback является отдельным результатом с
manual recovery instructions.

## Безопасность и инварианты

- По умолчанию per-user installation; elevation ограничена выбранной dependency.
- Downloads требуют подписанного manifest и проверенного digest.
- Runtime ACL остаётся ответственностью broker.
- Все discovery, download, inference и residency checks моделей проходят через
  единственный FIFO broker LocalAi.
- Установщик не может ослабить full-VRAM или zero-offload.
- Существующие модели, profiles, agent settings и несвязанные процессы
  сохраняются.
- Диагностика очищена и не содержит prompts, jobs, tokens или credentials.

## TDD и проверка

Production behavior реализуется только после падающего теста.

Core unit tests используют fake dependency detectors, command runners, download
и signature verifiers, hardware snapshots, broker clients, filesystems, clocks
и agent adapters. Обязательные сценарии:

- отсутствующие, актуальные, неподдерживаемые и конкурентно установленные
  зависимости;
- success, отказ, cancellation, elevation и failure `winget`;
- ошибки manifest, signature, digest, package layout и compatibility;
- atomic activation и rollback;
- multi-GPU и пограничные VRAM recommendations;
- успешный preflight и отклонение при `size_vram != size`;
- доказательство прохождения каждой model operation через broker abstraction;
- поддерживаемые, неизвестные, повреждённые и конкурентно изменённые agent configs;
- exact preview, backup, atomic write, read-back, rollback, resume и rerun.

Integration tests работают во временных roots с fake `winget`, release endpoint,
broker и client homes. WPF view-model tests проверяют navigation и consent gates
без UI automation.

Release gates:

- чистая Windows VM без LocalAi prerequisites;
- Windows с существующей совместимой установкой;
- проверка на реальном GPU для нескольких классов VRAM;
- детерминированные CI snapshots тех же решений без GPU;
- синхронность English/Russian документации;
- полный `dotnet test LocalAi.slnx -c Release --nologo`.

Установщик не публикуется до merge compatibility contract из issue #6 и успешной
проверки installed-vs-development scenario.
