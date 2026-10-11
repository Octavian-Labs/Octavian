# Реестр функций OctavianRSM (инвентаризация перед рефакторингом UX/UI)

> Этап 1 из ТЗ на редизайн. Документ — единственный источник правды о том,
> какая функция где живёт сейчас и куда переезжает. Обновлять при миграции.
> Обозначения колонок:
> - **Сущность**: A=приложение, S=сервер, P=игрок, PL=плагин, F=файл
> - **Исполнение**: RCON=команда по websocket, PROC=управление процессом, FS=файловая операция, LOCAL=локальная логика/UI, HTTP=сеть/браузер, DB=локальная SQLite
> - **Возможность**: L=нужен Local-режим (процесс/файлы), R=нужен живой RCON, —=всегда
> - **Риск**: 0=чтение, 1=безопасное изменение, 2=влияет на игроков/игру, 3=опасно (данные/права/остановка), 4=разрушительно

## 1. Поверхности приложения

| Поверхность | Файл | Строк | Роль |
|---|---|---|---|
| Главное окно | `MainWindow.xaml/.cs` | 140/180 | NavigationView, кастомная шапка (дубль OctaTitleBar!), чипы состояния в футере панели, статус-бар, StatusLoop (serverinfo/5с) |
| Шапка окон | `OctaTitleBar.xaml/.cs` | 24/160 | drag-зона, min/max/close, псевдо-maximize; используется окнами, но НЕ главным (у него своя копия логики) |
| Страница | `Pages/DashboardPage` | 542/458+430 | 2 вкладки: ОБЗОР, ЛИЦО СЕРВЕРА |
| Страница | `Pages/CommandsPage` | 726/777 | 6 вкладок: ОПЕРАЦИИ, ВРЕМЯ И ПОГОДА, ИГРОКИ И МОДЕРАЦИЯ, ИВЕНТЫ И СПАВН, DEEP SEA, РЕЖИМЫ, ЧАТ/ОБЪЯВЛЕНИЯ, CARBON/ПЛАГИНЫ (фактически 7) |
| Страница | `Pages/ConsolePage` | 63/159 | RCON-консоль, ItemsControl БЕЗ виртуализации |
| Страница | `Pages/PlayersPage` | 195/287 | 2 вкладки: ОНЛАЙН (DataGrid+контекстное меню+панель модерации), БАЗА ИГРОКОВ (DataGrid из SQLite) |
| Страница | `Pages/PluginsPage` | 160/105 | 2 вкладки: ПЛАГИНЫ (карточки), МОДУЛИ CARBON |
| Страница | `Pages/StorePage` | 62/163 | WebView2 → codefling.com, авто-установка скачанного в carbon/plugins |
| Страница | `Pages/ServerCfgPage` | 266/382 | 4 вкладки: ЛИМИТЫ, КАРТА И СЕТЬ, ДОП. CONVARS, РЕДАКТОР ФАЙЛОВ (AvalonEdit) |
| Страница | `Pages/RatesPage` | 223/449 | 4 вкладки: СБОР И КРАФТ, СТАКИ, ЛУТ/BETTERLOOT, ПРЕСЕТЫ ЛУТА |
| Страница | `Pages/FarmPage` | 192/367 | 3 вкладки: СКОТ, ПЛОТНОСТЬ, РАСТЕНИЯ |
| Страница | `Pages/LogsPage` | 77/230 | 2 вкладки: ЖУРНАЛ СЕРВЕРА (live), ФАЙЛЫ ЛОГОВ |
| Страница | `Pages/WipePage` | 77/76 | статус + 2 вкладки: АВТОВАЙП, ВЫПОЛНИТЬ ВАЙП |
| Страница | `Pages/InstallPage` | 86/115 | 2 вкладки: STEAMCMD/СЕРВЕР, CARBON |
| Страница | `Pages/SettingsPage` | 165/80 | 3 вкладки: ПУТИ И СЕРВЕР, МИР, АВТОМАТИКА |
| Окно | `PluginConfigWindow` | 77/479 | канонический редактор конфигов плагинов: таблица (OctaDes-описания) + JSON (AvalonEdit), dirty-флаг при закрытии |
| Окно | `SetupWizardWindow` | 423/574 | 6 шагов: Режим→Профиль→Основы→Мир→Установка / Remote-подключение |
| Окно | `InfoWindow` | 39/18 | модальное чтение длинного текста (статистика, combatlog, гайд по домену) |
| Тема | `Themes/OctavianTheme.xaml` | ~470 | цвета C.*/кисти B.* + стили Octa* (панели, карточки, кнопки, шапки окон) |

## 2. Динамические и скрытые элементы

