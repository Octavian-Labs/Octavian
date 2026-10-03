using System;
using System.Collections.Generic;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctaBarrel", "Octavian", "1.0.0")]
    [Description("Лут из бочек и дорожных знаков сразу падает в инвентарь. Опционально: разбивание с одного удара и подбор бонусного дропа.")]
    public class OctaBarrel : RustPlugin
    {
        private ConfigData _config;

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
