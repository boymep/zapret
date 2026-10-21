# zapret

Форк [flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) для Windows: обход DPI-блокировок (Discord, YouTube и др.) с удобным треем-приложением **ZapretGUI**.

Под капотом - [bol-van/zapret](https://github.com/bol-van/zapret) (`winws`) и [WinDivert](https://github.com/basil00/WinDivert). Основная работа сделана авторами этих проектов; здесь - упаковка и надстройка над ними.

## Что это

Upstream-проект [flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) упаковывает `winws` (из bol-van/zapret) и WinDivert для Windows вместе с набором готовых стратегий (`general*.bat`), списками доменов/IP и `service.bat` для установки в автозапуск. Этот форк сохраняет все эти файлы и работает так же.

## Что добавляет этот форк

- **ZapretGUI** - приложение в системном трее: включение и выключение в один клик, выбор стратегии, переключатель игрового фильтра, автозапуск.
- Telegram через AmneziaWG с регистрацией Cloudflare WARP для каждого пользователя отдельно.
- Встроенное обновление из релизов этого репозитория с проверкой SHA256.
- Расширенные настройки скрыты по умолчанию.
- Релизы выпускаются вручную после просмотра изменений upstream.

Подробности о GUI - в [README_GUI.md](./README_GUI.md).

## Безопасность

Разговоры о "вирусах" вокруг zapret возникают регулярно, поэтому - по фактам.

- **Ядро.** Файлы `winws.exe`, `cygwin1.dll`, `WinDivert.dll`, `WinDivert64.sys` проверены: они побайтово совпадают с официальным релизом bol-van/zapret v72.9 (`sha256sum.txt`). Драйвер `WinDivert64.sys` имеет действительную подпись Authenticode (EV) - в таком виде его поставляет и upstream.
- **Скрипты.** `.bat`/`.ps1` просмотрены: они не скачивают и не запускают код из сети, файл `hosts` автоматически не изменяется (только по явному действию пользователя в `service.bat`).
- **Антивирусы.** WinDivert - драйвер перехвата пакетов; антивирусы часто помечают его как HackTool/RiskWare. Это обычная причина слухов о "вирусе": инструмент универсальный и может использоваться кем угодно. При необходимости добавьте папку в исключения.
- **Скачивайте только из [Releases](https://github.com/boymep/zapret/releases)** и сверяйте хэш:

  ```powershell
  Get-FileHash .\zapret-<версия>.zip -Algorithm SHA256
  ```

  Результат должен совпасть с содержимым `zapret-<версия>.zip.sha256` и с хэшем в описании релиза.
- **Права.** Приложение работает от имени администратора. Держите папку там, где обычные (не администраторские) программы не могут её изменить, например не в `Downloads` и не в общей записываемой папке.
- `utils/test zapret.ps1` при тестировании загружает список проверяемых хостов со стороннего сайта `hyperion-cs.github.io`. Это только данные, исполняемый код не загружается.

## Установка

1. Скачайте `zapret-<версия>.zip` со [страницы релизов](https://github.com/boymep/zapret/releases/latest) и проверьте SHA256 (см. выше).
2. Распакуйте архив в папку с путём без кириллицы и пробелов (рекомендуется), например `C:\zapret`.
3. Запустите `ZapretGUI.exe` от имени администратора.

## Использование

- В ZapretGUI выберите стратегию и включите обход из меню в трее. Если одна стратегия не работает, попробуйте другую.
- Старый способ тоже работает: `general*.bat` - ручной запуск выбранной стратегии (удобно для проверки), `service.bat` - установка в автозапуск, статус, диагностика, тесты.
- Меню `service.bat`: Install/Remove Services, Check Status, **Game Filter** (обход для UDP/TCP на портах выше 1023; после переключения перезапустите стратегию), **IPSet Filter** (`none` / `loaded` / `any`), Auto-Update Check, Update IPSet List, Update Hosts File, Run Diagnostics, Run Tests.
- Свои адреса добавляйте в файлы `lists`: `list-general-user.txt` (домены), `list-exclude-user.txt` (исключения доменов), `ipset-exclude-user.txt` (исключения IP). Файлы `*-user.txt` создаются при первом запуске.
- Рекомендуется включить безопасный DNS (Secure DNS) в браузере или ОС.

## Обновления

ZapretGUI проверяет новые версии в [Releases](https://github.com/boymep/zapret/releases) этого репозитория и перед установкой сверяет SHA256 архива. Версии имеют вид `<версия upstream>.<номер сборки форка>`, например `1.10.3.1`. `service.bat` также умеет проверять обновления.

## Для мейнтейнеров: как выпустить версию

1. Влейте изменения upstream (`flowseal/zapret-discord-youtube`) и просмотрите их.
2. Обновите версию в `.service/version.txt` и в `set "LOCAL_VERSION=..."` в `service.bat` (значения должны совпадать).
3. По желанию добавьте текст в `.github/RELEASE_NOTES.md` - он попадёт в описание релиза.
4. Запустите workflow **Release** (Actions - Release - Run workflow). Он проверит версию, соберёт `ZapretGUI.exe`, упакует `zapret-<версия>.zip` и `.sha256` и создаст релиз `v<версия>`. Файл `gui/warp.conf` в репозитории быть не должен.

## Частые проблемы

- **Ничего не происходит / нет эффекта:** запустите от имени администратора; запустите `service.bat` - Run Diagnostics; убедитесь, что не запущено несколько обходов сразу.
- **Ни одна стратегия не подходит:** попробуйте остальные `general*.bat`, включите Secure DNS, проверьте `IPSet Filter` (`none`) и `Game Filter`.
- **Не работает игра или приложение:** отключите `Game Filter` и поставьте `IPSet Filter` в `none`; для игр обновите ipset и включите `Game Filter`.
- **Антивирус удаляет файлы WinDivert:** добавьте папку в исключения (см. раздел "Безопасность").
- **Античит ругается на WinDivert:** отключите обход на время игры.
- **Другое:** создайте [issue](https://github.com/boymep/zapret/issues).

## Лицензия и благодарности

Проект распространяется на условиях лицензии MIT, см. [LICENSE.txt](./LICENSE.txt) (Copyright (c) 2024-2026 Flowseal).

- [bol-van/zapret](https://github.com/bol-van/zapret) - ядро `winws` (MIT).
- [basil00/WinDivert](https://github.com/basil00/WinDivert) - драйвер перехвата пакетов (LGPLv3 / GPLv2).
- [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) - исходный проект, стратегии и списки (MIT).
