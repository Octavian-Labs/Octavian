# Octa API v1 — контракты модульной экосистемы

Статус: спецификация (фаза 0). Документ описывает целевые контракты
между плагинами Octavian и инвентаризацию того, что есть сейчас.
Дата: 2026-10-10.

## 1. Принципы

- **Данные принадлежат модулю-владельцу.** Kits хранит выдачи и кулдауны,
  VIP — назначения статусов, Boss — бои и награды, Rel — списки
  друзей/врагов. Чужой файл данных — не API.
- **Один авторитетный обработчик действия.** `/kit`, собственное меню,
  карточка в Hub — все пути сходятся в `Kits_Redeem`. UI-состояние —
  подсказка; финальное решение принимает владелец в момент выполнения.
- **Уведомление ≠ приказ.** Хуки сообщают «состояние изменилось»,
  действия выполняются через вызовы.
- **Отсутствие провайдера ≠ разрешение.** Нет Economics — платный кит
  недоступен, а не бесплатен. Нет OctVIP — VIP-бонус не применяется.
- **Порядок загрузки не важен.** Любой плагин может подняться первым;
  зависимости разрешаются живым поиском (`RootPluginManager`), а не
  залипающим `PluginReference` (см. PITFALLS §11).
- **Независимая работа.** Каждый игровой модуль работает без OctaHub
  и без OctaUI (резервный режим: собственное простое CUI или команды).

## 2. Конверт ответа

Все межплагинные контракты данных/действий возвращают `JObject`:

```json
{
  "ok": true,
  "code": "OK",
  "msg": "человекочитаемое сообщение для игрока (локализовано)",
  "data": { ... },
  "rev": 1
}
```

- `ok` — bool, успех/отказ.
- `code` — стабильная строка для машинной обработки (таблица ниже).
- `msg` — текст для пользователя; потребитель показывает как есть.
- `data` — полезная нагрузка, только JSON-совместимые типы
  (`JObject`/`JArray`/числа/строки/bool). Не отдавать внутренние
  классы (`BossState`, `Kit`, списки наград) и не-JSON типы
  (`Vector3` → `{"x":..,"y":..,"z":..}` или `[x,y,z]`).
- `rev` — ревизия данных (необязательно; растёт при изменении
  snapshot — потребитель может кэшировать).

**Исключение:** OctaUI — библиотека рисования (Panel/Text/Button/
Stepper/...), её вызовы не являются контрактами данных и в конверт
не заворачиваются. Внешние плагины (ImageLibrary, Economics,
ServerRewards) оборачиваются внутри модуля-владельца — наружу
торчит только конверт.

### Коды

| code | Смысл |
|---|---|
| `OK` | успех |
| `NOT_FOUND` | объект/кит/босс не найден |
| `NO_PERMISSION` | нет права/vip-benefit |
| `COOLDOWN` | кулдаун активен (`data.cooldownLeft` — сек) |
| `LIMIT_REACHED` | исчерпаны использования (`data.usesLeft`) |
| `NO_SPACE` | не хватает места в инвентаре |
| `INSUFFICIENT_FUNDS` | не хватает валюты (`data.balance`, `data.price`) |
| `PROVIDER_UNAVAILABLE` | нужный плагин не загружен |
| `CONFLICT` | операция уже выполняется/выполнена (requestId) |
| `INVALID_ARGUMENT` | неверные параметры вызова |
| `UNAVAILABLE` | временно недоступно (reload, цель вышла и т.п.) |

**Нельзя** маскировать неизвестное под ноль/успех: неизвестный баланс
— не `0`; недоступный статус события — не «событий нет»; сбой проверки
кулдауна — не «можно забрать».

## 3. Инвентаризация: что есть сейчас

### OctaKits (владелец: каталог наборов, выдачи, кулдауны)

Текущие вызовы (сырые типы, без конверта):

