# PITFALLS — подводные камни Carbon/Chaos и их обходы

Заметки о неочевидном поведении сервера, Carbon и Chaos, найденные при работе
над плагинами Octavian. Обновлять по мере нахождения новых грабель.

---

## 1. Chaos + горячая загрузка: `Config == null` → NRE в `LoadConfig`

**Симптом:** плагин-наследник `ChaosPlugin` при `c.reload`/автоподхвате падает:
```
[ERRO] [Plugin] Failed ILoadConfig (Object reference not set to an instance of an object)
   at void Oxide.Ext.Chaos.ChaosPlugin.LoadConfig()
```
Затем плагин "загружается", но `Configuration == null` → NRE в Init, таймерах,
командах. Конфиг-файл при этом НЕ создаётся.

**Причина:** Carbon присваивает `Plugin.Config` **внутри** `Plugin.LoadConfig`
(`Config = new DynamicConfigFile(...)`), а `ChaosPlugin.LoadConfig` обращается
к `Config` раньше. Если файла `<name>.json` в `carbon/configs/` нет — Config
остаётся null → NRE. При старте сервера инициализация идёт иначе, поэтому
OctaESP/OctaMenu грузились чисто (их конфиги уже существовали).

**Обход (использован в OctVIP):** переопределить `LoadConfig`, создать
`DynamicConfigFile` и записать дефолты до вызова `base.LoadConfig()`.
Сеттер `Config` — internal, присваиваем рефлексией:

```csharp
protected override void LoadConfig()
{
    if (Config == null)
    {
        var path = Path.Combine(Manager.ConfigPath, Name + ".json");
        var cfg = new DynamicConfigFile(path);
        if (!cfg.Exists(null)) cfg.WriteObject(new ConfigData());
        typeof(Plugin).GetProperty("Config").SetValue(this, cfg);
    }
    base.LoadConfig();
}
```

**Проверено:** 2026-10-02, OctVIP — при отсутствии файла плагин сам создаёт
конфиг и грузится без ошибок.

---

## 2. Скин-варианты оружия — отдельные `ItemDefinition` с `isRedirectOf`

Кристальные/магазинные стволы (`rifle.ak.glass`, `rifle.ak.ice`,
`rifle.lr300.space`, `pistol.semiauto.a.m15` и т.д.) — это ОТДЕЛЬНЫЕ
ItemDefinition со своими shortname и префабами. Их общее поле —
`ItemDefinition.isRedirectOf` указывает на базовый предмет.

**Правило:** всегда резолвить `item.info.isRedirectOf ?? item.info`, иначе
конфиг раздувается, а варианты не получают настроек базового предмета.
Пример: `plugins/OctAmmo.cs` → `ApplyForItem`, автозаполнение пропускает
редиректы (42 базовых ствола вместо 110 записей).

---

## 3. Реальная ёмкость магазина — `builtInSize`, а не `capacity`

У префаба `BaseProjectile` поле `primaryMagazine.capacity` у большинства
стволов = 0. Заводская ёмкость лежит в
`primaryMagazine.definition.builtInSize` (ссылка на ассет Magazine.Definition;
Definition — value type, `?.` не работает → проверять через `!= null`
на объекте префаба, а не на самом definition).

Фолбэк на `mag.capacity` нужен: у `minigun` значение (300) именно там.

**Важно:** читать из ПРЕФАБА (`ItemModEntity.entityPrefab.GetEntity()`),
а не из живого инстанса — иначе если плагин уже поднял capacity, live-read
запишет модифицированное значение как «заводское» (баг: AK записал
`BaseGame:60` вместо 30).

`ItemCategory.Weapon` включает мили, гранаты, аттачи, осадную технику —
фильтровать по реальному наличию магазина в префабе.

---

## 4. Carbon API-изменения (актуальная версия сервера)

- `arg.Args[i]` в `[ChatCommand]`/консольных командах — `Facepunch.StringView`,
  нужен `.ToString()` при передаче в `string`-параметры.
- `player.displayName` — `StringView` → `.ToString()` при сравнении/присвоении
  string-полям (интерполяция `$"{...}"` работает сама).
- `BuildingPrivlidge.authorizedPlayers` — `Hash<ulong>` (NetworkableId?),
  НЕ `HashSet<PlayerNameID>` — проверять по `userID`.
- `player.IsNpc` существует — для NPC-фильтра достаточно его + страховка
  `userID < 10_000_000` (NPC имеют малые id).

