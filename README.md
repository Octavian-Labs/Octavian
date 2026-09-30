# OctavianRSM

WPF-менеджер Rust Dedicated Server: автономная установка, Carbon, оперативное управление.

## Возможности

- **Portable-режим**: один EXE в пустой папке → мастер первого запуска ставит SteamCMD, Rust (app `258550`) и Carbon в фоне, пока вы отвечаете на вопросы
- **Remote-режим**: управление удалённым сервером только по RCON, без локальной установки
- **Профили сервера**: vanilla / 2x / 5x / 10x / PvE / hardcore / custom — генерация `server.cfg` + конфигов GatherManager/StackSizeController
- **Управление**: инструменты выбранного игрока (через Carbon-плагин OctaAdminTools), очередь и maintenance-режим, ивенты/спавн, режимы нагрузки и мира со снимком и откатом, чат/объявления
- **Дашборд**: состояние, игроки/нагрузка, ресурсы хоста, журнал с прилипанием к последней строке
- **Самообновление** через GitHub Releases (приватный репо — нужен `GH_TOKEN`/`GITHUB_TOKEN` в окружении)

## Сборка

```powershell
dotnet build -c Release
```

Portable single-file EXE:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

## Плагины

`plugins/OctaAdminTools.cs` — единственный Carbon-плагин из коробки: нужен менеджеру для инструментов игрока. В дев-окружении (рядом `Server\carbon\plugins`) встраивается живой исходник; в чистом клоне — копия из `plugins/`. Остальные плагины ставятся вручную со вкладки «Плагины».

## Стек

.NET 8 (net8.0-windows), WPF, WPF-UI 4.3, AvalonEdit, Microsoft.Data.Sqlite, WebSocket RCON.
