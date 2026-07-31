# Оценка retrieval в CodeSearch

[English version](codesearch-evaluation.md)

## Область и происхождение данных

В этом отчёте исторический режим поиска без порога сравнивается с откалиброванным
профилем `qwen3-embedding:8b-q8_0`. Измеренные факты отделены от эвристик токенов и
недоступной телеметрии.

- Дата измерения: 2026-07-31.
- Корпус: schema 1, 24 случая: 8 natural-language, 6 exact-symbol, 4 generic-text и
  6 unrelated/no-answer.
- Идентификатор корпуса:
  `schema1:sha256:d675331cb7008a67a7335c5a1f2aba85e382974b71b1473e34b9e4685f0d7a52`.
- Коммит исходников: `966aae8eda5653897190b4b69f7b5074deef9652`.
- Дерево исходников: `8f1d9458a60bcd4ba04aae1c29b6c500bba0c7e5`.
- Generation индекса:
  `399fcc0b53b35ede05dc64f1a84cbc3bfc6bf382bdd2de7d71f2f9dc1ae8debc`,
  содержащая 203 файла и 1 529 чанков.
- Модель эмбеддингов: `qwen3-embedding:8b-q8_0`; откалиброванный векторный порог:
  `0.43`.
- Каждый эмбеддинг прошёл по цепочке
  `SearchService` -> `BrokerEmbeddingClient` -> общий FIFO-брокер LocalAi.
  Прямых запросов к Ollama не было.

Перед каждым запуском валидатор fixture проверял все релевантные пути и символы по
дереву исходников. Первый запуск профиля классифицирован как холодный: непосредственно
перед ним инспекция процессов не обнаружила резидентного runner модели. Второй запуск
профиля выполнен сразу после первого и классифицирован как тёплый. Оба
реконструированных запуска без порога были тёплыми.

## Команды и сырые артефакты

Поддерживаемая команда evaluator:

```powershell
dotnet run --project src/CodeSearch.Cli/CodeSearch.Cli.csproj -c Release -- evaluate --cases tests/CodeSearch.Tests/Fixtures/SearchEvaluation/cases.json --root C:\Users\Mr.Aliev\tools\LocalAi --profile
dotnet run --project src/CodeSearch.Cli/CodeSearch.Cli.csproj -c Release -- evaluate --cases tests/CodeSearch.Tests/Fixtures/SearchEvaluation/cases.json --root C:\Users\Mr.Aliev\tools\LocalAi --no-floor
```

Во время измерения в этой ветке работал брокер из неизменяемой установленной версии
`966aae8`. В issue #6 отслеживается привязка к пути сборки, из-за которой клиент из
worktree не принимает уже запущенный брокер. Установленная версия не заменялась и не
перезапускалась. Игнорируемый временный adapter проверял канонический процесс брокера,
после чего использовал обычный путь `SearchService` -> `BrokerEmbeddingClient` -> общая
устойчивая очередь. Точные команды сбора:

```powershell
dotnet run --project artifacts\eval-harness\EvalHarness.csproj -c Release --no-build -- profile cold tests\CodeSearch.Tests\Fixtures\SearchEvaluation\cases.json C:\Users\Mr.Aliev\tools\LocalAi C:\Users\Mr.Aliev\tools\LocalAi\bin\versions\966aae8\LocalAi.Broker.dll C:\Users\Mr.Aliev\AppData\Local\Temp\codesearch-eval-profile-cold-20260731.json
dotnet run --project artifacts\eval-harness\EvalHarness.csproj -c Release --no-build -- profile warm tests\CodeSearch.Tests\Fixtures\SearchEvaluation\cases.json C:\Users\Mr.Aliev\tools\LocalAi C:\Users\Mr.Aliev\tools\LocalAi\bin\versions\966aae8\LocalAi.Broker.dll C:\Users\Mr.Aliev\AppData\Local\Temp\codesearch-eval-profile-warm-20260731.json
dotnet run --project artifacts\eval-harness\EvalHarness.csproj -c Release --no-build -- no-floor warm tests\CodeSearch.Tests\Fixtures\SearchEvaluation\cases.json C:\Users\Mr.Aliev\tools\LocalAi C:\Users\Mr.Aliev\tools\LocalAi\bin\versions\966aae8\LocalAi.Broker.dll C:\Users\Mr.Aliev\AppData\Local\Temp\codesearch-eval-no-floor-run1-20260731.json
dotnet run --project artifacts\eval-harness\EvalHarness.csproj -c Release --no-build -- no-floor warm tests\CodeSearch.Tests\Fixtures\SearchEvaluation\cases.json C:\Users\Mr.Aliev\tools\LocalAi C:\Users\Mr.Aliev\tools\LocalAi\bin\versions\966aae8\LocalAi.Broker.dll C:\Users\Mr.Aliev\AppData\Local\Temp\codesearch-eval-no-floor-run2-20260731.json
```

