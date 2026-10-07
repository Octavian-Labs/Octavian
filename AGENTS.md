# Octavian RSM — заметки для агента

## Сборка

- Обычная сборка: `dotnet build -c Release` → `bin\Release\net8.0-windows\`
- **После каждого изменения собирать portable-EXE сразу:**
  ```
  dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o bin\publish-portable
  ```
  Результат: `bin\publish-portable\OctavianRSM.exe` (~76 МБ self-contained single-file).
  Всегда один файл — пользователь сам копирует его в тестовую папку, версионных копий не держать.
- Если запущенный менеджер держит `bin\Release` — собирать проверку в `bin\checkN` и сообщать пользователю.
- Тест-хук окна конфига плагина: `OctavianRSM.exe --test-config <ИмяПлагина>`.

## Git

- Репозиторий самодостаточен: плагин OctaAdminTools встраивается из `..\..\Server\carbon\plugins` в дев-окружении, иначе из `plugins/` внутри проекта.
- Пушим в `main` напрямую. Релизы — через `gh release create` + загрузка `bin\publish-portable\OctavianRSM-v<версия>-win-x64.exe` (копия publish-EXE с версией в имени).

## Правила проекта

- Из коробки встраивается ТОЛЬКО OctaAdminTools — других плагинов в ресурсах быть не должно.
- Конфиги плагинов — редактор по ⚙ на вкладке «Плагины» (таблица + JSON, поле OctaDes).
- Сырые конфиги сервера/ядра Carbon — вкладка «РЕДАКТОР ФАЙЛОВ» в «Серв. конфиг».
- Все окна — WindowStyle=None + OctaTitleBar (кастомная шапка).
- Ошибки редактора конфигов пишутся в `data\config-errors.log`.

## Плагины

- Перед работой с плагинами читать `PITFALLS.md` — там грабли Carbon/Chaos
  (Config==null при горячей загрузке, isRedirectOf у скинов, builtInSize
  вместо capacity, StringView в новых API) и фирменный стиль чата.
- Внутренности Gen2 AI (FSM, сенсоры, бегство): `Docs/Gen2-AI.md` +
  полный декомпиль неймспейса в `_decomp_out/` (обновить: `_decomp.ps1`).
  Оффлайн-проверка компиляции плагина против серверных сборок:
  `_compile_plugin.ps1` (ложные ошибки Provoke/TerrainPath — норма,
  Carbon паблицит сборки на сервере).
- Стиль админ-панелей CUI — эталон `Docs/AdminUI-Style.md` (золото/стекло,
  черновик, тосты, баланс колонок).
- Админ-панели плагинов — по эталону `Docs/AdminUI-Style.md` (золото/стекло,
  черновики, тосты, подтверждения, гранулярные права).
