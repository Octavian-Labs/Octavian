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
  проверенного набора, в CUI можно шире.

---

## 7. Кодировки — НЕ редактировать файлы через PowerShell-строки

`Get-Content -Raw` в PowerShell 5.1 читает UTF-8 без BOM как Windows-1251 →
`-replace | Set-Content -Encoding utf8` записывает кракозябры (инцидент:
OctVIP.cs, исправлено обратным перекодированием + BOM). Для sed-подобных
замен использовать `edit`-инструмент агента или `[IO.File]::ReadAllText/WriteAllText`
с явным `[Text.Encoding]::UTF8`. Файлы с кириллицей держать в UTF-8 **с BOM**.

---

## 8. Общие правила плагинов Octavian

- Префикс чата — золотой: `<color=#E8B84B>◆ OctXXX</color>`, значения
  `#FFD35C`, шортнеймы серые.
- Никакого рутинного лог-спама (apply/skip на каждый чих) — только реальные
  ошибки и события загрузки.
- Новые Chaos-плагины: не забыть про пункт 1 — иначе первый деплой сломается.