| Метод | Возврат | Кто зовёт |
|---|---|---|
| `GetAllKits()` | `string[]` | Hub |
| `GetKitObject(name)` | `JObject` (весь кит) | Hub |
| `GetKitImage/Description/MaxUses/Cooldown` | примитивы | Hub |
| `GetPlayerKitUses(uid, name)` | int | Hub |
| `GetPlayerKitCooldown(uid, name)` | double | Hub |
| `HasKitPermission(player, perm)` | bool | Hub |
| `GetCurrencyLabel()` | string | Hub |
| `TryClaimKit(player, name, usingUI)` | bool | Hub (единый путь выдачи ✓) |
| `GetKitContents(name)` | `string[]` | Hub |
| `GetKitInfo(name)` | object | совместимость |

Команды: `/kit` (собственное меню — резервный режим есть).

### OctaBoss (владелец: боссы, бои, награды)

| Метод | Возврат | Кто зовёт |
|---|---|---|
| `GetBossName(ent)` | string/null | OctaFeed |
| `GetBossStates()` | `JArray` (key,name,image,alive,hp,nextSpawnUnix,grid,phase…) | Hub |

Команды: `/boss_admin`, админ-панель (своя CUI).
Хуки жизненного цикла наружу не публикуются — Hub поллит snapshot.

### OctVIP (владелец: статусы и сроки)

| Метод | Возврат | Кто зовёт |
|---|---|---|
| `GetVipLevel(uid)` | int | Hub |
| `GetVipLevelName(uid)` | string | Hub |
| `GetVipColor(uid)` | string | Hub |
| `HasVipLevel(uid, lvl)` | bool | модули |
| `GetVipExpiry(uid)` | long | Hub |
| `API_SetLevel(uid, lvl, duration)` | bool | админка |
| `Vip_GetPlayerStatus(uid)` | конверт `{level,name,color,expiresAt,benefits[]}` | Hub |
| `Vip_GetCatalog()` | конверт `{tiers[]}` | витрины |
| `Vip_HasBenefit(uid, id)` | конверт `{has}` | OctaKits (`octaben.*`) |
| `Vip_GetBenefitValue(uid, id)` | конверт `{value}` / `NOT_FOUND` | модули |

Назначения — level-модель; поверх неё реализован benefit-слой (фаза 4).

### OctaFeed (владелец: киллфид, настройки ленты)

| Метод | Возврат | Кто зовёт |
|---|---|---|
| `API_GetSettings(uid)` | `JObject` | Hub |
| `API_SetSetting(uid, key, bool)` | bool | Hub |

### OctaRel (владелец: друзья/враги, радар, ESP-пинг)

| Метод | Возврат | Кто зовёт |
|---|---|---|
| `GetRelations(uid)` | `JObject` {friends[], enemies[]} | Hub |
| `GetNotify(uid)` / `SetNotify(uid, v)` | bool | Hub |
| `AddRelation`/`RemoveRelation(uid, type, target)` | — | Hub |
| `GetPingInfo()` | `JObject` {rp, coins, sec, rpOn, coinsOn, testOn} | Hub |
| `ScanEnemy(payer, uid, currency)` | string-ошибка/null | Hub |

### OctaWC / OctaTP / прочие провайдеры для Hub

| Метод | Возврат | Кто зовёт |
|---|---|---|
| `OctaWC.API_GetWipeInfo()` | `JObject` | Hub |
| `OctaWC.GetCountdownFormated_API()` | string | Hub (legacy) |
| `OctaTP.API_GetHomes(player)` | `Dictionary<string,Vector3>` ⚠ не JSON | Hub |

## 4. Целевые контракты v1

Имена методов — `Модуль_Действие`. Все — конверт §2.
Старые методы остаются (deprecated) до полной миграции потребителей.

### OctaKits

