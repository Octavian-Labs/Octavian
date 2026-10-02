# OctaPack — гайд

Ребренд **Backpacks** (WheteThunger 3.17.8) → `OctaPack v1.0.0` от Octavian.
Личный рюкзак игрока: переносимый инвентарь с вкладками, сбором лута,
дропом при смерти и интеграцией с **OctVIP** (размер по VIP-уровню).

Файлы на сервере:

- Плагин: `carbon/plugins/OctaPack.cs`
- Конфиг: `carbon/configs/OctaPack.json`
- Данные: `carbon/data/OctaPack/<steamid>.json`

---

## Команды

### Игрок

| Команда | Что делает | Пермишен |
|---|---|---|
| `/backpack` | открыть свой рюкзак | `octapack.use` |
| `backpack.next` / `backpack.prev` | листать страницы (консоль, для биндов) | — |
| `backpackgui` | показать/скрыть GUI-кнопку рюкзака | `octapack.gui` |
| `backpack.fetch` | вернуть выброшенный рюкзак себе | `octapack.fetch` |
| `backpack.setgathermode <режим> [стр]` | режим автосбора для страницы | `octapack.gather` |

Режимы сбора (`<режим>`): `All` (всё), `Existing` (только стеки, которые уже есть),
или фильтры категорий — список выводится при вызове без аргумента.

Кнопки в интерфейсе рюкзака (`backpack.ui.togglegather` / `backpack.ui.toggleretrieve`)
переключают сбор и автовозврат лута прямо из UI.

### Админ (консоль/RCON)

| Команда | Что делает | Пермишен |
|---|---|---|
| `viewbackpack <steamid>` | открыть чужой рюкзак | `octapack.admin.view` (+`.edit` для изменений) |
| `backpack.addsize <steamid> <слотов>` | +N слотов игроку | `octapack.admin.resize` |
| `backpack.setsize <steamid> <слотов>` | задать точный размер | `octapack.admin.resize` |
| `backpack.erase <steamid>` | стереть рюкзак игрока | `octapack.admin` |
| `backpack.wipeall` | стереть ВСЕ рюкзаки | `octapack.admin` |
| `backpack.debug.size <steamid>` | диагностика размера (VIP/пермишены) | `octapack.admin.debug` |
| `backpack.debug.gather` | диагностика автосбора | `octapack.admin.debug` |

> **Carbon-нюанс:** выдача пермишенов — `c.grant user <steamid> <perm>`,
> а не `oxide.grant` (тот молча не работает). См. PITFALLS §15.

## Пермишены

| Пермишен | Назначение |
|---|---|
| `octapack.use` | базовый доступ к рюкзаку (выдавать игрокам) |
| `octapack.gui` | право на GUI-кнопку |
| `octapack.fetch` | возврат выброшенного рюкзака |
| `octapack.gather` | автосбор лута в рюкзак |
| `octapack.retrieve` | автовозврат лута из контейнеров |
| `octapack.size.N` | размер N слотов (по массиву `Permission sizes`) |
| `octapack.keepondeath` | рюкзак не выпадает при смерти |
| `octapack.nofoodspoiling` | еда в рюкзаке не портится |
| `octapack.admin*` | админские (см. таблицу команд выше) |
| `octapack.keeponwipe`, `octapack.noblacklist` | legacy-пермишены совместимости |

## Конфиг `OctaPack.json`

### `"Backpack size"` — размеры

```json
{
  "Default size": 6,
  "Max size per page": 48,
  "Permission sizes": [6, 12, 18, 24, 30, 36, 42, 48, 96, 144],
  "OctVIP level sizes": { "1": 12, "2": 18, "3": 24, "4": 30, "5": 36, "6": 42 },
  "Dynamic Size (EXPERIMENTAL)": { "Enabled": false }
}
```

Приоритет размера: **`octapack.size.N` пермишен vs OctVIP — берётся max()**,
ниже него не проседает. `octapack.size.48` → пермишен `octapack.size.48`.
`Max size per page` — потолок одной страницы (48 = 8 рядов), большие размеры
распределяются по страницам.

### `"OctVIP level sizes"` — связка с OctVIP

Уровень OctVIP → размер в слотах. 1=Bronze … 6=Admin.
Смена уровня применяется мгновенно (хук `OnVipLevelChanged`),
сбрасывать ничего не надо.