| Элемент | Где | Что генерирует |
|---|---|---|
| `PnlTitlePresets` | Dashboard/Лицо | кнопки пресетов названия сервера |
| `PnlFaceTagsChips` | Dashboard/Лицо | чипы тегов в превью карточки |
| `DsCounts/DsGeom/DsLoot/DsAccess*/DsTimers/DsPerf*` | Commands/DEEP SEA | ~35 полей convars `deepsea.*`, `debug.disablelootnavobstaclesindeepsea` |
| `PnlStackPresets` | Rates/СТАКИ | кнопки пресетов OctaStack |
| `Cards`, `ModuleCards` | Plugins | карточки плагинов и модулей Carbon |
| `CbTarget`, `CbTeleportDest` | Commands | два разных селектора онлайн-игрока |
| `CmbTpTarget` | Players | третий селектор игрока (цель ТП) |
| `CbAnnTemplates` | Commands/ЧАТ | шаблоны объявлений из `RsmSettings.AnnouncementTemplates` |
| `ChatHistory` | Commands/ЧАТ | история чата из `DbService` (SQLite) |
| `CmbLog` | Logs/Файлы | список server.log, carbon logs, data\*.log |
| `LstProfiles` | Wizard | профили сервера `ServerProfiles` |
| `LstServer`, `LstCarbon` | ServerCfg/ФАЙЛЫ | дерево сырых конфигов сервера и ядра Carbon |
| MessageBox-подтверждения | почти везде | ~20 диалогов YesNo (kill, ban, owner, wipe, maintenance, spawn, restart…) |
| `InfoWindow` | Players, Dashboard | stats, combatlog, гайд по домену |
| `PluginConfigWindow` | Plugins ⚙, Rates (OpenBLCfg), App `--test-config` | открывается минимум из 3 мест |
| ContextMenu | Players (2 грида), Logs (журнал) | модерация, копирование строк |

## 3. Модель возможностей (фактическое состояние)

| Режим | Что скрыто/доступно сейчас |
|---|---|
| Remote (`DeploymentMode=Remote`) | `MainWindow` прячет: Плагины, Серв. конфиг, Рейты, Ферма, Логи, Вайпы, Установка. Дашборд показывает `remote` вместо диска/RAM, «REMOTE ONLINE» вместо состояния процесса. Видны: Дашборд, Управление, Консоль, Игроки, Магазин, Настройки |
| Local, сервер остановлен | Dashboard гасит Restart/ForceSave/Kill; ServerCfg помечает Карту/Сеть замком «только при выключенном» (но не блокирует поля!); Wipe требует остановленный сервер |
| RCON отключён | Все кнопки команд остаются активными — ошибка показывается только после отправки (нет up-front гейтинга) |
| Проблемы | Единой модели нет: `IsRemoteMode` читается в 3 местах, `App.Server.IsRunning`/`App.Rcon.IsConnected` размазаны по страницам, пункты меню и кнопки не связаны общей capability-моделью |

## 4. Реестр функций

### 4.1 Оболочка (MainWindow / OctaTitleBar / App)

| FeatureId | Текущее место | Назначение | Сущ. | Источник | Исполн. | Возм. | Риск | Новое место | Дубли | Сценарий проверки |
|---|---|---|---|---|---|---|---|---|---|---|
| SHELL-01 | MainWindow шапка | заголовок, drag, min/max/close | A | — | LOCAL | — | 0 | общий `OctaTitleBar` | OctaTitleBar (тот же код) | псевдо-max по WorkArea, restore |
| SHELL-02 | MainWindow NavigationView | 12 пунктов меню + футер Настройки | A | статично | LOCAL | — | 0 | новая оболочка/маршруты | — | все маршруты открываются, клавиатура |
| SHELL-03 | NavPane футер: чипы SERVER/RCON | индикация состояния | S | Server/Rcon events | LOCAL | — | 0 | статус-бар (единая модель) | SHELL-04 | оба индикатора синхронны |
| SHELL-04 | Статус-бар: состояние/игроки/fps/uptime/режим/часы | телеметрия | S | serverinfo /5с | RCON | R | 0 | статус-бар | SHELL-03, DASH-INFO | при RCON-off — «—», без ошибок |
| SHELL-05 | `App.Settings` (octavian-rsm.json) | конфиг приложения, автокорень, миграция SetupCompleted | A | файл | FS | — | 1 | AppContext/ServerContext | — | перенос portable, сохранение полей |
| SHELL-06 | `--test-config <Плагин>` | диагностический запуск редактора | A | argv | LOCAL | — | 0 | сохранить | — | открывает окно конфига |
| SHELL-07 | crash-лог `data\crash.log` | глобальный перехват исключений | A | — | FS | — | 0 | сохранить | — | исключение не роняет UI |
| SHELL-08 | wizard при старте | SetupWizard если `!SetupCompleted` или нет exe | A | Settings | LOCAL | — | 1 | обновлённый мастер | — | cold-start, remote-start |

### 4.2 DashboardPage