---

## 5. ImageLibrary — рабочая версия 2.0.67

- RPC-слой: официальный `ClientRPC<T1,T2,T3>` через `RpcTarget.SendInfo`
  (наш ручной фикс `ClientRPCEx` больше не нужен).
- `http→https` принудительно — без этого веб-очередь висла.
- Размер PNG >3МБ: в 2.0.8 очередь умирала навсегда (`isLoading` не
  сбрасывался), в 2.0.67 исправлено.
- Источник иконок: `cdn.rusthelp.com/images/256/` (автомиграция конфига
  при версии < 2.0.67). rustedit.io мёртв.

---

## 6. Чат-форматирование и Chaos UI

- `Chat.ChatChannel` / `Chat.ChatEntry` в Carbon — это `ConVar.Chat.*`
  (нужен `using ConVar;` или полное имя).
- `RCon` и `Epoch` в Carbon-плагинах недоступны из коробки — не тащить
  паттерн RCon.Broadcast из Clans, он не компилится.
- Форматтер чата: `OnPlayerChat(BasePlayer, string, ConVar.Chat.ChatChannel)`,
  вернуть `true` чтобы заглушить стандартный вывод, рассылать самим через
  `p.SendConsoleCommand("chat.add", (int)channel, userIdString, line)`.
- При пересечении форматтеров Carbon берёт ПОСЛЕДНИЙ non-null результат и
  пишет warning о конфликте — эксклюзивный хук, держать один форматтер.
- `ChaosStyle.BlackOutline/GreenOutline/RedOutline` — это `OutlineComponent`,
  НЕ `Style`: свой цвет рамки через `new Style(){ImageColor=...}` не сделать.
  Маркировать выбранное — ✓ + жирный шрифт + GreenOutline.
- Полупрозрачный `ChaosStyle.Panel` на светлых сценах нечитаем — задавать
  `ImageColor` с альфой ~0.96.
- **Глифы чата ≠ глифы панели.** CUI панели рисуют широкий Unicode
  (✦ ♛ ⚡ ☠ ❖ ⚙ и т.п.), а чатовый шрифт клиента — ограниченный набор.
  Проверенно видны в чате: `★ ☆ ● ○ ◆ ◇ ♦ ■ □ ▲ ► ♥ ♠ ♣ ± ° §`.
  Экзотика отображается как `?`. Иконки для чата выбирать только из
  проверенного набора, в CUI можно шире. Полного публичного списка
  поддерживаемых глифов нет — только практическая проверка.

- **Серверные эмодзи — решение для картинок в чате** (Rust, август 2023).
  PNG/JPG кладутся в `<identity>/serveremoji/` (у нас
  `server/server/octavian/serveremoji`), используются в чате как `:имя:`.
  Требования: ≤256×256, <250 КБ, не 8/16-бит PNG. После добавления файлов —
  `server.resetserveremoji`, клиентам нужен перезаход. У клиента есть
  настройка отключения серверных эмодзи. Путь к identity-папке из плагина:
  `Carbon.Core.Defines.GetRustIdentityFolder()`. Панель может показывать
  ту же картинку: `ImageLibrary.Call("AddImageData", name, bytes, 0UL, cb)`
  → `GetImage` → `RawImageContainer.WithPNG`. Реализовано в OctVIP 1.3.0,
  генератор бейджей — `gen-vip-badges.ps1`.

- **«8-бит PNG не работает» = палитровые (colortype 3), а не 8 бит/канал.**
  Twemoji и многие стоковые эмодзи сохранены как indexed PNG → сервер
  отвечает `X.png is an invalid png` при ресcане. Лечение: загрузить в
  Bitmap и пересохранить как Format32bppArgb PNG (colortype 6).
  `server.resetserveremoji` возвращает пустой ответ при успехе или имя
  первого битого файла при ошибке.

- **RCON тестового сервера — WebSocket**, не классический Source RCON:
  `ws://127.0.0.1:28116/<password>`, сообщения JSON
  `{Identifier, Message, Type:3}` — Type 3 = команда.

---

## 7. Кодировки — НЕ редактировать файлы через PowerShell-строки

`Get-Content -Raw` в PowerShell 5.1 читает UTF-8 без BOM как Windows-1251 →
`-replace | Set-Content -Encoding utf8` записывает кракозябры (инцидент:
OctVIP.cs, исправлено обратным перекодированием + BOM). Для sed-подобных
замен использовать `edit`-инструмент агента или `[IO.File]::ReadAllText/WriteAllText`
с явным `[Text.Encoding]::UTF8`. Файлы с кириллицей держать в UTF-8 **с BOM**.

