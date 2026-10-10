using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
    //  - бегство Gen2 — реальные состояния графа FSM, не ключи blackboard:
    //    переходы в flee перенаправляем на экземпляре босса (TameFlee);
    //  - PopulateObject при сохранении админки дублировал списки лута —
    //    заменено на замену экземпляра + авто-дедупликация при загрузке.
    [Info("OctaBoss", "Octavian", "1.9.1")]
    [Description("Фреймворк животных-боссов: расписание, свита, маркер, лут, награды")]
    public class OctaBoss : RustPlugin
    {
        #region Config

        private ConfigData _cfg;

        // картинки зверей в админ-панели — мягкая зависимость
        [PluginReference] private Plugin ImageLibrary;
        [PluginReference] private Plugin OctaUI;

        // фирменные тайлы боссов (Gemini-спрайт 4x2 → images/bosses/tiles),
        // хостинг — ассет-репозиторий; переопределяются UiImageUrl в конфиге
        private const string BossTileImg = "https://raw.githubusercontent.com/Octavian-Labs/qk7f2m9x/main/z3n/";
        private static readonly HashSet<string> KnownBossTiles = new HashSet<string>
            { "bull", "boar", "wolf", "tiger", "panther", "bear", "crocodile" };

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
            [JsonProperty("Картинка для админ-панели (URL или путь, через ImageLibrary)")] public string UiImageUrl = "";
            [JsonProperty("Акцент панели \"r g b a\" (пусто = по виду зверя)")] public string UiAccent = "";
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

        // AI-сторож: Gen2-животные кроме волка/тигра не имеют State_AttackUnreachable —
        // цель на камне руды или другом недосягаемом месте их просто парализует.
        // Сторож кусает «над головой» и подталкивает застрявших к цели.
        private class WatchdogSettings
        {
            [JsonProperty("Включено")] public bool Enabled = true;
            [JsonProperty("Стоит с целью дольше (сек) — застрял")] public float StuckSeconds = 5f;
            [JsonProperty("Сдвиг меньше (м) за тик — считается стоянием")] public float MinMove = 0.6f;
            [JsonProperty("Укус «над головой»: горизонталь до цели (м)")] public float BiteHorizontal = 2.8f;
            [JsonProperty("Укус «над головой»: макс. превышение цели (м)")] public float BiteMaxHeight = 5f;
            [JsonProperty("Урон укуса")] public float BiteDamage = 25f;
            [JsonProperty("Кулдаун укуса (сек)")] public float BiteCooldown = 2.5f;
            [JsonProperty("Подталкивание к цели (м)")] public float NudgeMeters = 4f;
            [JsonProperty("Макс. подталкиваний подряд (потом оставляем в покое)")] public int MaxNudges = 3;
        }

        private class ConfigData
        {
            [JsonProperty("Виды боссов")] public List<BossSpec> Bosses = new List<BossSpec>();
            [JsonProperty("Уведомления")] public NotifySettings Notify = new NotifySettings();
            [JsonProperty("AI Watchdog")] public WatchdogSettings Watchdog = new WatchdogSettings();
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
                        UiImageUrl = BossTileImg + "bull.png",
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
                        UiImageUrl = BossTileImg + "boar.png",
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
                        UiImageUrl = BossTileImg + "wolf.png",
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
                        UiImageUrl = BossTileImg + "tiger.png",
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
                        UiImageUrl = BossTileImg + "panther.png",
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
                        UiImageUrl = BossTileImg + "bear.png",
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
                        UiImageUrl = BossTileImg + "crocodile.png",
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
            else
            {
                // Защита от старого бага раздувания лут-таблицы (PopulateObject дублировал её при каждом клике «Сохранить»)
                bool cleaned = false;
                foreach (var b in _cfg.Bosses)
                {
                    // миграция: у старых конфигов пустой UiImageUrl — подставляем фирменный тайл по ключу
                    if (b != null && string.IsNullOrEmpty(b.UiImageUrl) && KnownBossTiles.Contains(b.Key))
                    {
                        b.UiImageUrl = BossTileImg + b.Key + ".png";
                        cleaned = true;
                    }
                    if (b?.Rewards?.Loot == null || b.Rewards.Loot.Count <= 25) continue;
                    var dedupped = DeduplicateLoot(b.Rewards.Loot);
                    if (dedupped.Count < b.Rewards.Loot.Count)
                    {
                        PrintWarning($"OctaBoss[{b.Key}]: обнаружено раздувание лут-таблицы ({b.Rewards.Loot.Count} -> {dedupped.Count}). Очищено.");
                        b.Rewards.Loot = dedupped;
                        cleaned = true;
                    }
                }
                if (cleaned) SaveConfig();
            }
        }

        private static List<LootEntry> DeduplicateLoot(List<LootEntry> src)
        {
            if (src == null || src.Count <= 25) return src;
            for (int period = 15; period <= 35 && period <= src.Count / 2; period++)
            {
                if (src.Count % period != 0) continue;
                bool isRepetition = true;
                for (int i = period; i < src.Count; i++)
                {
                    var a = src[i % period];
                    var b = src[i];
                    if (a.ShortName != b.ShortName || a.BlueprintTarget != b.BlueprintTarget ||
                        a.MinAmount != b.MinAmount || a.MaxAmount != b.MaxAmount ||
                        a.Pool != b.Pool || Math.Abs(a.Chance - b.Chance) > 0.001f)
                    {
                        isRepetition = false;
                        break;
                    }
                }
                if (isRepetition)
                    return src.Take(period).ToList();
            }
            return src;
        }

        protected override void LoadDefaultConfig() => _cfg = DefaultBosses();
        // _bossRev растёт при каждой записи каталога — потребители конверта
        // могут кэшировать Boss_GetCatalog по rev
        protected override void SaveConfig()
        {
            _bossRev++;
            Config.WriteObject(_cfg = _cfg ?? DefaultBosses(), true);
        }

        #endregion

        #region State

        // активный босс-инстанс: спека + рантайм-состояние
        private class BossState
        {
            public BossSpec Live;  // живой спек из конфига — расписание/Enabled
            public BossSpec Spec;  // боевой СЛЕПОК на момент спавна — правки
                                   // конфига посреди боя живого босса не меняют
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
            public DateTime LeapStrikeAt; // !=default — замах идёт, удар в этот момент
            public Vector3 LeapDest;      // точка удара, зафиксированная в начале замаха
            public ulong LeapTargetId;
            public readonly Dictionary<ulong, float> ThreatBy = new Dictionary<ulong, float>();
            // урон за последние ~20с — «кто сейчас бесит босса»
            public class RecentHit { public DateTime T; public ulong Id; public float D; }
            public readonly List<RecentHit> RecentHits = new List<RecentHit>();
            // роли свиты: 0=охранник (держится у босса), 1=приманка (идёт на игрока)
            public readonly Dictionary<BaseCombatEntity, int> EscortRoles = new Dictionary<BaseCombatEntity, int>();
            public DateTime LastEscortTick;
            public float CurSpeedMult = 1f; // накопленный множитель скорости (фазы)
            // боевое состояние FSM, найденное при «приручении» — сторож
            // возвращает сюда босса, если он всё же оказался в flee-состоянии
            public Rust.Ai.Gen2.FSMStateBase AiCombatState;

            public bool Alive => Entity != null && !Entity.IsDestroyed && Entity.IsAlive();
        }

        private readonly Dictionary<string, BossState> _bosses = new Dictionary<string, BossState>();

        // имена босса/свиты по сущности — переживают смерть сущности,
        // потому что киллфид (OctaFeed) читает имя в том же тике гибели,
        // когда Alive уже false, а свита уже вычищена из списка
        private readonly Dictionary<BaseEntity, string> _bossNames = new Dictionary<BaseEntity, string>();

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

        // слепок живых боссов для переживания reload плагина:
        // Unload пишет netID -> key, Init подхватывает через ReattachSaved
        private class SavedBoss
        {
            public string Key;
            public ulong NetId;
            public List<ulong> EscortIds = new List<ulong>();
        }

        // AI-сторож: трекер простоя по каждой боевой сущности (netID)
        private class Watch
        {
            public Vector3 LastPos;
            public DateTime StillSince;
            public int Nudges;
            public DateTime LastBite;
            public DateTime LastForce;
        }
        private readonly Dictionary<ulong, Watch> _watch = new Dictionary<ulong, Watch>();

        // «клеймо» компонентом на GameObject сущности: переживает reload
        // плагина, потому что живёт на самой энтити, а не в стейте. По нему
        // после аварийной выгрузки (дата-файл OctaBoss_state потерян) находим
        // наших зверей и зачищаем сирот
        private class OctaBossTag : MonoBehaviour
        {
            public string SpecKey;
            public int Role; // 0 = босс, 1 = свита, 2 = маркер карты
        }
        private const string TagTypeName = "OctaBossTag";

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
            foreach (var p in UiPerms) permission.RegisterPermission(p, this);
            // дубли Key в конфиге = тихая потеря вида (state перезаписывается) — орём в лог
            foreach (var g in _cfg.Bosses.GroupBy(b => b.Key).Where(g => g.Count() > 1))
                PrintError($"OctaBoss: дублирующийся ключ вида '{g.Key}' — в спавне участвует только последний!");
            foreach (var spec in _cfg.Bosses.Where(s => s.Enabled))
            {
                var st = new BossState { Live = spec, Spec = spec };
                ScheduleNext(st, first: true);
                _bosses[spec.Key] = st;
                Puts($"OctaBoss[{spec.Key}]: следующий спавн через {(st.NextSpawnAt - DateTime.UtcNow).TotalMinutes:0} мин");
            }
            _timerSpawn = timer.Every(30f, SpawnTick);
            _timerAggro = timer.Every(1f, AggroTick);
            ReattachSaved();
            SweepOrphans();
        }

        private void Unload()
        {
            _timerSpawn?.Destroy();
            _timerAggro?.Destroy();
            // живых боссов не бросаем сиротами: пишем netID -> key в дата-файл,
            // после reload подхватываем их обратно в стейт (ReattachSaved)
            var saved = new List<SavedBoss>();
            foreach (var kv in _bosses)
            {
                var st = kv.Value;
                if (!st.Alive || st.Entity.net == null) continue;
                saved.Add(new SavedBoss
                {
                    Key = kv.Key,
                    NetId = st.Entity.net.ID.Value,
                    EscortIds = st.Escorts
                        .Where(e => e != null && !e.IsDestroyed && e.net != null)
                        .Select(e => e.net.ID.Value).ToList()
                });
            }
            if (saved.Count > 0)
                Interface.Oxide.DataFileSystem.WriteObject("OctaBoss_state", saved);
            foreach (var st in _bosses.Values) ClearMarker(st);
            foreach (var p in BasePlayer.activePlayerList)
            {
                foreach (var part in UiParts) CuiHelper.DestroyUi(p, UiN(part));
                CuiHelper.DestroyUi(p, AdminUIName);
            }
            _ui.Clear();
        }

        // подхват боссов, переживших reload (OctaBoss_state пишется в Unload)
        private void ReattachSaved()
        {
            List<SavedBoss> saved;
            try { saved = Interface.Oxide.DataFileSystem.ReadObject<List<SavedBoss>>("OctaBoss_state"); }
            catch { saved = null; }
            if (saved == null || saved.Count == 0) return;
            // файл потреблён — чистим, чтобы следующий reload не подхватил призраков
            Interface.Oxide.DataFileSystem.WriteObject("OctaBoss_state", new List<SavedBoss>());
            foreach (var sb in saved)
            {
                var spec = _cfg.Bosses.FirstOrDefault(b => b.Key == sb.Key && b.Enabled);
                if (spec == null) continue;
                BossState st;
                if (!_bosses.TryGetValue(spec.Key, out st) || st.Alive) continue;
                var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(sb.NetId)) as BaseCombatEntity;
                if (ent == null || ent.IsDestroyed || !ent.IsAlive()) continue;
                // после рестарта netID могли переиспользовать — сверяем префаб,
                // иначе подхватим чужую сущность как «босса»
                if (!string.Equals(ent.PrefabName, spec.Prefab, StringComparison.OrdinalIgnoreCase)) continue;
                AdoptBoss(st, spec, ent);
                foreach (var eid in sb.EscortIds)
                {
                    var e = BaseNetworkable.serverEntities.Find(new NetworkableId(eid)) as BaseCombatEntity;
                    if (e != null && !e.IsDestroyed && e.IsAlive() && e is Rust.Ai.Gen2.BaseNPC2)
                    {
                        st.Escorts.Add(e);
                        _bossNames[e] = $"Свита ({st.Spec.DisplayName})";
                        EnsureTag(e, spec.Key, 1);
                    }
                }
                var guardN = Mathf.CeilToInt(st.Escorts.Count * 0.6f);
                for (var i = 0; i < st.Escorts.Count; i++)
                {
                    st.EscortRoles[st.Escorts[i]] = i < guardN ? 0 : 1;
                    try { TameFlee(st.Escorts[i].GetComponent<Rust.Ai.Gen2.FSMComponent>()); } catch { }
                }
                Puts($"OctaBoss[{spec.Key}]: босс пережил reload — стейт подхвачен (свита: {st.Escorts.Count})");
                EmitBossStarted(st, restored: true);
            }
        }

        // общий код «усыновления» найденного на карте босса — и по netID
        // из дата-файла (ReattachSaved), и по клейму (SweepOrphans)
        private void AdoptBoss(BossState st, BossSpec spec, BaseCombatEntity ent)
        {
            st.Spec = CloneSpec(st.Live); // как при спавне — свой слепок
            st.Entity = ent;
            _bossNames[ent] = st.Spec.DisplayName;
            st.Ai = ent as Rust.Ai.Gen2.BaseNPC2;
            st.Livestock = ent as Rust.Ai.Gen2.LivestockAnimal;
            st.SpawnedAt = DateTime.UtcNow;
            st.LastDamageAt = DateTime.UtcNow;
            st.ResetDone = true;
            st.DeathHandled = false;
            st.CurSpeedMult = spec.SpeedMultiplier;
            EnsureTag(ent, spec.Key, 0);
            TameBossFlee(st);
            SpawnMarker(st, ent.transform.position);
        }

        // находит наше клеймо на сущности: компонент свежего билда — прямо,
        // копию от ПРОШЛОЙ загрузки плагина (тип старой сборки, GetComponent
        // её не видит) — по имени класса, данные переливаем в свежий тег
        private static OctaBossTag TagOf(BaseEntity ent)
        {
            var tag = ent.GetComponent<OctaBossTag>();
            if (tag != null) return tag;
            OctaBossTag fresh = null;
            foreach (var c in ent.GetComponents<MonoBehaviour>())
            {
                if (c == null || c.GetType().Name != TagTypeName) continue;
                if (fresh == null)
                {
                    fresh = ent.gameObject.AddComponent<OctaBossTag>();
                    fresh.SpecKey = c.GetType().GetField("SpecKey")?.GetValue(c) as string;
                    var r = c.GetType().GetField("Role")?.GetValue(c);
                    if (r is int ri) fresh.Role = ri;
                }
                UnityEngine.Object.Destroy(c);
            }
            return fresh;
        }

        // вешает/обновляет клеймо — идемпотентно, дублей не плодит
        private static void EnsureTag(BaseEntity ent, string specKey, int role)
        {
            var t = TagOf(ent) ?? ent.gameObject.AddComponent<OctaBossTag>();
            t.SpecKey = specKey;
            t.Role = role;
        }

        // свип по клеймам: что удалось усыновить — возвращаем в стейт,
        // остальных (сирот после аварийной выгрузки / потерянного дата-файла /
        // маркеры-призраки) убиваем, чтобы не бродили «дикими» по карте
        private void SweepOrphans()
        {
            var tagged = new List<KeyValuePair<BaseEntity, OctaBossTag>>();
            foreach (var nb in BaseNetworkable.serverEntities)
            {
                var ent = nb as BaseEntity;
                if (ent == null || ent.IsDestroyed) continue;
                var tag = TagOf(ent);
                if (tag != null) tagged.Add(new KeyValuePair<BaseEntity, OctaBossTag>(ent, tag));
            }
            if (tagged.Count == 0) return;

            var tracked = new HashSet<BaseEntity>();
            foreach (var st in _bosses.Values)
            {
                if (st.Entity != null) tracked.Add(st.Entity);
                foreach (var e in st.Escorts) if (e != null) tracked.Add(e);
                if (st.Marker != null) tracked.Add(st.Marker);
            }

            var adopted = 0;
            // проход 1 — боссы: свой вид, живой, без живого стейта
            foreach (var kv in tagged)
            {
                if (kv.Value.Role != 0 || tracked.Contains(kv.Key)) continue;
                var combat = kv.Key as BaseCombatEntity;
                var spec = _cfg.Bosses.FirstOrDefault(b => b.Key == kv.Value.SpecKey && b.Enabled);
                BossState st;
                if (spec == null || combat == null || !combat.IsAlive()
                    || !_bosses.TryGetValue(spec.Key, out st) || st.Alive
                    || !string.Equals(combat.PrefabName, spec.Prefab, StringComparison.OrdinalIgnoreCase))
                    continue;
                AdoptBoss(st, spec, combat);
                tracked.Add(kv.Key);
                adopted++;
                Puts($"OctaBoss[{spec.Key}]: босс подхвачен по клейму");
                EmitBossStarted(st, restored: true);
            }
            // проход 2 — свита, только при живом боссе своего вида
            foreach (var kv in tagged)
            {
                if (kv.Value.Role != 1 || tracked.Contains(kv.Key)) continue;
                var combat = kv.Key as BaseCombatEntity;
                BossState st;
                if (combat == null || !combat.IsAlive() || !(combat is Rust.Ai.Gen2.BaseNPC2)
                    || !_bosses.TryGetValue(kv.Value.SpecKey ?? "", out st) || !st.Alive)
                    continue;
                st.Escorts.Add(combat);
                st.EscortRoles[combat] = 0;
                _bossNames[combat] = $"Свита ({st.Spec.DisplayName})";
                try { TameFlee(combat.GetComponent<Rust.Ai.Gen2.FSMComponent>()); } catch { }
                tracked.Add(kv.Key);
                adopted++;
            }
            // остаток — сироты и маркеры-призраки
            var killed = 0;
            foreach (var kv in tagged)
            {
                if (tracked.Contains(kv.Key) || kv.Key.IsDestroyed) continue;
                try { kv.Key.Kill(); killed++; } catch { }
            }
            if (adopted > 0 || killed > 0)
                Puts($"OctaBoss: свип по клеймам — усыновлено {adopted}, сирот убито {killed}");
        }

        private void ScheduleNext(BossState st, bool first = false)
        {
            // расписание — по ЖИВОМУ конфигу, а не по боевому слепку:
            // правка интервала в панели вступает в силу уже для след. цикла
            var jitter = UnityEngine.Random.Range(-st.Live.Spawn.IntervalJitterMinutes, st.Live.Spawn.IntervalJitterMinutes);
            var mins = Math.Max(1f, st.Live.Spawn.IntervalMinutes + jitter);
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
                if (!st.Live.Enabled || st.Alive) continue;
                if (!st.Warned && now >= st.NextSpawnAt.AddMinutes(-st.Live.Spawn.WarnBeforeMinutes))
                {
                    st.Warned = true;
                    var vars = st.Live.WarnAnnounceVariants;
                    var tpl = vars != null && vars.Count > 0
                        ? vars[UnityEngine.Random.Range(0, vars.Count)]
                        : st.Live.WarnAnnounce;
                    Announce(Fmt(tpl, st.Live, $"{st.Live.Spawn.WarnBeforeMinutes:0}"));
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

            // боевой слепок настроек на момент спавна: правки конфига
            // в панели применятся к СЛЕДУЮЩЕМУ боссу, живой доиграет на своих
            st.Spec = CloneSpec(st.Live);

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
            EnsureTag(ent, st.Spec.Key, 0);

            st.Entity = ent;
            _bossNames[ent] = st.Spec.DisplayName;
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
            st.ShieldUntil = default(DateTime);
            st.LeapStrikeAt = default(DateTime);
            st.LeapTargetId = 0;

            ent.InitializeHealth(st.Spec.Health, st.Spec.Health);
            ent.startHealth = st.Spec.Health;
            ent.maxHealthOverride = st.Spec.Health;
            try { ent.SendNetworkUpdateImmediate(); } catch { }

            ApplySpeed(ent, st.Spec.SpeedMultiplier);
            SpawnMarker(st, pos);
            SpawnEscorts(st, pos);
            // вырезаем боевое бегство из графа FSM этого экземпляра
            TameBossFlee(st);

            var grid = GridOf(pos);
            Announce(Fmt(st.Spec.SpawnAnnounce, st.Spec, grid));
            NotifyDiscord($"{st.Spec.DisplayName} появился в квадрате {grid} ({pos.x:0},{pos.z:0})");
            EmitBossStarted(st, restored: false);
        }

        private static readonly JsonSerializerSettings JsonClonerSettings = new JsonSerializerSettings
        {
            ObjectCreationHandling = ObjectCreationHandling.Replace
        };

        // глубокая копия спека — боевой слепок босса
        private static BossSpec CloneSpec(BossSpec src)
        {
            return JsonConvert.DeserializeObject<BossSpec>(JsonConvert.SerializeObject(src), JsonClonerSettings);
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
            EnsureTag(marker, st.Spec.Key, 2);
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
            foreach (var old in st.Escorts)
                if (old != null) _bossNames.Remove(old);
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
                EnsureTag(e, st.Spec.Key, 1);
                try { e.SendNetworkUpdateImmediate(); } catch { }
                // свита — тоже без боевой паники
                try { TameFlee(e.GetComponent<Rust.Ai.Gen2.FSMComponent>()); } catch { }
                st.Escorts.Add(e);
                _bossNames[e] = $"Свита ({st.Spec.DisplayName})";
            }
            // роли: первые ceil(N*0.6) — охранники у босса, остальные — приманки
            var guardN = Mathf.CeilToInt(st.Escorts.Count * 0.6f);
            for (var i = 0; i < st.Escorts.Count; i++)
                st.EscortRoles[st.Escorts[i]] = i < guardN ? 0 : 1;
        }

        // тихий деспавн без лута/наград (старая копия при респавне, таймаут жизни)
        private void DespawnBoss(BossState st, string reason = "despawn")
        {
            // идентификатор инстанса захватываем до обнуления Entity — хук
            // OnOctaBossEnded уходит ПОСЛЕ фактического деспавна
            var wasAlive = st.Alive;
            var iid = wasAlive ? InstanceId(st) : null;
            var nm = st.Spec?.DisplayName ?? st.Live?.Key ?? "?";
            ClearMarker(st);
            if (st.Entity != null) _bossNames.Remove(st.Entity);
            foreach (var e in st.Escorts)
                if (e != null) _bossNames.Remove(e);
            // чистка AI-сторожа
            if (st.Entity?.net != null) _watch.Remove(st.Entity.net.ID.Value);
            foreach (var e in st.Escorts)
                if (e?.net != null) _watch.Remove(e.net.ID.Value);
            if (st.Spec.Escort.KillOnDespawn) KillEscorts(st); else st.Escorts.Clear();
            if (st.Alive) st.Entity.Kill();
            st.Entity = null;
            st.Ai = null;
            st.Livestock = null;
            if (wasAlive)
                EmitBossHook("OnOctaBossEnded", new JObject
                {
                    ["instanceId"] = iid,
                    ["key"] = st.Spec.Key,
                    ["name"] = nm,
                    ["reason"] = reason,
                    ["rewardsGranted"] = false,
                    ["rewardOpId"] = iid
                });
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

        // полный сброс боя: трекеры урона/угрозы, цель, щит, замах,
        // фаза и накопленная скорость — как будто боя не было
        private void ResetCombat(BossState st)
        {
            if (st.DamageBy.Count > 0 || st.ThreatBy.Count > 0)
                Announce(Fmt(st.Spec.ResetAnnounce, st.Spec, null));
            st.DamageBy.Clear();
            st.ThreatBy.Clear();
            st.RecentHits.Clear();
            st.ThreatTargetId = 0;
            st.Raging = false;
            st.ShieldUntil = default(DateTime);
            st.LeapStrikeAt = default(DateTime);
            st.Phase = 1;
            ApplyPhaseSpeed(st);
            st.ResetDone = true;
        }

        private void AggroTick()
        {
            var now = DateTime.UtcNow;
            foreach (var st in _bosses.Values)
            {
                // «зомби»: сущность ходит, а смерть не оформлена.
                // A) hp ≈ 0 при lifestate=Alive — добиваем Die(): <1 HP —
                //    это не жизнь, а застрявший остаток (урон заблокирован
                //    защитой/зоной или ушёл в обход Hurt).
                // B) lifestate=Dead, но Kill() внутри OnDied не добежал —
                //    «мёртвая» туша ходит, урон не проходит — добиваем Kill().
                var z = st.Entity;
                if (z != null && !z.IsDestroyed && (z.Health() <= 1f || !z.IsAlive()))
                {
                    Puts($"OctaBoss[{st.Spec.Key}]: зомби-детект {z.PrefabName} " +
                         $"hp={z.Health():0.###} lifestate={z.lifestate} dead={z.IsDead()}");
                    try { z.Die(); } catch { }
                    // оформляем смерть ПОКА transform жив — HandleBossDeath читает
                    // позицию для мешка с лутом; Kill() не триггерит OnEntityDeath,
                    // DeathHandled отсекает дубль при нормальной смерти через Die
                    if (st.Entity == z && !z.IsDestroyed) HandleBossDeath(st, null);
                    if (!z.IsDestroyed) { try { z.Kill(); } catch { } }
                    continue;
                }
                if (!st.Alive) continue;

                // «зализывание ран»: нападавшие сдохли/сбежали — если RegenDelaySeconds
                // без урона, босс регенится и трекер урона сбрасывается.
                // Иначе прибежавший вторым добивает полудохлого босса за полудара.
                var regenDelay = st.Spec.RegenDelaySeconds;
                if (regenDelay > 0 && (now - st.LastDamageAt).TotalSeconds >= regenDelay)
                {
                    var ent = st.Entity;
                    // реген идёт до ПОЛНОГО HP, не до порога сброса
                    if (ent.Health() < ent.MaxHealth())
                        ent.SetHealth(Mathf.Min(ent.MaxHealth(),
                            ent.Health() + ent.MaxHealth() * st.Spec.RegenPercentPerSec * 0.01f));
                    // на 95%+ бой считается полностью сброшенным — один раз
                    if (!st.ResetDone && ent.Health() >= ent.MaxHealth() * 0.95f)
                        ResetCombat(st);
                }

                // деспавн по старости жизни
                if ((DateTime.UtcNow - st.SpawnedAt).TotalMinutes > st.Spec.MaxLifeMinutes)
                {
                    Announce(Fmt(st.Spec.DespawnAnnounce, st.Spec, null));
                    DespawnBoss(st, "timeout");
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
                    EmitBossHook("OnOctaBossPhase", new JObject
                    {
                        ["instanceId"] = InstanceId(st),
                        ["key"] = st.Spec.Key,
                        ["name"] = st.Spec.DisplayName,
                        ["phase"] = newPhase,
                        ["hp"] = Mathf.CeilToInt(st.Entity.Health()),
                        ["hpMax"] = (int)st.Entity.MaxHealth()
                    });
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

                // фаза 3: замах → удар по зафиксированной точке
                if (st.Phase == 3 && st.Spec.LeapSeconds > 0)
                {
                    if (st.LeapStrikeAt != default(DateTime) && now >= st.LeapStrikeAt)
                    {
                        st.LeapStrikeAt = default(DateTime);
                        ExecuteLeap(st);
                    }
                    else if (st.LeapStrikeAt == default(DateTime) &&
                        (now - st.LastLeapAt).TotalSeconds >= st.Spec.LeapSeconds)
                    {
                        st.LastLeapAt = now;
                        WindupLeap(st);
                    }
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
                // цель по угрозе важнее ближайшего — иначе threat-таблица
                // декоративная: Provoke на ближайшего затирал бы её каждый тик
                var target = ThreatPlayer(st) ?? NearestPlayer(st.Entity.transform.position, radius);
                if (target == null) continue;

                // сторож: недосягаемая цель (камень руды) — укус/подталкивание.
                // во время замаха прыжка P3 не трогаем — босс стоит намеренно
                if (st.LeapStrikeAt == default)
                    WatchEntity(st, st.Entity, target);

                if (st.Spec.HyperAggro)
                {
                    var npc = st.Ai;
                    if (npc != null)
                    {
                        // flee/food-переходы вырезаны из графа при спавне;
                        // здесь — сторож на случай обходных путей (босс вошёл
                        // в flee/food до «приручения» или переход проскочил)
                        try
                        {
                            var fsm = npc.GetComponent<Rust.Ai.Gen2.FSMComponent>();
                            if (fsm != null && st.AiCombatState != null &&
                                (fsm.CurrentState is Rust.Ai.Gen2.State_Flee ||
                                 IsFoodState(fsm.CurrentState)))
                                fsm.SetState(st.AiCombatState);
                        }
                        catch { }
                        try { npc.MarkHostileFor(60f); } catch { }
                        // и актуальная цель — иначе Senses.Target пуст и босс
                        // предпочитает есть тушу, пока в него стреляют
                        try { npc.GetComponent<Rust.Ai.Gen2.SenseComponent>()?.TrySetTarget(target); } catch { }
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

        // текущая цель по угрозе; мёртвая/отключившаяся освобождается
        // немедленно, без ожидания таймера ретаргета и гистерезиса
        private BasePlayer ThreatPlayer(BossState st)
        {
            if (st.ThreatTargetId == 0) return null;
            var p = FindPlayer(st.ThreatTargetId);
            if (p == null || p.IsDead() || !p.IsConnected)
            {
                st.ThreatTargetId = 0;
                return null;
            }
            return p;
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

        // API для других плагинов (OctaFeed, статистика и т.д.)
        [HookMethod("GetBossName")]
        public string GetBossName(BaseCombatEntity ent)
        {
            if (ent == null) return null;
            // реестр переживает смерть сущности — киллфиду нужно имя
            // даже когда Alive уже false и свита вычищена из списка
            if (_bossNames.TryGetValue(ent, out string bossName)) return bossName;
            var st = StateOf(ent);
            if (st != null && st.Alive) return st.Spec.DisplayName;
            var esc = EscortOf(ent);
            if (esc != null && esc.Alive) return $"Свита ({esc.Spec.DisplayName})";
            return null;
        }

        [HookMethod("IsBoss")]
        public bool IsBoss(BaseCombatEntity ent) => ent != null && StateOf(ent) != null;

        // одна строка снапшота на ВИД босса: каталог + runtime в одном объекте.
        // status: active | scheduled | disabled. Общий билдер для legacy
        // GetBossStates и контрактного Boss_GetActiveInstances — поля совпадают.
        // Локация только при публичном маркере — скрытый босс не светится.
        private JObject BossRow(BossSpec spec, BossState st)
        {
            bool alive = st != null && st.Alive;
            var o = new JObject
            {
                ["key"] = spec.Key,
                ["name"] = spec.DisplayName,
                ["enabled"] = spec.Enabled,
                ["status"] = !spec.Enabled ? "disabled" : alive ? "active" : "scheduled",
                ["alive"] = alive,
                ["nextSpawnUnix"] = !alive && st != null && st.NextSpawnAt.Year > 2000
                    ? (long)new DateTimeOffset(st.NextSpawnAt, TimeSpan.Zero).ToUnixTimeSeconds()
                    : 0L,
                ["warnMinutes"] = (int)spec.Spawn.WarnBeforeMinutes,
                ["image"] = BossImagePng(spec) ?? "",
                ["accent"] = AccentOf(spec),
            };
            if (alive)
            {
                o["instanceId"] = InstanceId(st);
                // ceil: живой на долях HP не должен отображаться нулём
                o["hp"] = Mathf.CeilToInt(st.Entity.Health());
                o["hpMax"] = (int)st.Entity.MaxHealth();
                o["phase"] = st.Phase;
                o["spawnedUnix"] = (long)new DateTimeOffset(st.SpawnedAt, TimeSpan.Zero).ToUnixTimeSeconds();
                o["escortsAlive"] = st.Escorts.Count(e => e != null && !e.IsDestroyed && e.IsAlive());
                if (spec.Spawn.ShowMarker)
                    o["grid"] = GridOf(st.Entity.transform.position);
            }
            return o;
        }

        // LEGACY: снимок всех видов для внешних панелей (OctaHub «События»).
        // Deprecated — заменён на Boss_GetActiveInstances; форма строк та же.
        [HookMethod("GetBossStates")]
        public JArray GetBossStates()
        {
            var arr = new JArray();
            if (_cfg?.Bosses == null) return arr;
            foreach (var spec in _cfg.Bosses)
            {
                if (spec == null || string.IsNullOrEmpty(spec.Key)) continue;
                BossState st = null;
                _bosses.TryGetValue(spec.Key, out st);
                arr.Add(BossRow(spec, st));
            }
            return arr;
        }

        // ═══ Octa API v1 — конверт {ok,code,msg,data,rev} (Docs/OctaAPI-v1.md) ═══

        private int _bossRev; // ++ при каждом SaveConfig — кэш-ключ для каталога

        private JObject OctaOk(JObject data = null)
        {
            var o = new JObject { ["ok"] = true, ["code"] = "OK", ["rev"] = _bossRev };
            if (data != null) o["data"] = data;
            return o;
        }

        private JObject OctaFail(string code, string msg, JObject data = null)
        {
            var o = new JObject { ["ok"] = false, ["code"] = code, ["rev"] = _bossRev };
            if (!string.IsNullOrEmpty(msg)) o["msg"] = msg;
            if (data != null) o["data"] = data;
            return o;
        }

        // instanceId = "<key>:<spawnedUnix>" — уникален на каждый спавн.
        // После reload SpawnedAt пересоздаётся → новый instanceId: честно,
        // восстановленный бой — новый инстанс с точки зрения подписчиков.
        private static string InstanceId(BossState st)
        {
            long su = st.SpawnedAt.Year > 2000
                ? (long)new DateTimeOffset(st.SpawnedAt, TimeSpan.Zero).ToUnixTimeSeconds() : 0;
            return $"{st.Spec.Key}:{su}";
        }

        // широковещательные хуки жизненного цикла (Docs/OctaAPI-v1.md §OctaBoss)
        private void EmitBossHook(string hook, JObject payload)
        {
            try { Interface.CallHook(hook, payload); } catch { }
            // реестр событий Hub: любая смена жизненного цикла — повод
            // перечитать снапшот (мгновенный рефреш вместо 20-сек поллинга)
            try { Interface.CallHook("OnOctaEventChanged", "octaboss", "bosses"); } catch { }
        }

        private void EmitBossStarted(BossState st, bool restored)
        {
            var p = new JObject
            {
                ["instanceId"] = InstanceId(st),
                ["key"] = st.Spec.Key,
                ["name"] = st.Spec.DisplayName,
                ["restored"] = restored,
                ["spawnedUnix"] = (long)new DateTimeOffset(st.SpawnedAt, TimeSpan.Zero).ToUnixTimeSeconds(),
                ["escorts"] = st.Escorts.Count(e => e != null && !e.IsDestroyed)
            };
            // квадрат — только при публичном маркере (та же политика, что в снапшоте)
            if (st.Spec.Spawn.ShowMarker && st.Alive)
                p["grid"] = GridOf(st.Entity.transform.position);
            EmitBossHook("OnOctaBossStarted", p);
        }

        // каталог видов: публичные поля + всё, что нужно витрине для
        // лор-страницы (свита, биомы, монументы, награды) — Hub больше
        // не читает OctaBoss.json напрямую
        [HookMethod("Boss_GetCatalog")]
        public JObject Boss_GetCatalog()
        {
            var arr = new JArray();
            if (_cfg?.Bosses != null)
                foreach (var spec in _cfg.Bosses)
                {
                    if (spec == null || string.IsNullOrEmpty(spec.Key)) continue;
                    arr.Add(new JObject
                    {
                        ["key"] = spec.Key,
                        ["name"] = spec.DisplayName,
                        ["enabled"] = spec.Enabled,
                        ["image"] = BossImagePng(spec) ?? "",
                        ["accent"] = AccentOf(spec),
                        ["health"] = (int)spec.Health,
                        ["intervalMinutes"] = spec.Spawn.IntervalMinutes,
                        ["warnMinutes"] = (int)spec.Spawn.WarnBeforeMinutes,
                        ["escortCount"] = spec.Escort.Count,
                        ["nativeBiome"] = spec.Spawn.NativeBiome,
                        ["nativeBiomes"] = spec.Spawn.NativeBiome ? spec.Spawn.NativeBiomes : "любой",
                        ["monuments"] = spec.Spawn.MonumentNames ?? "",
                        ["needsWater"] = spec.Spawn.NeedsWater,
                        ["rewardCurrency"] = spec.Rewards.Currency,
                        ["rewardParticipation"] = spec.Rewards.CurrencyParticipation,
                    });
                }
            return OctaOk(new JObject { ["bosses"] = arr });
        }

        // живые бои + расписание. data.bosses — слитный вид (строка на вид,
        // как legacy GetBossStates); instances — только живые; schedule —
        // запланированные и выключенные виды
        [HookMethod("Boss_GetActiveInstances")]
        public JObject Boss_GetActiveInstances()
        {
            var all = GetBossStates();
            var inst = new JArray(); var sched = new JArray();
            foreach (var o in all.OfType<JObject>())
                if (o["alive"]?.Value<bool>() == true) inst.Add(o); else sched.Add(o);
            return OctaOk(new JObject
            {
                ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["bosses"] = all,
                ["instances"] = inst,
                ["schedule"] = sched
            });
        }

        // детали одного вида/инстанса: BossRow + для админа (octaboss.admin
        // или доверенный вызов admin=true) — точная позиция и таблица урона
        [HookMethod("Boss_GetInstance")]
        public JObject Boss_GetInstance(string id, object admin)
        {
            if (string.IsNullOrEmpty(id))
                return OctaFail("INVALID_ARGUMENT", "Пустой ключ вида");
            var key = id.Split(':')[0]; // принимаем и key, и instanceId
            var spec = _cfg?.Bosses?.FirstOrDefault(b =>
                string.Equals(b?.Key, key, StringComparison.OrdinalIgnoreCase));
            if (spec == null) return OctaFail("NOT_FOUND", $"Вид '{key}' не найден");
            BossState st = null;
            _bosses.TryGetValue(spec.Key, out st);
            var o = BossRow(spec, st);
            bool isAdmin = (admin is bool ab && ab) ||
                (admin is BasePlayer ap &&
                 permission.UserHasPermission(ap.UserIDString, "octaboss.admin"));
            if (isAdmin && st != null && st.Alive)
            {
                var p = st.Entity.transform.position;
                o["pos"] = new JArray(p.x, p.y, p.z);
                o["raging"] = st.Raging;
                var dmg = new JObject();
                foreach (var kv in st.DamageBy) dmg[kv.Key.ToString()] = (int)kv.Value;
                o["damageBy"] = dmg;
            }
            return OctaOk(o);
        }

        // идентификация сущности для киллфида/панелей: boss | escort | NOT_FOUND.
        // retained=true — имя восстановлено из посмертного реестра (_bossNames):
        // киллфид резолвит сущность уже после смерти, когда Alive==false и
        // свита вычищена из списков.
        [HookMethod("Boss_ResolveEntity")]
        public JObject Boss_ResolveEntity(BaseCombatEntity ent)
        {
            if (ent == null) return OctaFail("INVALID_ARGUMENT", "Сущность не задана");
            var st = StateOf(ent);
            if (st != null)
                return OctaOk(new JObject
                {
                    ["role"] = "boss",
                    ["key"] = st.Spec.Key,
                    ["name"] = st.Spec.DisplayName,
                    ["instanceId"] = InstanceId(st),
                    ["alive"] = st.Alive,
                    ["retained"] = !st.Alive
                });
            var esc = EscortOf(ent);
            if (esc != null)
                return OctaOk(new JObject
                {
                    ["role"] = "escort",
                    ["key"] = esc.Spec.Key,
                    ["bossName"] = esc.Spec.DisplayName,
                    ["name"] = $"Свита ({esc.Spec.DisplayName})",
                    ["instanceId"] = InstanceId(esc),
                    ["alive"] = esc.Alive
                });
            // посмертный реестр: имена живут ~2 сек после смерти босса и
            // ~60 сек для отставшей свиты. Роль различаем по формату записи.
            if (_bossNames.TryGetValue(ent, out string nm))
            {
                var d = new JObject
                {
                    ["name"] = nm,
                    ["role"] = nm.StartsWith("Свита (") ? "escort" : "boss",
                    ["retained"] = true,
                    ["alive"] = false
                };
                return OctaOk(d);
            }
            return OctaFail("NOT_FOUND", "Не босс и не свита");
        }

        // ── Провайдер событий OctaHub (Docs/OctaAPI-v1.md §Реестр событий) ──
        // Одна категория "bosses": карточка раздела СОБЫТИЯ. detail="bosses" —
        // встроенный вид Hub (сетка видов + лор); items — те же строки, что и
        // data.bosses, для потребителей, рисующих свой детал-вид.
        [HookMethod("Events_GetSnapshot")]
        public JObject Events_GetSnapshot(BasePlayer player)
        {
            var all = GetBossStates();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int aliveN = 0; long nextAt = long.MaxValue; string nextName = null;
            bool anyEnabled = false;
            foreach (var o in all.OfType<JObject>())
            {
                if (o["enabled"]?.Value<bool>() != true) continue;
                anyEnabled = true;
                if (o["alive"]?.Value<bool>() == true) { aliveN++; continue; }
                long ns = o["nextSpawnUnix"]?.Value<long>() ?? 0;
                if (ns > 0 && ns < nextAt) { nextAt = ns; nextName = o["name"]?.ToString(); }
            }
            string status, tone;
            if (aliveN > 0) { status = $"ЖИВЫХ СЕЙЧАС: {aliveN}"; tone = "red"; }
            else if (nextName != null)
            { status = $"ближайший: {nextName} · {FmtDur(nextAt - now)}"; tone = "accent"; }
            else if (anyEnabled) { status = "ожидают расписания"; tone = "muted"; }
            else { status = "все выключены"; tone = "muted"; }

            return OctaOk(new JObject
            {
                ["categories"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = "bosses",
                        ["title"] = "БОССЫ ОСТРОВА",
                        ["image"] = "",            // Hub подставит тайл по id
                        ["active"] = aliveN > 0,
                        ["status"] = status,
                        ["tone"] = tone,
                        ["detail"] = "bosses",     // встроенный вид Hub
                        ["items"] = all
                    }
                }
            });
        }

        // "12м" / "1ч 23м" / "2д 5ч" — как FmtLeft в OctaHub, дублируем:
        // провайдер сам форматирует статусную строку категории.
        private static string FmtDur(long seconds)
        {
            if (seconds <= 0) return "скоро";
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}д {ts.Hours}ч";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}ч {ts.Minutes}м";
            return $"{(int)ts.TotalMinutes}м";
        }

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
                    _bossNames.Remove(e);
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
                // тот же зомби-детект, что у босса: hp < 1 или lifestate=Dead,
                // а сущность не убрана — добиваем (см. AggroTick)
                if (e.Health() <= 1f || !e.IsAlive())
                {
                    try { e.Die(); } catch { }
                    if (!e.IsDestroyed) try { e.Kill(); } catch { }
                    continue;
                }
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
                        try { npc?.GetComponent<Rust.Ai.Gen2.SenseComponent>()?.TrySetTarget(tp); } catch { }
                        try { lv?.Provoke(tp); lv?.RememberAggressor(tp); } catch { }
                        WatchEntity(st, e, tp);
                    }
                }
                else
                {
                    var np = NearestPlayer(e.transform.position, 60f);
                    if (np != null)
                    {
                        try { npc?.MarkHostileFor(30f); } catch { }
                        try { npc?.GetComponent<Rust.Ai.Gen2.SenseComponent>()?.TrySetTarget(np); } catch { }
                        try { lv?.Provoke(np); lv?.RememberAggressor(np); } catch { }
                        WatchEntity(st, e, np);
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

        // лучшее, что можно выжать из AI зверей извне: hostile + реальная
        // установка цели в SenseComponent + provoke/aggressor для скотины
        private void ForceTarget(BossState st, BasePlayer p)
        {
            if (p == null) return;
            try { st.Ai?.MarkHostileFor(60f); } catch { }
            try { st.Ai?.GetComponent<Rust.Ai.Gen2.SenseComponent>()?.TrySetTarget(p); } catch { }
            if (st.Livestock != null)
            {
                try { st.Livestock.Provoke(p); } catch { }
                try { st.Livestock.RememberAggressor(p); } catch { }
            }
        }

        // ── AI Watchdog: недосягаемые цели и застревание ───────────────────
        // Ванильный Gen2 умеет выпрыгнуть на камень только у волка/тигра —
        // остальные звери встают у подножия навсегда. Здесь два клапана:
        // 1) цель «над головой» вплотную — прямой укус + пуш (как у волка);
        // 2) стоит с целью вне досягаемости > StuckSeconds — шаг телепортом
        //    в её сторону по земле (ограничено MaxNudges подряд).
        private void WatchEntity(BossState st, BaseCombatEntity ent, BasePlayer target)
        {
            var wd = _cfg?.Watchdog;
            if (wd == null || !wd.Enabled) return;
            if (ent == null || ent.IsDestroyed || target == null || target.IsDead() || !target.IsConnected) return;
            if (ent.net == null) return;

            var now = DateTime.UtcNow;
            var pos = ent.transform.position;
            var tpos = target.transform.position;

            Watch w;
            var id = ent.net.ID.Value;
            if (!_watch.TryGetValue(id, out w)) w = _watch[id] = new Watch { LastPos = pos };

            var dx = tpos.x - pos.x; var dz = tpos.z - pos.z;
            var dHoriz = Mathf.Sqrt(dx * dx + dz * dz);
            var dy = tpos.y - pos.y;
            var mx = pos.x - w.LastPos.x; var mz = pos.z - w.LastPos.z;
            var moved = Mathf.Sqrt(mx * mx + mz * mz);
            w.LastPos = pos;

            if (moved > wd.MinMove) { w.StillSince = default(DateTime); w.Nudges = 0; }
            else if (w.StillSince == default(DateTime)) w.StillSince = now;

            // 1) игрок прямо над головой на камне — укус и пуш, как у волка:
            //    сбивает с насеста, проблема рассасывается сама собой
            if (dHoriz <= wd.BiteHorizontal && dy > 0.8f && dy <= wd.BiteMaxHeight
                && (now - w.LastBite).TotalSeconds >= wd.BiteCooldown)
            {
                w.LastBite = now;
                try { target.OnAttacked(wd.BiteDamage, DamageType.Bite, ent, false); } catch { }
                try { target.DoPush(ent.transform.forward * 10f + Vector3.up * 3f); } catch { }
                return;
            }

            // 2) стоит с живой целью вне досягаемости — подтолкнуть к ней
            if (w.StillSince != default(DateTime) &&
                (now - w.StillSince).TotalSeconds >= wd.StuckSeconds &&
                dHoriz > wd.BiteHorizontal + 0.5f)
            {
                w.StillSince = now;
                if (w.Nudges >= wd.MaxNudges) return;
                if (NudgeToward(ent, target, wd.NudgeMeters)) w.Nudges++;
                else w.Nudges = wd.MaxNudges; // тупик — не дёргаем больше
            }

            // 4) босс выкинул цель и ушёл пастись: FSM дропает недосягаемую
            // цель (камень), а сторож на возврат стоял только против flee.
            // С живым таргетом любое «мирное» состояние — возврат в бой.
            if (ent == st.Entity && st.AiCombatState != null)
            {
                var fsm2 = ent.GetComponent<Rust.Ai.Gen2.FSMComponent>();
                var cur2 = fsm2?.CurrentState;
                if (cur2 != null && cur2 != st.AiCombatState && IsPassiveState(cur2))
                    try { fsm2.SetState(st.AiCombatState); } catch { }
            }

            // 3) скотина: входы в боевую ветку FSM (warn→chargeRun→headbutt)
            // висят на protective/canReach-переходах — цель на бочке ветку не
            // открывает, бык остаётся пастбищным. Пинаем в боевые состояния
            // напрямую — headbutt рядом, chargeRun вдогонку.
            var lfsm = ent.GetComponent<Rust.Ai.Gen2.LivestockAnimalFSM>();
            if (lfsm != null && (now - w.LastForce).TotalSeconds >= 2f)
            {
                var cur = lfsm.CurrentState;
                var inCombat = cur == lfsm.chargeRun || cur == lfsm.headbutt ||
                               cur == lfsm.kick || cur == lfsm.kickBehind ||
                               cur == lfsm.warn || cur == lfsm.warnApproach;
                if (!inCombat && dHoriz <= 30f && dy < 2f)
                {
                    try { lfsm.SetState(dHoriz <= 2.2f ? (Rust.Ai.Gen2.FSMStateBase)lfsm.headbutt
                                                       : lfsm.chargeRun); } catch { }
                    w.LastForce = now;
                }
            }
        }

        // шаг телепортом в сторону цели по земле (как у свиты возврат к боссу):
        // рейкаст вниз из точки-кандидата, без провала > 6м
        private static bool NudgeToward(BaseCombatEntity ent, BasePlayer target, float meters)
        {
            var pos = ent.transform.position;
            var tp = target.transform.position;
            var dir = new Vector3(tp.x - pos.x, 0f, tp.z - pos.z);
            var dist = dir.magnitude;
            if (dist < 0.5f) return false;
            dir /= dist;
            var cand = pos + dir * Mathf.Min(meters, dist - 1f);
            RaycastHit hit;
            if (Physics.Raycast(cand + Vector3.up * 8f, Vector3.down, out hit, 16f,
                    LayerMask.GetMask("Terrain", "World", "Default", "Construction", "Deployed")))
                cand.y = hit.point.y + 0.3f;
            else
            {
                try { cand.y = TerrainMeta.HeightMap.GetHeight(cand) + 0.3f; }
                catch { return false; }
            }
            if (Mathf.Abs(cand.y - pos.y) > 6f) return false; // отвес — не лезем
            try { ent.transform.position = cand; } catch { return false; }
            try { (ent as Rust.Ai.Gen2.BaseNPC2)?.GetComponent<Rust.Ai.Gen2.SenseComponent>()?.TrySetTarget(target); } catch { }
            return true;
        }

        #region Gen2 FSM — вырезание боевого бегства

        // Бегство в Gen2 — это реальные состояния графа (State_Flee и
        // наследники: FleeToHide у кошек, FleeFire, HerdFlee у скотины), а не
        // ключи blackboard: чистка "flee"/"threatened" переход не отменяет.
        // Поэтому правим граф НА ЭКЗЕМПЛЯРЕ босса (списки transitions/
        // endTransitions у FSMStateBase публичные): tick-переходы в flee/food
        // УДАЛЯЕМ (условие страха просто теряет силу — редирект в roam давал
        // пинг-понг roam↔combat и зверь замирал), end-переходы перенаправляем
        // (их удалять нельзя — узел останется без выхода): flee→бой,
        // food→roam. Обычных животных не трогаем. Крокодила пропускаем
        // целиком: его «бегство» — GoBackToWater, засадный цикл, не паника.

        // имена полей боевого «резюме»-состояния в порядке предпочтения:
        // волк→fastApproach, тигр→approach, скотина→warnApproach, зверь→chase
        private static readonly string[] CombatStateNames =
        {
            "approach", "fastApproach", "warnApproach", "reacCircle", "comboCircle",
            "charge", "chargeRun", "chase", "fastSneak", "observe", "attack"
        };
        private static readonly string[] RoamStateNames = { "roam", "randomIdle", "idle", "graze" };

        private static bool IsFleeState(Rust.Ai.Gen2.FSMStateBase s) =>
            s is Rust.Ai.Gen2.State_Flee && s.GetType().Name != "State_GoBackToWater";

        // «мирные» состояния, из которых босс с живой целью должен
        // выходить в бой: бегство/еда + пастбищный roam/idle/sleep/patrol.
        // По имени типа — безопасно для любых FSM-графов.
        private static bool IsPassiveState(Rust.Ai.Gen2.FSMStateBase s)
        {
            if (s == null) return false;
            if (IsFleeState(s) || IsFoodState(s)) return true;
            var n = s.GetType().Name;
            return n.IndexOf("Roam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Idle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Patrol", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Sleep", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Graze", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // пищевая ветка: босс — ивент, а не фермер. Туши/корм он не ест —
        // иначе с отсутствующей целью в сенсорах валяется у жратвы,
        // пока в него стреляют. (У крока BringFoodBackToWater есть, но он
        // пропускается целиком — засадный цикл не трогаем.)
        private static readonly HashSet<string> FoodStateNames = new HashSet<string>
        {
            "State_ApproachFood", "State_EatFood", "State_DragCorpse",
            "State_BringFoodBackToWater"
        };
        private static bool IsFoodState(Rust.Ai.Gen2.FSMStateBase s) =>
            s != null && FoodStateNames.Contains(s.GetType().Name);

        private static Rust.Ai.Gen2.FSMStateBase FsmStateByField(
            Rust.Ai.Gen2.FSMComponent fsm, string[] names)
        {
            const System.Reflection.BindingFlags fl =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            var t = fsm.GetType();
            foreach (var n in names)
            {
                var f = t.GetField(n, fl);
                if (f != null && typeof(Rust.Ai.Gen2.FSMStateBase).IsAssignableFrom(f.FieldType))
                {
                    var s = f.GetValue(fsm) as Rust.Ai.Gen2.FSMStateBase;
                    if (s != null) return s;
                }
            }
            return null;
        }

        // точка входа для босса: правит граф и запоминает боевое состояние
        private void TameBossFlee(BossState st)
        {
            st.AiCombatState = null;
            if (st.Entity == null || st.Entity.IsDestroyed) return;
            st.AiCombatState = TameFlee(st.Entity.GetComponent<Rust.Ai.Gen2.FSMComponent>());
        }

        // Хирургия графа на экземпляре. Возвращает боевое состояние для
        // сторожа (null — FSM нет/не тронут). Переходы в flee переписываем
        // на боевое или roam-состояние — сами flee-узлы становятся
        // недостижимыми и больше не входят в работу.
        private Rust.Ai.Gen2.FSMStateBase TameFlee(Rust.Ai.Gen2.FSMComponent fsm)
        {
            if (fsm == null || fsm is Rust.Ai.Gen2.CrocodileFSM) return null;
            const System.Reflection.BindingFlags fl =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;

            // собираем все узлы графа: поля FSM-компонента + parent-цепочки
            // (анонимные State_Nothing-ветки висят только на parent) + BFS
            // по переходам; повторяем до стабилизации — у найденных
            // parent-узлов могут быть свои переходы в новые узлы
            var states = new HashSet<Rust.Ai.Gen2.FSMStateBase>();
            foreach (var f in fsm.GetType().GetFields(fl))
                if (typeof(Rust.Ai.Gen2.FSMStateBase).IsAssignableFrom(f.FieldType) &&
                    f.GetValue(fsm) is Rust.Ai.Gen2.FSMStateBase s) states.Add(s);
            if (fsm.CurrentState != null) states.Add(fsm.CurrentState);
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var s in states.ToList())
                    for (var p = s.parent; p != null; p = p.parent)
                        if (states.Add(p)) grew = true; else break;
                var q = new Queue<Rust.Ai.Gen2.FSMStateBase>(states);
                while (q.Count > 0)
                {
                    var s = q.Dequeue();
                    if (s.transitions != null)
                        foreach (var t in s.transitions)
                            if (t.dstState != null && states.Add(t.dstState)) { q.Enqueue(t.dstState); grew = true; }
                    if (s.endTransitions != null)
                        foreach (var t in s.endTransitions)
                            if (t.dstState != null && states.Add(t.dstState)) { q.Enqueue(t.dstState); grew = true; }
                }
            }

            var combat = FsmStateByField(fsm, CombatStateNames);
            var roam = FsmStateByField(fsm, RoamStateNames);
            if (combat == null && roam == null) return null;
            if (combat == null) combat = roam;
            if (roam == null) roam = combat;

            int rewired = 0;
            foreach (var s in states)
            {
                if (IsFleeState(s) || IsFoodState(s)) continue;
                if (s.transitions != null)
                for (var i = s.transitions.Count - 1; i >= 0; i--)
                {
                    var e = s.transitions[i];
                    if (e.dstState == null || (!IsFleeState(e.dstState) && !IsFoodState(e.dstState))) continue;
                    // tick-переходы просто УДАЛЯЕМ: условие (страх, взгляд,
                    // сейф-зона, вода, еда) теряет силу, а состояние не
                    // перескакивает. Редирект в roam здесь опасен — при
                    // постоянно истинном условии и живом таргете получался
                    // пинг-понг roam↔combat, и зверь замирал.
                    s.transitions.RemoveAt(i);
                    rewired++;
                }
                if (s.endTransitions != null)
                for (var i = 0; i < s.endTransitions.Count; i++)
                {
                    var e = s.endTransitions[i];
                    if (e.dstState == null || (!IsFleeState(e.dstState) && !IsFoodState(e.dstState))) continue;
                    // end-переход удалять нельзя — узел может остаться без
                    // выхода. flee → бой («ударил-убежал» → дожимает),
                    // food → roam (доедает и спокойно уходит)
                    var dst = IsFoodState(e.dstState) ? roam : combat;
                    s.endTransitions[i] = (e.transition, dst, e.status);
                    rewired++;
                }
            }

            // у базового зверя страх по здоровью — сериализованное поле;
            // обнуляем как страховку (переход уже перенаправлен, но дёшево)
            try
            {
                fsm.GetType().GetField("fleeBelowHealthFraction", fl)?.SetValue(fsm, 0f);
            }
            catch { }

            // если зверь УЖЕ в бегстве или жрёт тушу (подхват после
            // reload) — штатный выход через SetState: корректно
            // отработают OnStateExit/Enter
            try
            {
                if (fsm.CurrentState != null &&
                    (IsFleeState(fsm.CurrentState) || IsFoodState(fsm.CurrentState)))
                    fsm.SetState(combat);
            }
            catch { }

            if (rewired > 0)
                Puts($"OctaBoss: {fsm.GetType().Name} — {rewired} flee/food-переход(ов) вырезано " +
                     $"(бой→{combat?.Name}, отход→{roam?.Name})");
            return combat;
        }

        #endregion

        // фаза 3: рывок к самому ДАЛЬНЕМУ активному дамагеру + AoE-удар.
        // Дистанционный фоллоуфол: <=3м полный урон, <=7м половина, дальше ноль —
        // разбежались вовремя = живы. Водные звери не прыгают: телепорт
        // порвёт их водный навмеш (см. DieOffNavmesh у крокодила).
        // фаза 3, рывок: замах 1.5 сек с ФИКСАЦИЕЙ точки удара — игрок,
        // вышедший из зоны за замах, удар не получает. Сквозь постройки
        // не прыгаем. Водные виды не прыгают вообще — телепорт рвёт
        // их водный навмеш (DieOffNavmesh у крокодила).
        private void WindupLeap(BossState st)
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

            var dir = tgt.transform.position - bossPos; dir.y = 0;
            dir = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.forward;
            var dest = tgt.transform.position - dir * 3f;
            dest.y = TerrainMeta.HeightMap.GetHeight(dest) + 0.5f;

            // через стены и внутрь баз не прыгаем — отказ рывка, а не
            // незаметный телепорт сквозь фундамент
            var flat = dest - bossPos; flat.y = 0;
            try
            {
                RaycastHit hit;
                if (Physics.Raycast(bossPos + Vector3.up, flat.normalized, out hit,
                        flat.magnitude, LayerMask.GetMask("Construction", "Deployed")))
                    return;
            }
            catch { }

            st.LeapDest = dest;
            st.LeapTargetId = Uid(tgt);
            st.LeapStrikeAt = DateTime.UtcNow.AddSeconds(1.5);
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || p.IsDead() || !p.IsConnected) continue;
                if (Vector3.Distance(p.transform.position, bossPos) <= st.Spec.P3AggroRadius + 40f)
                    SendReply(p, $"{Tag} ★ <color=#E8B84B>{st.Spec.DisplayName}</color> ГОТОВИТ РЫВОК — расходитесь!");
            }
        }

        private void ExecuteLeap(BossState st)
        {
            if (!st.Alive) return;
            var dest = st.LeapDest;
            var ent = st.Entity;
            try { ent.transform.position = dest; } catch { return; }
            try { ent.SendNetworkUpdateImmediate(); } catch { }

            // Множитель исходящего урона применяет OnEntityTakeDamage
            // (ветка «урон ОТ босса») — сюда идёт голая база × дистанция,
            // иначе был двойной множитель (~2200 вместо ~360 до брони).
            // Радиус считаем от фактической точки удара.
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || p.IsDead()) continue;
                var d = Vector3.Distance(p.transform.position, dest);
                var f = d <= 3f ? 1f : d <= 7f ? 0.5f : 0f;
                if (f <= 0f) continue;
                var before = p.Health();
                p.Hurt(st.Spec.LeapDamage * f, DamageType.Slash, ent);
                Puts($"OctaBoss[{st.Spec.Key}]: рывок -> {CleanName(p.displayName)}: " +
                     $"база {st.Spec.LeapDamage:0} ×{f} ×mult={DmgMult(st):0.##} -> снято {before - p.Health():0}");
            }
            st.ShieldUntil = DateTime.UtcNow.AddSeconds(st.Spec.LeapShieldSeconds);
            var tgt = FindPlayer(st.LeapTargetId);
            if (tgt != null) ForceTarget(st, tgt);
        }

        #endregion

        #region Damage

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;

            // урон ПО боссу/свите: броня + иммунитеты + трекинг урона + гипер-агрессия.
            // EscortOf важен: урон по свите раньше молча игнорировался — ни
            // угрозы, ни цели; бьёшь быка-охранника, а он «как ни в чём не бывало»
            var st = StateOf(entity) ?? EscortOf(entity);
            if (st != null && st.Alive)
            {
                var attacker = info.InitiatorPlayer;
                if (attacker != null && !attacker.IsNpc)
                {
                    // целим ту сущность, КОТОРУЮ ударили: по свите бил — свита и дерётся
                    try { (entity as Rust.Ai.Gen2.BaseNPC2)?.MarkHostileFor(60f); } catch { }
                    try { entity.GetComponent<Rust.Ai.Gen2.SenseComponent>()?.TrySetTarget(attacker); } catch { }
                    var lvHit = entity as Rust.Ai.Gen2.LivestockAnimal;
                    if (lvHit != null)
                    {
                        try { lvHit.Provoke(attacker); } catch { }
                        try { lvHit.RememberAggressor(attacker); } catch { }
                    }
                    // а босс получает тот же пинок — бой общий
                    if (entity != st.Entity)
                    {
                        try { st.Ai?.MarkHostileFor(60f); } catch { }
                        try { st.Ai?.GetComponent<Rust.Ai.Gen2.SenseComponent>()?.TrySetTarget(attacker); } catch { }
                    }
                }
                ApplyBossArmor(st.Spec, info);
                // щит после рывка (фаза 3): -25% входящего на пару секунд
                if (DateTime.UtcNow < st.ShieldUntil) info.damageTypes.ScaleAll(0.75f);
                if (attacker != null && !attacker.IsNpc)
                {
                    // нулевой урон (полный иммунитет/отмена) — это НЕ бой:
                    // LastDamageAt не трогаем, иначе заблокированный выстрел
                    // вечно откладывал бы реген
                    var dealt = info.damageTypes.Total();
                    if (dealt > 0.01f)
                    {
                        // кап по текущему HP: оверкилл (500 урона в тело на 3 HP)
                        // не должен накручивать трекер и красть «топ-дамагер»
                        var capped = Mathf.Min(dealt, entity.Health());
                        AddDamage(st.DamageBy, Uid(attacker), capped);
                        AddDamage(st.ThreatBy, Uid(attacker), capped);
                        st.RecentHits.Add(new BossState.RecentHit
                            { T = DateTime.UtcNow, Id = Uid(attacker), D = capped });
                        if (st.RecentHits.Count > 400) st.RecentHits.RemoveRange(0, st.RecentHits.Count - 200);
                        st.LastDamageAt = DateTime.UtcNow;
                        st.ResetDone = false; // бой идёт — реген отключён
                    }
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
                // имя держим ещё пару секунд — киллфид читает его в этом же тике смерти
                timer.Once(2f, () => _bossNames.Remove(entity));
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
            // имя босса держим пару секунд после смерти — киллфид читает его в этом же тике
            if (ent != null) timer.Once(2f, () => _bossNames.Remove(ent));

            // туша — новая сущность с дефолтным масштабом; масштабируем в OnEntitySpawned по parentEnt
            RegisterCorpseScale(ent, st.Spec.Scale);

            var killer = info?.InitiatorPlayer;
            var killerName = killer != null ? CleanName(killer.displayName) : (adminKill ? "админ" : "сервер");
            _lastKillInfo[st.Spec.Key] = $"{killerName} — {DateTime.UtcNow:HH:mm}";

            ClearMarker(st);

            // «бой» = реальный киллер, админ-килл или СВЕЖИЙ урон (60 сек).
            // Иначе один давний выстрел легализует лут при смерти от
            // окружения (DieOffNavmesh, утопление) — эксплойт выманивания.
            var recentCombat = st.DamageBy.Values.Sum() > 0 &&
                (DateTime.UtcNow - st.LastDamageAt).TotalSeconds < 60;
            var hadCombat = adminKill || killer != null || recentCombat;
            Puts($"OctaBoss[{st.Spec.Key}]: смерть — добил={killerName}, " +
                 $"adminKill={adminKill}, свежийБой={recentCombat}, " +
                 $"урон={st.DamageBy.Values.Sum():0}, лут={(hadCombat ? "да" : "нет")}");
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

            var endReason = adminKill ? "adminKill"
                : (killer != null || recentCombat) ? "playerKill" : "technicalDeath";
            EmitBossHook("OnOctaBossEnded", new JObject
            {
                ["instanceId"] = InstanceId(st),
                ["key"] = st.Spec.Key,
                ["name"] = st.Spec.DisplayName,
                ["reason"] = endReason,
                ["rewardsGranted"] = hadCombat,
                ["winner"] = killerName,
                ["winnerId"] = killer != null ? killer.UserIDString : null,
                ["rewardOpId"] = InstanceId(st)
            });

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
                        if (e != null && !e.IsDestroyed)
                        {
                            _bossNames.Remove(e);
                            e.Kill();
                        }
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

        // ------------------------------------------------------------------
        //  Командная панель босса: тёмное стекло + золото.
        //  Все правки сначала идут в черновик (Draft) выбранного вида и ложатся
        //  в конфиг только по кнопке СОХРАНИТЬ — иначе каждый клик степпера
        //  дёргал бы диск.
        // ------------------------------------------------------------------

        private const string AdminUIName = "OctaBoss_AdminUI";

        private const string GoldBright = "0.95 0.75 0.30 1";
        private const string Gold = "0.82 0.61 0.20 1";
        private const string GoldDark = "0.48 0.32 0.10 1";
        private const string GoldGlass = "0.82 0.61 0.20 0.18";
        private const string GlassBg = "0.015 0.012 0.010 0.94";
        private const string GlassLt = "0.05 0.045 0.035 0.92";
        private const string GlassRow = "0.08 0.07 0.05 0.82";
        private const string GlassHov = "0.13 0.105 0.06 0.95";
        private const string TMain = "0.93 0.88 0.75 1";
        private const string TMuted = "0.58 0.53 0.43 1";
        private const string TWhite = "1 0.98 0.90 1";
        private const string CGreen = "0.20 0.52 0.27 0.90";
        private const string CRed = "0.58 0.13 0.10 0.92";
        private const string CBlue = "0.18 0.35 0.55 0.90";
        private const string CHpBg = "0.10 0.05 0.04 0.95";

        private static readonly string[] UiPerms =
        {
            "octaboss.admin.view", "octaboss.admin.edit", "octaboss.admin.spawn",
            "octaboss.admin.kill", "octaboss.admin.rewards",
            "octaboss.admin.messages", "octaboss.admin.presets"
        };

        private bool Perm(BasePlayer p, string suffix) =>
            p.IsAdmin
            || permission.UserHasPermission(p.UserIDString, "octaboss.admin")
            || permission.UserHasPermission(p.UserIDString, "octaboss.admin." + suffix);

        private enum AdminTab { Overview, Combat, Phases, Escort, Spawn, Rewards, Messages, Danger }

        private class AdminUiState
        {
            public string BossKey;
            public AdminTab Tab = AdminTab.Overview;
            public BossSpec Draft;      // рабочая копия настроек — живая до СОХРАНИТЬ
            public bool Dirty;
            public string Pending;      // ожидает подтверждения: kill/respawn/despawn/reload/preset:x/boss:x
            public string MsgId = "m_spawn";
            public int WarnIdx;
            public string Toast;
            public string ToastColor = CGreen;
            public int ToastSeq;
        }

        private readonly Dictionary<ulong, AdminUiState> _ui = new Dictionary<ulong, AdminUiState>();

        // ---------- дескрипторы полей: id -> геттер/сеттер на BossSpec ----------

        private class UiNum
        {
            public string Id, Label, Warn, Perm = "edit", Fmt = "0.##";
            public float Min, Max, Step;
            public Func<BossSpec, float> Get;
            public Action<BossSpec, float> Set;
            public Func<float, bool> WarnIf;
        }

        private class UiBool
        {
            public string Id, Label, Perm = "edit";
            public Func<BossSpec, bool> Get;
            public Action<BossSpec, bool> Set;
        }

        private class UiTxt
        {
            public string Id, Label, Perm = "edit";
            public int Chars = 120;
            public Func<BossSpec, string> Get;
            public Action<BossSpec, string> Set;
        }

        private static UiNum N(string id, string label, float min, float max, float step,
            Func<BossSpec, float> g, Action<BossSpec, float> s,
            string fmt = "0.##", string warn = null, Func<float, bool> warnIf = null, string perm = "edit") =>
            new UiNum { Id = id, Label = label, Min = min, Max = max, Step = step, Get = g, Set = s,
                Fmt = fmt, Warn = warn, WarnIf = warnIf, Perm = perm };

        private static UiBool TB(string id, string label, Func<BossSpec, bool> g,
            Action<BossSpec, bool> s, string perm = "edit") =>
            new UiBool { Id = id, Label = label, Get = g, Set = s, Perm = perm };

        private static UiTxt TX(string id, string label, Func<BossSpec, string> g,
            Action<BossSpec, string> s, string perm = "edit", int chars = 120) =>
            new UiTxt { Id = id, Label = label, Get = g, Set = s, Perm = perm, Chars = chars };

        private static readonly List<UiNum> Nums = new List<UiNum>
        {
            N("health",   "Здоровье",            100, 50000, 100,   b => b.Health, (b, v) => b.Health = v,
              fmt: "0", warn: "Значение может сделать босса почти неубиваемым", warnIf: v => v > 30000),
            N("scale",    "Масштаб модели",      0.2f, 5, 0.05f,    b => b.Scale, (b, v) => b.Scale = v),
            N("speed",    "Скорость (x)",        0.1f, 5, 0.05f,    b => b.SpeedMultiplier, (b, v) => b.SpeedMultiplier = v),
            N("dmg",      "Урон босса (x)",      0.1f, 10, 0.1f,    b => b.DamageMultiplier, (b, v) => b.DamageMultiplier = v,
              warn: "Урон выше x5 убивает плейтест за один укус", warnIf: v => v > 5),
            N("rage",     "Урон в ярости (x)",   1, 5, 0.05f,       b => b.RageDamageMultiplier, (b, v) => b.RageDamageMultiplier = v),
            N("rageth",   "Порог ярости (HP)",   0.05f, 0.95f, 0.05f, b => b.RageThreshold, (b, v) => b.RageThreshold = v, "0.00"),
            N("bullet",   "Входящий: пули (x)",  0, 2, 0.05f,       b => b.BulletScale, (b, v) => b.BulletScale = v, "0.00",
              warn: "Пули почти не бьют — фарм превратится в расстрел ресурсов", warnIf: v => v < 0.25f),
            N("expl",     "Входящий: взрывы (x)",0, 2, 0.05f,       b => b.ExplosionScale, (b, v) => b.ExplosionScale = v, "0.00"),
            N("other",    "Входящий: прочее (x)",0, 2, 0.05f,       b => b.OtherScale, (b, v) => b.OtherScale = v, "0.00"),
            N("aggro",    "Радиус агро (м)",     10, 300, 5,        b => b.AggroRadius, (b, v) => b.AggroRadius = v, "0"),
            N("life",     "Время жизни (мин)",   5, 240, 5,         b => b.MaxLifeMinutes, (b, v) => b.MaxLifeMinutes = v, "0"),
            N("regend",   "Задержка регена (сек)",0, 600, 5,        b => b.RegenDelaySeconds, (b, v) => b.RegenDelaySeconds = v, "0"),
            N("regen",    "Реген % HP/сек",      0, 10, 0.1f,       b => b.RegenPercentPerSec, (b, v) => b.RegenPercentPerSec = v,
              warn: "Отступление на минуту не будет стоить команде боя", warnIf: v => v > 5),
            N("p2th",     "Порог входа (HP)",    0.5f, 0.95f, 0.05f, b => b.Phase2Threshold, (b, v) => b.Phase2Threshold = v, "0.00"),
            N("p3th",     "Порог входа (HP)",    0.05f, 0.5f, 0.05f, b => b.Phase3Threshold, (b, v) => b.Phase3Threshold = v, "0.00"),
            N("p2dmg",    "Урон (x)",            1, 3, 0.05f,       b => b.P2DamageMult, (b, v) => b.P2DamageMult = v, "0.00"),
            N("p3dmg",    "Урон (x)",            1, 5, 0.05f,       b => b.P3DamageMult, (b, v) => b.P3DamageMult = v, "0.00"),
            N("p2aggro",  "Радиус агро (м)",     30, 300, 5,        b => b.P2AggroRadius, (b, v) => b.P2AggroRadius = v, "0"),
            N("p3aggro",  "Радиус агро (м)",     30, 300, 5,        b => b.P3AggroRadius, (b, v) => b.P3AggroRadius = v, "0"),
            N("p2spd",    "Скорость (x)",        1, 3, 0.05f,       b => b.P2SpeedMult, (b, v) => b.P2SpeedMult = v, "0.00"),
            N("p3spd",    "Скорость (x)",        1, 3, 0.05f,       b => b.P3SpeedMult, (b, v) => b.P3SpeedMult = v, "0.00"),
            N("flee",     "Паника свиты (0-1)",  0, 1, 0.05f,       b => b.P3EscortFleeChance, (b, v) => b.P3EscortFleeChance = v, "0.00"),
            N("retarget", "Пересмотр цели (сек)",0, 120, 1,         b => b.RetargetSeconds, (b, v) => b.RetargetSeconds = v, "0"),
            N("hyst",     "Гистерезис цели (x)", 1, 3, 0.05f,       b => b.ThreatHysteresis, (b, v) => b.ThreatHysteresis = v, "0.00"),
            N("leapsec",  "Рывок каждые (сек)",  0, 120, 5,         b => b.LeapSeconds, (b, v) => b.LeapSeconds = v, "0"),
            N("leapdmg",  "Урон рывка AoE",      0, 500, 5,         b => b.LeapDamage, (b, v) => b.LeapDamage = v, "0"),
            N("leapsh",   "Щит после рывка (сек)",0, 30, 1,         b => b.LeapShieldSeconds, (b, v) => b.LeapShieldSeconds = v, "0"),
            N("escn",     "Количество",          0, 20, 1,          b => b.Escort.Count, (b, v) => b.Escort.Count = (int)v, "0"),
            N("escr",     "Радиус появления (м)",2, 100, 1,         b => b.Escort.Radius, (b, v) => b.Escort.Radius = v, "0"),
            N("escs",     "Масштаб",             0.2f, 3, 0.05f,    b => b.Escort.Scale, (b, v) => b.Escort.Scale = v, "0.00"),
            N("interval", "Интервал спавна (мин)",1, 1440, 5,       b => b.Spawn.IntervalMinutes, (b, v) => b.Spawn.IntervalMinutes = v, "0"),
            N("jitter",   "Разброс ± (мин)",     0, 120, 5,         b => b.Spawn.IntervalJitterMinutes, (b, v) => b.Spawn.IntervalJitterMinutes = v, "0"),
            N("warnmin",  "Предупреждение за (мин)",0, 60, 1,       b => b.Spawn.WarnBeforeMinutes, (b, v) => b.Spawn.WarnBeforeMinutes = v, "0"),
            N("marker",   "Радиус маркера",      0, 0.25f, 0.005f,  b => b.Spawn.MarkerRadius, (b, v) => b.Spawn.MarkerRadius = v, "0.000"),
            N("margin",   "От края карты (м)",   0, 1500, 50,       b => b.Spawn.MapMargin, (b, v) => b.Spawn.MapMargin = v, "0"),
            N("monmin",   "От монумента мин (м)",0, 2000, 10,       b => b.Spawn.MonumentMinDist, (b, v) => b.Spawn.MonumentMinDist = v, "0"),
            N("monmax",   "От монумента макс (м)",0, 3000, 10,      b => b.Spawn.MonumentMaxDist, (b, v) => b.Spawn.MonumentMaxDist = v, "0"),
            N("waterr",   "Радиус воды (м)",     5, 200, 5,         b => b.Spawn.WaterRadius, (b, v) => b.Spawn.WaterRadius = v, "0"),
            N("currency", "Награда команде (RP)",0, 100000, 50,     b => b.Rewards.Currency, (b, v) => b.Rewards.Currency = (int)v, "0", perm: "rewards"),
            N("currpart", "Награда участникам",  0, 10000, 25,      b => b.Rewards.CurrencyParticipation, (b, v) => b.Rewards.CurrencyParticipation = (int)v, "0", perm: "rewards"),
            N("partpct",  "Порог участия (доля)",0, 0.5f, 0.01f,    b => b.Rewards.ParticipationPct, (b, v) => b.Rewards.ParticipationPct = v, "0.00", perm: "rewards"),
            N("buffdmg",  "Бафф урона (x)",      1, 2, 0.05f,       b => b.Rewards.BuffDamage, (b, v) => b.Rewards.BuffDamage = v, "0.00",
              warn: "Сильный бафф победителя может нарушить PvP-баланс", warnIf: v => v > 1.25f, perm: "rewards"),
            N("buffmin",  "Длительность баффа (мин)",0, 120, 5,     b => b.Rewards.BuffMinutes, (b, v) => b.Rewards.BuffMinutes = v, "0", perm: "rewards"),
            N("lootlock", "Лут закрыт (сек)",    0, 120, 5,         b => b.Rewards.LootLockSeconds, (b, v) => b.Rewards.LootLockSeconds = v, "0",
              warn: "Игроки почти не получат времени на финальный конфликт", warnIf: v => v < 5, perm: "rewards"),
            N("lootlife", "Жизнь мешка (сек)",   60, 86400, 60,     b => b.Rewards.LootLifetimeSeconds, (b, v) => b.Rewards.LootLifetimeSeconds = v, "0", perm: "rewards"),
        };

        private static readonly List<UiBool> Bools = new List<UiBool>
        {
            TB("enabled",  "Включён",                b => b.Enabled, (b, v) => b.Enabled = v),
            TB("phases",   "Фазы боя",               b => b.PhasesEnabled, (b, v) => b.PhasesEnabled = v),
            TB("hyper",    "Гиперагрессия",          b => b.HyperAggro, (b, v) => b.HyperAggro = v),
            TB("provoke",  "Force Provoke",          b => b.ForceProvoke, (b, v) => b.ForceProvoke = v),
            TB("immune",   "Элем. неуязвимость",     b => b.ElementalImmune, (b, v) => b.ElementalImmune = v),
            TB("esckill",  "Убивать свиту при деспавне", b => b.Escort.KillOnDespawn, (b, v) => b.Escort.KillOnDespawn = v),
            TB("showmark", "Маркер на карте",        b => b.Spawn.ShowMarker, (b, v) => b.Spawn.ShowMarker = v),
            TB("biome",    "Только родной биом",     b => b.Spawn.NativeBiome, (b, v) => b.Spawn.NativeBiome = v),
            TB("water",    "Спавн у воды",           b => b.Spawn.NeedsWater, (b, v) => b.Spawn.NeedsWater = v),
        };

        private static readonly List<UiTxt> Txts = new List<UiTxt>
        {
            TX("name",     "Имя босса",        b => b.DisplayName, (b, v) => b.DisplayName = v, chars: 40),
            TX("prefab",   "Префаб босса",     b => b.Prefab, (b, v) => b.Prefab = v, chars: 150),
            TX("biomes",   "Родные биомы",     b => b.Spawn.NativeBiomes, (b, v) => b.Spawn.NativeBiomes = v, chars: 80),
            TX("mons",     "Монументы-якоря",  b => b.Spawn.MonumentNames, (b, v) => b.Spawn.MonumentNames = v, chars: 100),
            TX("escpre",   "Префаб свиты",     b => b.Escort.Prefab, (b, v) => b.Escort.Prefab = v, chars: 150),
            TX("img",      "Картинка панели",  b => b.UiImageUrl, (b, v) => b.UiImageUrl = v, chars: 200),
            TX("accent",   "Акцент \"r g b a\"",b => b.UiAccent, (b, v) => b.UiAccent = v, chars: 30),
        };

        // события вкладки «Сообщения» -> строковые поля вида
        private static readonly string[][] MsgDefs =
        {
            new[] { "m_spawn",   "СПАВН" },
            new[] { "m_warn",    "ПРЕДУПРЕЖДЕНИЕ" },
            new[] { "m_phase2",  "ФАЗА 2" },
            new[] { "m_phase3",  "ФАЗА 3" },
            new[] { "m_rage",    "ЯРОСТЬ" },
            new[] { "m_reset",   "СБРОС БОЯ" },
            new[] { "m_despawn", "ДЕСПАВН" },
            new[] { "m_death",   "СМЕРТЬ" },
            new[] { "m_quiet",   "СМЕРТЬ БЕЗ БОЯ" },
        };

        private static string MsgGet(BossSpec b, string id)
        {
            switch (id)
            {
                case "m_spawn": return b.SpawnAnnounce;
                case "m_warn": return b.WarnAnnounce;
                case "m_phase2": return b.Phase2Announce;
                case "m_phase3": return b.Phase3Announce;
                case "m_rage": return b.RageAnnounce;
                case "m_reset": return b.ResetAnnounce;
                case "m_despawn": return b.DespawnAnnounce;
                case "m_death": return b.DeathAnnounce;
                case "m_quiet": return b.QuietDeathAnnounce;
            }
            return "";
        }

        private static void MsgSet(BossSpec b, string id, string v)
        {
            switch (id)
            {
                case "m_spawn": b.SpawnAnnounce = v; break;
                case "m_warn": b.WarnAnnounce = v; break;
                case "m_phase2": b.Phase2Announce = v; break;
                case "m_phase3": b.Phase3Announce = v; break;
                case "m_rage": b.RageAnnounce = v; break;
                case "m_reset": b.ResetAnnounce = v; break;
                case "m_despawn": b.DespawnAnnounce = v; break;
                case "m_death": b.DeathAnnounce = v; break;
                case "m_quiet": b.QuietDeathAnnounce = v; break;
            }
        }

        // вторичный акцент под зверя (базовая тема — золото)
        private static readonly Dictionary<string, string> AccentMap = new Dictionary<string, string>
        {
            ["bull"] = "0.75 0.35 0.15 1", ["boar"] = "0.55 0.20 0.12 1",
            ["wolf"] = "0.35 0.45 0.60 1", ["tiger"] = "0.85 0.50 0.15 1",
            ["panther"] = "0.45 0.30 0.60 1", ["bear"] = "0.45 0.32 0.20 1",
            ["crocodile"] = "0.35 0.50 0.25 1",
        };

        // ---------- состояние ----------

        private BossSpec UiSpec(BasePlayer player)
        {
            var st = UiStateOf(player);
            var s = _cfg.Bosses.FirstOrDefault(b => b.Key == st.BossKey);
            return s ?? _cfg.Bosses.FirstOrDefault();
        }

        private AdminUiState UiStateOf(BasePlayer player)
        {
            var id = Uid(player);
            AdminUiState st;
            if (!_ui.TryGetValue(id, out st))
            {
                st = new AdminUiState { BossKey = _cfg.Bosses.FirstOrDefault()?.Key };
                _ui[id] = st;
            }
            var live = _cfg.Bosses.FirstOrDefault(b => b.Key == st.BossKey) ?? _cfg.Bosses.FirstOrDefault();
            if (live != null && st.BossKey != live.Key) st.BossKey = live.Key;
            if (live != null && st.Draft == null)
                st.Draft = CloneSpec(live);
            return st;
        }

        // свежая копия живого вида вместо черновика
        private void ResetDraft(AdminUiState st)
        {
            var live = _cfg.Bosses.FirstOrDefault(b => b.Key == st.BossKey);
            if (live != null)
                st.Draft = CloneSpec(live);
            st.Dirty = false;
        }

        private static string ShortName(BossSpec b) =>
            (b.DisplayName ?? b.Key).Replace("-БОСС", "").Replace("-босс", "").ToUpper();

        private static string AccentOf(BossSpec b) =>
            !string.IsNullOrEmpty(b.UiAccent) ? b.UiAccent
            : (AccentMap.TryGetValue(b.Key ?? "", out var a) ? a : Gold);

        private string BossImagePng(BossSpec spec)
        {
            if (ImageLibrary == null || !ImageLibrary.IsLoaded || string.IsNullOrEmpty(spec.UiImageUrl))
                return null;
            var shortName = "octaboss_" + spec.Key;
            var png = ImageLibrary.Call("GetImage", shortName) as string;
            if (string.IsNullOrEmpty(png)) png = ImageLibrary.Call("GetImage", shortName, 0UL) as string;
            if (!string.IsNullOrEmpty(png)) return png;
            var url = spec.UiImageUrl;
            if (!url.StartsWith("http")) url = "file:///" + url.Replace('\\', '/');
            ImageLibrary.Call("AddImage", url, shortName);
            return null; // докачается — при следующей отрисовке подхватится
        }

        // ---------- вход ----------

        [ChatCommand("boss_admin")]
        private void CmdAdminUI(BasePlayer player)
        {
            if (!Perm(player, "view"))
            {
                SendReply(player, $"{Tag} Нет прав (octaboss.admin.view)");
                return;
            }
            UiStateOf(player); // гарантирует состояние+черновик
            UiDrawAll(player);
        }

        // ---------- отрисовка: корень + части ----------

        private static string UiN(string part) => AdminUIName + "_" + part;

        // OctaUI — живой поиск (PluginReference залипает на reload, PITFALLS §11).
        // Нет библиотеки → все делегаты ниже падают обратно в legacy-отрисовку.
        private Plugin UiLib()
        {
            if (OctaUI != null && OctaUI.IsLoaded) return OctaUI;
            foreach (var pl in Interface.Oxide.RootPluginManager.GetPlugins())
                if (pl.Name == "OctaUI" && pl.IsLoaded) return pl;
            return null;
        }
        private static readonly string[] UiParts = { "HDR", "LST", "STS", "TAB", "CNT", "BOT", "TST", "CFM" };

        private void UiDrawAll(BasePlayer player)
        {
            foreach (var p in UiParts) CuiHelper.DestroyUi(player, UiN(p));
            CuiHelper.DestroyUi(player, AdminUIName);

            var st = UiStateOf(player);
            var spec = UiSpec(player);
            if (spec == null) return;

            var c = new CuiElementContainer();
            var root = c.Add(new CuiPanel
            {
                Image = { Color = GlassBg },
                RectTransform = { AnchorMin = "0.17 0.08", AnchorMax = "0.83 0.92" },
                CursorEnabled = true
            }, "Overlay", AdminUIName);
            c.Add(new CuiElement { Components = { new CuiNeedsCursorComponent() }, Parent = root });

            UiDrawHeader(c, player, st, spec);
            UiDrawBossList(c, player, st);
            UiDrawTabs(c, player, st);
            UiDrawStatus(c, player, st, spec);
            UiDrawContent(c, player, st);
            UiDrawBottom(c, player, st, spec);
            UiDrawToast(c, st);
            UiDrawConfirm(c, player, st, spec);
            CuiHelper.AddUi(player, c);
        }

        // частичная перерисовка — только перечисленные панели
        private void UiRefresh(BasePlayer player, params string[] parts)
        {
            if (!_ui.ContainsKey(Uid(player))) return;
            var st = UiStateOf(player);
            var spec = UiSpec(player);
            if (spec == null) return;
            var c = new CuiElementContainer();
            foreach (var p in parts)
            {
                CuiHelper.DestroyUi(player, UiN(p));
                switch (p)
                {
                    case "HDR": UiDrawHeader(c, player, st, spec); break;
                    case "LST": UiDrawBossList(c, player, st); break;
                    case "TAB": UiDrawTabs(c, player, st); break;
                    case "STS": UiDrawStatus(c, player, st, spec); break;
                    case "CNT": UiDrawContent(c, player, st); break;
                    case "BOT": UiDrawBottom(c, player, st, spec); break;
                    case "TST": UiDrawToast(c, st); break;
                    case "CFM": UiDrawConfirm(c, player, st, spec); break;
                }
            }
            CuiHelper.AddUi(player, c);
        }

        // ---------- шапка: зверь + имя + статус + [X] ----------

        private void UiDrawHeader(CuiElementContainer c, BasePlayer player, AdminUiState st, BossSpec spec)
        {
            var hdr = c.Add(new CuiPanel
            {
                Image = { Color = GlassLt },
                RectTransform = { AnchorMin = "0 0.925", AnchorMax = "1 1" }
            }, AdminUIName, UiN("HDR"));

            // золотая линия снизу шапки
            c.Add(new CuiPanel
            {
                Image = { Color = GoldDark },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.03" }
            }, hdr);

            // картинка зверя в золотой рамке (или буква-фолбэк)
            c.Add(new CuiPanel
            {
                Image = { Color = GoldDark },
                RectTransform = { AnchorMin = "0.008 0.10", AnchorMax = "0.065 0.94" }
            }, hdr, UiN("HDR_imgf"));
            var png = BossImagePng(spec);
            if (png != null)
            {
                c.Add(new CuiElement
                {
                    Parent = UiN("HDR_imgf"),
                    Components =
                    {
                        new CuiRawImageComponent { Png = png, Color = "1 1 1 0.85" },
                        new CuiRectTransformComponent { AnchorMin = "0.03 0.03", AnchorMax = "0.97 0.97" }
                    }
                });
            }
            else
            {
                c.Add(new CuiPanel
                {
                    Image = { Color = AccentOf(spec) },
                    RectTransform = { AnchorMin = "0.03 0.03", AnchorMax = "0.97 0.97" }
                }, UiN("HDR_imgf"));
                c.Add(new CuiLabel
                {
                    Text = { Text = ShortName(spec).Substring(0, 1), FontSize = 26, Align = TextAnchor.MiddleCenter, Color = TWhite },
                    RectTransform = { AnchorMin = "0.03 0.03", AnchorMax = "0.97 0.97" }
                }, UiN("HDR_imgf"));
            }

            c.Add(new CuiLabel
            {
                Text = { Text = $"◆ OCTABOSS — {spec.DisplayName}", FontSize = 15, Align = TextAnchor.MiddleLeft, Color = GoldBright },
                RectTransform = { AnchorMin = "0.075 0.55", AnchorMax = "0.75 0.95" }
            }, hdr);

            var dirty = st.Dirty ? "   ● ЕСТЬ НЕСОХРАНЁННЫЕ ИЗМЕНЕНИЯ" : "";
            c.Add(new CuiLabel
            {
                Text = { Text = (st.Dirty ? "<color=#E8B84B>" : "") + dirty + (st.Dirty ? "</color>" : ""),
                    FontSize = 10, Align = TextAnchor.MiddleLeft, Color = GoldBright },
                RectTransform = { AnchorMin = "0.075 0.10", AnchorMax = "0.75 0.55" }
            }, hdr);

            c.Add(new CuiButton
            {
                Button = { Command = "octabossui close", Color = CRed },
                Text = { Text = "✕", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = TWhite },
                RectTransform = { AnchorMin = "0.965 0.15", AnchorMax = "0.995 0.90" }
            }, hdr);
        }

        // ---------- левая колонка: карточки видов ----------

        private void UiDrawBossList(CuiElementContainer c, BasePlayer player, AdminUiState st)
        {
            var lst = c.Add(new CuiPanel
            {
                Image = { Color = GlassLt },
                RectTransform = { AnchorMin = "0.008 0.30", AnchorMax = "0.205 0.918" }
            }, AdminUIName, UiN("LST"));

            c.Add(new CuiLabel
            {
                Text = { Text = "ВИДЫ БОССОВ", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = TMuted },
                RectTransform = { AnchorMin = "0 0.955", AnchorMax = "1 1" }
            }, lst);

            var n = _cfg.Bosses.Count;
            var cardH = 0.088f; var gap = 0.008f;
            for (var i = 0; i < n; i++)
            {
                var b = _cfg.Bosses[i];
                var yTop = 0.948f - i * (cardH + gap);
                var yBot = yTop - cardH;
                var sel = b.Key == st.BossKey;
                _bosses.TryGetValue(b.Key, out var bs);
                var alive = bs != null && bs.Alive;

                var lib = UiLib();
                if (lib != null) // OctaUI.ListCard — карточка с полосой статуса и рамкой выбора
                {
                    string stTxt;
                    if (!b.Enabled) stTxt = "OFF";
                    else if (alive) stTxt = "<color=#59BF66>ЖИВ</color>";
                    else if (bs != null) stTxt = $"{Math.Max(0, (int)(bs.NextSpawnAt - DateTime.UtcNow).TotalMinutes)} мин";
                    else stTxt = "—";
                    lib.Call("ListCard", c, lst, UiN($"LST_c{i}"), new JObject
                    {
                        ["rect"] = string.Format(CultureInfo.InvariantCulture, "0.02 {0} 0.98 {1}", yBot, yTop),
                        ["cmd"] = $"octabossui boss {b.Key}",
                        ["title"] = b.Enabled || sel ? ShortName(b) : $"<color=#94876E>{ShortName(b)}</color>",
                        ["sub"] = stTxt,
                        ["strip"] = !b.Enabled ? TMuted : alive ? CGreen : Gold,
                        ["selected"] = sel
                    });
                    continue;
                }

                // карточка
                var cardColor = sel ? GoldGlass : alive ? GlassHov : GlassBg;
                c.Add(new CuiButton
                {
                    Button = { Command = $"octabossui boss {b.Key}", Color = cardColor },
                    Text = { Text = "", Color = "0 0 0 0" },
                    RectTransform = { AnchorMin = $"0.02 {yBot}", AnchorMax = $"0.98 {yTop}" }
                }, lst, UiN($"LST_c{i}"));

                // левая полоса состояния
                c.Add(new CuiPanel
                {
                    Image = { Color = !b.Enabled ? TMuted : alive ? CGreen : Gold },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0.025 1" }
                }, UiN($"LST_c{i}"));

                c.Add(new CuiLabel
                {
                    Text = { Text = ShortName(b), FontSize = 12, Align = TextAnchor.MiddleLeft,
                             Color = sel ? GoldBright : b.Enabled ? TMain : TMuted },
                    RectTransform = { AnchorMin = "0.07 0.50", AnchorMax = "0.98 0.98" }
                }, UiN($"LST_c{i}"));

                string status;
                if (!b.Enabled) status = "OFF";
                else if (alive) status = "ЖИВ";
                else if (bs != null) status = $"{Math.Max(0, (int)(bs.NextSpawnAt - DateTime.UtcNow).TotalMinutes)} мин";
                else status = "—";
                c.Add(new CuiLabel
                {
                    Text = { Text = status, FontSize = 9, Align = TextAnchor.MiddleLeft,
                             Color = alive ? "0.35 0.75 0.40 1" : TMuted },
                    RectTransform = { AnchorMin = "0.07 0.05", AnchorMax = "0.98 0.52" }
                }, UiN($"LST_c{i}"));

                if (sel) // золотая рамка выбранного
                {
                    c.Add(new CuiPanel { Image = { Color = GoldBright }, RectTransform = { AnchorMin = "0 0.97", AnchorMax = "1 1" } }, UiN($"LST_c{i}"));
                    c.Add(new CuiPanel { Image = { Color = GoldBright }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.03" } }, UiN($"LST_c{i}"));
                }
            }
        }

        // ---------- левая колонка: вкладки ----------

        private void UiDrawTabs(CuiElementContainer c, BasePlayer player, AdminUiState st)
        {
            var lib = UiLib();
            if (lib != null)
            {
                var tabNames = new[]
                {
                    "ОБЗОР", "БОЕЦ", "ФАЗЫ", "СВИТА",
                    "СПАВН", "НАГРАДЫ", "СООБЩЕНИЯ", "ОПАСНАЯ"
                };
                var tabs = new JArray();
                for (var i = 0; i < tabNames.Length; i++)
                    tabs.Add(new JObject
                    {
                        ["label"] = tabNames[i],
                        ["cmd"] = $"octabossui tab {i}",
                        ["selected"] = (int)st.Tab == i
                    });
                lib.Call("TabStrip", c, AdminUIName, UiN("TAB"), new JObject
                {
                    ["rect"] = "0.008 0.072 0.205 0.295",
                    ["vertical"] = true, ["tabs"] = tabs
                });
                return;
            }
            var tab = c.Add(new CuiPanel
            {
                Image = { Color = GlassLt },
                RectTransform = { AnchorMin = "0.008 0.072", AnchorMax = "0.205 0.295" }
            }, AdminUIName, UiN("TAB"));

            var names = new[]
            {
                "ОБЗОР", "БОЕЦ", "ФАЗЫ", "СВИТА",
                "СПАВН", "НАГРАДЫ", "СООБЩЕНИЯ", "ОПАСНАЯ"
            };
            var h = 0.115f;
            for (var i = 0; i < names.Length; i++)
            {
                var sel = (int)st.Tab == i;
                var y1 = 0.99f - i * h;
                c.Add(new CuiButton
                {
                    Button = { Command = $"octabossui tab {i}", Color = sel ? GoldGlass : "0 0 0 0" },
                    Text = { Text = (sel ? "▸ " : "   ") + names[i], FontSize = 10,
                             Align = TextAnchor.MiddleLeft, Color = sel ? GoldBright : TMuted },
                    RectTransform = { AnchorMin = $"0.03 {y1 - h + 0.008f}", AnchorMax = $"0.97 {y1 - 0.008f}" }
                }, tab);
            }
        }

        // ---------- карточка статуса: HP-полоса + фаза + квадрат ----------

        private void UiDrawStatus(CuiElementContainer c, BasePlayer player, AdminUiState st, BossSpec spec)
        {
            var s = c.Add(new CuiPanel
            {
                Image = { Color = GlassLt },
                RectTransform = { AnchorMin = "0.212 0.845", AnchorMax = "0.992 0.918" }
            }, AdminUIName, UiN("STS"));

            _bosses.TryGetValue(spec.Key, out var bs);
            var alive = bs != null && bs.Alive;

            c.Add(new CuiLabel
            {
                Text = { Text = spec.DisplayName, FontSize = 14, Align = TextAnchor.MiddleLeft, Color = TWhite },
                RectTransform = { AnchorMin = "0.015 0.50", AnchorMax = "0.45 0.98" }
            }, s);

            var phaseTxt = !spec.PhasesEnabled ? "ФАЗЫ OFF" : alive ? $"ФАЗА {bs.Phase}" : "—";
            c.Add(new CuiLabel
            {
                Text = { Text = phaseTxt, FontSize = 10, Align = TextAnchor.MiddleCenter,
                         Color = !spec.PhasesEnabled ? TMuted : alive && bs.Phase >= 3 ? "1 0.3 0.25 1" : alive && bs.Phase == 2 ? GoldBright : "0.4 0.8 0.45 1" },
                RectTransform = { AnchorMin = "0.45 0.15", AnchorMax = "0.56 0.85" }
            }, s);

            if (alive)
            {
                var hp = bs.Entity.Health();
                var max = bs.Entity.MaxHealth();
                var frac = Mathf.Clamp01(max > 0 ? hp / max : 0f);
                // полоса HP
                c.Add(new CuiPanel
                {
                    Image = { Color = CHpBg },
                    RectTransform = { AnchorMin = "0.015 0.10", AnchorMax = "0.42 0.45" }
                }, s, UiN("STS_hp"));
                var hpCol = bs.Phase >= 3 ? "0.75 0.12 0.08 0.95" : bs.Phase == 2 ? "0.80 0.45 0.10 0.95" : "0.55 0.60 0.25 0.95";
                c.Add(new CuiPanel
                {
                    Image = { Color = hpCol },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = $"{frac:0.###} 1" }
                }, UiN("STS_hp"));

                c.Add(new CuiLabel
                {
                    Text = { Text = $"HP {hp:0} / {max:0}  •  {frac * 100f:0}%", FontSize = 9,
                             Align = TextAnchor.MiddleLeft, Color = TMuted },
                    RectTransform = { AnchorMin = "0.015 0.52", AnchorMax = "0.45 0.85" },
                }, s);

                var t = DateTime.UtcNow - bs.SpawnedAt;
                c.Add(new CuiLabel
                {
                    Text = { Text = $"Квадрат {GridOf(bs.Entity.transform.position)}  •  на карте {t.Hours:00}:{t.Minutes:00}",
                             FontSize = 10, Align = TextAnchor.MiddleRight, Color = TMuted },
                    RectTransform = { AnchorMin = "0.58 0", AnchorMax = "0.985 1" }
                }, s);
            }
            else
            {
                var left = bs != null ? Math.Max(0, (int)(bs.NextSpawnAt - DateTime.UtcNow).TotalMinutes) : 0;
                c.Add(new CuiLabel
                {
                    Text = { Text = spec.Enabled ? $"НЕТ НА КАРТЕ  •  спавн через ~{left} мин" : "ВИД ВЫКЛЮЧЕН",
                             FontSize = 10, Align = TextAnchor.MiddleRight, Color = TMuted },
                    RectTransform = { AnchorMin = "0.58 0", AnchorMax = "0.985 1" }
                }, s);
            }
        }

        // ---------- контент вкладки (правая зона) ----------

        private void UiDrawContent(CuiElementContainer c, BasePlayer player, AdminUiState st)
        {
            var cnt = c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.212 0.075", AnchorMax = "0.992 0.838" }
            }, AdminUIName, UiN("CNT"));

            var d = st.Draft;
            if (d == null) return;
            var canEdit = Perm(player, "edit");
            var colX = new[] { 0.008f, 0.505f };
            var colW = 0.487f;
            var yL = 0.985f; var yR = 0.985f;

            // локальные помощники укладки в две колонки
            Action<UiNum, int> num = (f, col) =>
            {
                var x = colX[col]; var y = col == 0 ? yL : yR;
                DrawStepper(c, cnt, player, f, d, Perm(player, f.Perm), x, y - 0.088f, colW);
                if (col == 0) yL = y - 0.098f; else yR = y - 0.098f;
            };
            Action<UiBool, int> tgl = (f, col) =>
            {
                var x = colX[col]; var y = col == 0 ? yL : yR;
                DrawToggle(c, cnt, player, f, d, Perm(player, f.Perm), x, y - 0.058f, colW);
                if (col == 0) yL = y - 0.068f; else yR = y - 0.068f;
            };
            Action<UiTxt, int> txt = (f, col) =>
            {
                var x = colX[col]; var y = col == 0 ? yL : yR;
                DrawTextRow(c, cnt, player, f, d, Perm(player, f.Perm), x, y - 0.082f, colW);
                if (col == 0) yL = y - 0.092f; else yR = y - 0.092f;
            };
            Action<string, int> sec = (t, col) =>
            {
                var x = colX[col]; var y = col == 0 ? yL : yR;
                DrawSection(c, cnt, t, x, y - 0.045f, colW);
                if (col == 0) yL = y - 0.055f; else yR = y - 0.055f;
            };
            // серая пояснительная строка — итоговые значения из черновика
            Action<string, int> info = (t, col) =>
            {
                var x = colX[col]; var y = col == 0 ? yL : yR;
                c.Add(new CuiLabel
                {
                    Text = { Text = t, FontSize = 10, Align = TextAnchor.MiddleLeft, Color = TMuted },
                    RectTransform = { AnchorMin = $"{x} {y - 0.046f}", AnchorMax = $"{x + colW} {y}" }
                }, cnt);
                if (col == 0) yL = y - 0.056f; else yR = y - 0.056f;
            };
            Func<string, UiNum> nf = id => Nums.First(n => n.Id == id);
            Func<string, UiBool> bf = id => Bools.First(n => n.Id == id);
            Func<string, UiTxt> tf = id => Txts.First(n => n.Id == id);

            switch (st.Tab)
            {
                case AdminTab.Overview:
                {
                    // пресеты — полоса сверху на всю ширину
                    DrawPresetBar(c, cnt, player, st);
                    yL -= 0.085f; yR -= 0.085f;
                    sec("ОСНОВНОЕ", 0); sec("ПАРАМЕТРЫ", 1);
                    txt(tf("name"), 0);
                    tgl(bf("enabled"), 0);
                    num(nf("health"), 0);
                    num(nf("dmg"), 0);
                    num(nf("speed"), 0);
                    sec("ОФОРМЛЕНИЕ", 0);
                    txt(tf("img"), 0);
                    txt(tf("accent"), 0);
                    num(nf("scale"), 1);
                    num(nf("escn"), 1);
                    num(nf("interval"), 1);
                    num(nf("currency"), 1);
                    break;
                }
                case AdminTab.Combat:
                {
                    // баланс колонок: обе ≈0.96 высоты — иначе левая уезжает
                    // под нижнюю панель и кнопки наезжают на последний тоггл
                    sec("ЖИВОТНОЕ", 0); sec("БОЙ", 1);
                    num(nf("health"), 0);
                    num(nf("scale"), 0);
                    num(nf("speed"), 0);
                    num(nf("life"), 0);
                    num(nf("dmg"), 1);
                    num(nf("rage"), 1);
                    num(nf("rageth"), 1);
                    sec("ВХОДЯЩИЙ УРОН", 0); sec("АГРО / РЕГЕН", 1);
                    num(nf("bullet"), 0);
                    num(nf("expl"), 0);
                    num(nf("other"), 0);
                    num(nf("aggro"), 1);
                    num(nf("regend"), 1);
                    num(nf("regen"), 1);
                    // итоговые множители исходящего урона — по черновику
                    var dm = d.DamageMultiplier;
                    info($"Итог урона: ф1 ×{dm:0.##} · ф2 ×{dm * d.P2DamageMult:0.##} · ф3 ×{dm * d.P3DamageMult:0.##}", 0);
                    info($"Ярость при HP ≤ {d.RageThreshold * 100:0}%: ещё ×{d.RageDamageMultiplier:0.##}", 0);
                    sec("ПОВЕДЕНИЕ", 1);
                    tgl(bf("hyper"), 1);
                    tgl(bf("provoke"), 1);
                    tgl(bf("immune"), 1);
                    break;
                }
                case AdminTab.Phases:
                {
                    tgl(bf("phases"), 0);
                    sec("ФАЗА 2 — РАЗЪЯРЁННЫЙ", 0); sec("ФАЗА 3 — ПОСЛЕДНИЙ РЫВОК", 1);
                    num(nf("p2th"), 0);
                    num(nf("p2dmg"), 0);
                    num(nf("p2aggro"), 0);
                    num(nf("p2spd"), 0);
                    num(nf("p3th"), 1);
                    num(nf("p3dmg"), 1);
                    num(nf("p3aggro"), 1);
                    num(nf("p3spd"), 1);
                    sec("РЫВОК / УГРОЗА", 0);
                    num(nf("leapsec"), 0);
                    num(nf("leapdmg"), 0);
                    num(nf("leapsh"), 0);
                    num(nf("flee"), 1);
                    num(nf("retarget"), 1);
                    num(nf("hyst"), 1);
                    // реальный урон рывка в ф3 — база × фаза (× ярость)
                    var dm3 = d.DamageMultiplier * d.P3DamageMult;
                    info($"Рывок ф3: {d.LeapDamage:0} базы ×{dm3:0.##} = до {d.LeapDamage * dm3:0}", 0);
                    info($"В ярости: до {d.LeapDamage * dm3 * d.RageDamageMultiplier:0} до брони", 0);
                    break;
                }
                case AdminTab.Escort:
                {
                    sec("СВИТА", 0); sec("РОЛИ", 1);
                    num(nf("escn"), 0);
                    num(nf("escr"), 0);
                    num(nf("escs"), 0);
                    tgl(bf("esckill"), 0);
                    txt(tf("escpre"), 0);
                    // визуальный блок ролей
                    DrawEscortRoles(c, cnt, d, colX[1], yR - 0.14f, colW);
                    break;
                }
                case AdminTab.Spawn:
                {
                    sec("РАСПИСАНИЕ", 0); sec("МЕСТО", 1);
                    num(nf("interval"), 0);
                    num(nf("jitter"), 0);
                    num(nf("warnmin"), 0);
                    num(nf("margin"), 0);
                    tgl(bf("showmark"), 1);
                    num(nf("marker"), 1);
                    tgl(bf("biome"), 1);
                    txt(tf("biomes"), 1);
                    tgl(bf("water"), 1);
                    num(nf("waterr"), 1);
                    sec("МОНУМЕНТЫ-ЯКОРЯ", 0);
                    num(nf("monmin"), 0);
                    num(nf("monmax"), 0);
                    txt(tf("mons"), 0);
                    DrawMonumentPick(c, cnt, player, colX[0], yL - 0.06f, colW);
                    yL -= 0.075f; // кнопка занимает строку — иначе prefab ляжет поверх
                    txt(tf("prefab"), 0);
                    break;
                }
                case AdminTab.Rewards:
                {
                    sec("ВАЛЮТА", 0); sec("БАФФ / ЛУТ", 1);
                    num(nf("currency"), 0);
                    num(nf("currpart"), 0);
                    num(nf("partpct"), 0);
                    num(nf("buffdmg"), 1);
                    num(nf("buffmin"), 1);
                    num(nf("lootlock"), 1);
                    num(nf("lootlife"), 1);
                    break;
                }
                case AdminTab.Messages:
                {
                    DrawMessagesTab(c, cnt, player, st, d, Perm(player, "messages"));
                    break;
                }
                case AdminTab.Danger:
                {
                    DrawDangerTab(c, cnt, player, st);
                    break;
                }
            }
        }

        // ---------- элементы ----------

        private static void DrawSection(CuiElementContainer c, string parent, string text, float x, float y, float w)
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = 10, Align = TextAnchor.MiddleLeft, Color = Gold },
                RectTransform = { AnchorMin = $"{x} {y}", AnchorMax = $"{x + w} {y + 0.038f}" }
            }, parent);
            c.Add(new CuiPanel
            {
                Image = { Color = GoldDark },
                RectTransform = { AnchorMin = $"{x} {y}", AnchorMax = $"{x + w} {y + 0.004f}" }
            }, parent);
        }

        // степпер: подпись | [-] [поле] [+] + тонкая золотая шкала
        private void DrawStepper(CuiElementContainer c, string parent, BasePlayer player,
            UiNum f, BossSpec d, bool edit, float x, float y, float w)
        {
            var v = f.Get(d);
            var warn = f.WarnIf != null && f.WarnIf(v);
            var lib = UiLib();
            if (lib != null) // OctaUI.Stepper — та же геометрия (PITFALLS: конверт не нужен, это draw-API)
            {
                lib.Call("Stepper", c, parent, parent + "_r" + f.Id, new JObject
                {
                    ["rect"] = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", x, y, x + w, y + 0.088f),
                    ["label"] = f.Label, ["value"] = v, ["min"] = f.Min, ["max"] = f.Max,
                    ["fmt"] = f.Fmt, ["edit"] = edit,
                    ["warn"] = warn, ["warnText"] = f.Warn, ["limit"] = 12,
                    ["cmdMinus"] = $"octabossui adj {f.Id} {(-f.Step).ToString("0.#####", CultureInfo.InvariantCulture)}",
                    ["cmdPlus"] = $"octabossui adj {f.Id} {f.Step.ToString("0.#####", CultureInfo.InvariantCulture)}",
                    ["cmdSet"] = $"octabossui set {f.Id}"
                });
                return;
            }
            var rowBg = warn ? "0.25 0.12 0.03 0.85" : GlassRow;
            c.Add(new CuiPanel
            {
                Image = { Color = rowBg },
                RectTransform = { AnchorMin = $"{x} {y}", AnchorMax = $"{x + w} {y + 0.088f}" }
            }, parent, parent + "_r" + f.Id);

            c.Add(new CuiLabel
            {
                Text = { Text = f.Label, FontSize = 11, Align = TextAnchor.MiddleLeft,
                         Color = warn ? GoldBright : TMain },
                RectTransform = { AnchorMin = "0.02 0.45", AnchorMax = "0.52 0.97" }
            }, parent + "_r" + f.Id);
            if (warn && !string.IsNullOrEmpty(f.Warn))
                c.Add(new CuiLabel
                {
                    Text = { Text = "⚠ " + f.Warn, FontSize = 8, Align = TextAnchor.MiddleLeft, Color = GoldBright },
                    RectTransform = { AnchorMin = "0.02 0.04", AnchorMax = "0.60 0.42" }
                }, parent + "_r" + f.Id);

            var cmdBase = $"octabossui set {f.Id}";
            var minus = $"octabossui adj {f.Id} {(-f.Step).ToString("0.#####", CultureInfo.InvariantCulture)}";
            var plus = $"octabossui adj {f.Id} {f.Step.ToString("0.#####", CultureInfo.InvariantCulture)}";

            var btnC = edit ? GlassHov : GlassLt;
            var txtC = edit ? TWhite : TMuted;
            c.Add(new CuiButton
            {
                Button = { Command = edit ? minus : "", Color = btnC },
                Text = { Text = "−", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = txtC },
                RectTransform = { AnchorMin = "0.60 0.15", AnchorMax = "0.70 0.90" }
            }, parent + "_r" + f.Id);
            c.Add(new CuiButton
            {
                Button = { Command = edit ? plus : "", Color = btnC },
                Text = { Text = "+", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = txtC },
                RectTransform = { AnchorMin = "0.88 0.15", AnchorMax = "0.98 0.90" }
            }, parent + "_r" + f.Id);

            // значение — поле ручного ввода
            c.Add(new CuiElement
            {
                Parent = parent + "_r" + f.Id,
                Name = parent + "_v" + f.Id,
                Components =
                {
                    new CuiImageComponent { Color = edit ? GlassBg : "0.03 0.03 0.03 0.6" },
                    new CuiRectTransformComponent { AnchorMin = "0.715 0.15", AnchorMax = "0.865 0.90" }
                }
            });
            if (edit)
                c.Add(new CuiElement
                {
                    Parent = parent + "_v" + f.Id,
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Text = v.ToString(f.Fmt, CultureInfo.InvariantCulture),
                            Command = cmdBase, FontSize = 12, Align = TextAnchor.MiddleCenter,
                            Color = warn ? GoldBright : TWhite, CharsLimit = 12, NeedsKeyboard = true
                        },
                        new CuiNeedsCursorComponent()
                    }
                });
            else
                c.Add(new CuiLabel
                {
                    Text = { Text = v.ToString(f.Fmt, CultureInfo.InvariantCulture) + " 🔒", FontSize = 10,
                             Align = TextAnchor.MiddleCenter, Color = TMuted },
                    RectTransform = { AnchorMin = "0.715 0.15", AnchorMax = "0.865 0.90" }
                }, parent + "_r" + f.Id);

            // шкала
            var t = Mathf.Clamp01((v - f.Min) / (f.Max - f.Min));
            c.Add(new CuiPanel
            {
                Image = { Color = "0.02 0.02 0.015 0.9" },
                RectTransform = { AnchorMin = "0.60 0.04", AnchorMax = "0.98 0.09" }
            }, parent + "_r" + f.Id, parent + "_t" + f.Id);
            c.Add(new CuiPanel
            {
                Image = { Color = Gold },
                RectTransform = { AnchorMin = "0 0", AnchorMax = $"{t.ToString("0.###", CultureInfo.InvariantCulture)} 1" }
            }, parent + "_t" + f.Id);
        }

        private void DrawToggle(CuiElementContainer c, string parent, BasePlayer player,
            UiBool f, BossSpec d, bool edit, float x, float y, float w)
        {
            var on = f.Get(d);
            var lib = UiLib();
            if (lib != null)
            {
                lib.Call("Toggle", c, parent, parent + "_r" + f.Id, new JObject
                {
                    ["rect"] = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", x, y, x + w, y + 0.058f),
                    ["label"] = f.Label, ["on"] = on, ["edit"] = edit,
                    ["cmd"] = $"octabossui toggle {f.Id}"
                });
                return;
            }
            c.Add(new CuiPanel
            {
                Image = { Color = GlassRow },
                RectTransform = { AnchorMin = $"{x} {y}", AnchorMax = $"{x + w} {y + 0.058f}" }
            }, parent, parent + "_r" + f.Id);
            c.Add(new CuiLabel
            {
                Text = { Text = f.Label, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = TMain },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.70 1" }
            }, parent + "_r" + f.Id);
            c.Add(new CuiButton
            {
                Button = { Command = edit ? $"octabossui toggle {f.Id}" : "", Color = on ? CGreen : GlassLt },
                Text = { Text = on ? "ВКЛ" : "OFF", FontSize = 10, Align = TextAnchor.MiddleCenter,
                         Color = on ? TWhite : TMuted },
                RectTransform = { AnchorMin = "0.74 0.16", AnchorMax = "0.97 0.84" }
            }, parent + "_r" + f.Id);
            if (on)
                c.Add(new CuiPanel
                {
                    Image = { Color = GoldBright },
                    RectTransform = { AnchorMin = "0.74 0.05", AnchorMax = "0.97 0.14" }
                }, parent + "_r" + f.Id);
        }

        private void DrawTextRow(CuiElementContainer c, string parent, BasePlayer player,
            UiTxt f, BossSpec d, bool edit, float x, float y, float w)
        {
            var lib = UiLib();
            if (lib != null)
            {
                lib.Call("TextRow", c, parent, parent + "_r" + f.Id, new JObject
                {
                    ["rect"] = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", x, y, x + w, y + 0.082f),
                    ["label"] = f.Label, ["value"] = f.Get(d) ?? "",
                    ["edit"] = edit, ["limit"] = f.Chars,
                    ["cmd"] = $"octabossui set {f.Id}"
                });
                return;
            }
            c.Add(new CuiPanel
            {
                Image = { Color = GlassRow },
                RectTransform = { AnchorMin = $"{x} {y}", AnchorMax = $"{x + w} {y + 0.082f}" }
            }, parent, parent + "_r" + f.Id);
            c.Add(new CuiLabel
            {
                Text = { Text = f.Label, FontSize = 10, Align = TextAnchor.MiddleLeft, Color = TMuted },
                RectTransform = { AnchorMin = "0.02 0.55", AnchorMax = "0.98 0.98" }
            }, parent + "_r" + f.Id);
            c.Add(new CuiElement
            {
                Parent = parent + "_r" + f.Id,
                Name = parent + "_v" + f.Id,
                Components =
                {
                    new CuiImageComponent { Color = edit ? GlassBg : "0.03 0.03 0.03 0.6" },
                    new CuiRectTransformComponent { AnchorMin = "0.02 0.06", AnchorMax = "0.98 0.50" }
                }
            });
            var val = f.Get(d) ?? "";
            if (edit)
                c.Add(new CuiElement
                {
                    Parent = parent + "_v" + f.Id,
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Text = val, Command = $"octabossui set {f.Id}", FontSize = 10,
                            Align = TextAnchor.MiddleLeft, Color = TWhite,
                            CharsLimit = f.Chars, NeedsKeyboard = true
                        },
                        new CuiNeedsCursorComponent()
                    }
                });
            else
                c.Add(new CuiLabel
                {
                    Text = { Text = (val.Length > 60 ? val.Substring(0, 60) + "…" : val) + " 🔒", FontSize = 9,
                             Align = TextAnchor.MiddleLeft, Color = TMuted },
                    RectTransform = { AnchorMin = "0.02 0.06", AnchorMax = "0.98 0.50" }
                }, parent + "_r" + f.Id);
        }

        private void DrawPresetBar(CuiElementContainer c, string parent, BasePlayer player, AdminUiState st)
        {
            var canP = Perm(player, "presets");
            var labels = new[] { "PVP-ЛЁГКИЙ", "PVP-СТАНДАРТ", "PVP-ХАРДКОР", "PVE-ТЕСТ" };
            var ids = new[] { "light", "std", "hard", "pve" };
            c.Add(new CuiPanel
            {
                Image = { Color = GlassRow },
                RectTransform = { AnchorMin = "0.008 0.915", AnchorMax = "0.992 0.99" }
            }, parent, parent + "_presets");
            c.Add(new CuiLabel
            {
                Text = { Text = "ПРЕСЕТ", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = Gold },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.10 1" }
            }, parent + "_presets");
            for (var i = 0; i < labels.Length; i++)
            {
                var x0 = 0.105f + i * 0.16f;
                c.Add(new CuiButton
                {
                    Button = { Command = canP ? $"octabossui preset {ids[i]}" : "", Color = canP ? GoldDark : GlassLt },
                    Text = { Text = labels[i], FontSize = 9, Align = TextAnchor.MiddleCenter,
                             Color = canP ? TWhite : TMuted },
                    RectTransform = { AnchorMin = $"{x0} 0.15", AnchorMax = $"{x0 + 0.145f} 0.85" }
                }, parent + "_presets");
            }
        }

        private void DrawEscortRoles(CuiElementContainer c, string parent, BossSpec d, float x, float y, float w)
        {
            var guards = Mathf.CeilToInt(d.Escort.Count * 0.6f);
            var decoys = Mathf.Max(0, d.Escort.Count - guards);
            c.Add(new CuiPanel
            {
                Image = { Color = GlassRow },
                RectTransform = { AnchorMin = $"{x} {y}", AnchorMax = $"{x + w} {y + 0.14f}" }
            }, parent, parent + "_roles");
            c.Add(new CuiLabel
            {
                Text = { Text = $"ОХРАНА  {guards} шт  •  держится у босса\nПРИМАНКИ  {decoys} шт  •  бегут на игрока\nНа фазе 3 часть разбегается, часть входит в ярость.",
                         FontSize = 10, Align = TextAnchor.MiddleLeft, Color = TMain },
                RectTransform = { AnchorMin = "0.03 0", AnchorMax = "0.97 1" }
            }, parent + "_roles");
        }

        private void DrawMonumentPick(CuiElementContainer c, string parent, BasePlayer player, float x, float y, float w)
        {
            c.Add(new CuiButton
            {
                Button = { Command = "octabossui mon", Color = CBlue },
                Text = { Text = "⊕ МОНУМЕНТ РЯДОМ СО МНОЙ", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = TWhite },
                RectTransform = { AnchorMin = $"{x} {y}", AnchorMax = $"{x + w} {y + 0.05f}" }
            }, parent);
        }

        private void DrawMessagesTab(CuiElementContainer c, string parent, BasePlayer player,
            AdminUiState st, BossSpec d, bool canMsg)
        {
            // список событий слева
            var lw = 0.26f;
            c.Add(new CuiPanel
            {
                Image = { Color = GlassRow },
                RectTransform = { AnchorMin = "0.008 0.30", AnchorMax = $"{0.008f + lw} 0.99" }
            }, parent, parent + "_mlist");
            for (var i = 0; i < MsgDefs.Length; i++)
            {
                var sel = MsgDefs[i][0] == st.MsgId;
                // 9 пунктов в панели 0.30..0.99: шаг 0.075, иначе хвост
                // списка вылезает за нижний край и рисуется поверх другого UI
                var y1 = 0.98f - i * 0.075f;
                c.Add(new CuiButton
                {
                    Button = { Command = $"octabossui msg {MsgDefs[i][0]}", Color = sel ? GoldGlass : "0 0 0 0" },
                    Text = { Text = MsgDefs[i][1], FontSize = 10, Align = TextAnchor.MiddleLeft,
                             Color = sel ? GoldBright : TMuted },
                    RectTransform = { AnchorMin = $"0.04 {y1 - 0.066f}", AnchorMax = $"0.96 {y1 - 0.004f}" }
                }, parent + "_mlist");
            }

            // большое поле справа
            var cur = MsgGet(d, st.MsgId);
            c.Add(new CuiPanel
            {
                Image = { Color = GlassRow },
                RectTransform = { AnchorMin = "0.285 0.80", AnchorMax = "0.992 0.99" }
            }, parent, parent + "_medit");
            c.Add(new CuiLabel
            {
                Text = { Text = "{name} — имя босса   •   {0} — квадрат / минуты / игрок", FontSize = 9,
                         Align = TextAnchor.MiddleLeft, Color = TMuted },
                RectTransform = { AnchorMin = "0.02 0.78", AnchorMax = "0.98 1" }
            }, parent + "_medit");
            c.Add(new CuiElement
            {
                Parent = parent + "_medit", Name = parent + "_mv",
                Components =
                {
                    new CuiImageComponent { Color = canMsg ? GlassBg : "0.03 0.03 0.03 0.6" },
                    new CuiRectTransformComponent { AnchorMin = "0.02 0.06", AnchorMax = "0.98 0.72" }
                }
            });
            if (canMsg)
                c.Add(new CuiElement
                {
                    Parent = parent + "_mv",
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Text = cur, Command = $"octabossui set {st.MsgId}", FontSize = 11,
                            Align = TextAnchor.MiddleLeft, Color = TWhite, CharsLimit = 220, NeedsKeyboard = true
                        },
                        new CuiNeedsCursorComponent()
                    }
                });
            else
                c.Add(new CuiLabel
                {
                    Text = { Text = cur + " 🔒", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = TMuted },
                    RectTransform = { AnchorMin = "0.02 0.06", AnchorMax = "0.98 0.72" }
                }, parent + "_medit");

            // варианты предупреждения — навигатор
            var list = d.WarnAnnounceVariants;
            var idx = Mathf.Clamp(st.WarnIdx, 0, Math.Max(0, list.Count - 1));
            st.WarnIdx = idx;
            c.Add(new CuiPanel
            {
                Image = { Color = GlassRow },
                RectTransform = { AnchorMin = "0.285 0.42", AnchorMax = "0.992 0.78" }
            }, parent, parent + "_wvar");
            c.Add(new CuiLabel
            {
                Text = { Text = $"ВАРИАНТЫ ПРЕДУПРЕЖДЕНИЯ  {list.Count} шт", FontSize = 10,
                         Align = TextAnchor.MiddleLeft, Color = Gold },
                RectTransform = { AnchorMin = "0.02 0.82", AnchorMax = "0.98 1" }
            }, parent + "_wvar");
            c.Add(new CuiButton
            {
                Button = { Command = "octabossui warn -1", Color = GlassHov },
                Text = { Text = "◄", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = TWhite },
                RectTransform = { AnchorMin = "0.02 0.05", AnchorMax = "0.08 0.75" }
            }, parent + "_wvar");
            c.Add(new CuiButton
            {
                Button = { Command = "octabossui warn 1", Color = GlassHov },
                Text = { Text = "►", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = TWhite },
                RectTransform = { AnchorMin = "0.84 0.05", AnchorMax = "0.90 0.75" }
            }, parent + "_wvar");
            var wv = list.Count > 0 ? list[idx] : "(пусто — используется основной текст)";
            c.Add(new CuiElement
            {
                Parent = parent + "_wvar", Name = parent + "_wv",
                Components =
                {
                    new CuiImageComponent { Color = canMsg ? GlassBg : "0.03 0.03 0.03 0.6" },
                    new CuiRectTransformComponent { AnchorMin = "0.10 0.05", AnchorMax = "0.82 0.75" }
                }
            });
            if (canMsg && list.Count > 0)
                c.Add(new CuiElement
                {
                    Parent = parent + "_wv",
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Text = wv, Command = "octabossui warnset", FontSize = 10,
                            Align = TextAnchor.MiddleLeft, Color = TWhite, CharsLimit = 220, NeedsKeyboard = true
                        },
                        new CuiNeedsCursorComponent()
                    }
                });
            else
                c.Add(new CuiLabel
                {
                    Text = { Text = wv, FontSize = 10, Align = TextAnchor.MiddleLeft, Color = TMuted },
                    RectTransform = { AnchorMin = "0.10 0.05", AnchorMax = "0.82 0.75" }
                }, parent + "_wvar");
            c.Add(new CuiButton
            {
                Button = { Command = canMsg ? "octabossui warnadd" : "", Color = canMsg ? CGreen : GlassLt },
                Text = { Text = "+", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = TWhite },
                RectTransform = { AnchorMin = "0.915 0.42", AnchorMax = "0.98 0.75" }
            }, parent + "_wvar");
            c.Add(new CuiButton
            {
                Button = { Command = canMsg && list.Count > 0 ? "octabossui warndel" : "", Color = canMsg ? CRed : GlassLt },
                Text = { Text = "−", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = TWhite },
                RectTransform = { AnchorMin = "0.915 0.05", AnchorMax = "0.98 0.38" }
            }, parent + "_wvar");
        }

        private void DrawDangerTab(CuiElementContainer c, string parent, BasePlayer player, AdminUiState st)
        {
            var spec = UiSpec(player);
            var items = new[]
            {
                new { T = "ТИХИЙ ДЕСПАВН — без лута и наград", Cmd = "octabossui ask despawn", Col = GoldDark, Prm = "kill" },
                new { T = "СЛЕДУЮЩИЙ СПАВН ЧЕРЕЗ 1 МИН", Cmd = "octabossui next1", Col = CBlue, Prm = "spawn" },
                new { T = "СБРОСИТЬ ТРЕКЕР УРОНА/УГРОЗЫ", Cmd = "octabossui resetdmg", Col = GlassHov, Prm = "edit" },
                new { T = "ПРИМЕНИТЬ ЧЕРНОВИК К ЖИВОМУ БОССУ", Cmd = "octabossui ask applive", Col = Gold, Prm = "edit" },
                new { T = "ПЕРЕЧИТАТЬ КОНФИГ С ДИСКА", Cmd = "octabossui ask reloadcfg", Col = CRed, Prm = "edit" },
            };
            var y = 0.90f;
            c.Add(new CuiLabel
            {
                Text = { Text = "Действия выполняются сразу и необратимо.", FontSize = 10,
                         Align = TextAnchor.MiddleLeft, Color = TMuted },
                RectTransform = { AnchorMin = "0.008 0.95", AnchorMax = "0.99 0.995" }
            }, parent);
            foreach (var it in items)
            {
                var ok = Perm(player, it.Prm);
                c.Add(new CuiButton
                {
                    Button = { Command = ok ? it.Cmd : "", Color = ok ? it.Col : GlassLt },
                    Text = { Text = it.T, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = ok ? TWhite : TMuted },
                    RectTransform = { AnchorMin = $"0.008 {y - 0.085f}", AnchorMax = $"0.60 {y - 0.005f}" }
                }, parent);
                y -= 0.10f;
            }
        }

        // ---------- нижняя панель быстрых действий ----------

        private void UiDrawBottom(CuiElementContainer c, BasePlayer player, AdminUiState st, BossSpec spec)
        {
            var bar = c.Add(new CuiPanel
            {
                Image = { Color = GlassLt },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.068" }
            }, AdminUIName, UiN("BOT"));
            c.Add(new CuiPanel
            {
                Image = { Color = GoldDark },
                RectTransform = { AnchorMin = "0 0.97", AnchorMax = "1 1" }
            }, bar);

            var btns = new[]
            {
                new { T = "СПАВНИТЬ",   Cmd = "octabossui ask spawn",   Col = Gold,    Prm = "spawn" },
                new { T = "УБИТЬ",      Cmd = "octabossui ask kill",    Col = CRed,    Prm = "kill" },
                new { T = "КО МНЕ",     Cmd = "octabossui tome",        Col = CBlue,   Prm = "spawn" },
                new { T = "СОХРАНИТЬ",  Cmd = "octabossui save",        Col = CGreen,  Prm = "edit" },
                new { T = "ОБНОВИТЬ",   Cmd = "octabossui ask reload",  Col = GlassHov,Prm = "view" },
                new { T = "ЗАКРЫТЬ",    Cmd = "octabossui close",       Col = GoldDark,Prm = "view" },
            };
            var bw = 0.155f; var gap = 0.008f; var x = 0.012f;
            foreach (var b in btns)
            {
                var ok = Perm(player, b.Prm);
                c.Add(new CuiButton
                {
                    Button = { Command = ok ? b.Cmd : "", Color = ok ? b.Col : GlassBg },
                    Text = { Text = b.T, FontSize = 11, Align = TextAnchor.MiddleCenter,
                             Color = ok ? TWhite : TMuted },
                    RectTransform = { AnchorMin = $"{x} 0.18", AnchorMax = $"{x + bw} 0.82" }
                }, bar);
                x += bw + gap;
            }
        }

        // ---------- toast + confirm ----------

        private void UiDrawToast(CuiElementContainer c, AdminUiState st)
        {
            if (string.IsNullOrEmpty(st.Toast)) return;
            c.Add(new CuiPanel
            {
                Image = { Color = st.ToastColor, FadeIn = 0.15f },
                RectTransform = { AnchorMin = "0.30 0.075", AnchorMax = "0.78 0.115" }
            }, AdminUIName, UiN("TST"));
            c.Add(new CuiLabel
            {
                Text = { Text = st.Toast, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = TWhite, FadeIn = 0.15f },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, UiN("TST"));
        }

        private void UiToast(BasePlayer player, string text, string color)
        {
            var lib = UiLib();
            if (lib != null) // OctaUI.Toast: те же якоря + авто-затухание внутри
            {
                lib.Call("Toast", player, AdminUIName, text, color, 2.5f);
                return;
            }
            var st = UiStateOf(player);
            st.Toast = text; st.ToastColor = color; st.ToastSeq++;
            var seq = st.ToastSeq;
            UiRefresh(player, "TST");
            timer.Once(2.5f, () =>
            {
                if (!_ui.TryGetValue(Uid(player), out var s2) || s2.ToastSeq != seq) return;
                s2.Toast = null;
                CuiHelper.DestroyUi(player, UiN("TST"));
            });
        }

        private void UiDrawConfirm(CuiElementContainer c, BasePlayer player, AdminUiState st, BossSpec spec)
        {
            if (string.IsNullOrEmpty(st.Pending)) return;
            // затемнение всей панели
            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.75" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, AdminUIName, UiN("CFM"));
            string title, yes = "ПОДТВЕРДИТЬ", col = CRed;
            switch (st.Pending.Split(':')[0])
            {
                case "kill": title = $"УБИТЬ {spec.DisplayName}?"; yes = "УБИТЬ"; break;
                case "spawn": title = "Босс уже на карте.\nЗаменить текущего?"; yes = "ЗАМЕНИТЬ"; col = Gold; break;
                case "despawn": title = $"ТИХИЙ ДЕСПАВН {spec.DisplayName}?\nЛута и наград не будет."; yes = "ДЕСПАВН"; break;
                case "reload": title = "Отбросить несохранённые\nизменения черновика?"; yes = "ОТБРОСИТЬ"; col = Gold; break;
                case "reloadcfg": title = "Перечитать конфиг с диска?\nВсе черновики будут потеряны."; yes = "ПЕРЕЧИТАТЬ"; break;
                case "preset": title = $"Применить пресет {st.Pending.Split(':')[1].ToUpper()}?\nТекущий черновик будет заменён."; yes = "ПРИМЕНИТЬ"; col = Gold; break;
                case "applive": title = "Применить черновик к ЖИВОМУ\nбоссу прямо сейчас?\nСохранённый конфиг не меняется."; yes = "ПРИМЕНИТЬ"; col = Gold; break;
                case "boss": title = "Несохранённые изменения\nбудут потеряны. Продолжить?"; yes = "ПЕРЕЙТИ"; col = Gold; break;
                default: title = "Подтвердить действие?"; break;
            }
            var lib = UiLib();
            if (lib != null) // OctaUI.Confirm — геометрия идентична
            {
                lib.Call("Confirm", c, AdminUIName, UiN("CFM"), new JObject
                {
                    ["title"] = title, ["yes"] = yes, ["no"] = "ОТМЕНА",
                    ["color"] = col,
                    ["cmdYes"] = "octabossui confirm", ["cmdNo"] = "octabossui cancel"
                });
                return;
            }
            c.Add(new CuiPanel
            {
                Image = { Color = GlassBg },
                RectTransform = { AnchorMin = "0.32 0.40", AnchorMax = "0.68 0.62" }
            }, UiN("CFM"), UiN("CFM_box"));
            c.Add(new CuiPanel
            {
                Image = { Color = col },
                RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" }
            }, UiN("CFM_box"));
            c.Add(new CuiLabel
            {
                Text = { Text = title, FontSize = 13, Align = TextAnchor.MiddleCenter, Color = TWhite },
                RectTransform = { AnchorMin = "0.05 0.35", AnchorMax = "0.95 0.90" }
            }, UiN("CFM_box"));
            c.Add(new CuiButton
            {
                Button = { Command = "octabossui cancel", Color = GlassHov },
                Text = { Text = "ОТМЕНА", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = TMain },
                RectTransform = { AnchorMin = "0.08 0.10", AnchorMax = "0.46 0.30" }
            }, UiN("CFM_box"));
            c.Add(new CuiButton
            {
                Button = { Command = "octabossui confirm", Color = col },
                Text = { Text = yes, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = TWhite },
                RectTransform = { AnchorMin = "0.54 0.10", AnchorMax = "0.92 0.30" }
            }, UiN("CFM_box"));
        }

        // ---------- команды ----------

        [ConsoleCommand("octabossui")]
        private void CmdBossUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !Perm(player, "view")) return;
            var args = arg.Args;
            if (args == null || args.Length == 0) return;
            var st = UiStateOf(player);
            var spec = UiSpec(player);
            if (spec == null) return;
            var a0 = args[0].ToString();
            var a1 = args.Length > 1 ? args[1].ToString() : "";

            switch (a0)
            {
                case "close":
                    _ui.Remove(Uid(player));
                    foreach (var p in UiParts) CuiHelper.DestroyUi(player, UiN(p));
                    CuiHelper.DestroyUi(player, AdminUIName);
                    return;

                case "boss":
                    if (a1 == st.BossKey) break;
                    if (st.Dirty)
                    {
                        st.Pending = "boss:" + a1;
                        UiRefresh(player, "CFM");
                        return;
                    }
                    st.BossKey = a1;
                    ResetDraft(st);
                    UiDrawAll(player);
                    return;

                case "tab":
                    st.Tab = (AdminTab)Mathf.Clamp(ParseI(a1, 0), 0, 7);
                    UiRefresh(player, "CNT", "TAB");
                    return;

                case "toggle":
                {
                    if (!Perm(player, "edit")) { UiToast(player, "✕ Нет права edit", CRed); return; }
                    var f = Bools.FirstOrDefault(b => b.Id == a1);
                    if (f != null && Perm(player, f.Perm)) { f.Set(st.Draft, !f.Get(st.Draft)); st.Dirty = true; }
                    UiRefresh(player, "CNT", "HDR");
                    return;
                }

                case "adj":
                {
                    var f = Nums.FirstOrDefault(n => n.Id == a1);
                    if (f == null) return;
                    if (!Perm(player, f.Perm)) { UiToast(player, "✕ Нет права " + f.Perm, CRed); return; }
                    var v = Mathf.Clamp(f.Get(st.Draft) + ParseF(ArgAt(arg, 2), 0f), f.Min, f.Max);
                    v = Mathf.Round(v / f.Step) * f.Step;
                    f.Set(st.Draft, v); st.Dirty = true;
                    UiRefresh(player, "CNT", "HDR");
                    return;
                }

                case "set":
                {
                    var value = string.Join(" ", args.Skip(2).Select(x => x.ToString()));
                    var nf = Nums.FirstOrDefault(n => n.Id == a1);
                    if (nf != null)
                    {
                        if (!Perm(player, nf.Perm)) { UiToast(player, "✕ Нет права " + nf.Perm, CRed); return; }
                        var v = ParseF(value, nf.Get(st.Draft));
                        v = Mathf.Clamp(v, nf.Min, nf.Max);
                        v = Mathf.Round(v / nf.Step) * nf.Step;
                        nf.Set(st.Draft, v); st.Dirty = true;
                        UiRefresh(player, "CNT", "HDR");
                        return;
                    }
                    if (a1.StartsWith("m_"))
                    {
                        if (!Perm(player, "messages")) { UiToast(player, "✕ Нет права messages", CRed); return; }
                        MsgSet(st.Draft, a1, value); st.Dirty = true;
                        return;
                    }
                    var tf = Txts.FirstOrDefault(t => t.Id == a1);
                    if (tf != null)
                    {
                        if (!Perm(player, tf.Perm)) { UiToast(player, "✕ Нет права " + tf.Perm, CRed); return; }
                        tf.Set(st.Draft, value); st.Dirty = true;
                        UiRefresh(player, "CNT", "HDR");
                    }
                    return;
                }

                case "msg":
                    st.MsgId = a1;
                    UiRefresh(player, "CNT");
                    return;

                case "warn":
                    st.WarnIdx += ParseI(a1, 0);
                    var wl = st.Draft.WarnAnnounceVariants;
                    st.WarnIdx = wl.Count == 0 ? 0 : ((st.WarnIdx % wl.Count) + wl.Count) % wl.Count;
                    UiRefresh(player, "CNT");
                    return;

                case "warnset":
                {
                    if (!Perm(player, "messages")) { UiToast(player, "✕ Нет права messages", CRed); return; }
                    var wl2 = st.Draft.WarnAnnounceVariants;
                    if (wl2.Count > 0)
                    {
                        st.WarnIdx = Mathf.Clamp(st.WarnIdx, 0, wl2.Count - 1);
                        wl2[st.WarnIdx] = string.Join(" ", args.Skip(1).Select(x => x.ToString()));
                        st.Dirty = true;
                    }
                    return;
                }

                case "warnadd":
                    if (!Perm(player, "messages")) { UiToast(player, "✕ Нет права messages", CRed); return; }
                    st.Draft.WarnAnnounceVariants.Add("{name} появится через {0} мин. Готовьтесь!");
                    st.WarnIdx = st.Draft.WarnAnnounceVariants.Count - 1;
                    st.Dirty = true;
                    UiRefresh(player, "CNT", "HDR");
                    return;

                case "warndel":
                    if (!Perm(player, "messages")) { UiToast(player, "✕ Нет права messages", CRed); return; }
                    var wl3 = st.Draft.WarnAnnounceVariants;
                    if (wl3.Count > 0)
                    {
                        wl3.RemoveAt(Mathf.Clamp(st.WarnIdx, 0, wl3.Count - 1));
                        st.WarnIdx = Mathf.Clamp(st.WarnIdx, 0, wl3.Count - 1);
                        st.Dirty = true;
                    }
                    UiRefresh(player, "CNT", "HDR");
                    return;

                case "ask": // действие требует подтверждения
                {
                    _bosses.TryGetValue(spec.Key, out var bs);
                    if (a1 == "spawn" && (bs == null || !bs.Alive)) { DoSpawn(player, spec); return; }
                    if ((a1 == "kill" || a1 == "applive") && (bs == null || !bs.Alive)) { UiToast(player, "⚠ Босса нет на карте", Gold); return; }
                    if (a1 == "reload" && !st.Dirty) { ResetDraft(st); UiDrawAll(player); UiToast(player, "✓ Черновик обновлён", CGreen); return; }
                    st.Pending = a1;
                    UiRefresh(player, "CFM");
                    return;
                }

                case "preset":
                    if (!Perm(player, "presets")) { UiToast(player, "✕ Нет права presets", CRed); return; }
                    st.Pending = "preset:" + a1;
                    UiRefresh(player, "CFM");
                    return;

                case "cancel":
                    st.Pending = null;
                    UiRefresh(player, "CFM");
                    return;

                case "confirm":
                {
                    var pending = st.Pending;
                    st.Pending = null;
                    if (string.IsNullOrEmpty(pending)) { UiRefresh(player, "CFM"); return; }
                    var parts = pending.Split(':');
                    switch (parts[0])
                    {
                        case "kill":
                            AdminKill(FindState(spec.Key));
                            UiToast(player, "✓ Босс убит", CGreen);
                            break;
                        case "spawn":
                        {
                            var bs2 = FindState(spec.Key);
                            if (bs2 != null && bs2.Alive) DespawnBoss(bs2);
                            DoSpawn(player, spec);
                            break;
                        }
                        case "despawn":
                        {
                            var bsd = FindState(spec.Key);
                            if (bsd != null) DespawnBoss(bsd);
                            UiToast(player, "✓ Тихий деспавн", CGreen);
                            break;
                        }
                        case "reload":
                            ResetDraft(st);
                            UiToast(player, "✓ Черновик обновлён", CGreen);
                            break;
                        case "applive":
                        {
                            // явная точечная замена боевого слепка живого босса
                            // черновиком — конфиг при этом не трогаем
                            var bsa = FindState(spec.Key);
                            if (bsa != null && bsa.Alive)
                            {
                                bsa.Spec = CloneSpec(st.Draft);
                                UiToast(player, "✓ Применено к живому боссу", CGreen);
                            }
                            else UiToast(player, "⚠ Босса нет на карте", Gold);
                            break;
                        }
                        case "reloadcfg":
                            LoadConfig();
                            _ui.Clear();
                            UiStateOf(player);
                            UiDrawAll(player);
                            UiToast(player, "✓ Конфиг перечитан", CGreen);
                            return;
                        case "preset":
                            ApplyPreset(st.Draft, parts[1]);
                            st.Dirty = true;
                            UiToast(player, "✓ Пресет применён к черновику", CGreen);
                            break;
                        case "boss":
                            st.BossKey = parts[1];
                            ResetDraft(st);
                            UiDrawAll(player);
                            return;
                    }
                    UiDrawAll(player);
                    return;
                }

                case "tome":
                {
                    var bs3 = FindState(spec.Key);
                    if (bs3 == null || !bs3.Alive) { UiToast(player, "⚠ Босса нет на карте", Gold); return; }
                    var pos = player.transform.position + player.transform.forward * 12f;
                    pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 0.3f;
                    try { bs3.Entity.transform.position = pos; bs3.Entity.SendNetworkUpdateImmediate(); }
                    catch { }
                    UiToast(player, "✓ Босс перенесён к вам", CGreen);
                    UiRefresh(player, "STS");
                    return;
                }

                case "next1":
                {
                    var bs4 = SelectedState(player);
                    if (bs4 != null)
                    {
                        bs4.NextSpawnAt = DateTime.UtcNow.AddMinutes(1);
                        bs4.Warned = true;
                        UiToast(player, "✓ Спавн через 1 мин", CGreen);
                    }
                    UiRefresh(player, "STS", "LST");
                    return;
                }

                case "resetdmg":
                {
                    var bs5 = FindState(spec.Key);
                    if (bs5 != null)
                    {
                        bs5.DamageBy.Clear(); bs5.ThreatBy.Clear(); bs5.RecentHits.Clear();
                        UiToast(player, "✓ Трекер урона очищен", CGreen);
                    }
                    return;
                }

                case "mon":
                {
                    var mon = NearestMonument(player.transform.position, 400f);
                    if (mon == null) { UiToast(player, "✕ Рядом нет монумента", CRed); return; }
                    var name = System.IO.Path.GetFileNameWithoutExtension(mon.name ?? "");
                    if (string.IsNullOrEmpty(name)) { UiToast(player, "✕ Не распознан монумент", CRed); return; }
                    var list = st.Draft.Spawn.MonumentNames.Split(',')
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    if (list.Contains(name))
                    {
                        list.Remove(name);
                        UiToast(player, $"✓ «{name}» убран из якорей", CGreen);
                    }
                    else
                    {
                        list.Add(name);
                        UiToast(player, $"✓ «{name}» добавлен в якоря", CGreen);
                    }
                    st.Draft.Spawn.MonumentNames = string.Join(",", list);
                    st.Dirty = true;
                    UiRefresh(player, "CNT", "HDR");
                    return;
                }

                case "save":
                {
                    if (!Perm(player, "edit")) { UiToast(player, "✕ Нет права edit", CRed); return; }
                    string warnMsg;
                    ValidateDraft(st.Draft, out warnMsg);
                    var idx = _cfg.Bosses.FindIndex(b => b.Key == st.BossKey);
                    if (idx >= 0)
                    {
                        var newLive = CloneSpec(st.Draft);
                        _cfg.Bosses[idx] = newLive;
                        if (_bosses.TryGetValue(st.BossKey, out var bst))
                        {
                            bst.Live = newLive;
                            // имя — косметика: переименовываем живого босса сразу,
                            // боевые статы остаются со слепка спавна
                            if (bst.Entity != null && !bst.Entity.IsDestroyed &&
                                bst.Spec != null && bst.Spec.DisplayName != newLive.DisplayName)
                            {
                                bst.Spec.DisplayName = newLive.DisplayName;
                                _bossNames[bst.Entity] = newLive.DisplayName;
                                try { if (bst.Livestock != null) bst.Livestock.AnimalName = newLive.DisplayName; } catch { }
                                foreach (var e in bst.Escorts)
                                    if (e != null) _bossNames[e] = $"Свита ({newLive.DisplayName})";
                            }
                        }
                    }
                    st.Dirty = false;
                    SyncRuntime();
                    SaveConfig();
                    UiDrawAll(player);
                    UiToast(player, warnMsg != null
                            ? "⚠ " + warnMsg
                            : "✓ Сохранено — живой босс на старых настройках",
                            warnMsg != null ? Gold : CGreen);
                    return;
                }
            }
        }

        private void DoSpawn(BasePlayer player, BossSpec spec)
        {
            var st = SelectedState(player);
            if (st == null) return;
            SpawnBoss(st, player.transform.position + player.transform.forward * 15f);
            UiToast(player, st.Alive ? "✓ Босс заспавнен" : "✕ Не удалось найти точку спавна",
                    st.Alive ? CGreen : CRed);
            UiDrawAll(player);
        }

        private BossState SelectedState(BasePlayer player)
        {
            var spec = UiSpec(player);
            if (spec == null) return null;
            BossState st;
            if (!_bosses.TryGetValue(spec.Key, out st))
            {
                st = new BossState { Live = spec, Spec = spec };
                ScheduleNext(st);
                _bosses[spec.Key] = st;
            }
            return st;
        }

        private static MonumentInfo NearestMonument(Vector3 pos, float maxDist)
        {
            MonumentInfo best = null; var bestD = maxDist;
            var mons = TerrainMeta.Path?.Monuments;
            if (mons == null) return null;
            foreach (var m in mons)
            {
                var d = Vector3.Distance(pos, m.transform.position);
                if (d < bestD) { bestD = d; best = m; }
            }
            return best;
        }

        // ---------- валидация черновика ----------

        private string ValidateDraft(BossSpec d, out string warnMsg)
        {
            warnMsg = null;
            d.Health = Mathf.Max(100f, d.Health);
            d.Scale = Mathf.Max(0.2f, d.Scale);
            d.SpeedMultiplier = Mathf.Max(0f, d.SpeedMultiplier);
            d.DamageMultiplier = Mathf.Max(0f, d.DamageMultiplier);
            d.Phase2Threshold = Mathf.Clamp01(d.Phase2Threshold);
            d.Phase3Threshold = Mathf.Clamp01(d.Phase3Threshold);
            if (d.Phase3Threshold >= d.Phase2Threshold)
            {
                d.Phase3Threshold = Mathf.Max(0.05f, d.Phase2Threshold - 0.1f);
                warnMsg = "Фаза 3 должна начинаться ниже порога фазы 2 — исправлено";
            }
            d.Escort.Count = Mathf.Max(0, d.Escort.Count);
            d.Escort.Radius = Mathf.Max(1f, d.Escort.Radius);
            d.Spawn.IntervalMinutes = Mathf.Max(1f, d.Spawn.IntervalMinutes);
            d.Spawn.MarkerRadius = Mathf.Clamp01(d.Spawn.MarkerRadius);
            d.Rewards.Currency = Mathf.Max(0, d.Rewards.Currency);
            d.Rewards.BuffDamage = Mathf.Max(1f, d.Rewards.BuffDamage);
            d.Rewards.LootLockSeconds = Mathf.Max(0f, d.Rewards.LootLockSeconds);
            return warnMsg;
        }

        private static void ApplyPreset(BossSpec d, string name)
        {
            switch (name)
            {
                case "light":
                    d.Health = 3000; d.DamageMultiplier = 2f; d.Escort.Count = 2;
                    d.RegenPercentPerSec = 1.5f; d.Rewards.LootLockSeconds = 10f; d.Rewards.BuffDamage = 1.05f;
                    break;
                case "std":
                    d.Health = 9000; d.DamageMultiplier = 3f; d.Escort.Count = 5;
                    d.RegenPercentPerSec = 2.5f; d.Rewards.LootLockSeconds = 10f; d.Rewards.BuffDamage = 1.10f;
                    break;
                case "hard":
                    d.Health = 15000; d.DamageMultiplier = 4f; d.Escort.Count = 8;
                    d.RegenPercentPerSec = 3f; d.Rewards.LootLockSeconds = 20f; d.Rewards.BuffDamage = 1.15f;
                    break;
                case "pve":
                    d.Health = 1500; d.DamageMultiplier = 1f; d.Escort.Count = 0;
                    d.RegenPercentPerSec = 5f; d.Rewards.LootLockSeconds = 0f; d.Rewards.BuffDamage = 1f;
                    d.LeapSeconds = 0f;
                    break;
            }
        }

        // подхватывает виды, включённые/выключенные через панель или конфиг на лету
        private void SyncRuntime()
        {
            foreach (var spec in _cfg.Bosses)
            {
                if (spec.Enabled && !_bosses.ContainsKey(spec.Key))
                {
                    var st = new BossState { Live = spec, Spec = spec };
                    ScheduleNext(st, first: true);
                    _bosses[spec.Key] = st;
                }
                else if (!spec.Enabled && _bosses.TryGetValue(spec.Key, out var st))
                {
                    if (st.Entity != null) _bossNames.Remove(st.Entity);
                    foreach (var e in st.Escorts)
                        if (e != null) _bossNames.Remove(e);
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
                return $"{name}: ЖИВ HP {Mathf.CeilToInt(st.Entity.Health())}/{st.Entity.MaxHealth():0} @ {GridOf(st.Entity.transform.position)}{(st.Raging ? " ЯРОСТЬ" : "")}";
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
                SendReply(player, $"{Tag} <color=#E8B84B>{st.Spec.DisplayName}</color>: HP {Mathf.CeilToInt(st.Entity.Health())}/{st.Entity.MaxHealth():0}, квадрат {GridOf(st.Entity.transform.position)}, топ-дамагер: {top}");
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