| FeatureId | Текущее место | Назначение | Сущ. | Источник | Исполн. | Возм. | Риск | Новое место | Дубли | Сценарий проверки |
|---|---|---|---|---|---|---|---|---|---|---|
| DASH-01 | ОБЗОР/ПОДКЛЮЧЕНИЕ | адрес сервера, «📋 КОПИРОВАТЬ» client.connect, «▶ В STEAM» steam://run | S | DNS SRV/IP+порт | LOCAL/HTTP | — | 0 | Подключение (server.connection) + дашборд-краткое | — | длинный домен, remote-режим |
| DASH-02 | ОБЗОР/УПРАВЛЕНИЕ BtnPower | старт/стоп процесса | S | ServerManager | PROC | L | 3 | Главная (блок управления) | CMD-01 (quit) | старт→RCON connect→статус |
| DASH-03 | BtnRestart | рестарт: save→quit→watchdog поднимает | S | Server+Rcon | PROC+RCON | L,R | 3 | Главная | — | подтверждение, мир сохранён |
| DASH-04 | ForceSave | `server.save` | S | — | RCON | R | 1 | Главная | CMD-01 | RCON off → явная недоступность |
| DASH-05 | BtnKill | убить процесс без сохранения | S | ServerManager | PROC | L | 3 | Главная | — | предупреждение «без сейва» |
| DASH-06 | ОПЦИИ: Watchdog + задержка | автоподнятие процесса | A | Settings | LOCAL | L | 1 | Автоматизация | SET-03 | toggle+delay сохраняются |
| DASH-07 | ОПЦИИ: Авторестарт + время + warn | restart по расписанию | S | Settings, Watchdog | LOCAL+RCON | L | 2 | Автоматизация | SET-03 | формат HH:mm валидируется |
| DASH-08 | ОПЦИИ: Автовайп + дни + полный | автовайп при старте | S | Settings, WipeService | FS | L | 3 | Автоматизация/Вайпы | WIPE-02 | due-проверка при старте |
| DASH-09 | Информация о сервере (11 строк) | стат, онлайн, очередь, fps, сеть, RAM, CPU, диск, аптайм, вайп, RCON | S | serverinfo+snapshot | RCON+PROC | R | 0 | Главная (правая колонка) | SHELL-04 | remote: RAM/диск → «remote» |
| DASH-10 | BtnUpdate | самообновление менеджера с GitHub | A | UpdateService | HTTP | — | 2 | Настройки приложения/Об о программе | — | показ кнопки только при новой версии |
| FACE-01 | ЛИЦО: конструктор hostname | пресеты, регион, база, рейты, пати, фичи, вайп → `server.hostname` | S | Settings.ServerTitleBuilder | FS+RCON | L(R) | 1 | Сервер→Лицо | — | длинное имя, счётчик символов |
| FACE-02 | ЛИЦО: лимит пати `relationshipmanager.maxteamsize` | размер тимов | S | server.cfg+RCON | RCON+FS | L,R | 1 | Сервер→Лицо | — | 0 = тимы выкл |
| FACE-03 | ЛИЦО: медиа | description/url/tags/headerimage/logoimage/favoritesEndpoint | S | server.cfg+Settings | FS | L | 1 | Сервер→Лицо | — | длинные URL не ломают форму |
| FACE-04 | ЛИЦО: АВТО-теги | сбор тегов из региона/вайпа/режима | A | Settings | LOCAL | — | 0 | Сервер→Лицо | — | совпадает с превью |
| FACE-05 | ЛИЦО: проверка публикации | проверка портов/домена | S | DnsSrvResolver/GeoService | HTTP | — | 0 | Сервер→Лицо | — | remote/нет DNS → корректный вывод |
| FACE-06 | ЛИЦО: гайд по домену | InfoWindow с инструкцией | A | статик | LOCAL | — | 0 | Сервер→Лицо | — | окно открывается |
| FACE-07 | ЛИЦО: живой превью | карточка сервера как в игре (баннер, лого, hostname, теги, игроки/карта/вайп) | A | поля формы | LOCAL | — | 0 | Сервер→Лицо | — | обновляется на ввод, DPI |
| FACE-08 | ЛИЦО: «СОХРАНИТЬ ЛИЦО» | запись server.cfg + readcfg при живом сервере | S | — | FS+RCON | L(R) | 1 | Сервер→Лицо | — | отдельный результат файла и RCON |

### 4.3 CommandsPage