---

## 8. Мультиязычные плагины (на примере OctaGrade)

Стандартный механизм Oxide/Carbon — `lang.RegisterMessages` +
`lang.GetMessage`. Язык сообщения выбирается **по языку клиента игрока**
автоматически — ничего вручную переключать не надо.

**Языки проекта:** `en` (дефолт, фолбэк для всех), `ru`, `pl`, `uk`, `he`
(иврит). Коды ISO 639-1. ВАЖНО: иврит — RTL, перед релизом проверить
вживую, как его рисует чат/панель Rust (возможны приколы с порядком слов
и <color>-тегами).

**Каркас:**

```csharp
protected override void LoadDefaultMessages()
{
    // английский — ДЕФОЛТНЫЙ: RegisterMessages без кода языка
    lang.RegisterMessages(new Dictionary<string, string>
    {
        ["NotAllowed"]   = "<color=#E06666>You do not have permission.</color>",
        ["OnCooldown"]   = "You must wait <color=#FFD35C>{0}</color> seconds.",
        // ...
    }, this);                        // en

    lang.RegisterMessages(new Dictionary<string, string>
    {
        ["NotAllowed"]   = "<color=#E06666>Нет прав на эту команду.</color>",
        ["OnCooldown"]   = "Подожди <color=#FFD35C>{0}</color> сек.",
        // ...
    }, this, "ru");

    // дальше по аналогии: "pl", "uk", "he"
}
```

**Использование:**

```csharp
// хелпер с защитой от кривых плейсхолдеров
private string Lang(string key, string id = null, params object[] args)
{
    try { return string.Format(lang.GetMessage(key, this, id), args); }
    catch (Exception ex) { PrintError($"Lang '{key}' (uid {id}): {ex}"); throw; }
}

// отправка игроку — id игрока определяет язык
Player.Message(player, Lang("OnCooldown", player.UserIDString, secs),
               "<color=#E8B84B>◆ OctaGrade</color>", 0UL);
```

**Нюансы:**

- Если у игрока язык клиента, которого нет в плагине (напр. `de`) —
  получит английский. Поэтому английский словарь обязан быть полным.
- Если в словаре языка нет ключа — фолбэк тоже на английский (не на ru!).
- Плейсхолдеры `{0} {1}` — позиционные, порядок в string.Format тот же
  для всех языков; в RTL-иврите это может потребовать перестановки слов
  в строке, НО не плейсхолдеров.
- Carbon пишет локали в `carbon/lang/<код>/<Плагин>.json` — можно править
  текст без перекомпиляции (на сервере уже лежат en/es-ES/ru/uk-папки).
- `lang.GetLanguage(player.UserIDString)` вернёт код языка игрока —
  пригодится для выбора переводов имён предметов (RustTranslationAPI).
- Префикс `◆ OctaGrade` НЕ переводится — он фирменный для всех языков.

### Иврит (he) — RTL, особый случай

Rust рендерит текст строго LTR, bidi-алгоритма нет. Иврит в чате и панели
появится **в обратном порядке слов**, если его не подготовить.
Серверные плагины-фиксы существуют: `RightToLeft` (Wulf, Oxide) и
`Arabic Chat Shaper` (Carbon) — оба делают серверный пререндер.

**Что нужно ивриту:** только визуальный разворот RTL-пробегов
(контекстных форм букв как у арабского нет — shaping не нужен).
Диапазон символов: U+0590–U+05FF.

**Грабли:**

- `Array.Reverse(вся строка)` НЕЛЬЗЯ — сломает `<color>`-теги, числа,
  латинские вставки и имена эмодзи `:vip_x:`.
- Неизвестно, есть ли глифы иврита в чатовом шрифте клиента — как с
  ★-набором, проверять вживую (см. раздел 6). Если глифов нет — в чате
  будут `?`, и сервером это не лечится (только CUI-панель или
  транслитерация).
- Ввод игрока тоже надо разворачивать — наш OnPlayerChat в OctVIP уже
  эксклюзивно форматирует чат: обработку иврита встраивать ТУДА ЖЕ,
  а не отдельным плагином (иначе конфликт форматтеров, см. раздел 6).

