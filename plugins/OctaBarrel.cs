using System;
using System.Collections.Generic;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctaBarrel", "Octavian", "1.1.0")]
    [Description("Лут из бочек и дорожных знаков сразу падает в инвентарь. Взятие предмета из ящика высыпает остаток и схлопывает его. Опционально: one-hit и подбор бонусного дропа.")]
    public class OctaBarrel : RustPlugin
    {
        private ConfigData _config;

        // net.ID мировых ящиков, из которых игрок уже что-то забрал — «взведённые»
        private readonly HashSet<NetworkableId> _armed = new HashSet<NetworkableId>();

        #region Config

        private class ConfigData
        {
            [JsonProperty("Авто-подбор лута из бочек и дорожных знаков в инвентарь")]
            public bool AutoLoot = true;

            [JsonProperty("Бочки и дорожные знаки ломаются с одного удара")]
            public bool OneHit = false;

            [JsonProperty("Бонусный дроп (скрап под чаем и т.п.) тоже попадает в инвентарь")]
            public bool BonusLoot = true;

            [JsonProperty("Допустимая дистанция между игроком и контейнером для работы плагина")]
            public float Distance = 10f;

            [JsonProperty("Взятие хотя бы одного предмета из ящика высыпает остаток, ящик схлопывается")]
            public bool DumpOnLoot = true;

            [JsonProperty("Исключённые контейнеры для высыпания (подстрока ShortPrefabName)")]
            public List<string> DumpExclude = new List<string>();

            [JsonProperty("Логировать высыпания ящиков в консоль сервера")]
            public bool LogDumps = false;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null) LoadDefaultConfig();
                if (_config.Distance <= 0f) _config.Distance = 10f;
            }
            catch
            {
                PrintWarning($"Ошибка чтения конфигурации 'carbon/configs/{Name}', создаём новую.");
                LoadDefaultConfig();
            }
            NextTick(SaveConfig);
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        protected override void LoadDefaultConfig() => _config = new ConfigData();

        #endregion

        #region Хуки и подписки

        private void Init() => ToggleHooks();

        // Carbon может сбросить подписки при перезагрузке соседних плагинов —
        // перевешиваем хуки по конфигу
        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin == null) return;
            NextTick(ToggleHooks);
        }

        private void ToggleHooks()
        {
            if (_config == null) return;
            ToggleHook(nameof(OnEntityTakeDamage), _config.OneHit);
            ToggleHook(nameof(OnContainerDropItems), _config.AutoLoot);
            ToggleHook(nameof(OnBonusItemDropped), _config.BonusLoot);
            ToggleHook(nameof(OnItemRemovedFromContainer), _config.DumpOnLoot);
            ToggleHook(nameof(OnLootEntityEnd), _config.DumpOnLoot);
            ToggleHook(nameof(OnEntityKill), _config.DumpOnLoot);
        }

        private void ToggleHook(string hook, bool enabled)
        {
            if (enabled) Subscribe(hook);
            else Unsubscribe(hook);
        }

        #endregion

        // Все лутабельные бочки и дорожные знаки
        private static bool IsValidContainer(LootContainer c) =>
            c.ShortPrefabName.Contains("barrel") || c.ShortPrefabName.Contains("roadsign");

        private static bool InRange(BasePlayer player, Vector3 target, float distance) =>
            player != null && Vector3.Distance(player.transform.position, target) <= distance;

        // Режим «один удар»: добиваем любой урон игрока до уничтожения
        private object OnEntityTakeDamage(LootContainer container, HitInfo hitInfo)
        {
            if (container == null || hitInfo?.InitiatorPlayer == null) return null;
            if (!IsValidContainer(container)) return null;
            hitInfo.damageTypes.ScaleAll(1000f);
            return null;
        }

        // Авто-подбор: при разрушении контейнера весь лут уходит атакующему
        private void OnContainerDropItems(ItemContainer container)
        {
            if (container == null) return;

            var lootContainer = container.entityOwner as LootContainer;
            if (lootContainer == null || !IsValidContainer(lootContainer)) return;

            var player = lootContainer.lastAttacker as BasePlayer;
            if (!InRange(player, lootContainer.transform.position, _config.Distance)) return;

            List<Item> items = Pool.Get<List<Item>>();
            foreach (Item item in lootContainer.inventory.itemList)
                items.Add(item);

            foreach (Item item in items)
                player.GiveItem(item);

            Pool.FreeUnmanaged(ref items);
        }

        #region Высыпание ящиков

        private bool DumpExcluded(LootContainer lc)
        {
            var name = lc.ShortPrefabName;
            foreach (var pattern in _config.DumpExclude)
                if (!string.IsNullOrEmpty(pattern) && name.Contains(pattern))
                    return true;
            return false;
        }

        // Любой предмет покинул мировой ящик — взводим его.
        // StorageContainer игроков сюда не попадает: это другой класс.
        private void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            if (container == null) return;
            var lc = container.entityOwner as LootContainer;
            if (lc == null || lc.net == null || DumpExcluded(lc)) return;
            _armed.Add(lc.net.ID);
        }

        // Ящик закрыли после того, как из него взяли — высыпаем остаток.
        // Kill не зовём: пустой ящик схлопывает сам Rust (Kill дёргается и лагает).
        private void OnLootEntityEnd(BasePlayer player, LootContainer container)
        {
            if (container == null || container.net == null) return;
            if (!_armed.Remove(container.net.ID)) return;
            if (container.IsDestroyed) return;
            DumpRest(container);
        }

        // Уборка «взведённых» ID мёртвых ящиков (сломали, не закрыв лут)
        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity?.net != null) _armed.Remove(entity.net.ID);
        }

        private void DumpRest(LootContainer lc)
        {
            List<Item> items = Pool.Get<List<Item>>();
            foreach (Item item in lc.inventory.itemList)
                items.Add(item);

            var origin = lc.transform.position + Vector3.up * 0.5f;
            foreach (Item item in items)
            {
                // Drop() сам вынимает предмет из контейнера — ящик становится пустым,
                // как будто игрок достал всё
                item.Drop(origin, new Vector3(
                    UnityEngine.Random.Range(-1.5f, 1.5f),
                    UnityEngine.Random.Range(1.5f, 3f),
                    UnityEngine.Random.Range(-1.5f, 1.5f)));
            }
            var count = items.Count;
            Pool.FreeUnmanaged(ref items);

            if (_config.LogDumps)
                Puts($"Высыпано {count} предм. из '{lc.ShortPrefabName}' при ({lc.transform.position.x:F0}, {lc.transform.position.z:F0})");
        }

        #endregion

        // Бонусный дроп (скрап под чаем и пр.): не оставляем на земле
        private void OnBonusItemDropped(Item item, BasePlayer player)
        {
            if (item == null || player == null) return;
            if (item.GetRootContainer() != null) return;

            var worldEntity = item.GetWorldEntity();
            if (worldEntity == null) return;
            if (!InRange(player, worldEntity.transform.position, _config.Distance)) return;

            player.GiveItem(item);
        }
    }
}