| FeatureId | Текущее место | Назначение | Сущ. | Источник | Исполн. | Возм. | Риск | Новое место | Дубли | Сценарий проверки |
|---|---|---|---|---|---|---|---|---|---|---|
| CMD-TGT | шапка: селектор ЦЕЛЬ + refresh | выбор онлайн-игрока для адресных действий | P | playerlist | RCON | R | 0 | Игроки (единый выбранный игрок) | PLR-01, CbTeleportDest, CmbTpTarget | пустой онлайн, длинные ники |
| CMD-01 | ОПЕРАЦИИ | server.save / writecfg / restart N / fps.limit / readcfg / quit | S | — | RCON | R | 2–3 | Главная+Мир (fps) ; quit→Главная | DASH-02/03/04, CFG-02 | подтверждения, RCON off |
| CMD-02 | ВРЕМЯ | env.time слайдер+пресеты | S | — | RCON | R | 1 | Мир и события | — | точный ввод числом |
| CMD-03 | ПОГОДА | weather.load пресеты, reset, report | S | — | RCON | R | 1 | Мир и события | — | отчёт читается в ▲ |
| CMD-04 | ПОГОДА-ШАНСЫ | weather.*_chance 6 слайдеров + применить | S | — | RCON | R | 1 | Мир и события | — | batch-отправка |
| CMD-05 | АТМОСФЕРА | rain/fog/wind/thunder + радуга | S | — | RCON | R | 1 | Мир и события | — | — |
| CMD-06 | ЦЕЛЬ: состояние | octa.targetinfo, combatlog, octa.heal, octa.revive, octa.god, octa.kill | P | OctaAdminTools | RCON | R | 2–3 | Игроки→карточка игрока | PLR-* | плагин отсутствует → понятная ошибка |
| CMD-07 | ЦЕЛЬ: телепорт | в координаты / к игроку | P | OctaAdminTools | RCON | R | 2 | Игроки | PLR-TP | валидация x y z |
| CMD-08 | Очередь/доступ | serverinfo, skipqueueid±, octa.allow±/list, octa.maintenance | S,P | OctaAdminTools | RCON | R | 2–3 | Игроки→Доступ | — | maintenance on с текстом |
| CMD-09 | Loadout/предметы | inventory.giveto, listloadouts, deployloadout, octa.strip | P | OctaAdminTools | RCON | R | 2 | Игроки | — | несуществующий loadout |
| CMD-10 | Модерация (базовая) | kick, unban, mute/unmute, moderatorid, ownerid, remove* | P | — | RCON | R | 3 | Игроки (контекст/панель) | PLR-* (полный дубль) | удалить форму, оставить адресные |
| CMD-11 | ИВЕНТЫ: триггеры | airdrop.drop, spawn.ch47event, cargoship, heli.call, bradley(у цели), bradley.quickrespawn | S | OctaAdminTools | RCON | R | 2 | Мир и события | — | bradley у цели требует игрока |
| CMD-12 | ИВЕНТЫ: планировщик | events.print/set_enabled/set_min_delay/set_max_delay | S | — | RCON | R | 1 | Мир и события | — | расписание в ▲ |
| CMD-13 | ВЕРТ | heli.call/death/flee/lifetimeminutes | S | — | RCON | R | 2 | Мир и события | — | — |
| CMD-14 | ПОПУЛЯЦИИ | spawn.fill_*, spawn.report, delete_all_populations | S | — | RCON | R | 3 | Мир и события | — | delete_all с подтверждением |
| CMD-15 | СПАВН-плотность | spawn.min/max_rate+density слайдеры | S | — | RCON | R | 1 | Мир и события | FARM-05 (частично) | — |
| CMD-16 | СПАВН у игрока | octa.spawnat пресеты, octa.spawnatpos, octa.killtype | P,S | OctaAdminTools | RCON | R | 2–3 | Мир и события (+выбор цели) | — | нет цели → недоступно |
| CMD-17 | DEEP SEA: цикл | open/close/createdeepsea, status, timetowipe, entitycount, printloot | S | — | RCON | R | 2–3 | Мир и события→DeepSea | — | createdeepsea — тяжёлая, с подтв. |
| CMD-18 | DEEP SEA: ~35 convars | counts/geom/loot/access/timers/perf поля | S | deepsea.* | RCON | R | 2 | Мир и события→DeepSea | — | batch-применение по группам |
| CMD-19 | DEEP SEA: блок «локальные команды» | инфо-текст: spawn*here через RCON заблокирован | A | статик | — | — | 0 | Мир и события→DeepSea | — | текст сохранить |
| CMD-20 | РЕЖИМЫ: профиль нагрузки | relief/emergency/restore + снимок ai.*/spawn.* | S | Settings.LoadModeSnapshot | RCON | R | 2–3 | Мир и события→Режимы | — | restore возвращает снимок |
| CMD-21 | РЕЖИМЫ: мир | pve on/off, decay off, upkeep off, restore + «в server.cfg» | S | Settings.WorldModeSnapshot | RCON+FS | R(L) | 3 | Мир и события→Режимы | SET-04? (server.pve в cfg) | чекбокс persist работает |
| CMD-22 | ЧАТ: объявление сейчас | `say` текст | S | — | RCON | R | 1 | Чат и объявления | — | Enter отправляет |
| CMD-23 | ЧАТ: шаблоны | add/delete/send шаблонов | A,S | Settings.AnnouncementTemplates | LOCAL+RCON | — | 1 | Чат и объявления | — | сохранение списка |
| CMD-24 | ЧАТ: расписание | повторяющееся объявление N мин, работает через Watchdog | A,S | Settings | LOCAL+RCON | — | 1 | Чат и объявления/Автоматизация | — | таймер переживает навигацию |
| CMD-25 | ЧАТ: история | поиск по SQLite-истории чата | A | DbService | DB | — | 0 | Логи/Чат | LOG-04? | 10k строк виртуализовано |
| CMD-26 | CARBON | c.reload <имя>, c.plugins, c.reloadmodules, c.loadmoduleconfig | PL | — | RCON | R | 2 | Плагины (быстрые действия) | PLG-03 | — |
| CMD-27 | CARBON | gc.collect, banlist | S | — | RCON | R | 1 | Консоль/Игроки→Баны | — | banlist → раздел модерации |
| CMD-RES | низ страницы | полоса результата + разворачиваемый полный ответ + копировать/очистить | A | RCON | LOCAL | — | 0 | общий CommandResult-компонент | — | длинный ответ, ▲ раскрытие |

### 4.4 PlayersPage