**Хелпер разворота RTL-пробегов (для наших строк и чата игроков):**

```csharp
// разворачивает только ивритские пробеги; теги, числа, латиница —
// остаются в исходном порядке
private static bool IsHebrew(char c) => c >= '\u0590' && c <= '\u05ff'; // U+0590..U+05FF

private static string VisualRtl(string s)
{
    if (string.IsNullOrEmpty(s) || !s.Any(IsHebrew)) return s;
    var sb = new StringBuilder(s.Length);
    var run = new StringBuilder();
    void Flush()
    {
        for (var i = run.Length - 1; i >= 0; i--) sb.Append(run[i]);
        run.Clear();
    }
    foreach (var c in s)
    {
        if (IsHebrew(c) || c == ' ' && run.Length > 0) run.Append(c); // пробелы идут с ивритским пробегом
        else { Flush(); sb.Append(c); }
    }
    Flush();
    return sb.ToString();
}
```

Локализационные строки в lang-файле пишем в НОРМАЛЬНОМ (логическом)
порядке, разворот применяем при отправке: `if (lang.GetLanguage(uid) == "he") msg = VisualRtl(msg)`.

---

## 9. Общие правила плагинов Octavian

- Префикс чата — золотой: `<color=#E8B84B>◆ OctXXX</color>`, значения
  `#FFD35C`, шортнеймы серые.
- Никакого рутинного лог-спама (apply/skip на каждый чих) — только реальные
  ошибки и события загрузки.
- Новые Chaos-плагины: не забыть про пункт 1 — иначе первый деплой сломается.

---

## 10. `player.userID` — EncryptedValue<ulong>, ломает Plugin.Call

`BasePlayer.userID` в текущей сборке — `EncryptedValue<ulong>` (античит-
обёртка). В `Dictionary<ulong>` и арифметике неявный каст срабатывает, но в
`plugin.Call("Hook", player.userID)` объект боксится как EncryptedValue —
сигнатура `(ulong)` не совпадает → Call возвращает null/0 **молча**.

Правило: в Call всегда `(ulong)player.userID`. Проверяется пробником:
`OctaProbe_2` → `octaprobe2.vip` (консоль/RCON) или `/probe2` в чате.

## 11. PluginReference залипает после перезагрузки целевого плагина

`[PluginReference] Plugin X` может остаться на старой (выгруженной)
инстанции — `IsLoaded` ещё true, Call уходит в мёртвый объект → 0/null.
Надёжнее искать по имени каждый раз:

```csharp
foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
    if (p.Name == "OctVIP" && p.IsLoaded) { ... }
```

## 12. OctaProbe — полигон для проверки связок

Перед внедрением фичи в боевой плагин — сначала прогоняем её через пробник
на живом сервере (`carbon/plugins/OctaProbe*.cs`). Команды вызываются
через RCON: `octaprobe.vip`, `octaprobe2.vip` (WebSocket `ws://host:port/pass`,
JSON `{Identifier, Message, Type:3}`; читать ответ — `carbon/logs/Carbon.Core.log`).

---

## 13. OctVIP: запись в данных перекрывает octavip.*-пермишены

`ResolveLevel` сперва смотрит живую запись в `data/OctVIP/players.json`
(Level>0 после выдачи из панели/команды, `Level=0` после «Снять») — и только
если её нет/истекла, считает уровень по пермишенам `octavip.bronze..admin`.

Следствия:
- Админская выдача через панель всегда побеждает пермишены — можно
  понижать/снимать VIP у держателей octavip.*.
- «Снять» пишет `Level=0`-запись (не удаляет) — иначе пермишен «отращивал»
  уровень обратно. Нулевые записи не показываются в оффлайн-списке.
- Истёкшая запись удаляется таймером → пермишен снова вступает в силу.
- Тестовый шлак: если выдал себе все octavip.* «на всякий случай» — панель
  всё равно работает, но эффективный уровень после снятия записи вернётся
  к максимальному пермишену.

## 14. Связка OctaFeed → OctVIP (цвет ника в киллфиде)

`OctaFeed.GetColorFor` цепочка приоритетов:
1. `octafeed.*`-пермишен из `Cfg.UIConfig.Colors` (octafeed.vip и т.д.) —
   явный оверрайд.
2. Цвет уровня OctVIP через API `GetVipLevel`/`GetVipColor` — поиск плагина
   сканом `RootPluginManager` (см. п.11), в Call — `(ulong)player.userID`
   (см. п.10).