### Смерть и вайп

| Ключ | Значение по умолчанию | Что даёт |
|---|---|---|
| `"Drop on Death (true/false)"` | `true` | рюкзак выпадает контейнером, лутабельно |
| `"Erase on Death (true/false)"` | `false` | если `true` — содержимое просто уничтожается |
| `"Minimum Despawn Time (Seconds)"` | `300` | мин. время жизни выпавшего рюкзака |
| `"Clear on wipe" → "Enabled"` | `false` | при `true` — чистить рюкзаки на вайпе |
| `"Clear on wipe" → "Max slots to keep"` | — | сколько первых слотов сохранить |

`"Clear on wipe"` поддерживает `Rulesets by permission` — VIP можно
оставить рюкзак целиком, обычным — обнулить.

### `"Item restrictions"` — запрет предметов

```json
{
  "Enabled": false,
  "Feedback effect": "assets/.../lock.code.denied.prefab",
  "Default ruleset": { ... },
  "Rulesets by permission": [ ... ]
}
```

Позволяет запретить класть в рюкзак отдельные предметы (взрывчатку, квестовые).
По пермишенам — свои правила для каждой группы.

### `"GUI Button"` — кнопка в инвентаре

`Enabled`, `Enabled by default`, `Skin Id`, `Image`, `Background Color`,
`GUI Button Position` (anchors/offsets — положение кнопки на экране инвентаря).

### `"Softcore"` → `"Reclaim Fraction"` — доля лута после смерти в softcore-режиме.

---

## Типовые сценарии

### 1. «Рюкзак всем, VIP — больше» (текущий сетап)

```
c.grant group default octapack.use octapack.gui
```

`"Default size": 6` + `"OctVIP level sizes"` — обычный игрок получает 6 слотов,
Bronze 12 … Admin 42. Пермишенов на размер не нужно вообще.

### 2. Размер отдельной группе вне VIP

```
c.grant group farmers octapack.size.30
```

Пермишен `octapack.size.30` ↔ значение из `Permission sizes`. Если у игрока
ещё и VIP — возьмётся больший из двух.

### 3. ПвЕ-сервер: рюкзак не дропается

```json
"Drop on Death (true/false)": false
```

Или точечно: `c.grant user <id> octapack.keepondeath` — у игрока рюкзак
не выпадет, у остальных — по конфигу.

### 4. Фарм-бот для VIP

```
c.grant group vip octapack.gather octapack.retrieve
```

`/backpack.setgathermode All` — весь лут с добычи улетает в рюкзак;
`Existing` — только то, что уже есть (не захламляет).

### 5. Рейд-сервер: запретить прятать взрывчатку

`"Item restrictions" → "Enabled": true`, в `Default ruleset` добавить
шортнеймы (`explosive.timed`, `ammo.rpg` и т.д.).

### 6. Вайп с сохранением VIP-рюкзаков

`"Clear on wipe" → "Enabled": true`; в `Rulesets by permission` VIP-пермишену —
`AllowAll`, дефолту — `Max slots to keep: 6` или полная очистка.

### 7. Диагностика «у игрока не тот размер»

```
backpack.debug.size 76561198153210224
```

Покажет expected/actual размер, источник (VIP-уровень или пермишен)
и список size-пермишенов игрока — как у нас было с фидом, сразу видно,
откуда взялось значение.

---

## Известные нюансы

- **Размер считается `max(пермишен, VIP)`** — пермишеном нельзя сделать
  рюкзак МЕНЬШЕ, чем даёт VIP-уровень. Только больше.
- `viewbackpack` открывает чужой рюкзак в контейнерном UI — правки требуют
  `octapack.admin.edit`, иначе только просмотр.
- Выпавший при смерти рюкзак — реальный контейнер в мире (`item_drop_backpack`),
  лутается любым, живёт минимум `Minimum Despawn Time`.
- Данные — на каждого игрока свой JSON в `carbon/data/OctaPack/`;
  стереть рюкзак извне = удалить файл или `backpack.erase`.
- Свежая версия ребренжена с апстрима **3.17.8** — при апдейтах игры
  сначала сверяться с актуальным Backpacks, а не патчить старый код.