| FeatureId | Текущее место | Назначение | Сущ. | Источник | Исполн. | Возм. | Риск | Новое место | Дубли | Сценарий проверки |
|---|---|---|---|---|---|---|---|---|---|---|
| PLR-01 | ОНЛАЙН грид | playerlist: ник/steamid/ping/hp/ip/страна/сессия/всего | P | playerlist+Geo+Db | RCON+DB | R | 0 | Игроки→Онлайн | — | сортировки, длинные ники |
| PLR-02 | поиск онлайн | фильтр ник/steamid/ip | A | грид | LOCAL | — | 0 | Игроки | — | регистр, частичное совпадение |
| PLR-03 | панель МОДЕРАЦИЯ | причина + kick/ban/unban/mute(5 длит.)/unmute | P | — | RCON | R | 3 | Игроки (контекст+панель) | CMD-10 | причина экранируется |
| PLR-04 | панель ПРАВА | +owner/+модератор/снять права | P | — | RCON | R | 3 | Игроки | CMD-10 | подтверждение owner |
| PLR-05 | панель ДЕЙСТВИЯ | ТП к игроку, stats, combatlog, copy id | P | — | RCON | R | 2 | Игроки | CMD-06/07 | InfoWindow для stats/combatlog |
| PLR-06 | контекстное меню ОНЛАЙН | тот же набор + «Замьютить 10 мин» | P | — | RCON | R | 3 | общие команды (ICommand) | PLR-03/04/05 | меню = панель = те же команды |
| PLR-07 | БАЗА ИГРОКОВ | все виденные: ник/steamid/ip/страна/всего/первый/последний визит | P | DbService | DB | — | 0 | Игроки→База | — | большая база, виртуализация |
| PLR-08 | контекстное меню БАЗЫ | ban/unban/owner/mod/снять/copy на офлайн-игроке | P | — | RCON | R | 3 | Игроки | — | офлайн-бан работает |

### 4.5 ConsolePage / LogsPage

| FeatureId | Текущее место | Назначение | Сущ. | Источник | Исполн. | Возм. | Риск | Новое место | Дубли | Сценарий проверки |
|---|---|---|---|---|---|---|---|---|---|---|
| CON-01 | консоль | лента сообщений RCON (ItemsControl без виртуализации!) | S | Rcon.OnMessage | RCON | R | 0 | общий LogViewer | — | 10k строк не вешает UI |
| CON-02 | фильтры | ALL/ERR/CHAT/SYS + текстовый фильтр | A | лента | LOCAL | — | 0 | LogViewer | — | комбинирование |
| CON-03 | ввод команды | TextBox + Enter + кнопка | S | — | RCON | R | 2 | Консоль | — | история команд (↑)? |
| CON-04 | переподключить/очистить/копировать | управление | A | Rcon | RCON/LOCAL | R | 0 | Консоль | — | — |
| LOG-01 | журнал сервера | stdout процесса + tail server.log + история БД | S | Server.OnOutput+Db | PROC+FS+DB | L | 0 | LogViewer (вкладка) | CON-01 (перекрёстно) | autoscroll, RCON-off |
| LOG-02 | файлы логов | выбор файла + фильтр + просмотр | F | FS scan | FS | L | 0 | LogViewer | — | большой файл, недоступная папка |
| LOG-03 | копирование | строка/всё, Ctrl+C в гриде | A | — | LOCAL | — | 0 | LogViewer | — | — |
| LOG-04 | Db.Log | запись rcon:*/server в SQLite | A | события | DB | — | 0 | сохранить | — | ротация/рост БД |

### 4.6 ServerCfgPage

| FeatureId | Текущее место | Назначение | Сущ. | Источник | Исполн. | Возм. | Риск | Новое место | Дубли | Сценарий проверки |
|---|---|---|---|---|---|---|---|---|---|---|
| CFG-01 | ЛИМИТЫ И ФЛАГИ | maxplayers, saveinterval, idlekick, pve, censorplayerlist, radiation | S | server.cfg | FS | L | 1 | Параметры сервера | SET-04 (maxplayers/save) | launch-lock индикация |
| CFG-02 | верхние кнопки | ОТКАТИТЬ / ПРИМЕНИТЬ(readcfg) / СОХРАНИТЬ | S | server.cfg | FS+RCON | L(R) | 1 | Параметры (раздельный результат файла и RCON) | CMD-01 | файл записан ≠ применён — два статуса |
| CFG-03 | КАРТА | levelurl, seed, worldsize | S | server.cfg | FS | L | 1 | Параметры→Карта | SET-04, WIZ-04 | замок при работающем сервере |
| CFG-04 | библиотека карт | папка .map → ComboBox → file:/// levelurl | F | FS scan | FS | L | 0 | Параметры→Карта | — | пустая/недоступная папка |
| CFG-05 | СЕТЬ | server.ip/port/queryport/rcon.port/password | S | server.cfg | FS | L | 2 | Параметры→Сеть | SET-02, WIZ-03 | ⚠ rcon.* в cfg применяется только при старте — показать замок |
| CFG-06 | ДОП. CONVARS | произвольные ключ «значение» в конец cfg | S | server.cfg | FS | L | 1 | Параметры→Доп. | — | парсер TryParseLine |
| CFG-07 | РЕДАКТОР ФАЙЛОВ | AvalonEdit: server.cfg и конфиги ядра Carbon, dirty-флаг, сохранить/откатить | F | FS | FS | L | 2 | Параметры→Файлы / общий FileEditor | PCW-01 (тот же редактор) | несохранённый файл при навигации |
| CFG-08 | статус-строка | «готов»/путь файла | A | — | LOCAL | — | 0 | общий паттерн | — | — |