3. Дефолт из `UIConfig.NameProperty.Color`.

Если OctVIP выгружен/игрок без уровня — тихий откат на дефолт.

Шортнеймы NPC в фиде берутся из `OctaFeed.json` → «[1] Названия NPC»;
встреченные незамапленные префабы (типа `waterwell_shopkeeper`) докидываем
туда руками по мере находок — отображаемое имя чисто конфигурационное.

## 15. Carbon: пермишен-команды и деплой-нюансы

- **Пермишен-команды в Carbon — `c.*`**, а не `oxide.*`:
  `c.grant user <steamid> <perm>`, `c.revoke user <steamid> <perm>`.
  `oxide.grant` съедается молча — ответа нет, пермишен не выдаётся.
  Трачу время дважды — записано.
- `oxide.users.data` — **protobuf**, не JSON; читается только через сервер.
- Консольные команды плагина (chat console) через RCON идут без префикса:
  `backpack.debug.size <steamid>`, `octafeed show` и т.д.
- При обновлении плагина с апстрима — сверять ВЕРСИЮ с актуальной,
  иначе отладка старых API-ошибок уходит впустую (Backpacks 3.16.1
  держал устаревшие canAcceptItem/Pool.GetList/NetWrite/DecayTick;
  3.17.8 уже собрана под новый API — взять апстрим и ребрендить заново
  оказалось быстрее, чем патчить).
- **Пул в текущем Carbon:** `Facepunch.Pool.GetList<T>()` мёртв
  (CS0117). Рабочий API: `Pool.Get<List<T>>()` +
  `Pool.FreeUnmanaged(ref list)` (using `Facepunch`). См. OctaGrade.
- **Тип `Plugin` в хуках** (`OnPluginLoaded(Plugin p)` и т.п.) требует
  `using Oxide.Core.Plugins` — без него CS0246/CS0103.
- **`[ConsoleCommand]` без проверки — дыра.** Серверные консольные
  команды плагина доходят до хендлера из F1-консоли ЛЮБОГО игрока —
  auth не проверяется автоматически. Все админские команды обязаны
  начинаться с `if (!arg.IsAdmin) return;` (RCON/серверная консоль
  проходят: там `Connection == null` → `IsAdmin = true`).
  Инцидент: OctaAdminTools до 1.1.1 — 14 команд (`octa.god`, `octa.kill`,
  `octa.spawnat`…) были открыты всем. Чатовые `[ChatCommand]` — отдельная
  система, там проверять `player.IsAdmin`.

## 16. Деплой: NTFS-дубли файлов и «залипшая» версия

- NTFS может держать **два файла, отличающиеся только регистром/опечаткой**
  (`OctAmmo.cs` + `OctaAmmo.cs` рядом). Carbon грузит по имени файла —
  легко получить ситуацию «деплою новое, а работает старое».
- Симптом: `plugins` показывает старую версию после c.reload, хотя
  свежий файл точно лежит в папке. Лечение: `ls` всей папки плагинов
  и поиск дублей `find . -iname "*ammo*"` перед выводами о багах.
- Автозаполнение оружия в OctAmmo не проставляет Levels — для таких
  стволов ёмкость тира считается `BaseGame × TierMultipliers[tier]`
  (Senior 1.3x / Middle 1.6x / High 2.0x), иначе MP5 и прочие
  автозасеянные заряжали бы дефолтный магазин при любом VIP-уровне.

## 17. Стаки: порядок загрузки и снапшот ваниллы (OctaStack)

- **Carbon автозагружает .cs при дропе в plugins/.** Если плагин
  снимает снапшот `itemDefinition.stackable` как «ваниллу» — нельзя
  класть его файл, пока другой стак-патчер активен: снапшот снимет
  УЖЕ ПАТЧЕНЫЕ значения (wood=10000 станет «ваниллой» навсегда).
  Порядок миграции: `c.unload` старого (RevertOnUnload вернёт стаки) →
  перенос datafile/конфига → дроп нового .cs.
- **Стак-пресеты — абсолютные капы, не множители.** Множители по
  категориям/глобальный — только для ручного тюнинга: ванилла=1
  (оружие/тулзы/двери) обязана отсекаться ДО категорийных множителей,
  иначе `setall 5` стакает калаши по 5. Явные оверрайды по предмету
  выше правила — админ может стакать и оружие, если очень хочет.
