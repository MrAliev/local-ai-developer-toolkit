## What this changes / Что меняется

<!-- One subject per pull request. / Одна тема на pull request. -->

## Why / Зачем

<!-- The failure or the need behind it. / Поломка или потребность, из-за которой это появилось. -->

## Checklist / Проверка

- [ ] Branched from `main`, one subject / ветка от `main`, одна тема
- [ ] Tests accompany the behaviour change, and none assert how fast the machine is /
      тесты идут вместе с поведением и не проверяют скорость машины
- [ ] `dotnet test LocalAi.slnx --configuration Release` passes locally / проходит локально
- [ ] Documentation updated for what this changes — or a `Docs: none — <why>` line above /
      документация обновлена под это изменение — либо выше есть строка `Docs: none — <почему>`
- [ ] Documentation exists in both languages and each file links to its pair /
      документация есть на обоих языках, и каждый файл ссылается на свою пару
- [ ] Documentation stays UTF-8 without BOM, CRLF / UTF-8 без BOM, CRLF