```
Kits_GetCatalog(player)
  data: { kits: [ {id, name, desc, image, cost, currency, currencyLabel,
                   cooldown, maxUses, perm, hidden,
                   avail: {can, code, cooldownLeft?, usesLeft?, balance?}} ],
          currency, currencyLabel }
  // avail — снапшот-подсказка для отрисовки кнопки; финальное решение
  // всегда принимает Kits_Redeem в момент выполнения

Kits_GetKit(player, kitId)
  data: { id, name, desc, image, cost, currency, currencyLabel, cooldown,
          maxUses, perm, requiredAuth, copyPaste,
          main/belt/wear: [ {Shortname, SkinID, Amount, ...} ],
          avail: {...} }

Kits_GetAvailability(player, kitId)
  data: { canClaim, reason(code), cooldownLeft, usesLeft,
          balance?, price, currency }

Kits_Redeem(player, kitId, requestId)
  ok=false → code ∈ {NOT_FOUND, NO_PERMISSION, COOLDOWN,
                     LIMIT_REACHED, INSUFFICIENT_FUNDS,
                     PROVIDER_UNAVAILABLE, CONFLICT}
  ok=true  → data: {kitId, granted: [items]}
```

`requestId` — строка-идемпотентность: повтор того же requestId
возвращает результат первой операции, не выдаёт второй набор.
Плюс анти-даблклик: повторное получение одного набора тем же игроком
чаще раза в секунду отклоняется с `CONFLICT`.
Оплата и выдача не атомарны между плагинами — при сбое выдачи после
списания выполняется возврат средств (RefundPlayer) и ответ
`UNAVAILABLE`; «транзакции» между плагинами не имитируются.
`TryClaimKit` (legacy) проксирует тот же обработчик — единый путь
выдачи сохраняется для /kit, собственного меню и Hub.

### OctaBoss (реализовано, v1.8.0)

```
Boss_GetCatalog()            → виды боссов (для витрины и лор-страниц)
  data: { bosses: [ {key, name, enabled, image, accent, health,
                     intervalMinutes, warnMinutes, escortCount,
                     nativeBiome, nativeBiomes, monuments, needsWater,
                     rewardCurrency, rewardParticipation} ] }

Boss_GetActiveInstances()    → живые бои + расписание
  data: { now,
          bosses:    [ BossRow* ],   // слитный вид: одна строка на вид
          instances: [ BossRow* ],   // только живые (status=active)
          schedule:  [ BossRow* ] }  // запланированные + выключенные
  *BossRow: {key, name, enabled, status: active|scheduled|disabled,
             alive, nextSpawnUnix, warnMinutes, image, accent,
             instanceId, spawnedUnix, hp, hpMax, phase, escortsAlive,
             grid}  // grid — только если маркер публичный;
             // hp/phase осознанно публичны: прогресс-бар босса —
             // геймплейная фича витрины (как у патрульного вертолёта)

Boss_GetInstance(id, admin)  → BossRow по key или instanceId;
  // admin = BasePlayer с octaboss.admin или true (доверенный плагин):
  // +pos:[x,y,z], raging, damageBy{uid: dmg}
  // без admin — только публичный BossRow | NOT_FOUND

Boss_ResolveEntity(entity)   → идентификация для киллфида/панелей
  data: {role: boss|escort, key, name, instanceId, alive, retained,
         bossName?}  // retained=true — имя из посмертного реестра
                     // (~2с босс / ~60с свита после смерти)
  | NOT_FOUND
```

Хуки (CallHook по подписчикам, аргументы — JObject):

```
OnOctaBossStarted   {instanceId, key, name, restored: bool,
                     spawnedUnix, escorts, grid?}
OnOctaBossPhase     {instanceId, key, name, phase, hp, hpMax}
OnOctaBossEnded     {instanceId, key, name, reason, rewardsGranted,
                     winner, winnerId, rewardOpId}
  reason ∈ playerKill | technicalDeath | adminKill | timeout | despawn
  instanceId = "<key>:<spawnedUnix>" — после reload выдаётся новый
  restored=true — бой восстановлен после reload, не «только начался»
  rewardOpId = instanceId — идемпотентность наград при повторной доставке
```

`GetBossStates`/`GetBossName`/`IsBoss` — deprecated-алиасы, форма данных
та же (строки BossRow). OctaFeed ходит через `Boss_ResolveEntity`,
OctaHub — через `Boss_GetActiveInstances` + `Boss_GetCatalog` (лор),
с фолбэками на legacy для старых версий OctaBoss.

### OctVIP (реализовано, фаза 4, v1.5.0)