### 4.7 SettingsPage

| FeatureId | Текущее место | Назначение | Сущ. | Источник | Исполн. | Возм. | Риск | Новое место | Дубли | Сценарий проверки |
|---|---|---|---|---|---|---|---|---|---|---|
| SET-01 | ПУТИ | ServerDir, SteamCmdExe | A | RsmSettings | FS | — | 1 | Настройки приложения | WIZ-03 | недоступная папка |
| SET-02 | РЕЖИМ И СЕТЬ | DeploymentMode, RconHost, GamePort/RconPort/QueryPort, RconPassword | A | RsmSettings | LOCAL | — | 2 | Подключение (server.connection) | CFG-05, WIZ-03/06 | смена режима → перечитать меню |
| SET-03 | АВТОМАТИКА | watchdog, авторестарт | A | RsmSettings | LOCAL | L | 1 | Автоматизация | DASH-06/07/08 | — |
| SET-04 | МИР | seed, worldsize, maxplayers, level, levelurl, identity, appport, saveinterval, gamemode | A→S | RsmSettings→launch args | FS | L | 1 | Параметры сервера (⚠ двойной источник: args vs server.cfg) | CFG-01/03, WIZ-04 | cfg перебивает args — приоритет зафиксировать |
| SET-05 | СОХРАНИТЬ | запись octavian-rsm.json | A | — | FS | — | 1 | черновик+save | — | миграция старых полей |

### 4.8 RatesPage

| FeatureId | Текущее место | Назначение | Сущ. | Источник | Исполн. | Возм. | Риск | Новое место | Дубли | Сценарий проверки |
|---|---|---|---|---|---|---|---|---|---|---|
| RATE-01 | СБОР | gather.rate dispenser/pickup/quarry/survey, dispenser.scale, craft.timescale | S | server.cfg+RCON | FS+RCON | L(R) | 1 | Баланс→Сбор и крафт | — | точный ввод числом, не только слайдер |
| RATE-02 | пресеты ×1/×2/×3/×5/×10 | выставить все слайдеры | A | — | LOCAL | — | 0 | Баланс | — | пресет НЕ применяет без кнопки |
| RATE-03 | «ПРИМЕНИТЬ РЕЙТЫ» | запись cfg + convars по RCON + авто-обновление hostname | S | — | FS+RCON | L(R) | 1 | Баланс | — | раздельный результат file/RCON |
| RATE-04 | СТАКИ | OctaStack пресеты капов | PL | PluginService+RCON | RCON | L(R) | 1 | Баланс→Стаки | — | плагин отсутствует → недоступно |
| RATE-05 | BETTERLOUT карточка | купить/открыть конфиг/reload | PL | FS+RCON+HTTP | LOCAL | L | 0 | Баланс→Лут | PLG-05 (OpenBLCfg→PluginConfigWindow) | нет плагина → подсказка |
| RATE-06 | множители лута | Loot/Scrap Multiplier → BetterLoot.json | PL | FS | FS | L | 1 | Баланс→Лут | PCW-01 (тот же файл можно править в редакторе!) | ⚠ два пути к одному конфигу — канон: редактор |
| RATE-07 | MagicLoot карточка | сайт/папка плагинов | PL | HTTP+FS | LOCAL | — | 0 | Баланс→Лут | — | — |
| RATE-08 | ПРЕСЕТЫ ЛУТА | библиотека: новый/сохранить/удалить/применить (6 целей) | A→PL | Settings.LootPresets | FS+RCON | L | 2 | Баланс→Пресеты | — | применение = запись файла + reload, два результата |

### 4.9 FarmPage

| FeatureId | Текущее место | Назначение | Сущ. | Источник | Исполн. | Возм. | Риск | Новое место | Дубли | Сценарий проверки |
|---|---|---|---|---|---|---|---|---|---|---|
| FARM-01 | СКОТ | agescale(+off), breedcooldown, calvingcooldownscale, breedingenabled | S | server.cfg+RCON | FS+RCON | L(R) | 1 | Баланс→Скот | — | чекбокс+слайдер конфликт |
| FARM-02 | СКОТ: содержание | allowmounting, overgrazingenabled, overgrazedareacooldownminutes, conditionfallseconds | S | — | FS+RCON | L(R) | 1 | Баланс→Скот | — | — |
| FARM-03 | пресеты фермы | vanilla/fast/farm5/forever | A | — | LOCAL | — | 0 | Баланс | — | не применяет сразу |
| FARM-04 | ПЛОТНОСТЬ | *.population по группам (динамические поля) | S | — | FS+RCON | L(R) | 1 | Баланс→Плотность | CMD-15 (spawn.* пересечение — разные convars, объединить в одну панель?) | — |
| FARM-05 | РАСТЕНИЯ | planttickscale/tick, nonplanterdeathchance, optimalplanterqualitysaturation, plantlightdetection, useminimumplantcondition | S | — | FS+RCON | L(R) | 1 | Баланс→Растения | — | — |
| FARM-06 | RefreshFromRcon | чтение текущих convars при открытии | S | — | RCON | R | 0 | Баланс (общая загрузка) | — | RCON off → дефолты cfg |

### 4.10 PluginsPage / StorePage

