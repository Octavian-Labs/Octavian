using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    // OctaBoss — фреймворк животных-боссов: список видов в конфиге
    // (бык, кабан, волк, тигр, пантера, медведь, крокодил и т.д.).
    // У каждого вида: префаб, статы, скорость, масштаб, свита, точка спавна
    // (рандом / у монумента по имени), расписание, лут и награды.
    //
    // Грабли, которые уже выловлены и учтены:
    //  - масштаб/пол ставятся ДО Spawn() — иначе снапшот уходит с дефолтом;
    //  - труп — отдельная entity, масштабируется в OnEntitySpawned по parentEnt;
    //  - Kill() не дёргает OnEntityDeath — смерть обрабатываем вручную;
    //  - arg.Args — StringView, сравнивать через .ToString();
    //  - бегство Gen2: снимаем flee-факты из blackboard + MarkHostileFor.
    [Info("OctaBoss", "Octavian", "1.5.0")]
    [Description("Фреймворк животных-боссов: расписание, свита, маркер, лут, награды")]
    public class OctaBoss : RustPlugin
    {
        #region Config

        private ConfigData _cfg;

        private class LootEntry
        {
            [JsonProperty("Шортнейм предмета")] public string ShortName = "scrap";
            [JsonProperty("Мин. кол-во")] public int MinAmount = 100;
            [JsonProperty("Макс. кол-во")] public int MaxAmount = 300;
            [JsonProperty("Шанс (0-1)")] public float Chance = 0.5f;
            [JsonProperty("Скин ID (0 = без)")] public ulong SkinId = 0;
            [JsonProperty("Чертёж: целевой предмет (если задан)")] public string BlueprintTarget = "";
            // 0 = независимый шанс; >0 = из пула выпадает РОВНО один предмет (Chance = вес)
            [JsonProperty("Пул (0 = сам по себе, >0 = один предмет из пула)")] public int Pool = 0;
        }

        private class EscortSettings
        {
            [JsonProperty("Количество")] public int Count = 5;
            [JsonProperty("Радиус спавна (м)")] public float Radius = 10f;
            [JsonProperty("Масштаб (1 = обычный)")] public float Scale = 1f;
            [JsonProperty("Префаб (пусто = как у босса)")] public string Prefab = "";
            // null = пол как у префаба; true = самец; false = самка
            [JsonProperty("Пол: male/female (пусто = как у префаба)")] public bool? Male = null;
            [JsonProperty("Убивать свиту при деспавне босса")] public bool KillOnDespawn = true;
        }

        private class SpecSpawn
        {
            [JsonProperty("Интервал спавна (мин)")] public float IntervalMinutes = 180f;
            [JsonProperty("Разброс интервала ± (мин)")] public float IntervalJitterMinutes = 30f;
            [JsonProperty("Предупреждение за (мин)")] public float WarnBeforeMinutes = 5f;
            [JsonProperty("Радиус маркера на карте (0-1, доля карты)")] public float MarkerRadius = 0.03f;
            [JsonProperty("Показывать маркер на карте")] public bool ShowMarker = true;
            [JsonProperty("Держаться от края карты (м)")] public float MapMargin = 400f;
            [JsonProperty("Спавн только в родном биоме")] public bool NativeBiome = false;
            [JsonProperty("Родные биомы (arid,temperate,tundra,arctic,jungle через запятую)")] public string NativeBiomes = "temperate";
            [JsonProperty("Монументы-якоря (через запятую, частичное совпадение; пусто = любая точка)")] public string MonumentNames = "";
            [JsonProperty("Дистанция от края монумента мин (м)")] public float MonumentMinDist = 15f;
            [JsonProperty("Дистанция от края монумента макс (м)")] public float MonumentMaxDist = 70f;
            [JsonProperty("Требовать воду рядом (водные звери; крокодил иначе умирает сойдя с навмеша)")] public bool NeedsWater = false;
            [JsonProperty("Радиус поиска воды вокруг точки спавна (м)")] public float WaterRadius = 25f;
        }

        private class SpecRewards
        {
            [JsonProperty("Валюта за убийство (ServerRewards/Economics)")] public int Currency = 750;
            [JsonProperty("Валюта участникам (нанёс >= порога урона)")] public int CurrencyParticipation = 150;
            [JsonProperty("Порог участия (доля урона)")] public float ParticipationPct = 0.05f;
            [JsonProperty("Бафф урона победителя (x)")] public float BuffDamage = 1.2f;
            [JsonProperty("Длительность баффа (мин)")] public float BuffMinutes = 15f;
            [JsonProperty("Время жизни мешка с лутом (сек)")] public float LootLifetimeSeconds = 3600f;
            [JsonProperty("Мешок закрыт после смерти (сек)")] public float LootLockSeconds = 10f;
            [JsonProperty("Лут-таблица")] public List<LootEntry> Loot = new List<LootEntry>();
        }

        private class BossSpec
        {
            [JsonProperty("Включён")] public bool Enabled = true;
            [JsonProperty("Ключ (латиницей, для команд)")] public string Key = "boar";
            [JsonProperty("Префаб босса")] public string Prefab = "assets/rust.ai/agents/boar/boar.prefab";
            [JsonProperty("Имя босса")] public string DisplayName = "КАБАН-БОСС";
            [JsonProperty("Пол босса: male/female (пусто = как у префаба)")] public bool? Male = null;
            [JsonProperty("Здоровье")] public float Health = 2000f;
            [JsonProperty("Множитель входящего урона (пули)")] public float BulletScale = 0.7f;
            [JsonProperty("Множитель входящего урона (взрывы)")] public float ExplosionScale = 0.9f;
            [JsonProperty("Множитель входящего урона (миляшник/прочее)")] public float OtherScale = 0.9f;
            [JsonProperty("Иммунитет к огню/кровотечению/отравлению/холоду")] public bool ElementalImmune = true;
            [JsonProperty("Множитель исходящего урона")] public float DamageMultiplier = 3f;
            [JsonProperty("Порог ярости (доля HP)")] public float RageThreshold = 0.5f;
            [JsonProperty("Множитель урона в ярости")] public float RageDamageMultiplier = 1.5f;
            [JsonProperty("Радиус агро (м)")] public float AggroRadius = 90f;
            [JsonProperty("Макс. время жизни (мин)")] public float MaxLifeMinutes = 60f;
            [JsonProperty("Масштаб модели (1 = обычный)")] public float Scale = 2f;
            [JsonProperty("Множитель скорости (1 = обычная)")] public float SpeedMultiplier = 1f;
            [JsonProperty("Экстра агрессия (никогда не убегает)")] public bool HyperAggro = false;
            [JsonProperty("Форс-агро Provoke (для скотины вместо HyperAggro)")] public bool ForceProvoke = false;
            [JsonProperty("Перезарядка боя: сек без урона до регена (0 = выкл)")] public float RegenDelaySeconds = 60f;
            [JsonProperty("Реген % макс. HP в секунду")] public float RegenPercentPerSec = 2.5f;
            // --- фазы боя: P1 пасущийся (>P2Threshold), P2 разъярённый, P3 последний рывок ---
            [JsonProperty("Фазы боя включены")] public bool PhasesEnabled = true;
            [JsonProperty("Фаза 2: порог входа (доля HP, вход при ниже)")] public float Phase2Threshold = 0.8f;
            [JsonProperty("Фаза 3: порог входа (доля HP, вход при ниже)")] public float Phase3Threshold = 0.3f;
            [JsonProperty("Фаза 2: множитель исходящего урона")] public float P2DamageMult = 1.15f;
            [JsonProperty("Фаза 3: множитель исходящего урона")] public float P3DamageMult = 1.35f;
            [JsonProperty("Фаза 2: радиус агро (м)")] public float P2AggroRadius = 120f;
            [JsonProperty("Фаза 3: радиус агро (м)")] public float P3AggroRadius = 150f;
            [JsonProperty("Фаза 2: множитель скорости")] public float P2SpeedMult = 1.15f;
            [JsonProperty("Фаза 3: множитель скорости")] public float P3SpeedMult = 1.35f;
            [JsonProperty("Фаза 3: часть свиты разбегается (0-1)")] public float P3EscortFleeChance = 0.4f;
            [JsonProperty("Пересмотр цели по угрозе каждые (сек, 0=выкл)")] public float RetargetSeconds = 12f;
            [JsonProperty("Гистерезис смены цели (x угрозы текущей)")] public float ThreatHysteresis = 1.35f;
            [JsonProperty("Фаза 3: рывок к дальнему дамагеру каждые (сек, 0=выкл)")] public float LeapSeconds = 15f;
            [JsonProperty("Фаза 3: урон рывка по площади")] public float LeapDamage = 60f;
            [JsonProperty("Фаза 3: щит после рывка (сек, -25% входящего)")] public float LeapShieldSeconds = 3f;
            [JsonProperty("Анонс фазы 2")] public string Phase2Announce = "{name} РАЗЪЯРЁН — ищет, кто больше всех его обидел!";
            [JsonProperty("Анонс фазы 3")] public string Phase3Announce = "{name} ОТЧАЯЛСЯ — финальный рывок! Держитесь вместе!";
            [JsonProperty("Свита")] public EscortSettings Escort = new EscortSettings();
            [JsonProperty("Спавн")] public SpecSpawn Spawn = new SpecSpawn();
            [JsonProperty("Награды")] public SpecRewards Rewards = new SpecRewards();
            [JsonProperty("Текст анонса спавна ({0}=квадрат)")] public string SpawnAnnounce = "★ {name} появился! Квадрат: <color=#FFD35C>{0}</color>";
            [JsonProperty("Текст предупреждения ({0}=минуты)")] public string WarnAnnounce = "{name} появится через {0} мин. Готовьтесь!";
            [JsonProperty("Варианты предупреждения (случайный, {0}=минуты)")] public List<string> WarnAnnounceVariants = new List<string>();
            [JsonProperty("Текст зализывания ран (босс отлежался)")] public string ResetAnnounce = "{name} зализал раны и снова в форме!";
            [JsonProperty("Текст деспавна по таймауту")] public string DespawnAnnounce = "{name} ушёл — никто не решился.";
            [JsonProperty("Текст смерти ({0}=добивший)")] public string DeathAnnounce = "★ {name} повержен! Добил: <color=#FFD35C>{0}</color>. Лут на месте падения.";
            [JsonProperty("Текст смерти без боя (лута нет)")] public string QuietDeathAnnounce = "{name} погиб без боя — лута не будет.";
            [JsonProperty("Текст ярости")] public string RageAnnounce = "★ {name} ВПАЛ В ЯРОСТЬ — урон вырос!";
        }

        private class NotifySettings
        {
            [JsonProperty("Глобальный чат")] public bool ChatGlobal = true;
            [JsonProperty("Имя плагина в чате (бренд перед текстом)")] public string ChatName = "◆ OctaBoss";
            [JsonProperty("Discord webhook (пусто = выкл)")] public string DiscordWebhook = "";
        }

        private class ConfigData
        {
            [JsonProperty("Виды боссов")] public List<BossSpec> Bosses = new List<BossSpec>();
            [JsonProperty("Уведомления")] public NotifySettings Notify = new NotifySettings();
        }

        // компактная запись строки лута: LE(шортнейм, мин, макс, шанс[, цель чертежа][, пул])
        private static LootEntry LE(string sn, int min, int max, float ch, string bp = "", int pool = 0) =>
            new LootEntry { ShortName = sn, MinAmount = min, MaxAmount = max, Chance = ch, BlueprintTarget = bp, Pool = pool };

        private static List<LootEntry> RichLoot() => new List<LootEntry>
        {
            LE("scrap", 1500, 3000, 1f),
            LE("metal.refined", 300, 700, 0.9f),
            LE("sulfur", 6000, 12000, 0.9f),
            LE("gunpowder", 3000, 6000, 0.7f),
            LE("techparts", 30, 60, 0.6f),
            LE("keycard_blue", 1, 1, 0.6f, pool: 2),
            LE("keycard_red", 1, 1, 0.2f, pool: 2),
            LE("lmg.m249", 1, 1, 0.05f, pool: 1),
            LE("rifle.lr300", 1, 1, 0.25f, pool: 1),
            LE("rifle.l96", 1, 1, 0.15f, pool: 1),
            LE("rocket.launcher", 1, 1, 0.15f, pool: 1),
            LE("multiplegrenadelauncher", 1, 1, 0.2f, pool: 1),
            LE("shotgun.spas12", 1, 1, 0.2f, pool: 1),
            LE("ammo.rifle.explosive", 60, 128, 0.15f, pool: 2),
            LE("ammo.rocket.hv", 2, 4, 0.05f, pool: 2),
            LE("advancedblueprintfragment", 3, 3, 1f),
            LE("blueprintbase", 1, 1, 0.35f, "rifle.ak"),
            LE("blueprintbase", 1, 1, 0.25f, "rifle.lr300"),
            LE("blueprintbase", 1, 1, 0.08f, "lmg.m249"),
            LE("blueprintbase", 1, 1, 0.15f, "rocket.launcher"),
            LE("blueprintbase", 1, 1, 0.2f, "ammo.rocket.basic"),
            LE("blueprintbase", 1, 1, 0.18f, "explosive.timed"),
            LE("blueprintbase", 1, 1, 0.3f, "metal.facemask"),
            LE("blueprintbase", 1, 1, 0.3f, "metal.plate.torso"),
            LE("blueprintbase", 1, 1, 0.2f, "shotgun.spas12"),
        };

        private static List<LootEntry> MidLoot() => new List<LootEntry>
        {
            LE("scrap", 500, 1000, 1f),
            LE("metal.refined", 100, 250, 0.9f),
            LE("sulfur", 2000, 4000, 0.9f),
            LE("gunpowder", 1000, 2000, 0.7f),
            LE("techparts", 10, 20, 0.6f),
            LE("keycard_blue", 1, 1, 0.6f, pool: 2),
            LE("keycard_red", 1, 1, 0.1f, pool: 2),
            LE("lmg.m249", 1, 1, 0.03f, pool: 1),
            LE("rifle.lr300", 1, 1, 0.15f, pool: 1),
            LE("rifle.l96", 1, 1, 0.1f, pool: 1),
            LE("rocket.launcher", 1, 1, 0.12f, pool: 1),
            LE("multiplegrenadelauncher", 1, 1, 0.15f, pool: 1),
            LE("shotgun.spas12", 1, 1, 0.45f, pool: 1),
            LE("ammo.rifle.explosive", 20, 42, 0.25f, pool: 2),
            LE("ammo.rocket.hv", 1, 2, 0.05f, pool: 2),
            LE("advancedblueprintfragment", 1, 1, 1f),
            LE("blueprintbase", 1, 1, 0.12f, "rifle.ak"),
            LE("blueprintbase", 1, 1, 0.08f, "rifle.lr300"),
            LE("blueprintbase", 1, 1, 0.03f, "lmg.m249"),
            LE("blueprintbase", 1, 1, 0.05f, "rocket.launcher"),
            LE("blueprintbase", 1, 1, 0.07f, "ammo.rocket.basic"),
            LE("blueprintbase", 1, 1, 0.06f, "explosive.timed"),
            LE("blueprintbase", 1, 1, 0.1f, "metal.facemask"),
            LE("blueprintbase", 1, 1, 0.1f, "metal.plate.torso"),
            LE("blueprintbase", 1, 1, 0.07f, "shotgun.spas12"),
        };

        private static ConfigData DefaultBosses()
        {
            return new ConfigData
            {
                Bosses = new List<BossSpec>
                {
                    new BossSpec
                    {
                        Key = "bull",
                        Prefab = "assets/rust.ai/agents/bull/bull.prefab",
                        DisplayName = "БЫК-БОСС",
                        Male = true,
                        Health = 9000f,
                        Scale = 2f,
                        SpeedMultiplier = 2f,
                        DamageMultiplier = 3f,
                        ForceProvoke = true,
                        Escort = new EscortSettings
                        {
                            Count = 5, Radius = 10f, Scale = 1f,
                            Prefab = "assets/rust.ai/agents/bull/bull.prefab",
                            Male = false, KillOnDespawn = true
                        },
                        Spawn = new SpecSpawn
                        {
                            MonumentNames = "",
                            NativeBiome = true, NativeBiomes = "temperate,tundra"
                        },
                        Rewards = new SpecRewards { Loot = RichLoot() },
                        WarnAnnounceVariants = new List<string>
                        {
                            "Кто хочет поиграть в тореадора? {name} выходит на арену через {0} мин!",
                            "Фердинанд нюхает цветы в другом лесу — {name} нюхает твой страх. Через {0} мин.",
                            "{name} увидел красную тряпку. Тряпка — это ты. Встреча через {0} мин.",
                            "В бой идут одни старики... и {name} — через {0} мин.",
                            "Родео открывается через {0} мин. {name} уже точит рога.",
                        },
                        WarnAnnounce = "{name} появится через {0} мин. Готовьтесь — это не кабанчик с ранчо!",
                        DespawnAnnounce = "{name} ушёл в закат — никто не решился.",
                    },
                    new BossSpec
                    {
                        Key = "boar",
                        Prefab = "assets/rust.ai/agents/boar/boar.prefab",
                        DisplayName = "КАБАН-БОСС",
                        Health = 2000f,
                        Scale = 2f,
                        SpeedMultiplier = 1.5f,
                        HyperAggro = true,
                        Escort = new EscortSettings
                        {
                            Count = 5, Radius = 10f, Scale = 0.55f,
                            Prefab = "", Male = null, KillOnDespawn = true
                        },
                        // «кольцо Сатурна» вокруг ранчо: 300-900м по кругу — не в безопасной
                        // зоне и не кемпится на точке, но всегда где-то рядом с жильём
                        Spawn = new SpecSpawn
                        {
                            MonumentNames = "ranch,barn,stables",
                            MonumentMinDist = 300f, MonumentMaxDist = 900f,
                            NativeBiome = true, NativeBiomes = "temperate,tundra"
                        },
                        Rewards = new SpecRewards { Loot = MidLoot() },
                        WarnAnnounceVariants = new List<string>
                        {
                            "Кто хочет поиграть в выжившего? {name} появится через {0} мин возле ранчо!",
                            "Калидонский вепрь вернулся за долгами — {name} через {0} мин.",
                            "Хрю-хрю, мать вашу. {name} выходит через {0} мин.",
                            "Три поросёнка — это был аперитив. {name} через {0} мин.",
                            "Вепрь выходит на охоту. Угадай, кто трофей? {name} через {0} мин.",
                        },
                        WarnAnnounce = "{name} появится через {0} мин возле ранчо. Готовьтесь!",
                        DespawnAnnounce = "{name} убежал в лес — никто не решился.",
                        SpawnAnnounce = "★ {name} появился возле ранчо! Квадрат: <color=#FFD35C>{0}</color> — и его свита уже рыщет!",
                    },
                    // заготовки под остальных зверей — выключены, включить в конфиге или /boss_admin
                    new BossSpec
                    {
                        Enabled = false, Key = "wolf",
                        Prefab = "assets/rust.ai/agents/wolf/wolf2.prefab",
                        DisplayName = "ВОЛК-БОСС",
                        Health = 1500f, Scale = 2f, SpeedMultiplier = 1.6f,
                        HyperAggro = true,
                        Escort = new EscortSettings { Count = 4, Radius = 8f, Scale = 0.8f },
                        Spawn = new SpecSpawn { NativeBiome = true, NativeBiomes = "arctic,tundra,temperate" },
                        Rewards = new SpecRewards { Loot = MidLoot() },
                        WarnAnnounceVariants = new List<string>
                        {
                            "Кто боится большого злого волка? {name} ведёт стаю — через {0} мин!",
                            "Три поросёнка съедены, бабушка дожёвывается. Ты следующий: {name} через {0} мин.",
                            "Аууу! {name} зовёт родню — встреча через {0} мин.",
                            "Красная Шапочка не придёт — придёт {name}. Через {0} мин.",
                            "Волк — не тот, кто волк, а кто {name}. {0} мин на сборы.",
                        },
                        DespawnAnnounce = "{name} ушёл в чащу — стая растворилась в темноте.",
                    },
                    new BossSpec
                    {
                        Enabled = false, Key = "tiger",
                        Prefab = "assets/rust.ai/agents/tiger/tiger.prefab",
                        DisplayName = "ТИГР-БОСС",
                        Health = 2500f, Scale = 1.8f, SpeedMultiplier = 1.7f,
                        HyperAggro = true,
                        Escort = new EscortSettings { Count = 0 },
                        Spawn = new SpecSpawn { NativeBiome = true, NativeBiomes = "jungle" },
                        Rewards = new SpecRewards { Loot = MidLoot() },
                        WarnAnnounceVariants = new List<string>
                        {
                            "Шерхан не спрашивает дважды. {name} выходит через {0} мин!",
                            "Ричард Паркер голоден — {name} через {0} мин.",
                            "У Тони закончились хлопья. {name} идёт за настоящим мясом: {0} мин.",
                            "Укротителей не объявилось — {name} придёт сам через {0} мин.",
                            "Тигриный глаз смотрит на тебя. {name} через {0} мин.",
                        },
                        DespawnAnnounce = "{name} скрылся в джунглях.",
                    },
                    new BossSpec
                    {
                        Enabled = false, Key = "panther",
                        Prefab = "assets/rust.ai/agents/panther/panther.prefab",
                        DisplayName = "ПАНТЕРА-БОСС",
                        Health = 2500f, Scale = 1.8f, SpeedMultiplier = 1.7f,
                        HyperAggro = true,
                        Escort = new EscortSettings { Count = 0 },
                        Spawn = new SpecSpawn { NativeBiome = true, NativeBiomes = "jungle" },
                        Rewards = new SpecRewards { Loot = MidLoot() },
                        WarnAnnounceVariants = new List<string>
                        {
                            "Багира променяла Маугли на тебя. {name} через {0} мин!",
                            "Ты не услышишь её шагов — только свист пуль. {name} через {0} мин.",
                            "Ваканда шлёт привет: {name} материализуется через {0} мин.",
                            "Не розовая и не милая. {name} через {0} мин.",
                            "Тень, которая кусается насмерть: {name} через {0} мин.",
                        },
                        DespawnAnnounce = "{name} растворилась в тени.",
                    },
                    new BossSpec
                    {
                        Enabled = false, Key = "bear",
                        Prefab = "assets/rust.ai/agents/bear/bear.prefab",
                        DisplayName = "МЕДВЕДЬ-БОСС",
                        Health = 6000f, Scale = 2.2f, SpeedMultiplier = 1.3f,
                        HyperAggro = true,
                        Escort = new EscortSettings { Count = 2, Scale = 0.6f },
                        Spawn = new SpecSpawn { NativeBiome = true, NativeBiomes = "temperate,tundra" },
                        Rewards = new SpecRewards { Loot = RichLoot() },
                        WarnAnnounceVariants = new List<string>
                        {
                            "Кто хочет поиграть в выжившего? {name} выходит из берлоги через {0} мин!",
                            "Машка опять разбила горшок — {name} идёт разбираться через {0} мин.",
                            "ДиКаприо выжил. Посмотрим на тебя: {name} через {0} мин.",
                            "300 кг ярости против твоей гордости. {name} через {0} мин.",
                            "Людоед расписанием не связан — просто {name} через {0} мин.",
                        },
                        DespawnAnnounce = "{name} ушёл в берлогу.",
                    },
                    new BossSpec
                    {
                        Enabled = false, Key = "crocodile",
                        Prefab = "assets/rust.ai/agents/crocodile/crocodile.prefab",
                        DisplayName = "КРОКОДИЛ-БОСС",
                        Health = 4000f, Scale = 2f, SpeedMultiplier = 1.3f,
                        HyperAggro = true,
                        Escort = new EscortSettings { Count = 3, Scale = 0.7f },
                        Spawn = new SpecSpawn { NativeBiome = true, NativeBiomes = "jungle", NeedsWater = true, WaterRadius = 30f },
                        Rewards = new SpecRewards { Loot = MidLoot() },
                        WarnAnnounceVariants = new List<string>
                        {
                            "Кто хочет поиграть в выжившего? {name} всплывёт через {0} мин!",
                            "Гена пошёл за Чебурашкой и не вернулся. {name} ждёт через {0} мин.",
                            "Тик-так, тик-так — капитан Крюк уже сбежал. {name} через {0} мин.",
                            "Крокодильи слёзы — это ты будешь плакать. {name} через {0} мин.",
                            "Данди не приедет — придёт {name}. Через {0} мин.",
                        },
                        DespawnAnnounce = "{name} ушёл под воду.",
                    },
                }
            };
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { _cfg = Config.ReadObject<ConfigData>(); }
            catch { _cfg = null; }
            if (_cfg == null || _cfg.Bosses == null || _cfg.Bosses.Count == 0)
            {
                _cfg = DefaultBosses();
                SaveConfig();
            }
        }

        protected override void LoadDefaultConfig() => _cfg = DefaultBosses();
        protected override void SaveConfig() => Config.WriteObject(_cfg = _cfg ?? DefaultBosses(), true);

        #endregion

        #region State

        // активный босс-инстанс: спека + рантайм-состояние
        private class BossState
        {
            public BossSpec Spec;
            public BaseCombatEntity Entity;
            public Rust.Ai.Gen2.BaseNPC2 Ai;
            public Rust.Ai.Gen2.LivestockAnimal Livestock;
            public readonly List<BaseCombatEntity> Escorts = new List<BaseCombatEntity>();
            public MapMarkerGenericRadius Marker;
            public DateTime NextSpawnAt;
            public DateTime SpawnedAt;
            public DateTime LastDamageAt;
            public bool Warned;
            public bool Raging;
            public bool ResetDone; // уже «зализал раны» — реген и сброс трекера отработали
            public bool DeathHandled; // смерть уже обработана — защита от двойного лута/наград
            public readonly Dictionary<ulong, float> DamageBy = new Dictionary<ulong, float>();
            // фазы/угроза
            public int Phase = 1;
            public ulong ThreatTargetId;
            public DateTime LastThreatDecay;
            public DateTime LastRetargetAt;
            public DateTime LastLeapAt;
            public DateTime ShieldUntil;
            public readonly Dictionary<ulong, float> ThreatBy = new Dictionary<ulong, float>();
            // урон за последние ~20с — «кто сейчас бесит босса»
            public class RecentHit { public DateTime T; public ulong Id; public float D; }
            public readonly List<RecentHit> RecentHits = new List<RecentHit>();
            // роли свиты: 0=охранник (держится у босса), 1=приманка (идёт на игрока)
            public readonly Dictionary<BaseCombatEntity, int> EscortRoles = new Dictionary<BaseCombatEntity, int>();
            public DateTime LastEscortTick;
            public float CurSpeedMult = 1f; // накопленный множитель скорости (фазы)

            public bool Alive => Entity != null && !Entity.IsDestroyed && Entity.IsAlive();
        }

        private readonly Dictionary<string, BossState> _bosses = new Dictionary<string, BossState>();

        // «ждём труп этой смерти с таким масштабом»: матч по parentEnt,
        // фолбэк — по позиции смерти (parentEnt бывает пуст). TTL чистится лениво
        private class PendingCorpse
        {
            public BaseEntity Source;
            public Vector3 Pos;
            public float Scale;
            public DateTime Expiry;
            public bool Stale => DateTime.UtcNow > Expiry;
        }

        // бафф победителя: steamid -> запись
        private class Buff
        {
            public DateTime Expires;
            public float Mult;
            public bool Stale => DateTime.UtcNow > Expires;
        }

        private readonly List<PendingCorpse> _corpses = new List<PendingCorpse>();
        private readonly Dictionary<DroppedItemContainer, DateTime> _lockedBags = new Dictionary<DroppedItemContainer, DateTime>();
        private readonly Dictionary<ulong, Buff> _buffs = new Dictionary<ulong, Buff>();
        private readonly Dictionary<string, string> _lastKillInfo = new Dictionary<string, string>();
        private Timer _timerSpawn, _timerAggro;

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission("octaboss.admin", this);
            // дубли Key в конфиге = тихая потеря вида (state перезаписывается) — орём в лог
            foreach (var g in _cfg.Bosses.GroupBy(b => b.Key).Where(g => g.Count() > 1))
                PrintError($"OctaBoss: дублирующийся ключ вида '{g.Key}' — в спавне участвует только последний!");
            foreach (var spec in _cfg.Bosses.Where(s => s.Enabled))
            {
                var st = new BossState { Spec = spec };
                ScheduleNext(st, first: true);
                _bosses[spec.Key] = st;
                Puts($"OctaBoss[{spec.Key}]: следующий спавн через {(st.NextSpawnAt - DateTime.UtcNow).TotalMinutes:0} мин");
            }
            _timerSpawn = timer.Every(30f, SpawnTick);
            _timerAggro = timer.Every(1f, AggroTick);
        }

        private void Unload()
        {
            _timerSpawn?.Destroy();
            _timerAggro?.Destroy();
            foreach (var st in _bosses.Values) ClearMarker(st);
            foreach (var p in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(p, AdminUIName);
            // живых боссов не трогаем — доживают как обычные животные
        }

        private void ScheduleNext(BossState st, bool first = false)
        {
            var jitter = UnityEngine.Random.Range(-st.Spec.Spawn.IntervalJitterMinutes, st.Spec.Spawn.IntervalJitterMinutes);
            var mins = Math.Max(1f, st.Spec.Spawn.IntervalMinutes + jitter);
            // после рестарта все боссы спавнились почти одновременно через ~5 мин —
            // предсказуемый «вайп-фарм»; размазываем первый спавн случайно
            st.NextSpawnAt = DateTime.UtcNow.AddMinutes(
                first ? UnityEngine.Random.Range(5f, Math.Max(6f, Math.Min(mins, 45f))) : mins);
            st.Warned = false;
        }

        #endregion

        #region Spawn

        private void SpawnTick()
        {
            var now = DateTime.UtcNow;
            foreach (var st in _bosses.Values)
            {
                if (!st.Spec.Enabled || st.Alive) continue;
                if (!st.Warned && now >= st.NextSpawnAt.AddMinutes(-st.Spec.Spawn.WarnBeforeMinutes))
                {
                    st.Warned = true;
                    var vars = st.Spec.WarnAnnounceVariants;
                    var tpl = vars != null && vars.Count > 0
                        ? vars[UnityEngine.Random.Range(0, vars.Count)]
                        : st.Spec.WarnAnnounce;
                    Announce(Fmt(tpl, st.Spec, $"{st.Spec.Spawn.WarnBeforeMinutes:0}"));
                }
                if (now < st.NextSpawnAt) continue;
                SpawnBoss(st);
            }
        }

        private static readonly int AvoidTopo =
            (int)(TerrainTopology.Enum.Monument | TerrainTopology.Enum.Building | TerrainTopology.Enum.Ocean |
                  TerrainTopology.Enum.Lake | TerrainTopology.Enum.Cliff | TerrainTopology.Enum.Swamp |
                  TerrainTopology.Enum.River | TerrainTopology.Enum.Riverside | TerrainTopology.Enum.Beach |
                  TerrainTopology.Enum.Beachside);

        // имя биома (конфиг) -> флаг TerrainBiome.Enum
        private static bool TryParseBiome(string name, out int flag)
        {
            switch (name)
            {
                case "arid": flag = (int)TerrainBiome.Enum.Arid; return true;
                case "temperate": flag = (int)TerrainBiome.Enum.Temperate; return true;
                case "tundra": flag = (int)TerrainBiome.Enum.Tundra; return true;
                case "arctic": flag = (int)TerrainBiome.Enum.Arctic; return true;
                case "jungle": flag = (int)TerrainBiome.Enum.Jungle; return true;
                default: flag = 0; return false;
            }
        }

        // битовая маска разрешённых биомов; 0 = проверка биома выключена
        private static int NativeBiomeMask(BossSpec spec)
        {
            if (!spec.Spawn.NativeBiome || string.IsNullOrEmpty(spec.Spawn.NativeBiomes)) return 0;
            var mask = 0;
            foreach (var n in spec.Spawn.NativeBiomes.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int flag;
                if (TryParseBiome(n.Trim().ToLowerInvariant(), out flag)) mask |= flag;
            }
            return mask;
        }

        private bool ValidPoint(Vector3 p, int biomeMask, bool needsWater, float waterRadius)
        {
            var h = TerrainMeta.HeightMap.GetHeight(p);
            if (h < 1.5f) return false; // сама точка — вода/пляж, босс спавнится на суше
            p.y = h;
            // для водных зверей пляж/прибрежная топология как раз нужна — не отсекаем её
            try
            {
                var topo = needsWater
                    ? AvoidTopo & ~(int)(TerrainTopology.Enum.Beach | TerrainTopology.Enum.Beachside | TerrainTopology.Enum.Lake | TerrainTopology.Enum.Riverside)
                    : AvoidTopo;
                if (TerrainMeta.TopologyMap.GetTopology(p, topo)) return false;
            }
            catch { }
            // родной биом: доминирующий биом точки должен входить в маску
            if (biomeMask != 0)
            {
                try { if (((int)TerrainMeta.BiomeMap.GetBiomeMaxType(p) & biomeMask) == 0) return false; }
                catch { }
            }
            // вода рядом: хотя бы одна проба по кругу должна быть под водой
            if (needsWater && !WaterNearby(p, waterRadius)) return false;
            return true;
        }

        private static bool WaterNearby(Vector3 p, float radius)
        {
            try
            {
                for (var i = 0; i < 8; i++)
                {
                    var ang = i * (Mathf.PI * 0.25f);
                    var q = p + new Vector3(Mathf.Cos(ang) * radius, 0f, Mathf.Sin(ang) * radius);
                    if (TerrainMeta.WaterMap.GetHeight(q) > TerrainMeta.HeightMap.GetHeight(q) + 0.1f)
                        return true;
                }
            }
            catch { return true; } // карта воды недоступна — не блокируем спавн
            return false;
        }

        private Vector3 FindSpawnPos(BossState st)
        {
            var size = ConVar.Server.worldsize;
            var half = size * 0.5f - st.Spec.Spawn.MapMargin;
            var mask = NativeBiomeMask(st.Spec);
            var needWater = st.Spec.Spawn.NeedsWater;
            var waterR = st.Spec.Spawn.WaterRadius;

            // проход 0: биом + вода; проход 1: вода без биома; проход 2: вообще где угодно
            for (var pass = 0; pass < 3; pass++)
            {
                var useMask = pass == 0 ? mask : 0;
                var useWater = pass < 2 && needWater;
                for (var i = 0; i < 60; i++)
                {
                    var p = new Vector3(UnityEngine.Random.Range(-half, half), 0, UnityEngine.Random.Range(-half, half));
                    if (!ValidPoint(p, useMask, useWater, waterR)) continue;
                    p.y = TerrainMeta.HeightMap.GetHeight(p) + 0.5f;
                    return p;
                }
                if (pass == 0 && mask != 0)
                    PrintWarning($"OctaBoss[{st.Spec.Key}]: родной биом не найден за 60 попыток — ослабляю фильтр");
                if (pass == 1 && needWater)
                    PrintWarning($"OctaBoss[{st.Spec.Key}]: вода рядом не найдена — спавн где попало");
            }
            return Vector3.zero;
        }

        // точка рядом со случайным монументом из списка (ranch, barn и т.п.)
        private Vector3 FindMonumentSpawnPos(BossState st)
        {
            try
            {
                var wanted = st.Spec.Spawn.MonumentNames
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Where(s => s.Length > 0)
                    .ToArray();
                if (wanted.Length == 0) return Vector3.zero;
                var mons = TerrainMeta.Path?.Monuments?
                    .Where(m => m != null && wanted.Any(w => (m.name ?? "").ToLowerInvariant().Contains(w)))
                    .ToList();
                if (mons == null || mons.Count == 0) return Vector3.zero;

                var mon = mons[UnityEngine.Random.Range(0, mons.Count)];
                // ВАЖНО: m.Bounds — локальный бокс (center ~ 0,0)! Мировой центр — transform.position
                var center = mon.transform.position;
                var edge = Mathf.Max(mon.Bounds.extents.x, mon.Bounds.extents.z);
                var mask = NativeBiomeMask(st.Spec);
                var needWater = st.Spec.Spawn.NeedsWater;
                var waterR = st.Spec.Spawn.WaterRadius;
                // все монументы — для отсека точек внутри чужих (там же сейф-зоны:
                // аутпост/бандитка). Точку ВНУТРИ любого монумента не берём вообще.
                // Сравнение — по горизонтали кругом радиуса max-extent (запас на поворот).
                var allMons = TerrainMeta.Path?.Monuments?.Where(m => m != null).ToList()
                              ?? new List<MonumentInfo>();

                for (var i = 0; i < 40; i++)
                {
                    var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    var dist = edge + UnityEngine.Random.Range(st.Spec.Spawn.MonumentMinDist, st.Spec.Spawn.MonumentMaxDist);
                    var p = center + new Vector3(Mathf.Cos(ang) * dist, 0f, Mathf.Sin(ang) * dist);
                    if (allMons.Any(m => Mathf.Max(m.Bounds.extents.x, m.Bounds.extents.z)
                        > Vector2.Distance(new Vector2(p.x, p.z), new Vector2(m.transform.position.x, m.transform.position.z)))) continue;
                    if (!ValidPoint(p, mask, needWater, waterR)) continue;
                    p.y = TerrainMeta.HeightMap.GetHeight(p) + 0.5f;
                    return p;
                }
            }
            catch (Exception ex) { PrintWarning($"OctaBoss[{st.Spec.Key}]: поиск монумента упал: {ex.Message}"); }
            return Vector3.zero;
        }

        private void SpawnBoss(BossState st, Vector3? at = null)
        {
            // если старый босс жив — убираем тихо (Kill() не триггерит OnEntityDeath,
            // значит без лута/наград/перепланировки): иначе получим двух боссов
            if (st.Alive) DespawnBoss(st);

            var pos = at ?? FindMonumentSpawnPos(st);
            if (pos == Vector3.zero) pos = FindSpawnPos(st);
            if (pos == Vector3.zero)
            {
                PrintError($"OctaBoss[{st.Spec.Key}]: не нашёл точку спавна — перенос на следующий цикл");
                ScheduleNext(st);
                return;
            }

            var ent = GameManager.server.CreateEntity(st.Spec.Prefab, pos, Quaternion.identity) as BaseCombatEntity;
            if (ent == null)
            {
                PrintError($"OctaBoss[{st.Spec.Key}]: префаб {st.Spec.Prefab} не заспавнился");
                ScheduleNext(st);
                return;
            }

            // масштаб и пол — ДО Spawn(): иначе игроки рядом получат снапшот с дефолтом
            ent.enableSaving = false;
            ent.networkEntityScale = true;
            ent.transform.localScale = Vector3.one * st.Spec.Scale;
            var livestock = ent as Rust.Ai.Gen2.LivestockAnimal;
            if (livestock != null)
            {
                if (st.Spec.Male.HasValue) { try { livestock.IsMale = st.Spec.Male.Value; } catch { } }
                try { livestock.AnimalName = st.Spec.DisplayName; } catch { }
            }
            ent.Spawn();

            st.Entity = ent;
            st.Ai = ent as Rust.Ai.Gen2.BaseNPC2;
            st.Livestock = livestock;
            st.SpawnedAt = DateTime.UtcNow;
            st.LastDamageAt = DateTime.UtcNow;
            st.Raging = false;
            st.ResetDone = true; // свежий босс — регена не требуется
            st.DeathHandled = false;
            st.DamageBy.Clear();
            st.Phase = 1;
            st.ThreatTargetId = 0;
            st.ThreatBy.Clear();
            st.RecentHits.Clear();
            st.EscortRoles.Clear();
            st.LastThreatDecay = DateTime.UtcNow;
            st.LastRetargetAt = DateTime.UtcNow;
            st.LastLeapAt = DateTime.UtcNow;
            st.LastEscortTick = DateTime.UtcNow;
            st.CurSpeedMult = st.Spec.SpeedMultiplier;

            ent.InitializeHealth(st.Spec.Health, st.Spec.Health);
            ent.startHealth = st.Spec.Health;
            ent.maxHealthOverride = st.Spec.Health;
            try { ent.SendNetworkUpdateImmediate(); } catch { }

            ApplySpeed(ent, st.Spec.SpeedMultiplier);
            SpawnMarker(st, pos);
            SpawnEscorts(st, pos);

            var grid = GridOf(pos);
            Announce(Fmt(st.Spec.SpawnAnnounce, st.Spec, grid));
            NotifyDiscord($"{st.Spec.DisplayName} появился в квадрате {grid} ({pos.x:0},{pos.z:0})");
        }

        private static void ApplySpeed(BaseCombatEntity ent, float mult)
        {
            if (Mathf.Abs(mult - 1f) <= 0.01f) return;
            try
            {
                // NavMesh-агент Gen2 — крутим все пороги ходьбы/бега
                var agent = ent.GetComponentInChildren<Rust.Ai.Gen2.RustNavMeshAgent>();
                if (agent == null) return;
                agent.speed *= mult;
                agent.angularSpeed *= mult;
                agent.sneakSpeed *= mult;
                agent.walkSpeed *= mult;
                agent.jogSpeed *= mult;
                agent.runSpeed *= mult;
                agent.sprintSpeed *= mult;
                agent.fullSprintSpeed *= mult;
            }
            catch { }
        }

        private void SpawnMarker(BossState st, Vector3 pos)
        {
            ClearMarker(st);
            if (!st.Spec.Spawn.ShowMarker) return;
            var marker = GameManager.server.CreateEntity("assets/prefabs/tools/map/genericradiusmarker.prefab", pos) as MapMarkerGenericRadius;
            if (marker == null) return;
            marker.enableSaving = false;
            marker.Spawn();
            marker.radius = st.Spec.Spawn.MarkerRadius;
            marker.color1 = new Color(0.85f, 0.1f, 0.1f);
            marker.color2 = new Color(0.4f, 0f, 0f);
            marker.alpha = 0.55f;
            marker.SendUpdate();
            marker.SendNetworkUpdate();
            st.Marker = marker;
        }

        private void SpawnEscorts(BossState st, Vector3 center)
        {
            st.Escorts.Clear();
            var esc = st.Spec.Escort;
            var prefab = string.IsNullOrEmpty(esc.Prefab) ? st.Spec.Prefab : esc.Prefab;
            for (var i = 0; i < esc.Count; i++)
            {
                var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                var dist = UnityEngine.Random.Range(4f, esc.Radius);
                var p = center + new Vector3(Mathf.Cos(ang) * dist, 0f, Mathf.Sin(ang) * dist);
                try { p.y = TerrainMeta.HeightMap.GetHeight(p) + 0.3f; } catch { p.y = center.y; }

                var e = GameManager.server.CreateEntity(prefab, p, Quaternion.identity) as BaseCombatEntity;
                if (e == null) continue;
                e.enableSaving = false;
                // масштаб и пол — ДО Spawn(), иначе клиент получит дефолтный снапшот
                e.networkEntityScale = true;
                e.transform.localScale = Vector3.one * esc.Scale;
                var ai = e as Rust.Ai.Gen2.LivestockAnimal;
                if (ai != null && esc.Male.HasValue) { try { ai.IsMale = esc.Male.Value; } catch { } }
                e.Spawn();
                try { e.SendNetworkUpdateImmediate(); } catch { }
                st.Escorts.Add(e);
            }
            // роли: первые ceil(N*0.6) — охранники у босса, остальные — приманки
            var guardN = Mathf.CeilToInt(st.Escorts.Count * 0.6f);
            for (var i = 0; i < st.Escorts.Count; i++)
                st.EscortRoles[st.Escorts[i]] = i < guardN ? 0 : 1;
        }

        // тихий деспавн без лута/наград (старая копия при респавне, таймаут жизни)
        private void DespawnBoss(BossState st)
        {
            ClearMarker(st);
            if (st.Spec.Escort.KillOnDespawn) KillEscorts(st); else st.Escorts.Clear();
            if (st.Alive) st.Entity.Kill();
            st.Entity = null;
            st.Ai = null;
            st.Livestock = null;
        }

        private void KillEscorts(BossState st)
        {
            // Kill() не дёргает OnEntityDeath — регистрируем масштаб туши вручную,
            // иначе труп свиты «вырастет» до обычного размера
            foreach (var e in st.Escorts)
                if (e != null && !e.IsDestroyed)
                {
                    RegisterCorpseScale(e, st.Spec.Escort.Scale);
                    e.Kill();
                }
            st.Escorts.Clear();
            st.EscortRoles.Clear();
        }

        private void ClearMarker(BossState st)
        {
            if (st.Marker != null && !st.Marker.IsDestroyed) st.Marker.Kill();
            st.Marker = null;
        }

        private static string GridOf(Vector3 pos)
        {
            try { return MapHelper.PositionToString(pos); }
            catch { return $"{pos.x:0}, {pos.z:0}"; }
        }

        // шаблоны анонсов: {name} = имя босса (золотым), {0} = аргумент (квадрат/минуты/добивший)
        private static string Fmt(string tpl, BossSpec spec, string arg)
        {
            return (tpl ?? "").Replace("{name}", $"<color=#E8B84B>{spec.DisplayName}</color>").Replace("{0}", arg ?? "");
        }

        // ник игрока идёт в rich-text чат — вырезаем теги, иначе ник вида
        // "<size=60>ВАЙП</size>" становится фейковым админ-анонсом на весь сервер
        private static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var ch in name)
                if (ch != '<' && ch != '>') sb.Append(ch);
            return sb.ToString();
        }

        // --- мелкие хелперы, чтобы не таскать одно и то же по коду ---

        private static ulong Uid(BasePlayer p) => (ulong)p.userID;

        // arg.Args — StringView[], сравнивать только через .ToString()
        private static string ArgAt(ConsoleSystem.Arg arg, int i) =>
            arg.Args != null && arg.Args.Length > i ? arg.Args[i].ToString() : "";

        private static float ParseF(string v, float def) =>
            float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : def;
        private static int ParseI(string v, int def) =>
            int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : def;

        private static void AddDamage(Dictionary<ulong, float> map, ulong id, float dmg) =>
            map[id] = (map.TryGetValue(id, out var cur) ? cur : 0f) + dmg;

        // админ-килл: Kill() не всегда дёргает OnEntityDeath — догоняем вручную
        private void AdminKill(BossState st)
        {
            if (st == null || !st.Alive) return;
            var b = st.Entity;
            b.Kill();
            if (st.Entity == b) HandleBossDeath(st, null, adminKill: true);
        }

        private static BasePlayer FindPlayer(ulong id) =>
            BasePlayer.activePlayerList.FirstOrDefault(p => p != null && Uid(p) == id);

        #endregion

        #region Aggro & Rage

        private void AggroTick()
        {
            var now = DateTime.UtcNow;
            foreach (var st in _bosses.Values)
            {
                if (!st.Alive) continue;

                // «зализывание ран»: нападавшие сдохли/сбежали — если RegenDelaySeconds
                // без урона, босс регенится и трекер урона сбрасывается.
                // Иначе прибежавший вторым добивает полудохлого босса за полудара.
                var regenDelay = st.Spec.RegenDelaySeconds;
                if (regenDelay > 0 && (now - st.LastDamageAt).TotalSeconds >= regenDelay)
                {
                    var ent = st.Entity;
                    // на 95%+ бой считается полностью сброшенным: ярость снята,
                    // трекер очищен — новая группа начинает с чистого листа
                    if (ent.Health() >= ent.MaxHealth() * 0.95f)
                    {
                        if (!st.ResetDone)
                        {
                            st.ResetDone = true;
                            if (st.DamageBy.Count > 0)
                                Announce(Fmt(st.Spec.ResetAnnounce, st.Spec, null));
                            st.DamageBy.Clear();
                            st.Raging = false;
                        }
                    }
                    else
                    {
                        ent.SetHealth(Mathf.Min(ent.MaxHealth(),
                            ent.Health() + ent.MaxHealth() * st.Spec.RegenPercentPerSec * 0.01f));
                    }
                }

                // деспавн по старости жизни
                if ((DateTime.UtcNow - st.SpawnedAt).TotalMinutes > st.Spec.MaxLifeMinutes)
                {
                    Announce(Fmt(st.Spec.DespawnAnnounce, st.Spec, null));
                    DespawnBoss(st);
                    ScheduleNext(st);
                    continue;
                }

                // фазы по HP: 1 пасущийся / 2 разъярённый / 3 последний рывок
                var hpFrac = st.Entity.Health() / st.Entity.MaxHealth();
                var newPhase = !st.Spec.PhasesEnabled ? 1
                    : hpFrac > st.Spec.Phase2Threshold ? 1
                    : hpFrac > st.Spec.Phase3Threshold ? 2 : 3;
                if (newPhase != st.Phase)
                {
                    st.Phase = newPhase;
                    if (newPhase == 2 && !string.IsNullOrEmpty(st.Spec.Phase2Announce))
                        Announce(Fmt(st.Spec.Phase2Announce, st.Spec, null));
                    if (newPhase == 3)
                    {
                        if (!string.IsNullOrEmpty(st.Spec.Phase3Announce))
                            Announce(Fmt(st.Spec.Phase3Announce, st.Spec, null));
                        EscortPanic(st); // часть свиты разбегается, часть входит в ярость
                    }
                    ApplyPhaseSpeed(st);
                }

                // таблица угрозы: decay ×0.92 каждые 5 сек — свежая активность
                // важнее, а набежавший сунуть ножом не станет вечной целью
                if ((now - st.LastThreatDecay).TotalSeconds >= 5)
                {
                    st.LastThreatDecay = now;
                    foreach (var k in st.ThreatBy.Keys.ToList()) st.ThreatBy[k] *= 0.92f;
                }

                // пересмотр цели по угрозе с гистерезисом (фаза 2+)
                if (st.Phase >= 2 && st.Spec.RetargetSeconds > 0 &&
                    (now - st.LastRetargetAt).TotalSeconds >= st.Spec.RetargetSeconds)
                {
                    st.LastRetargetAt = now;
                    TryRetarget(st);
                }

                // фаза 3: рывок к самому дальнему дамагеру + AoE
                if (st.Phase == 3 && st.Spec.LeapSeconds > 0 &&
                    (now - st.LastLeapAt).TotalSeconds >= st.Spec.LeapSeconds)
                {
                    st.LastLeapAt = now;
                    TryLeap(st);
                }

                // ярость
                if (!st.Raging && st.Entity.Health() <= st.Entity.MaxHealth() * st.Spec.RageThreshold)
                {
                    st.Raging = true;
                    Announce(Fmt(st.Spec.RageAnnounce, st.Spec, null));
                }

                // поведение свиты по ролям — не чаще раза в 3 сек
                if ((now - st.LastEscortTick).TotalSeconds >= 3)
                {
                    st.LastEscortTick = now;
                    EscortTick(st);
                }

                var radius = AggroOf(st);
                var target = NearestPlayer(st.Entity.transform.position, radius);
                if (target == null) continue;

                if (st.Spec.HyperAggro)
                {
                    // кабаны/хищники Gen2 убегают — снимаем факты страха и держим боевой статус
                    var npc = st.Ai;
                    if (npc != null)
                    {
                        try
                        {
                            var bb = npc.GetComponent<Rust.Ai.Gen2.BlackboardComponent>();
                            if (bb != null)
                            {
                                bb.Remove("flee");
                                bb.Remove("fleeing");
                                bb.Remove("threatened");
                                bb.Remove("flee_damage_percentage");
                            }
                        }
                        catch { }
                        try { npc.MarkHostileFor(60f); } catch { }
                    }
                }
                else if (st.Spec.ForceProvoke && st.Livestock != null)
                {
                    // скотина: форс-провокация на ближайшего игрока
                    try { st.Livestock.Provoke(target); } catch { }
                    try { st.Livestock.RememberAggressor(target); } catch { }
                }
            }
        }

        private static BasePlayer NearestPlayer(Vector3 pos, float radius)
        {
            BasePlayer closest = null;
            var best = float.MaxValue;
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.IsConnected || p.IsDead() || p.IsFlying || p.limitNetworking) continue;
                var d = Vector3.Distance(p.transform.position, pos);
                if (d < radius && d < best) { best = d; closest = p; }
            }
            return closest;
        }

        private BossState StateOf(BaseEntity ent) =>
            _bosses.Values.FirstOrDefault(st => st.Entity == ent);

        private BossState EscortOf(BaseEntity ent) =>
            _bosses.Values.FirstOrDefault(st => st.Escorts.Contains(ent));

        // скорость по фазе: ApplySpeed умножает ТЕКУЩИЕ значения агента —
        // поэтому делим на накопленный множитель, а не передаём абсолют
        private void ApplyPhaseSpeed(BossState st)
        {
            var want = st.Spec.SpeedMultiplier *
                (st.Phase == 3 ? st.Spec.P3SpeedMult : st.Phase == 2 ? st.Spec.P2SpeedMult : 1f);
            if (!st.Alive || Mathf.Abs(want - st.CurSpeedMult) <= 0.01f) return;
            ApplySpeed(st.Entity, want / st.CurSpeedMult);
            st.CurSpeedMult = want;
        }

        // P3: часть свиты разбегается (тихий деспавн), часть входит в агро
        private void EscortPanic(BossState st)
        {
            foreach (var e in st.Escorts.ToList())
            {
                if (e == null || e.IsDestroyed) continue;
                if (UnityEngine.Random.value < st.Spec.P3EscortFleeChance)
                {
                    st.Escorts.Remove(e);
                    st.EscortRoles.Remove(e);
                    e.Kill();
                }
                else
                {
                    try { (e as Rust.Ai.Gen2.BaseNPC2)?.MarkHostileFor(60f); } catch { }
                }
            }
        }

        // охрана держится у босса (leash 50м — возврат телепортом), приманки
        // провоцируются на ближайшего игрока: жечь патроны придётся
        private void EscortTick(BossState st)
        {
            if (!st.Alive) return;
            var bossPos = st.Entity.transform.position;
            foreach (var e in st.Escorts)
            {
                if (e == null || e.IsDestroyed) continue;
                var role = st.EscortRoles.TryGetValue(e, out var r) ? r : 0;
                var npc = e as Rust.Ai.Gen2.BaseNPC2;
                var lv = e as Rust.Ai.Gen2.LivestockAnimal;
                if (role == 0)
                {
                    if (Vector3.Distance(e.transform.position, bossPos) > 50f)
                    {
                        var back = bossPos + new Vector3(
                            UnityEngine.Random.Range(-6f, 6f), 0f, UnityEngine.Random.Range(-6f, 6f));
                        try { back.y = TerrainMeta.HeightMap.GetHeight(back) + 0.3f; } catch { back.y = bossPos.y; }
                        try { e.transform.position = back; } catch { }
                        continue;
                    }
                    // охрана атакует того же, кого бесится босс (или последнего стрелка)
                    var tp = st.ThreatTargetId != 0 ? FindPlayer(st.ThreatTargetId) : null;
                    if (tp == null || tp.IsDead())
                    {
                        var last = st.RecentHits.LastOrDefault();
                        tp = last != null ? FindPlayer(last.Id) : null;
                    }
                    if (tp != null && !tp.IsDead())
                    {
                        try { npc?.MarkHostileFor(30f); } catch { }
                        try { lv?.Provoke(tp); lv?.RememberAggressor(tp); } catch { }
                    }
                }
                else
                {
                    var np = NearestPlayer(e.transform.position, 60f);
                    if (np != null)
                    {
                        try { npc?.MarkHostileFor(30f); } catch { }
                        try { lv?.Provoke(np); lv?.RememberAggressor(np); } catch { }
                    }
                }
            }
        }

        // итоговый множитель исходящего урона: база × фаза × ярость
        private static float DmgMult(BossState st)
        {
            var spec = st.Spec;
            var phase = st.Phase == 3 ? spec.P3DamageMult : st.Phase == 2 ? spec.P2DamageMult : 1f;
            return spec.DamageMultiplier * phase * (st.Raging ? spec.RageDamageMultiplier : 1f);
        }

        private static float AggroOf(BossState st)
        {
            var spec = st.Spec;
            var r = st.Phase == 3 ? spec.P3AggroRadius : st.Phase == 2 ? spec.P2AggroRadius : spec.AggroRadius;
            return st.Raging ? r * 1.5f : r;
        }

        // модификатор дистанции для скоринга цели
        private static float DistMod(float d) => d <= 30 ? 1f : d <= 60 ? 0.7f : d <= 120 ? 0.4f : 0.1f;

        // попытка перевести босса на новую цель: угроза × дистанция,
        // смена только если новая цель сильнее текущей ×гистерезис — иначе
        // босс будет метаться между игроками на каждый выстрел
        private void TryRetarget(BossState st)
        {
            if (!st.Alive) return;
            var now = DateTime.UtcNow;
            st.RecentHits.RemoveAll(h => (now - h.T).TotalSeconds > 20);
            var bossPos = st.Entity.transform.position;
            BasePlayer candP = null;
            var candScore = 0f;
            ulong candId = 0;
            foreach (var kv in st.ThreatBy)
            {
                var p = FindPlayer(kv.Key);
                if (p == null || p.IsDead() || !p.IsConnected) continue;
                var score = kv.Value * DistMod(Vector3.Distance(bossPos, p.transform.position));
                if (score > candScore) { candScore = score; candP = p; candId = kv.Key; }
            }
            var curScore = st.ThreatTargetId != 0 && st.ThreatBy.TryGetValue(st.ThreatTargetId, out var ct)
                ? ct * DistMod(Vector3.Distance(bossPos, FindPlayer(st.ThreatTargetId)?.transform.position ?? bossPos))
                : 0f;
            if (candP == null || candId == st.ThreatTargetId) return;
            if (st.ThreatTargetId == 0 || candScore > curScore * st.Spec.ThreatHysteresis)
            {
                st.ThreatTargetId = candId;
                ForceTarget(st, candP);
                // свита на P2+ идёт за топ-дамагером, а не за ближайшим
                if (st.Phase >= 2)
                    foreach (var e in st.Escorts)
                        try { (e as Rust.Ai.Gen2.BaseNPC2)?.MarkHostileFor(30f); } catch { }
            }
        }

        // лучшее, что можно выжать из AI зверей извне: hostile + provoke/aggressor
        private void ForceTarget(BossState st, BasePlayer p)
        {
            if (p == null) return;
            try { st.Ai?.MarkHostileFor(60f); } catch { }
            if (st.Livestock != null)
            {
                try { st.Livestock.Provoke(p); } catch { }
                try { st.Livestock.RememberAggressor(p); } catch { }
            }
        }

        // фаза 3: рывок к самому ДАЛЬНЕМУ активному дамагеру + AoE-удар.
        // Дистанционный фоллоуфол: <=3м полный урон, <=7м половина, дальше ноль —
        // разбежались вовремя = живы. Водные звери не прыгают: телепорт
        // порвёт их водный навмеш (см. DieOffNavmesh у крокодила).
        private void TryLeap(BossState st)
        {
            if (!st.Alive || st.Spec.Spawn.NeedsWater) return;
            var bossPos = st.Entity.transform.position;
            BasePlayer tgt = null;
            var bestD = 0f;
            foreach (var id in st.DamageBy.Keys)
            {
                var p = FindPlayer(id);
                if (p == null || p.IsDead() || !p.IsConnected) continue;
                var d = Vector3.Distance(bossPos, p.transform.position);
                if (d > bestD && d <= st.Spec.P3AggroRadius + 30f) { bestD = d; tgt = p; }
            }
            if (tgt == null) return;

            var ent = st.Entity;
            var dir = (tgt.transform.position - bossPos);
            dir.y = 0;
            dir = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.forward;
            var dest = tgt.transform.position - dir * 3f;
            dest.y = TerrainMeta.HeightMap.GetHeight(dest) + 0.5f;
            try { ent.transform.position = dest; } catch { return; }

            var mult = DmgMult(st);
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || p.IsDead()) continue;
                var d = Vector3.Distance(p.transform.position, tgt.transform.position);
                var f = d <= 3f ? 1f : d <= 7f ? 0.5f : 0f;
                if (f <= 0f) continue;
                p.Hurt(st.Spec.LeapDamage * mult * f, DamageType.Bleeding, ent);
            }
            st.ShieldUntil = DateTime.UtcNow.AddSeconds(st.Spec.LeapShieldSeconds);
            ForceTarget(st, tgt);
        }

        #endregion

        #region Damage

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;

            // урон ПО боссу: броня + иммунитеты + трекинг урона + гипер-агрессия
            var st = StateOf(entity);
            if (st != null && st.Alive)
            {
                var attacker = info.InitiatorPlayer;
                if (attacker != null && !attacker.IsNpc)
                {
                    // ответка на ЛЮБОЙ урон от игрока, в т.ч. снайп с 200м:
                    // без этого босс — бесплатный мешок HP, стоящий за радиусом агро
                    try { st.Ai?.MarkHostileFor(60f); } catch { }
                    if (st.Spec.ForceProvoke && st.Livestock != null)
                    {
                        try { st.Livestock.Provoke(attacker); } catch { }
                        try { st.Livestock.RememberAggressor(attacker); } catch { }
                    }
                    if (st.Spec.HyperAggro)
                    {
                        // каждый выстрел = снос фактов страха — FSM не уйдёт в бегство
                        try
                        {
                            var bb = st.Ai?.GetComponent<Rust.Ai.Gen2.BlackboardComponent>();
                            if (bb != null) { bb.Remove("flee"); bb.Remove("fleeing"); bb.Remove("threatened"); }
                        }
                        catch { }
                    }
                }
                ApplyBossArmor(st.Spec, info);
                // щит после рывка (фаза 3): -25% входящего на пару секунд
                if (DateTime.UtcNow < st.ShieldUntil) info.damageTypes.ScaleAll(0.75f);
                if (attacker != null && !attacker.IsNpc)
                {
                    // кап по текущему HP: оверкилл (500 урона в тело на 3 HP)
                    // не должен накручивать трекер и красть «топ-дамагер»
                    var capped = Mathf.Min(info.damageTypes.Total(), entity.Health());
                    AddDamage(st.DamageBy, Uid(attacker), capped);
                    AddDamage(st.ThreatBy, Uid(attacker), capped);
                    st.RecentHits.Add(new BossState.RecentHit
                        { T = DateTime.UtcNow, Id = Uid(attacker), D = capped });
                    if (st.RecentHits.Count > 400) st.RecentHits.RemoveRange(0, st.RecentHits.Count - 200);
                    st.LastDamageAt = DateTime.UtcNow;
                    st.ResetDone = false; // бой идёт — реген отключён
                }
                return;
            }

            // урон ОТ босса
            var src = info.Initiator as BaseCombatEntity;
            if (src != null)
            {
                var atk = StateOf(src);
                if (atk != null && atk.Alive)
                {
                    // танкование тоже угроза: полученный от босса урон ×0.25
                    var victim = entity as BasePlayer;
                    if (victim != null && !victim.IsNpc)
                        AddDamage(atk.ThreatBy, Uid(victim), info.damageTypes.Total() * 0.25f);
                    info.damageTypes.ScaleAll(DmgMult(atk));
                    return;
                }
            }

            // бафф победителя на исходящий урон игрока — множитель лежит в самой записи
            var bp = info.InitiatorPlayer;
            if (bp != null && !bp.IsNpc && _buffs.TryGetValue(Uid(bp), out var buff))
            {
                if (buff.Stale) _buffs.Remove(Uid(bp)); // протухший — выкидываем
                else info.damageTypes.ScaleAll(buff.Mult);
            }
        }

        private void ApplyBossArmor(BossSpec spec, HitInfo info)
        {
            var dt = info.damageTypes;
            if (spec.ElementalImmune)
            {
                dt.Set(DamageType.Heat, 0f);
                dt.Set(DamageType.Bleeding, 0f);
                dt.Set(DamageType.Poison, 0f);
                dt.Set(DamageType.Cold, 0f);
                dt.Set(DamageType.Radiation, 0f);
            }
            dt.Scale(DamageType.Bullet, spec.BulletScale);
            dt.Scale(DamageType.Slash, spec.OtherScale);
            dt.Scale(DamageType.Blunt, spec.OtherScale);
            dt.Scale(DamageType.Stab, spec.OtherScale);
            dt.Scale(DamageType.Explosion, spec.ExplosionScale);
            dt.Scale(DamageType.AntiVehicle, spec.ExplosionScale);
            dt.Scale(DamageType.Fun_Water, 0f);
            dt.Scale(DamageType.Fall, 0f);
            dt.Scale(DamageType.Decay, 0f);
        }

        #endregion

        #region Death & Rewards

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;

            // член свиты — его труп тоже должен остаться в своём масштабе
            var esc = EscortOf(entity);
            if (esc != null)
            {
                esc.Escorts.Remove(entity);
                esc.EscortRoles.Remove(entity);
                RegisterCorpseScale(entity, esc.Spec.Escort.Scale);
                return;
            }

            var st = StateOf(entity);
            if (st != null) HandleBossDeath(st, info);
        }

        // общий обработчик смерти босса — вызывается из хука И вручную из админ-килла
        // (BaseCombatEntity.Kill() не всегда триггерит OnEntityDeath).
        // adminKill=true: админ убил — ивент завершён, лут положен.
        // Без adminKill и без урона от игроков (DieOffNavmesh, утоплен, звери) —
        // лута и наград НЕТ: иначе бесплатный мешок = вымани крокодила с навмеша.
        private void HandleBossDeath(BossState st, HitInfo info, bool adminKill = false)
        {
            // Kill() из админки/деспавна и OnEntityDeath могут сработать оба —
            // без флага было бы двойной лут, двойные награды и два анонса
            if (st.DeathHandled) return;
            st.DeathHandled = true;
            if (!st.Alive && st.Entity == null) return;
            var ent = st.Entity;

            // туша — новая сущность с дефолтным масштабом; масштабируем в OnEntitySpawned по parentEnt
            RegisterCorpseScale(ent, st.Spec.Scale);

            var killer = info?.InitiatorPlayer;
            var killerName = killer != null ? CleanName(killer.displayName) : (adminKill ? "админ" : "сервер");
            _lastKillInfo[st.Spec.Key] = $"{killerName} — {DateTime.UtcNow:HH:mm}";

            ClearMarker(st);

            var hadCombat = adminKill || killer != null || st.DamageBy.Values.Sum() > 0;
            if (hadCombat)
            {
                SpawnLoot(st.Spec, ent.transform.position + Vector3.up * 0.5f);
                GiveRewards(st, killer);
                Announce(Fmt(st.Spec.DeathAnnounce, st.Spec, killerName));
                NotifyDiscord($"{st.Spec.DisplayName} убит. Добил: {killerName}");
            }
            else
            {
                Announce(Fmt(st.Spec.QuietDeathAnnounce, st.Spec, null));
                NotifyDiscord($"{st.Spec.DisplayName} погиб без боя.");
            }

            st.Entity = null;
            st.Ai = null;
            st.Livestock = null;
            // свита: 60 сек паникует у туши, потом исчезает — не бесплатное
            // мясо и не зверинец на карте после каждого ивента
            var leftover = st.Escorts.Where(e => e != null && !e.IsDestroyed).ToList();
            st.Escorts.Clear();
            if (leftover.Count > 0)
                timer.Once(60f, () =>
                {
                    foreach (var e in leftover)
                        if (e != null && !e.IsDestroyed) e.Kill();
                });
            ScheduleNext(st);
        }

        // труп животного — отдельная entity: масштаб ставим до ухода снапшота клиентам.
        // Труп спавнится внутри Die() РАНЬШЕ OnEntityDeath — матчим по parentEnt,
        // а не по очереди; фолбэк — свежая запись по позиции смерти (parentEnt бывает пуст)
        private const float CorpseTtlMinutes = 5f;
        private const float CorpsePosTolerance = 8f;

        private void RegisterCorpseScale(BaseEntity dead, float scale)
        {
            _corpses.RemoveAll(c => c.Stale);
            _corpses.Add(new PendingCorpse
            {
                Source = dead,
                Pos = dead.transform.position,
                Scale = scale,
                Expiry = DateTime.UtcNow.AddMinutes(CorpseTtlMinutes)
            });
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var bc = entity as BaseCorpse;
            if (bc == null) return;
            // трупы игроков/учёных/лошадей — тоже BaseCorpse: не даём им подобрать
            // боссовский масштаб по позиционному фолбэку (умер рядом → труп x2)
            if (bc is PlayerCorpse || bc is NPCPlayerCorpse || bc is LootableCorpse || bc is HorseCorpse) return;

            _corpses.RemoveAll(c => c.Stale);
            if (_corpses.Count == 0) return;

            var src = bc.parentEnt;
            var hit = src != null ? _corpses.FirstOrDefault(c => c.Source == src) : null;
            if (hit == null)
                hit = _corpses.LastOrDefault(c => Vector3.Distance(bc.transform.position, c.Pos) <= CorpsePosTolerance);
            if (hit == null) return;

            _corpses.Remove(hit);
            bc.networkEntityScale = true;
            bc.transform.localScale = Vector3.one * hit.Scale;
        }

        private void SpawnLoot(BossSpec spec, Vector3 pos)
        {
            // мешок чуть в стороне от туши — коллизия x2-босса перекрывает взаимодействие
            var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            var dropPos = pos + new Vector3(Mathf.Cos(ang) * 2f, 0.6f, Mathf.Sin(ang) * 2f);

            var bag = GameManager.server.CreateEntity("assets/prefabs/misc/item drop/item_drop_backpack.prefab", dropPos, Quaternion.Euler(0f, 90f, 0f)) as DroppedItemContainer;
            if (bag == null) return;
            bag.lootPanelName = "generic_resizable";
            bag.playerName = spec.DisplayName;
            bag.playerSteamID = 0;
            bag.onlyOwnerLoot = false;
            bag.buryLeftoverItems = false;

            // свой контейнер до Spawn() — как в Backpacks: ServerInit иначе делает свой
            bag.inventory = new ItemContainer();
            bag.inventory.ServerInitialize(null, 30);
            bag.inventory.GiveUID();
            bag.inventory.entityOwner = bag;
            bag.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);

            var rng = new System.Random();
            // пул 0 — независимые шансы как раньше; пул N>0 — ровно ОДИН
            // предмет из пула (Chance = вес). Иначе дроп превращался в склад.
            var pools = new Dictionary<int, List<LootEntry>>();
            foreach (var e in spec.Rewards.Loot)
            {
                if (e.Pool > 0)
                {
                    if (!pools.TryGetValue(e.Pool, out var g)) pools[e.Pool] = g = new List<LootEntry>();
                    g.Add(e);
                    continue;
                }
                if (rng.NextDouble() <= e.Chance) AddLootItem(spec, e, rng, bag.inventory);
            }
            foreach (var g in pools.Values)
            {
                var tw = g.Sum(x => x.Chance);
                if (tw <= 0) continue;
                var roll = rng.NextDouble() * tw;
                foreach (var e in g)
                {
                    roll -= e.Chance;
                    if (roll > 0) continue;
                    AddLootItem(spec, e, rng, bag.inventory);
                    break;
                }
            }

            bag.Spawn();
            bag.ResetRemovalTime(Mathf.Max(spec.Rewards.LootLifetimeSeconds, bag.CalculateRemovalTime()));
            // спорный лут: первые N секунд мешок закрыт — победитель должен
            // дожить до открытия, чужие получают шанс на врыв
            if (spec.Rewards.LootLockSeconds > 0)
                _lockedBags[bag] = DateTime.UtcNow.AddSeconds(spec.Rewards.LootLockSeconds);
        }

        private object CanLootEntity(BasePlayer player, BaseEntity entity)
        {
            var bag = entity as DroppedItemContainer;
            if (bag == null || !_lockedBags.TryGetValue(bag, out var until)) return null;
            var left = (float)(until - DateTime.UtcNow).TotalSeconds;
            if (left <= 0) { _lockedBags.Remove(bag); return null; }
            player.ChatMessage($"{Tag} Лут откроется через {Mathf.CeilToInt(left)} сек — не мешай ему стать твоим");
            return false;
        }

        private void AddLootItem(BossSpec spec, LootEntry e, System.Random rng, ItemContainer inv)
        {
            var item = CreateLootItem(spec, e, rng.Next(e.MinAmount, e.MaxAmount + 1));
            if (item != null && !item.MoveToContainer(inv))
                item.Remove(); // мест не хватило — теряется молча, но честно
        }

        // предмет по строке лут-таблицы: обычный или чертёж (blueprintbase + цель)
        private Item CreateLootItem(BossSpec spec, LootEntry e, int amount)
        {
            if (string.IsNullOrEmpty(e.BlueprintTarget))
            {
                var item = ItemManager.CreateByName(e.ShortName, amount, e.SkinId);
                if (item == null) PrintWarning($"OctaBoss[{spec.Key}]: неизвестный предмет в луте: {e.ShortName}");
                return item;
            }
            var bpTarget = ItemManager.FindItemDefinition(e.BlueprintTarget);
            if (bpTarget == null)
            {
                PrintWarning($"OctaBoss[{spec.Key}]: неизвестная цель чертежа: {e.BlueprintTarget}");
                return null;
            }
            var bp = ItemManager.CreateByName("blueprintbase", amount);
            if (bp == null)
            {
                PrintWarning("OctaBoss: нет blueprintbase в сборке");
                return null;
            }
            bp.blueprintTarget = bpTarget.itemid;
            bp.blueprintAmount = 1;
            return bp;
        }

        // сколько дамагеров максимум получат награду за участие — защита от
        // альт-фарма: без капа каждый твинк с одним ударом тащит участие
        private const int MaxParticipationRewards = 5;

        // ключ команды: нативный Rust-тим > плагин Clans > соло по SteamID.
        // Награда идёт КОМАНДЕ, а не ласт-хиттеру/одиночке из сквада.
        private string TeamKeyOf(BasePlayer p)
        {
            try
            {
                var t = p.Team;
                if (t != null && t.teamID != 0) return $"team:{t.teamID}";
            }
            catch { }
            foreach (var pl in Interface.Oxide.RootPluginManager.GetPlugins())
                if (pl.Name == "Clans" && pl.IsLoaded)
                {
                    try
                    {
                        var tag = pl.Call("GetClanOf", p) as string;
                        if (!string.IsNullOrEmpty(tag)) return $"clan:{tag}";
                    }
                    catch { }
                }
            return $"solo:{Uid(p)}";
        }

        private void GiveRewards(BossState st, BasePlayer killer)
        {
            var spec = st.Spec;

            // топ-дамагер — носитель баффа и имя в анонсе (не ласт-хиттер)
            BasePlayer winner = null;
            var top = st.DamageBy.OrderByDescending(k => k.Value).FirstOrDefault();
            if (top.Key != 0) winner = FindPlayer(top.Key);
            if (winner == null) winner = killer; // топ-дамагер оффлайн — добившему

            // урон по командам: победителей определяем командой
            var teamDmg = new Dictionary<string, float>();
            var members = new Dictionary<string, List<KeyValuePair<ulong, float>>>();
            foreach (var kv in st.DamageBy)
            {
                var p = FindPlayer(kv.Key);
                if (p == null) continue;
                var tk = TeamKeyOf(p);
                teamDmg[tk] = (teamDmg.TryGetValue(tk, out var v) ? v : 0) + kv.Value;
                if (!members.TryGetValue(tk, out var m)) members[tk] = m = new List<KeyValuePair<ulong, float>>();
                m.Add(kv);
            }
            var winTeam = teamDmg.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;

            // основная награда — команде-победителю, внутри делится пропорционально
            // урону; доля <10% не платится (анти-альт внутри сквада)
            if (winTeam != null && spec.Rewards.Currency > 0 && members.TryGetValue(winTeam, out var winMembers))
            {
                var tTotal = winMembers.Sum(m => m.Value);
                foreach (var m in winMembers)
                {
                    var share = tTotal > 0 ? m.Value / tTotal : 0;
                    if (share < 0.10f) continue;
                    var p = FindPlayer(m.Key);
                    if (p != null) PayCurrency(p, (int)(spec.Rewards.Currency * share), "за победу (команда)");
                }
            }

            // участие: топ-5 игроков НЕ из команды-победителя выше порога
            var total = st.DamageBy.Values.Sum();
            if (total > 0 && spec.Rewards.CurrencyParticipation > 0)
            {
                var paid = 0;
                foreach (var kv in st.DamageBy.OrderByDescending(k => k.Value))
                {
                    if (paid >= MaxParticipationRewards) break;
                    if (kv.Value / total < spec.Rewards.ParticipationPct) break;
                    var p = FindPlayer(kv.Key);
                    if (p == null || TeamKeyOf(p) == winTeam) continue;
                    PayCurrency(p, spec.Rewards.CurrencyParticipation, "за участие");
                    paid++;
                }
            }

            // бафф топ-дамагеру — но его квадрат раскрывается всему серверу:
            // цена за силу. «Герой метки» и есть кульминация PvP-ивента.
            if (winner != null && spec.Rewards.BuffDamage > 1f && spec.Rewards.BuffMinutes > 0)
            {
                _buffs[Uid(winner)] = new Buff { Expires = DateTime.UtcNow.AddMinutes(spec.Rewards.BuffMinutes), Mult = spec.Rewards.BuffDamage };
                SendReply(winner, $"{Tag} Бафф победителя: +{(spec.Rewards.BuffDamage - 1f) * 100:0}% урона на {spec.Rewards.BuffMinutes:0} мин");
                Announce($"★ {CleanName(winner.displayName)} забрал бафф победителя — квадрат <color=#FFD35C>{GridOf(winner.transform.position)}</color>. Заберите его.");
            }
        }

        private void PayCurrency(BasePlayer player, int amount, string reason)
        {
            // ServerRewards -> Economics, поиск живой инстанции (PluginReference залипает)
            foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
            {
                if (p.Name == "ServerRewards" && p.IsLoaded)
                {
                    p.Call("AddPoints", Uid(player), amount);
                    SendReply(player, $"{Tag} +{amount} RP {reason}");
                    return;
                }
            }
            foreach (var p in Interface.Oxide.RootPluginManager.GetPlugins())
            {
                if (p.Name == "Economics" && p.IsLoaded)
                {
                    p.Call("Deposit", player.UserIDString, (double)amount);
                    SendReply(player, $"{Tag} +{amount} монет {reason}");
                    return;
                }
            }
        }

        #endregion

        #region Notify

        // бренд в чате — из конфига, чтобы плагин можно было отдать под чужим именем
        private string Tag => $"<color=#E8B84B>{_cfg?.Notify?.ChatName ?? "◆ OctaBoss"}</color>";

        private void Announce(string msg)
        {
            if (!_cfg.Notify.ChatGlobal) return;
            Server.Broadcast($"{Tag} {msg}");
        }

        private void NotifyDiscord(string text)
        {
            if (string.IsNullOrEmpty(_cfg.Notify.DiscordWebhook)) return;
            var body = JsonConvert.SerializeObject(new { content = $"👑 {text}" });
            webrequest.Enqueue(_cfg.Notify.DiscordWebhook, body, (code, resp) => { }, this,
                RequestMethod.POST, new Dictionary<string, string> { ["Content-Type"] = "application/json" });
        }

        #endregion

        #region Admin UI

        private const string AdminUIName = "OctaBoss_AdminUI";
        private const string ColGold = "0.91 0.72 0.29 1";
        private const string ColGoldDim = "0.75 0.6 0.25 1";
        private const string ColBg = "0.06 0.06 0.06 0.97";
        private const string ColRow = "0.10 0.10 0.10 1";
        private const string ColInput = "0.16 0.16 0.16 1";
        private const string ColText = "0.9 0.85 0.7 1";
        private const string ColWhite = "1 1 1 1";

        // выбранный вид в панели каждого админа
        private readonly Dictionary<ulong, string> _uiSel = new Dictionary<ulong, string>();

        [ChatCommand("boss_admin")]
        private void CmdAdminUI(BasePlayer player)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, "octaboss.admin"))
            {
                SendReply(player, $"{Tag} Нет прав (octaboss.admin)");
                return;
            }
            DrawAdminUI(player);
        }

        private BossSpec UiSpec(BasePlayer player)
        {
            if (_uiSel.TryGetValue(Uid(player), out var key))
            {
                var s = _cfg.Bosses.FirstOrDefault(b => b.Key == key);
                if (s != null) return s;
            }
            return _cfg.Bosses.FirstOrDefault();
        }

        // ключ -> (название, getter, setter) для выбранного вида
        private List<KeyValuePair<string, Tuple<string, Func<string>, Action<string>>>> AdminFields(BossSpec b)
        {
            var s = b.Spawn; var r = b.Rewards; var e = b.Escort;
            return new List<KeyValuePair<string, Tuple<string, Func<string>, Action<string>>>>
            {
                Field("enabled",  "Включён (1/0)",           () => b.Enabled ? "1" : "0",            v => b.Enabled = ParseI(v, b.Enabled ? 1 : 0) == 1),
                Field("health",   "Здоровье",                () => b.Health.ToString("0"),           v => b.Health = ParseF(v, b.Health)),
                Field("scale",    "Масштаб (x обычного)",    () => b.Scale.ToString("0.##"),         v => b.Scale = ParseF(v, b.Scale)),
                Field("speed",    "Скорость (x)",            () => b.SpeedMultiplier.ToString("0.##"),v => b.SpeedMultiplier = ParseF(v, b.SpeedMultiplier)),
                Field("dmg",      "Урон босса (x)",          () => b.DamageMultiplier.ToString("0.##"),v => b.DamageMultiplier = ParseF(v, b.DamageMultiplier)),
                Field("rage",     "Урон в ярости (x)",       () => b.RageDamageMultiplier.ToString("0.##"), v => b.RageDamageMultiplier = ParseF(v, b.RageDamageMultiplier)),
                Field("rageth",   "Порог ярости (0-1)",      () => b.RageThreshold.ToString("0.##"), v => b.RageThreshold = ParseF(v, b.RageThreshold)),
                Field("aggro",    "Радиус агро (м)",         () => b.AggroRadius.ToString("0"),      v => b.AggroRadius = ParseF(v, b.AggroRadius)),
                Field("hyper",    "Экстра агрессия (1/0)",   () => b.HyperAggro ? "1" : "0",         v => b.HyperAggro = ParseI(v, b.HyperAggro ? 1 : 0) == 1),
                Field("life",     "Время жизни (мин)",       () => b.MaxLifeMinutes.ToString("0"),   v => b.MaxLifeMinutes = ParseF(v, b.MaxLifeMinutes)),
                Field("escort",   "Свита (шт)",              () => e.Count.ToString(),               v => e.Count = ParseI(v, e.Count)),
                Field("escortr",  "Радиус свиты (м)",        () => e.Radius.ToString("0"),           v => e.Radius = ParseF(v, e.Radius)),
                Field("escorts",  "Масштаб свиты",           () => e.Scale.ToString("0.##"),         v => e.Scale = ParseF(v, e.Scale)),
                Field("interval", "Респаун (мин)",           () => s.IntervalMinutes.ToString("0"),  v => s.IntervalMinutes = ParseF(v, s.IntervalMinutes)),
                Field("jitter",   "Разброс респауна ± (мин)",() => s.IntervalJitterMinutes.ToString("0"), v => s.IntervalJitterMinutes = ParseF(v, s.IntervalJitterMinutes)),
                Field("warn",     "Предупреждение за (мин)", () => s.WarnBeforeMinutes.ToString("0"),v => s.WarnBeforeMinutes = ParseF(v, s.WarnBeforeMinutes)),
                Field("marker",   "Радиус маркера (0-1)",    () => s.MarkerRadius.ToString("0.###"), v => s.MarkerRadius = ParseF(v, s.MarkerRadius)),
                Field("biome",    "Только родной биом (1/0)",() => s.NativeBiome ? "1" : "0",        v => s.NativeBiome = ParseI(v, s.NativeBiome ? 1 : 0) == 1),
                Field("water",    "Спавн у воды (1/0)",      () => s.NeedsWater ? "1" : "0",         v => s.NeedsWater = ParseI(v, s.NeedsWater ? 1 : 0) == 1),
                Field("currency", "Награда RP за килл",      () => r.Currency.ToString(),            v => r.Currency = ParseI(v, r.Currency)),
            };
        }

        private static KeyValuePair<string, Tuple<string, Func<string>, Action<string>>> Field(
            string key, string label, Func<string> get, Action<string> set) =>
            new KeyValuePair<string, Tuple<string, Func<string>, Action<string>>>(
                key, Tuple.Create(label, get, set));

        private void DrawAdminUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, AdminUIName);
            var c = new CuiElementContainer();
            var spec = UiSpec(player);
            if (spec == null) return;
            _bosses.TryGetValue(spec.Key, out var st);

            var fields = AdminFields(spec);
            // панель вписываем в экран: высота фикс, высоту строки считаем под число полей
            var midX = 0.5f; var w = 0.19f;
            var top = 0.94f; var bot = 0.06f;
            var rowsTop = 0.855f;  // внутри панели: низ вкладок видов
            var rowsBot = 0.065f;  // внутри панели: верх кнопок
            var rowH = (rowsTop - rowsBot) / fields.Count;

            var root = c.Add(new CuiPanel
            {
                Image = { Color = ColBg },
                RectTransform = { AnchorMin = $"{midX - w} {bot}", AnchorMax = $"{midX + w} {top}" },
                CursorEnabled = true
            }, "Overlay", AdminUIName);
            c.Add(new CuiElement { Components = { new CuiNeedsCursorComponent() }, Parent = root });

            // шапка — золото на чёрном
            c.Add(new CuiPanel
            {
                Image = { Color = ColGold },
                RectTransform = { AnchorMin = "0 0.955", AnchorMax = "1 1" }
            }, root);
            c.Add(new CuiLabel
            {
                Text = { Text = $"OCTABOSS — {spec.DisplayName}", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "0.05 0.05 0.05 1" },
                RectTransform = { AnchorMin = "0 0.955", AnchorMax = "0.9 1" }
            }, root);
            c.Add(new CuiButton
            {
                Button = { Command = "octabossui close", Color = "0 0 0 0" },
                Text = { Text = "X", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "0.05 0.05 0.05 1" },
                RectTransform = { AnchorMin = "0.93 0.955", AnchorMax = "0.99 1" }
            }, root);

            // статус
            var status = st != null && st.Alive
                ? $"ЖИВ  |  HP {st.Entity.Health():0}/{st.Entity.MaxHealth():0}  |  {GridOf(st.Entity.transform.position)}{(st.Raging ? "  |  ЯРОСТЬ" : "")}"
                : $"нет на карте  |  респаун через {(st != null ? Math.Max(0, (int)(st.NextSpawnAt - DateTime.UtcNow).TotalMinutes) : 0)} мин";
            c.Add(new CuiLabel
            {
                Text = { Text = status, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = ColGold },
                RectTransform = { AnchorMin = "0.03 0.905", AnchorMax = "0.97 0.95" }
            }, root);

            // вкладки видов — все боссы из конфига, выбранный подсвечен
            var n = _cfg.Bosses.Count;
            if (n > 0)
            {
                var tabW = 0.96f / n;
                for (var ti = 0; ti < n; ti++)
                {
                    var b = _cfg.Bosses[ti];
                    var x0 = 0.02f + ti * tabW;
                    var sel = b.Key == spec.Key;
                    c.Add(new CuiButton
                    {
                        Button = { Command = $"octabossui tab {b.Key}", Color = sel ? ColGoldDim : ColInput },
                        Text = { Text = b.Key, FontSize = 10, Align = TextAnchor.MiddleCenter, Color = sel ? "0.05 0.05 0.05 1" : ColText },
                        RectTransform = { AnchorMin = $"{x0} 0.862", AnchorMax = $"{x0 + tabW - 0.005f} 0.898" }
                    }, root);
                }
            }

            // строки полей
            var i = 0;
            foreach (var kv in fields)
            {
                var t = kv.Value;
                var yTop = rowsTop - i * rowH;
                var yBot = yTop - rowH + 0.008f;
                var stripe = i % 2 == 0;
                c.Add(new CuiPanel
                {
                    Image = { Color = stripe ? ColRow : "0.08 0.08 0.08 1" },
                    RectTransform = { AnchorMin = $"0.02 {yBot}", AnchorMax = $"0.98 {yTop}" }
                }, root);
                c.Add(new CuiLabel
                {
                    Text = { Text = t.Item1, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = ColText },
                    RectTransform = { AnchorMin = $"0.04 {yBot}", AnchorMax = $"0.62 {yTop}" }
                }, root);
                c.Add(new CuiPanel
                {
                    Image = { Color = ColInput },
                    RectTransform = { AnchorMin = $"0.64 {yBot + 0.003f}", AnchorMax = $"0.96 {yTop - 0.003f}" }
                }, root, $"{AdminUIName}_in{i}");
                c.Add(new CuiElement
                {
                    Parent = $"{AdminUIName}_in{i}",
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Text = t.Item2(),
                            Command = $"octabossui set {kv.Key}",
                            FontSize = 11,
                            Align = TextAnchor.MiddleCenter,
                            Color = ColWhite,
                            CharsLimit = 12,
                            NeedsKeyboard = true
                        },
                        new CuiNeedsCursorComponent()
                    }
                });
                i++;
            }

            // кнопки снизу
            var btnY = 0.012f;
            AddAdminButton(c, root, $"octabossui spawn {spec.Key}", "СПАВНИТЬ", $"0.03 {btnY}", $"0.33 {btnY + 0.045f}", ColGold);
            AddAdminButton(c, root, $"octabossui kill {spec.Key}", "УБИТЬ", $"0.36 {btnY}", $"0.66 {btnY + 0.045f}", "0.6 0.15 0.1 1");
            AddAdminButton(c, root, "octabossui save", "СОХРАНИТЬ", $"0.69 {btnY}", $"0.97 {btnY + 0.045f}", "0.25 0.45 0.2 1");

            CuiHelper.AddUi(player, c);
        }

        private void AddAdminButton(CuiElementContainer c, string parent, string cmd, string text, string min, string max, string color)
        {
            c.Add(new CuiButton
            {
                Button = { Command = cmd, Color = color },
                Text = { Text = text, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.05 0.05 0.05 1" },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        [ConsoleCommand("octabossui")]
        private void CmdBossUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, "octaboss.admin")) return;
            var args = arg.Args;
            if (args == null || args.Length == 0) return;

            var redraw = true;
            switch (args[0].ToString())
            {
                case "close":
                    CuiHelper.DestroyUi(player, AdminUIName);
                    redraw = false;
                    break;
                case "tab":
                    if (args.Length >= 2) _uiSel[Uid(player)] = args[1].ToString();
                    break;
                case "spawn":
                {
                    var st = SelectedState(player);
                    if (st != null) SpawnBoss(st, player.transform.position + player.transform.forward * 15f);
                    break;
                }
                case "kill":
                    AdminKill(SelectedState(player));
                    break;
                case "save":
                    SaveConfig();
                    SendReply(player, $"{Tag} Конфиг сохранён");
                    break;
                case "set":
                    if (args.Length >= 3)
                    {
                        var key = args[1].ToString();
                        var value = string.Join(" ", args.Skip(2).Select(a => a.ToString()));
                        var spec = UiSpec(player);
                        if (spec == null) break;
                        foreach (var kv in AdminFields(spec))
                            if (kv.Key == key) { kv.Value.Item3(value); break; }
                        // если вид выключили/включили — обновляем рантайм-список
                        SyncRuntime();
                        SaveConfig();
                    }
                    break;
            }
            if (redraw) DrawAdminUI(player);
        }

        private BossState SelectedState(BasePlayer player)
        {
            var spec = UiSpec(player);
            if (spec == null) return null;
            BossState st;
            if (!_bosses.TryGetValue(spec.Key, out st))
            {
                st = new BossState { Spec = spec };
                ScheduleNext(st);
                _bosses[spec.Key] = st;
            }
            return st;
        }

        // подхватывает виды, включённые/выключенные через панель или конфиг на лету
        private void SyncRuntime()
        {
            foreach (var spec in _cfg.Bosses)
            {
                if (spec.Enabled && !_bosses.ContainsKey(spec.Key))
                {
                    var st = new BossState { Spec = spec };
                    ScheduleNext(st, first: true);
                    _bosses[spec.Key] = st;
                }
                else if (!spec.Enabled && _bosses.TryGetValue(spec.Key, out var st))
                {
                    if (st.Alive) DespawnBoss(st);
                    _bosses.Remove(spec.Key);
                }
            }
        }

        #endregion

        #region Commands

        private string BossListLine(BossState st)
        {
            var name = $"<color=#E8B84B>{st.Spec.DisplayName}</color>";
            if (st.Alive)
                return $"{name}: ЖИВ HP {st.Entity.Health():0}/{st.Entity.MaxHealth():0} @ {GridOf(st.Entity.transform.position)}{(st.Raging ? " ЯРОСТЬ" : "")}";
            var left = st.NextSpawnAt - DateTime.UtcNow;
            return $"{name}: нет, спавн через {(int)left.TotalMinutes}м";
        }

        [ChatCommand("bosstimer")]
        private void CmdTimer(BasePlayer player)
        {
            foreach (var st in _bosses.Values)
                SendReply(player, $"{Tag} {BossListLine(st)}");
        }

        [ChatCommand("bossinfo")]
        private void CmdInfo(BasePlayer player, string cmd, string[] args)
        {
            foreach (var st in _bosses.Values)
            {
                if (!st.Alive) continue;
                var dmg = st.DamageBy.OrderByDescending(k => k.Value).FirstOrDefault();
                var top = dmg.Key > 0 ? (CleanName(FindPlayer(dmg.Key)?.displayName ?? "?")) + $" ({dmg.Value:0} dmg)" : "—";
                SendReply(player, $"{Tag} <color=#E8B84B>{st.Spec.DisplayName}</color>: HP {st.Entity.Health():0}/{st.Entity.MaxHealth():0}, квадрат {GridOf(st.Entity.transform.position)}, топ-дамагер: {top}");
            }
            if (!_bosses.Values.Any(s => s.Alive))
                SendReply(player, $"{Tag} Боссов на карте нет. /bosstimer — время до спавна.");
        }

        [ChatCommand("bosslast")]
        private void CmdLast(BasePlayer player)
        {
            foreach (var spec in _cfg.Bosses)
                SendReply(player, $"{Tag} <color=#E8B84B>{spec.DisplayName}</color> — последний килл: {(_lastKillInfo.TryGetValue(spec.Key, out var v) ? v : "ещё никто")}");
        }

        private BossState FindState(string key)
        {
            BossState st;
            return _bosses.TryGetValue(key ?? "", out st) ? st : null;
        }

        // вид из аргумента команды + ворнинг в консоль при промахе
        private BossState FindStateOrWarn(ConsoleSystem.Arg arg)
        {
            var key = ArgAt(arg, 0);
            var st = FindState(key);
            if (st == null)
                Puts($"OctaBoss: вид '{key}' не найден. Доступно: {string.Join(", ", _bosses.Keys)}");
            return st;
        }

        [ConsoleCommand("octaboss.spawn")]
        private void CmdSpawn(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var st = FindStateOrWarn(arg);
            if (st == null) return;
            var p = arg.Connection?.player as BasePlayer;
            SpawnBoss(st, p != null ? (Vector3?)(p.transform.position + p.transform.forward * 15f) : null);
            Puts($"OctaBoss: принудительный спавн [{st.Spec.Key}]");
        }

        [ConsoleCommand("octaboss.kill")]
        private void CmdKill(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var st = FindStateOrWarn(arg);
            if (st == null) return;
            if (!st.Alive) { Puts($"OctaBoss[{st.Spec.Key}]: босса нет"); return; }
            AdminKill(st);
            Puts($"OctaBoss[{st.Spec.Key}]: убит администратором");
        }

        [ConsoleCommand("octaboss.next")]
        private void CmdNext(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var st = FindStateOrWarn(arg);
            if (st == null) return;
            var mins = ParseF(ArgAt(arg, 1), 30f);
            st.NextSpawnAt = DateTime.UtcNow.AddMinutes(Math.Max(0.1f, mins));
            st.Warned = true;
            Puts($"OctaBoss[{st.Spec.Key}]: следующий спавн через {mins:0.#} мин");
        }

        [ConsoleCommand("octaboss.list")]
        private void CmdList(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            foreach (var st in _bosses.Values) Puts($"OctaBoss: {BossListLine(st)}");
        }

        #endregion
    }
}