- **`lowgradefuel` = ⅕ топлива вендор-вертолётов** — жирный стак
  топлива без фикса раздаёт тысячи на каждый купленный коптер.
- Команды covalence (`AddCovalenceCommand`) сами проверяют пермишен —
  `[ConsoleCommand]` без `arg.IsAdmin` при этом остаётся дырой (§15).

## 18. Nullable-аннотации под ref/out ломают кодеген Carbon

- **Баг компилятора Carbon**: `ref string?` / `ref T?` / `out string?` —
  `?` под byref-параметром ломает сгенерированный `InternalCallHook`.
  Симптомы — фантомные ошибки в «Internal» файлах:
  `Argument N must be passed with the 'ref' keyword [CS1620]` +
  `Use of unassigned local variable 'argN-1_0' [CS0165]`.
  Номера строк указывают не в исходник, а в сгенерированный прокси.
- **Диагноз**: Carbon проксирует КАЖДЫЙ метод класса-плагина
  (и nested-классов) в InternalCallHook — `ref`/`out` сами по себе ок,
  но `?` на типе параметра генератор переварить не может.
- **Лечение**: убрать `?` с ref/out-параметров (`ref T? x` → `ref T x`).
  Без `#nullable enable` аннотации всё равно мёртвая декорация.
  Альтернатива — вынести метод с ref-парами в отдельный top-level
  класс: генератор проксирует только члены класса-плагина.
- Воспроизведено на BetterLoot 4.5.2 (TGWA): патч из 5 строк
  `?`→удаление в CheckNull/ResolveImportedProfile/GetRandomProfile/
  CreateBonusItems/TryFillFromLootProfile — и плагин компилится.
- **Как дебажить «Internal» ошибки**: сводный пробник-репродьюсер
  с такой же сигнатурой в отдельном .cs — Carbon авто-подхватит и
  покажет те же Internal-ошибки, если гипотеза верна.

## 19. CUI: дубли имён элементов = сироты навсегда

- Клиент Rust хранит CUI-элементы словарём имя→элемент. `AddUi` с именем,
  которое уже есть на клиенте, НЕ заменяет старый элемент — он становится
  сиротой, а `DestroyUi(name)` сносит только последний зарегистрированный.
  Сироты живут до релога клиента, `DestroyUi` их не достаёт.
- **Правила**:
  - Перед каждой перерисовкой экрана — `CuiHelper.DestroyUi` по ВСЕМ именам
    верхнего уровня (корень + оверлеи: `_CFM`, `_PRM`, `_TST` и т.п.).
  - Элементы вне дерева (модалки, тосты) всегда именовать через
    `parentUi + "_СУФФИКС"` и сносить вместе с корнем — потерявший родителя
    элемент повисает на Overlay.
  - Одноразовые/анимированные элементы (числа урона и т.п.) — УНИКАЛЬНОЕ имя
    на каждый кадр/экземпляр (`seq++`), предыдущий шаг уничтожать явно.
    Пере-добавление того же имени НЕ «обновляет» элемент.
  - На Unload/disconnect чистить все известные имена, включая оверлеи.
- Укусились дважды: OctaLoadouts (слои окон) и OctaHits (залипшие цифры).

## 20. HitInfo.isHeadshot — вычисляемое свойство, не поле

`info.isHeadshot` НЕ хранится в HitInfo: геттер идёт через
`HitEntity.skeletonProperties.FindBone(HitBone).area == HitArea.Head`.
Если у жертвы нет `skeletonProperties` или кость не замаплена — всегда false.
Надёжно: `StringPool.Get(info.HitBone)` → имя кости, сравнивать с "head"
(сама игра так сравнивает "spine"/"spine2" в BasePlayer.OnAttacked).
Значения кости считать ДО NextTick — pooled HitInfo потом перезаписывается.

## 21. Newtonsoft: списки в конфиге раздуваются при каждой загрузке

`Config.ReadObject<T>()` НЕ заменяет инициализированный список — он
ДОБАВЛЯЕТ элементы из JSON в список из конструктора. Поле вида
`public List<X> promos { get; set; } = new List<X>{a,b,c}` после загрузки
файла с теми же 3 элементами даст 6, потом 9 и т.д.
Лечение: дедупликация после ReadObject (GroupBy/Distinct) в LoadConfig
или инициализировать списки только в CreateConfig, а не в свойствах.
Укусились: OctaHub homePromos/homeSlogans/eventProviders.