| FeatureId | Текущее место | Назначение | Сущ. | Источник | Исполн. | Возм. | Риск | Новое место | Дубли | Сценарий проверки |
|---|---|---|---|---|---|---|---|---|---|---|
| PLG-01 | список плагинов | карточки: имя, мета, описание, file, статус ВКЛ, Codefling-версия | PL | PluginService.Scan | FS | L | 0 | Плагины | — | много плагинов, длинные имена |
| PLG-02 | toggle вкл/выкл | переименование .cs↔.disabled | PL | — | FS | L | 2 | Плагины | — | файл занят → ошибка |
| PLG-03 | RELOAD карточки | c.reload <name> | PL | — | RCON | R | 1 | Плагины | CMD-26 | RCON off → подсказка |
| PLG-04 | ⚙ конфиг | PluginConfigWindow | PL | configs\*.json | FS | L | 1 | Плагины→канон. редактор | — | — |
| PLG-05 | 🗑 удалить | файл+конфиг в plugins\_trash | PL | — | FS | L | 4 | Плагины | — | восстановимо, подтверждение |
| PLG-06 | МОДУЛИ CARBON | список модулей + toggle (config enabled) | PL | — | FS | L | 2 | Плагины→Модули | — | — |
| PLG-07 | поиск/папка/сканировать | фильтр, открыть carbon\plugins, rescan | A | FS | LOCAL | L | 0 | Плагины | — | — |
| STO-01 | WebView2 codefling | браузер магазина + навигация | A | Web | HTTP | — | 0 | Магазин | — | fallback без WebView2 |
| STO-02 | авто-установка скачиваний | .cs/.zip → carbon\plugins | PL | WebView2 events | FS | L | 2 | Магазин | — | remote → качать некуда: недоступно |
| STO-03 | ВЕРСИИ | сверка версий с codefling | PL | CodeflingService | HTTP | L | 0 | Плагины | PLG-01 (CfStatus) | — |

### 4.11 WipePage / InstallPage

| FeatureId | Текущее место | Назначение | Сущ. | Источник | Исполн. | Возм. | Риск | Новое место | Дубли | Сценарий проверки |
|---|---|---|---|---|---|---|---|---|---|---|
| WIPE-01 | статус | последний/следующий вайп, файлов сохранения, отсчёт | S | WipeService | FS | L | 0 | Вайпы | DASH-09 (последний вайп) | — |
| WIPE-02 | АВТОВАЙП | вкл, каждые N дн, полный, случайный seed | A | Settings+WipeService | LOCAL+FS | L | 3 | Автоматизация/Вайпы | DASH-08 | единый источник настроек |
| WIPE-03 | ВЫПОЛНИТЬ | вайп карты / полный → _wipe_backup | S | WipeService.Execute | FS | L | 4 | Вайпы | — | сервер запущен → запрет |
| INS-01 | SteamCMD | установить/обновить (app 258550), validate, ветка | S | SteamCmdService | PROC | L | 2 | Установка | WIZ-05 | лог потоком, отмена? |
| INS-02 | Carbon | проверить версию, установить/обновить из GitHub | S | HTTP+FS | HTTP+FS | L | 2 | Установка | WIZ-02 | — |

### 4.12 Окна

| FeatureId | Текущее место | Назначение | Сущ. | Источник | Исполн. | Возм. | Риск | Новое место | Дубли | Сценарий проверки |
|---|---|---|---|---|---|---|---|---|---|---|
| PCW-01 | PluginConfigWindow | табличный редактор JSON-конфига (плоские+вложенные, OctaDes-подсказки, списки) | PL | configs\*.json | FS | L | 1 | канонический ConfigEditor (компонент) | CFG-07 (второй редактор), RATE-06 | открытие из 3 мест → один редактор |
| PCW-02 | режим JSON | AvalonEdit с валидацией | PL | — | FS | L | 2 | ConfigEditor | — | сломанный JSON → ошибка, не затереть |
| PCW-03 | dirty при закрытии | «закрыть без сохранения?» | A | — | LOCAL | — | 0 | Draft-модель | — | выбор: сохранить/отменить/закрыть |
| WIZ-01..06 | SetupWizard | режим → профиль → основы(host/ports/pass/domain/plugins) → мир → установка / remote(host/port/pass+тест) | A→S | RsmSettings | PROC+FS | — | 2 | обновлённый мастер | SET-*, CFG-* | remote-ветка, повторный запуск |
| INF-01 | InfoWindow | модальный просмотр текста | A | — | LOCAL | — | 0 | общий компонент | — | длинный текст, скролл |

## 5. Карта дублей → целевые места