```
Vip_GetCatalog()          → data:{tiers:[{level,name,color,icon,
                            benefits:[{id,name,desc,value,stackable}]}]}
Vip_GetPlayerStatus(uid)  → data:{level,name,color,expiresAt,benefits[id...]}
Vip_HasBenefit(uid, id)   → data:{has:bool}
Vip_GetBenefitValue(uid,id)→ data:{value} | INVALID_ARGUMENT / NOT_FOUND
```

Бенефиты — декларативный слой поверх уровней: конфиг `Benefits`
(id → name/desc/value/stackable) + `LevelBenefits` (уровень 1-6 → список id,
**кумулятивно**: gold получает id всех уровней ≤3). Назначения/сроки VIP
по-прежнему level-моделью — `Vip_HasBenefit` работает до редизайна
назначений (grantId/source/сроки).

Дефолтный маппинг: `vip`, `tag.icon`, `kit.vip`, `queue.priority`,
`welcome.clan` с Bronze; `welcome.clanonline` с Silver; `welcome.bases`,
`join.announce` с Gold. Внутренние пороги `*MinLevel` приветствия переведены
на `BenefitOrFallback`: id упомянут в LevelBenefits → маппинг авторитетен,
нет → старый числовой порог.

Числовые бонусы: `Vip_GetBenefitValue` отдаёт `value` из дефиниции;
`NOT_FOUND` — id не в каталоге или недоступен на уровне. Поле `stackable`
зарезервировано под мультиназначения; сейчас все бенефиты флаговые.

Потребительский протокол: `octaben.<id>` в `RequiredPermission` набора
OctaKits → `Vip_HasBenefit` (OctVIP отсутствует → deny, не grant).

### OctaRel (реализовано, v1.1.0)

```
Rel_GetRelations(uid)            → data: {friends[], enemies[], notify}
Rel_GetPingInfo()                → data: {rp, coins, sec, rpOn, coinsOn, testOn}
Rel_GetNotify(uid) / Rel_SetNotify(uid, v) → data: {value}
Rel_Add / Rel_Remove(uid,type,target) → ok | INVALID_ARGUMENT /
                                   CONFLICT / LIMIT_REACHED / NOT_FOUND
Rel_ScanEnemy(payer, uid, cur)   → ok {ping:true} | NO_PERMISSION /
                                   UNAVAILABLE / CONFLICT /
                                   INSUFFICIENT_FUNDS / PROVIDER_UNAVAILABLE
```

Внутри — единые обработчики (code,msg): legacy `ScanEnemy`/`AddRelation`
отдают текст ошибки, конвертные `Rel_*` — коды из таблицы §2.
Строже legacy: неизвестный `type` → `INVALID_ARGUMENT` (было «в друзья»).

### OctaFeed / OctaWC / OctaTP (реализовано, фаза 3)

```
Feed_GetSettings(uid)            → data: {hideMessages, hideMyName}
Feed_SetSetting(uid,key,v)       → data: {value} | INVALID_ARGUMENT
  // key ∈ hideMessages | hideMyName

WC_GetInfo()                     → data: {nextUnix, secondsLeft, mode,
                                   saveCreatedUnix, formatted}
  // legacy: GetWipeInfo, GetCountdownFormated_API

TP_GetHomes(player)              → data: {homes:[{name, pos:[x,y,z]}]}
  // legacy: API_GetHomes (Dictionary<string,Vector3> — не JSON)

Vip_GetPlayerStatus(uid)         → data: {level, name, color, expiresAt,
                                   benefits[]}
  // legacy: GetVipLevel/GetVipLevelName/GetVipColor/GetVipExpiry —
  // один вызов вместо четырёх; benefit-слой — реализован (см. выше)
```

### Реестр провайдеров событий (реализовано, фаза 5: OctaHub v2.2.0, OctaBoss v1.9.0)

Провайдер событий — любой плагин с API:

```
Events_GetSnapshot(player) → data: {categories: [
  { id, title, image, active, status, tone, detail, items[] }
]}
```

Поля категории:

