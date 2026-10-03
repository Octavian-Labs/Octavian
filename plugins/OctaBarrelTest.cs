using System.Collections.Generic;
using Facepunch;
using Newtonsoft.Json;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("OctaBarrelTest", "Octavian", "0.1.0")]
    [Description("[TEST] Ящик, из которого взяли хоть один предмет, при закрытии выбрасывает остаток лута и уничтожается.")]
    public class OctaBarrelTest : RustPlugin
    {
        private ConfigData _config;

        // net.ID мировых ящиков, из которых уже что-то забрали — «взведённые»
        private readonly HashSet<NetworkableId> _armed = new HashSet<NetworkableId>();

        #region Config

        private class ConfigData
        {
            [JsonProperty("Включено")]
            public bool Enabled = true;

            [JsonProperty("Исключённые контейнеры (подстрока ShortPrefabName)")]
            public List<string> Exclude = new List<string>();

            [JsonProperty("Логировать срабатывания в консоль сервера")]
            public bool Log = true;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null) LoadDefaultConfig();
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

        private bool Excluded(LootContainer lc)
        {
            var name = lc.ShortPrefabName;
            foreach (var pattern in _config.Exclude)
                if (!string.IsNullOrEmpty(pattern) && name.Contains(pattern))
                    return true;
            return false;
        }

        // Любой предмет покинул мировой ящик — взводим его.
        // StorageContainer игроков сюда не попадает: это другой класс.
        private void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            if (!_config.Enabled || container == null) return;
            var lc = container.entityOwner as LootContainer;
            if (lc == null || lc.net == null || Excluded(lc)) return;
            _armed.Add(lc.net.ID);
        }

        // Ящик закрыли после того, как из него взяли — высыпаем остаток
        private void OnLootEntityEnd(BasePlayer player, LootContainer container)
        {
            if (container == null || container.net == null) return;
            if (!_armed.Remove(container.net.ID)) return;
            if (container.IsDestroyed) return;
            DumpAndKill(container);
        }

        // Уборка «взведённых» ID мёртвых ящиков (сломали, не закрыв лут)
        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity?.net != null) _armed.Remove(entity.net.ID);
        }

        private void DumpAndKill(LootContainer lc)
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
                    Random.Range(-1.5f, 1.5f),
                    Random.Range(1.5f, 3f),
                    Random.Range(-1.5f, 1.5f)));
            }
            var count = items.Count;
            Pool.FreeUnmanaged(ref items);

            if (_config.Log)
                Puts($"Высыпано {count} предм. из '{lc.ShortPrefabName}' при ({lc.transform.position.x:F0}, {lc.transform.position.z:F0})");

            // пустой ящик Rust убирает сам (естественное схлопывание);
            // страховочный Kill — если в этой сборке авто-уборки нет
            NextTick(() =>
            {
                if (lc != null && !lc.IsDestroyed && lc.inventory.itemList.Count == 0)
                    lc.Kill();
            });
        }
    }
}