| # | Функция | Сейчас | Каноническое место | Действие |
|---|---|---|---|---|
| D1 | kick/ban/mute/owner/mod/demote/tp/combatlog/stats | Commands «ИГРОКИ И МОДЕРАЦИЯ» + Players (меню+панель) | **Игроки** (единый набор команд на выбранного игрока) | удалить вкладку из Commands |
| D2 | watchdog/авторестарт/автовайп | Dashboard ОПЦИИ + Settings АВТОМАТИКА + Wipe АВТОВАЙП | **Автоматизация** | убрать из Dashboard и Settings |
| D3 | seed/worldsize/maxplayers/level/levelurl | Settings МИР (launch args) + ServerCfg КАРТА (server.cfg) + Wizard | **Параметры сервера** | один редактор; cfg приоритетнее args — показать |
| D4 | hostname/description/url/tags/images | Dashboard ЛИЦО + RsmSettings + (note в Settings) | **Сервер→Лицо** | сохранить как есть, связать с params |
| D5 | rcon.host/port/password | Settings СЕТЬ (app) + ServerCfg СЕТЬ (cfg) + Wizard Remote | **Подключение** (app) | из cfg-формы rcon.* убрать или замкнуть с пояснением |
| D6 | c.reload плагина | Commands CARBON + Plugins ⟳ | **Плагины** | убрать карточку из Commands |
| D7 | server.save/quit/restart/writecfg/readcfg | Commands ОПЕРАЦИИ + Dashboard + ServerCfg кнопки | **Главная** (управление) + readcfg в Параметрах | удалить вкладку ОПЕРАЦИИ |
| D8 | BetterLoot.json | Rates «множители лута» (прямая запись) + PluginConfigWindow | **единый ConfigEditor** (Rates оставить как быстрый путь → открывает редактор) | договориться: быстрые слайдеры vs канон |
| D9 | шапка окна | MainWindow inline + OctaTitleBar | **OctaTitleBar** везде | перейти MainWindow на контрол |
| D10 | селектор игрока | Commands CbTarget + CbTeleportDest + Players CmbTpTarget | **единый SelectedPlayer** в ServerContext | одна модель выбора |
| D11 | лог-просмотр | Console ItemsControl + Logs ListBox + Install TxtLog + Wizard лог | **общий LogViewer** (виртуализация) | заменить реализации |
| D12 | «полный ответ команды» | Commands низ страницы | **CommandResult-компонент** (в статус/панель контекста) | переиспользовать |
| D13 | подтверждения | ~20 разных MessageBox с разным текстом | **ConfirmService** с целью+последствиями | единый формат |
| D14 | spawn fill/report/delete + farm population | Commands + Farm | **Мир→Популяции** | сгруппировать |
| D15 | announce-расписание | Commands ЧАТ + WatchdogService | **Чат/Автоматизация** — единый блок «Рассылки» | настройка в одном месте |

## 6. Целевая навигация (проект, утверждается на этапе 4)

```
ГЛАВНАЯ
  dashboard        Обзор: статус, управление питанием, подключение, телеметрия
СЕРВЕР
  server.face      Лицо сервера (hostname-конструктор, медиа, превью, публикация)
  server.params    Параметры: лимиты/флаги, карта, сеть, доп. convars (+ launch args)
  server.files     Редактор файлов (AvalonEdit, server.cfg / ядро Carbon)
  server.install   Установка: SteamCMD, Carbon
  server.wipe      Вайпы: статус, расписание, выполнение
  server.automation Автоматизация: watchdog, авторестарт, автовайп-правило
ИГРА
  players          Игроки: онлайн + база + все адресные действия и модерация
  world            Мир и события: время, погода, ивенты, спавн, популяции, режимы, DeepSea
  balance          Баланс: сбор/крафт, стаки, лут(BetterLoot/MagicLoot/пресеты), скот, плотность, растения
  chat             Чат и объявления: say, шаблоны, расписание, история
ИНСТРУМЕНТЫ
  plugins          Плагины + модули Carbon (редактор конфига = панель/окно ConfigEditor)
  store            Магазин (WebView2 codefling)
  console          Консоль RCON (LogViewer + ввод)
  logs             Логи: журнал сервера + файлы (LogViewer)
ПРИЛОЖЕНИЕ
  settings         Настройки приложения: пути, режим, обновления
  connection       Подключение: режим Local/Remote, RCON host/port/password, тест
```

## 7. Открытые вопросы и риски миграции

1. **Двойной источник параметров**: `RsmSettings` (launch args) vs `server.cfg`. `BuildServerArgs` добавляет `+server.cfg` последним — cfg побеждает. Нужно либо синхронизировать записи, либо честно показать приоритет.
2. **`rcon.*` в server.cfg** применяются только до старта RCON-сервера — в форме пометить launch-lock.
3. **Remote-режим**: Баланс сейчас целиком скрыт, хотя gather/livestock convars могли бы работать через RCON. Решить: remote-подмножество или честное «только Local».
4. **Цель-игрок вне страницы Игроки**: команды «спавн у игрока»/«Бредли у цели» в Мире требуют выбранного игрока — нужна глобальная модель SelectedPlayer или выбор внутри страницы.
5. **Виртуализация**: Console (ItemsControl), Commands ChatHistory, Players гриды — замерить на 10k строк.
6. **Долгие операции**: SteamCMD/Carbon-install/self-update — нет отмены и прогресса в процентах у части потоков; Install лог не виртуализован.
7. **Преференции**: все поля `RsmSettings` должны пережить рефакторинг (полный список — §RsmSettings, 60+ полей, включая снимки LoadMode/WorldMode, шаблоны объявлений, пресеты лута, конструктор названия).
8. **OctaDes**: канонический редактор должен сохранить поддержку блока описаний OctaDes.
9. **Wizard**: remote-ветка и тест подключения обязаны остаться; шаги привести к новой модели (приложение/сервер).
10. **Store в Remote**: скачивание некуда ставить — показывать просмотр без установки.
