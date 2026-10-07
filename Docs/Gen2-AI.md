# Rust Gen2 AI — внутренности для разработки плагинов

Декомпилированные исходники `Rust.Ai.Gen2.*` из локальной сборки сервера
лежат в `/_decomp_out/` (278 типов, build 423). Перечитаются после вайпа:

```powershell
pwsh -File _decomp.ps1 -Pattern "^Rust\.Ai\.Gen2\." -Dump
pwsh -File _decomp.ps1 -Pattern "Hostile"          # только список типов
```

`-Pattern` — regex по полному имени типа в `Assembly-CSharp.dll` тестового
сервера (`423\server\RustDedicated_Data\Managed`).

## Архитектура

- **Мозг** = `FSMComponent` (по одному на вид: `BasicAnimalFsm`,
  `TigerFSM`, `Wolf2FSM`, `CrocodileFSM`, `LivestockAnimalFSM`, …).
  Граф строится в `InitShared()` при спавне сущности.
- **Узлы графа** — `FSMStateBase`: публичные `transitions`,
  `endTransitions` (туда же идёт `AddFailureTransition`), `parent`,
  `Name`. Анонимные `State_Nothing` — структурные узлы-ветки.
- **Переходы** — `FSMTransitionBase`: конкретные `Trans_*` условия,
  композиты `Trans_And`/`Trans_Or`, инвертор `~`, триггеры
  `Trans_Triggerable` (событийные: hurt, death, encounter-end, fire-melee).
- **Переключение** — публичный `FSMComponent.SetState(state, payload)`
  корректно прогоняет `OnStateExit`/`OnStateEnter` по всей иерархии.
  `CurrentState` — только чтение. Переходы исполняются по тику.
- `FSMComponent` владельца = `GetComponent<FSMComponent>()` на сущности.

## Сенсоры и цель (`SenseComponent`)

- `Target` — текущая цель; **`TrySetTarget(entity, bypassCooldown=true)`**
  — реальная установка цели (то, что делает игра в `OnGrabbedBy`).
  `MarkHostileFor`/`Provoke` цель не назначают.
- `ClearTarget(forget)`, `Forget(entity)`, `NoteAttacker` (запомнить
  стрелка), `FindTarget`/`FindTargetPosition`/`FindTargetStatus`.
- `StanceToward(entity)` → `NPCStance` (`Attack`/`Fear`/`Ignore`) —
  читается из `NPCTeam` (ScriptableObject: `players`, `attacks`, `fears`,
  `attackers` — реакция на того, кто нас бил).
- Видимость: `VisibilityStatus`, `Trans_IsWatchedByTarget` (кошки:
  цель прицелилась >0.5с издалека → прячутся), `CanTarget`.

## Бегство — реальные состояния, НЕ blackboard

`State_Flee` и наследники: `State_FleeToHide` (кошки), `State_FleeFire`,
`State_HerdFlee` (скотина). У крокодила `State_GoBackToWater` —
`State_MoveToTarget`, засадный цикл, НЕ паника.

Входы в flee по видам (подтверждено по `*_FSM.cs`):

- **BasicAnimalFsm** (медведь/кабан): страх цели, HP <
  `fleeBelowHealthFraction` (приватное сериализуемое поле, 0.3),
  энкаунтер-таймер, сейф-зона, вода, `~CanReachTarget`.
- **TigerFSM** (тигр/пантера): `attack→flee` и `hurt→flee` —
  ударил-убежал это дизайн; «цель смотрит издалека» → `flee`;
  недосягаемость/огонь → `permaFlee`.
- **Wolf2FSM**: `hurt→flee` (HP, огонь, цель >50м), `hurt→fleeForHowl`
  когда стая не дерётся (без стаи — почти каждый удар), недосягаемость.
- **LivestockAnimalFSM** (бык): `hurt/turnAndRun/kickBehind/getUp→flee`
  по `Trans_WouldRunFromAHit`, спуг стада (`HerdFlee`).
- **CrocodileFSM**: вся «углублённая» логика через `GoBackToWater` +
  `diveRoam` (засада из воды) — боевой цикл, а не бег.

`BlackboardComponent` — временные факты (30с TTL): `HitDuringCharge`,
`HitByFire`, `WolfNearbyAlreadyHowled` — это детали переходов,
не переключатели поведения.

`NPCEncounterTimer` — give-up таймер (120с; x12 при цели на маунте,
x4 у огня) → `onShouldGiveUp` → переход в flee у многих видов.

## Как этим пользоваться (см. OctaBoss `TameFlee`)

1. Граф доступен и **изменяем на экземпляре** — `state.transitions[i]`
   перезаписывается (кортеж `(transition, dstState)`), без Harmony.
2. Перенаправление переходов в flee на боевое/roam-состояние — рабочий
   способ отключить боевую панику у конкретной сущности.
3. `SetState` — штатный способ вывести из нежелательного состояния
   (не перезаписывать `CurrentState` вручную — пропустятся exit/enter).
4. Цель ставить `SenseComponent.TrySetTarget`, агрессию дополнить
   `MarkHostileFor` / у скотины `Provoke`+`RememberAggressor`
   (приватные — видны через Carbon-паблицер).
5. Поля-конфиги FSM/переходов сериализуются на экземпляре —
   `fleeBelowHealthFraction`, `giveUpDurationSeconds` и т.п. можно крутить
   рефлексией на конкретной сущности, не трогая префаб.

## Осторожно

- Не менять общие объекты переходов/команды — они общие между экземплярами
  только для `Trans_*` условий? Нет: каждый `InitShared` создаёт граф
  заново на свой экземпляр — но поля-объекты `State_*` это instance-поля,
  безопасно. А вот статические конвары (`AI.*`, `Livestock.*`) — глобальны.
- `SetState` >3 раз за тик откладывается — не спамить.
- Скорость: `RustNavMeshAgent` имеет `speed` + пороги sneak/walk/jog/run/
  sprint/fullSprint — менять все (см. `ApplySpeed` в OctaBoss).