| Поле | Смысл |
|---|---|
| `id` | стабильный id категории ("bosses") |
| `title` | заголовок карточки |
| `image` | PNG-хендл ImageLibrary или http; пусто → Hub подставит `tiles/<id>.png` по соглашению |
| `active` | bool — акцентная кайма карточки |
| `status` | строка статуса без разметки ("ЖИВЫХ СЕЙЧАС: 2") |
| `tone` | `accent`/`red`/`muted` — Hub обернёт status в цвет; `raw` — status уже html |
| `detail` | ключ детал-вида для `octahub_evt`: `"bosses"` — встроенная сетка боссов; иные встроенные — `"we:<cat>"`; **пусто** → генерический `pv:<plugin>:<catId>` по `items[]` |
| `items` | строки генерического детала: `{name,status,tone,active,image,hp,hpMax,grid,cmd}`; `cmd` — необязательный ключ деталя item'а |

Хук изменения (провайдер → broadcast):

```
OnOctaEventChanged(pluginName, categoryId)
```

Hub перечитывает снапшоты у всех с открытым разделом СОБЫТИЯ —
мгновенный рефреш вместо ожидания 20-сек сканера. OctaBoss эмитит его
внутри `EmitBossHook` на каждый lifecycle-хук.

Реестр — конфиг Hub `generalSettings.eventProviders` (имена плагинов,
порядок = порядок карточек). Провайдер недоступен → карточка-заглушка
«модуль недоступен» (правило «нет провайдера ≠ пусто»). Провайдер без
контракта → legacy-мост только для OctaBoss (карточка из GetBossStates).
Новый провайдер подключается добавлением имени в конфиг — без правок Hub.

### OctaHub

Не является провайдером данных. Хранит только настройки интерфейса
(раскладка, выбранная вкладка). Потребляет контракты выше;
рисует встроенный контент сам (data-driven, не render-embedded).

## 5. Правила жизненного цикла

- **Reload плагина-источника**: потребитель показывает «источник
  недоступен» (`PROVIDER_UNAVAILABLE`), а не «событий нет».
- **Восстановление**: при поднятии источника потребитель заново
  берёт snapshot (`*_GetCatalog`/`GetActiveInstances`), а потом
  обрабатывает хуки. Порядок: snapshot → события.
- **Резолв ссылок**: живой поиск плагина по имени на каждый вызов
  либо перепривязка в `OnPluginLoaded/OnPluginUnloaded`.
- **Идентификация игроков**: «в сети» = `activePlayerList`;
  спящие — только через `FindSleeping`/sleepingPlayerList и только
  если контракт явно это разрешает (OctaRel test-mode).

## 6. Политика видимости полей

Публично (игроку): имя, картинка, описание, статус, время, сетка/
биом, публичные награды, дистанция.

Только админам: точные координаты, HP и фаза в реальном времени,
список дамагеров, текущая цель AI, служебные цены, скрытые киты.

Платный ESP-пинг (OctaRel) — исключение по позиции: даёт направление
на личного врага за валюту, с кулдауном и короткой длительностью.
Это геймплейная фича, не дыра в видимости.

## 7. План миграции

| Фаза | Содержание | Проверка |
|---|---|---|
| 1 | OctaKits: новые `Kits_*` + конверт; Hub вкладка Наборы → новые API | кит берётся из Hub и из /kit одинаково; requestId не дублирует выдачу |
| 2 | OctaBoss: `Boss_*` + хуки; OctaFeed → `Boss_ResolveEntity`; Hub → snapshot | киллфид отличает босса от свиты; reload не ломает карточки |
| 3 | Конверт для OctaRel/OctaFeed/OctaWC/OctVIP-геттеров + TP_GetHomes (JSON) | Hub работает целиком на конвертах |
| 4 | ✅ OctVIP: level→benefits, `Vip_HasBenefit` + `octaben.<id>` в Kits | Kits проверяет benefit, а не level |
| 5 | ✅ Реестр провайдеров событий в Hub (`Events_GetSnapshot` + `OnOctaEventChanged`) | второй провайдер (airstrike/ваниль) подключается без правок Hub |
