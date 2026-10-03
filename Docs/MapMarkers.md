# Маркеры на карте конкретного игрока (MapNote)

Сниппет-памятка: как подмешать точки на карту только для одного игрока
(например, админа), без сущностей в мире.

## Механизм

Клиент при открытии карты запрашивает маркеры (`BasePlayer.Server_RequestMarkers`
→ `SendMarkersToClient`). Хук `OnPlayerMarkersSend` отдаёт список
`ProtoBuf.MapNoteList` конкретному получателю — мутируем его:

```csharp
private readonly HashSet<ulong> _mapViewers = new HashSet<ulong>();

// ProtoBuf.MapNote (Rust.Data.dll): noteType, worldPosition (VectorData,
// implicit к/от Vector3), icon, colourIndex, label, isPing,
// timeRemaining, totalDuration, associatedId
// MapNoteList.notes — List<MapNote>

private void OnPlayerMarkersSend(BasePlayer player, ProtoBuf.MapNoteList list)
{
    if (list?.notes == null) return;
    if (!_mapViewers.Contains((ulong)player.userID)) return;

    var note = Pool.Get<ProtoBuf.MapNote>();
    note.ShouldPool = false;          // ручной прото — пулу не отдавать
    note.worldPosition = pos;         // Vector3 конвертируется implicit
    note.icon = 7;                    // sleeping bag
    note.colourIndex = 2;             // 0 жёлт 1 син 2 зел 3 крас 4 роз 5 циан
    note.label = "";
    list.notes.Add(note);
}
```

Принудительная отправка: `player.SendMarkersToClient()`.

## Иконки (icon)

`0` точка, `1` доллар, `2` дом, `3` эйрдроп, `4` цель, `5` оборона,
`6` смерть, `7` спальник, `8` сон, `9` оружие, `10` руда, `11` ящик.

## ВАЖНО: проверено и НЕ сработало (2026-10-03, dev OctaSpawnTest)

На тестовом сервере хук компилился и грузился, но маркеры на карте
админу не появились — либо сигнатура хука в Carbon отличается и он не
вызывался, либо клиент отрисовывает только markers из `State`/`notes`,
которые приходят по другому пути (team/`associatedId`). Если понадобится
снова — первым делом повесить debug-`Puts` внутрь хука и проверить, что
он вообще вызывается; запасной путь — `OnPlayerPingsSend` или
entity-маркеры `MapMarkerGenericRadius` с сетевой фильтрацией.