Сырые JSON-файлы сохранены по этим временным путям для локального ревью и не
закоммичены. Adapter также игнорируется и не является продуктовым путём выполнения.

## Измеренные факты

Метрики качества и объёма результатов совпали между двумя запусками каждого режима.
Поэтому для детерминированных метрик в таблице можно использовать любой запуск, а
время приведено отдельно.

| Метрика | Без порога, запуски 1/2 | Профиль, холодный/тёплый | Изменение |
|---|---:|---:|---:|
| Precision@5 | 0.133333 | 0.133333 | 0 |
| Recall@10 | 0.777778 | 0.777778 | 0 |
| Средний ранг первого релевантного результата | 4.388889 | 4.388889 | 0 |
| False positive для no-answer | 6/6 (доля 1.0) | 6/6 (доля 1.0) | 0 |
| Символы ответа | 151 071 | 147 651 | -3 420 (-2.26%) |
| Строки исходников | 10 966 | 10 568 | -398 (-3.63%) |
| Чтения чанков | 240 | 238 | -2 (-0.83%) |
| Сумма чтений различных файлов по случаям | 156 | 152 | -4 (-2.56%) |

| Запуск | Время evaluator |
|---|---:|
| Профиль, холодный | 72 246.5 мс |
| Профиль, тёплый | 71 308.5 мс |
| Без порога, тёплый запуск 1 | 67 625.1 мс |
| Без порога, тёплый запуск 2 | 68 556.8 мс |

Холодный запуск профиля был на 938.0 мс медленнее его тёплого повтора. Тёплый запуск
профиля был на 3 217.6 мс (4.73%) медленнее медианы двух тёплых запусков без порога.
При одной паре cold/warm и обычном шуме планировщика общего брокера это наблюдение
времени, а не доказательство того, что порог вызывает регрессию задержки.

Не изменились четыре пропуска ответов:
`intent-runtime-acl`, `text-vector-route`, `text-shared-fifo` и
`text-russian-install`. Всего порог удалил два возвращённых чанка. В случае
`none-email-reset` число результатов снизилось с 10 до 8, но случай остался false
positive, потому что кандидаты с положительным lexical score намеренно допускаются
даже при векторном score ниже порога.

## Эвристические оценки токенов

Авторитетны приведённые выше исходные количества символов. Точечная оценка токенов
равна `ceil(response characters / 4)`. Интервал
`ceil(characters / 6)..ceil(characters / 3)` — инженерная эвристика, а не
статистический доверительный интервал и не результат tokenizer.

| Метрика | Без порога | Профиль | Изменение |
|---|---:|---:|---:|
| Точечная оценка токенов | 37 768 | 36 913 | -855 (-2.26%) |
| Эвристический интервал | 25 179..50 357 | 24 609..49 217 | -570 снизу, -1 140 сверху |

## Недоступная телеметрия

Время ожидания очереди брокера недоступно. `BrokerEmbeddingClient` использует значение
эмбеддинга, но не предоставляет receipt брокера сервису `SearchService`, поэтому
evaluator записывает `null` в `brokerQueueWaitMilliseconds` и явную диагностику
недоступности. Общее время включает очередь, выполнение модели и локальную работу
поиска; разделить эти составляющие по данным измерения нельзя.

## Ограничения

- Корпус — небольшая инженерная fixture для одного репозитория, а не benchmark общего
  поиска по коду.
- Порог релевантности фильтрует только векторную ветвь до RRF. Кандидаты с
  положительным lexical score по проекту остаются допустимыми, поэтому измеренная доля
  false positive для no-answer не улучшилась, хотя кандидаты с низким vector score
  были удалены.
- `responseCharacters` измеряет сформированные evaluator путь, метаданные и snippet.
  Метрика не включает nonce-обёртки MCP и непрозрачные chunk ID, поэтому пригодна для
  этого A/B, но не является полным числом байтов транспорта MCP.
- Сохранён только один холодный и один следующий за ним тёплый запуск профиля. Оба
  реконструированных запуска без порога были тёплыми.
- В более ранней ручной заметке оператора, для которой недоступны сырые cold/warm JSON,
  записаны те же метрики качества и пропуски, но 170 457 символов ответа, точечная
  оценка 42 615 токенов и 123 489 мс. Эти полученные из памяти значения относятся к
  другому способу сбора вывода и не смешиваются с сопоставимой таблицей выше.
- Временные пути сырых JSON локальны для машины и могут быть удалены обычной очисткой
  временных файлов.
