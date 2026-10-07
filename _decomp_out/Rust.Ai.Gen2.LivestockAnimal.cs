#define UNITY_ASSERTIONS
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using ConVar;
using Development.Attributes;
using Facepunch;
using Facepunch.Rust;
using Network;
using ProtoBuf;
using Rust.Ai.Gen2.Nav;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Serialization;

namespace Rust.Ai.Gen2;

public class LivestockAnimal : BaseNPC2, ISimpleHearingReceiver, INaturalDeathPose, IFSMDebugInfo, ISenseObserver
{
	public enum DayActivity
	{
		Idling,
		Grazing,
		Resting,
		Other
	}

	public enum TrustTier
	{
		Stranger,
		Known,
		Tolerated,
		Bonded,
		Herd
	}

	private struct Acquaintance
	{
		public ulong userId;

		public float seconds;

		public TimeSince sinceCredited;
	}

	public enum FamiliarityReason
	{
		Handled,
		Purchased,
		Debug
	}

	private struct CullCandidate : IComparable<CullCandidate>
	{
		public readonly LivestockAnimal Animal;

		private readonly int group;

		private readonly float worth;

		public CullCandidate(LivestockAnimal animal)
		{
			Animal = animal;
			BuildingPrivlidge cupboard;
			if (!animal.IsFullyBonded)
			{
				group = 0;
			}
			else if (!animal.TryGetHomeCupboard(out cupboard))
			{
				group = 1;
			}
			else
			{
				group = 2;
			}
			worth = animal.SaleValueScale;
		}

		public int CompareTo(CullCandidate other)
		{
			if (group != other.group)
			{
				int num = group;
				return num.CompareTo(other.group);
			}
			float num2 = worth;
			return num2.CompareTo(other.worth);
		}
	}

	public enum AgeStage
	{
		Infant,
		Adult
	}

	public enum SexForm
	{
		Either,
		Female,
		Male
	}

	public enum LivestockSize
	{
		Large = 1,
		Small
	}

	[Flags]
	public enum LivestockSizeFlags
	{
		Large = 1,
		Small = 2
	}

	public enum Need
	{
		Fullness,
		PersonalSpace,
		Hydration
	}

	public struct TickableNeed
	{
		public float NeedValue;

		public float NextTick;

		public TickableNeed(float startingValue)
		{
			NeedValue = startingValue;
			NextTick = 0f;
		}

		[PoolAnalyzerGetWrapper]
		public LivestockNeed ToSavedData()
		{
			LivestockNeed livestockNeed = Facepunch.Pool.Get<LivestockNeed>();
			livestockNeed.needValue = NeedValue;
			livestockNeed.needTick = NextTick;
			return livestockNeed;
		}

		public void LoadFrom(LivestockNeed need)
		{
			NeedValue = need.needValue;
			NextTick = need.needTick;
		}
	}

	public class LivestockNeedsQueue : PersistentObjectWorkQueue<LivestockAnimal>
	{
		protected override void RunJob(LivestockAnimal entity)
		{
			if (entity == null || entity.IsDestroyed)
			{
				return;
			}
			using (LivestockProfiler.Sample(LivestockProfiler.Section.Needs))
			{
				entity.TickNeeds();
				if (!entity.IsDead())
				{
					entity.TickFamiliarity();
					entity.TickFrailty();
					entity.TickLead();
				}
			}
		}
	}

	private enum DebugValue
	{
		Age,
		Fullness,
		Hydration,
		PersonalSpace,
		Mood,
		Familiarity,
		Count
	}

	private struct FlashedValue
	{
		private float lastShown;

		private TimeSince sinceMoved;

		private int direction;

		private bool sampled;

		public int Sample(float value, float precision)
		{
			float showstateflashduration = AI.showstateflashduration;
			float num = Mathf.Round(value / Mathf.Max(precision, 0.0001f));
			if (!sampled || showstateflashduration <= 0f)
			{
				sampled = true;
				lastShown = num;
				direction = 0;
				return 0;
			}
			if (!Mathf.Approximately(num, lastShown))
			{
				direction = ((num > lastShown) ? 1 : (-1));
				lastShown = num;
				sinceMoved = 0f;
			}
			else if ((float)sinceMoved > showstateflashduration)
			{
				direction = 0;
			}
			return direction;
		}
	}

	private PersistentTimer ageTimer;

	private LivestockPattern pattern;

	private LivestockSexParts sexParts;

	private bool breedingPaused;

	private float conditionSecondsSpent;

	private float conditionSecondsSkipped;

	public const float ConditionHealthStep = 0.05f;

	private float conditionUnsentHealth;

	[Header("Day activity")]
	[Tooltip("Fullness at or below this is starving rather than peckish. A starving animal gets up from a lie down and breaks off a curious walk to go and eat.")]
	[Range(0f, 1f)]
	public float StarvingThreshold = 0.2f;

	[Tooltip("How many seconds of waking time the day activity read out is measured over. It only reports how the day split up, nothing reads it to decide what to do.")]
	public float DayBudgetWindow = 300f;

	private float[] activityWeights;

	[Header("Dung")]
	[Tooltip("The item dropped each time the animal dungs. Leave empty to disable it, which is how an age stage that shouldn't dung - a calf, say - opts out.")]
	public ItemDefinition DungItem;

	[Tooltip("How long (in seconds) between dung drops. Zero disables it.")]
	public float DungInterval;

	[Tooltip("Random variance applied to DungInterval, as a fraction of it. 0.2 means +/-20%, so a herd doesn't drop in lockstep.")]
	[Range(0f, 1f)]
	public float DungIntervalVariance = 0.2f;

	[Tooltip("Where the dung drops from. Falls back to the animal's origin when unset.")]
	public Transform DungSpawnPoint;

	private PersistentTimer dungTimer;

	public const int MaxAcquaintances = 8;

	public const int TrustTiersAboveStranger = 4;

	private Acquaintance[] acquaintances;

	private Acquaintance probation;

	private TimeSince probationCredited;

	private const float ProbationTimeout = 60f;

	private TimeSince lastFamiliarityTick;

	private EntityRef<BasePlayer> trustedNearby;

	private float trustedNearbyDistanceSqr;

	private TimeSince trustedNearbySeen;

	private EntityRef<BasePlayer> curiousAbout;

	private const float CuriousMinFullness = 0.7f;

	private float lastDefendedAttack = float.NegativeInfinity;

	[Header("Old age")]
	[Tooltip("How far through its life an animal drops a whole gait, so it jogs where it used to run. A tier rather than a multiplier: the animations are authored at the gait speeds, so anything in between reads as sliding.")]
	[Range(0f, 1f)]
	public float OldAgeSlowFraction = 0.55f;

	[Tooltip("How far through its life it drops a second gait. Never goes below a walk.")]
	[Range(0f, 1f)]
	public float OldAgeVerySlowFraction = 0.8f;

	[Tooltip("How much of its roaming range an animal still has at the very end of its life, so an old one keeps close to home and mostly only moves to get back to it.")]
	[Range(0.05f, 1f)]
	public float OldAgeRangeFactor = 0.15f;

	[Tooltip("How much longer an old animal rests than one in its prime. 4 means a lie down at the end of its life lasts four times as long.")]
	[Range(1f, 10f)]
	public float OldAgeRestDurationScale = 4f;

	private RustNavMeshAgent frailtyAgent;

	private LivestockAnimalFSM frailtyFsm;

	private Vector3 homePosition;

	private const float AdoptionCheckInterval = 5f;

	private TimeSince lastAdoptionCheck;

	[Header("Hydration")]
	[Tooltip("How much hydration refills per second while the animal has its head in the water.")]
	public float HydrationFillRate = 0.25f;

	[Tooltip("Where a freshly spawned animal's hydration starts. Full, so a new animal has one thing less to go and sort out before it settles in.")]
	[Range(0f, 1f)]
	public float StartingHydration = 1f;

	[Tooltip("How much hydration drains per second while the animal is up and about in daylight. Tiny, because one mouthful refills the animal completely: this sets how often it goes for a drink rather than how long it stands there. At 0.00001 a full animal is down to the ConsumeThreshold of 0.95 after about 5000 seconds of daylight, which is one trip to the water in a shipped waking day.")]
	public float HydrationDecayRate = 1E-05f;

	[Tooltip("Shallowest water (in metres) that counts as somewhere to drink from. Above zero so a dry riverside tile with the water still 10m away doesn't qualify.")]
	public float MinStandingWaterDepth = 0.05f;

	[Tooltip("Deepest water (in metres) the animal will stand in to drink, so it drinks from the edge rather than wading out into the middle of the river.")]
	public float MaxStandingWaterDepth = 0.5f;

	[Tooltip("How much water a drink from completely dry takes out of a barrel. A drink takes its share of this for the hydration it puts back, so topping up often costs no more water than waiting until empty. 200 makes the daily top up from 0.95 cost 10.")]
	public int BarrelWaterPerDrink = 200;

	public const int MaxGeneration = 20;

	private const string EnglishSecond = "{0} Junior";

	private const string EnglishOrdinal = "{0} the {1}";

	private const string StemMark = "{0}";

	private static readonly HashSet<string> reportedLongNames = new HashSet<string>();

	private static readonly Dictionary<LivestockSpecies, int> populationBySpecies = new Dictionary<LivestockSpecies, int>();

	[Header("Sleep")]
	[Tooltip("How long (in seconds) after dusk this animal can take to settle, and after dawn to rise. Each animal rolls its own point in that window, so a herd beds down over a spread rather than dropping on one frame. Zero puts the whole herd on the same clock.")]
	public float SleepTimeVariance = 45f;

	[Tooltip("How far (in metres) from the middle of the herd is close enough to lie down. Further out than this and the animal walks in first, so the herd sleeps as a group.")]
	public float HerdGatherDistance = 6f;

	[Tooltip("How long (in seconds) the lie down animation runs before the animal is actually on the ground. Setting the resting flag only STARTS it, so for this long the animal is flagged lying down while still visibly on its feet. The cow's cow_lay_down is 5.2s.")]
	public float LieDownDuration = 5.2f;

	[Tooltip("The same for bedding down, which is the lie down plus the settle into sleep. The cow's cow_lay_down into cow_sleep_in is 8.2s.")]
	public float BedDownDuration = 8.2f;

	[Tooltip("How long (in seconds) the animator takes to stand up once the lying flag clears, after it has finished whatever was left of the lie down. The cow's cow_stand_up is 3s.")]
	public float StandUpDuration = 3f;

	[Tooltip("The same out of a sleep, which is the wake and then the stand up. The cow's cow_wake_up into cow_stand_up is 6.5s.")]
	public float WakeUpDuration = 6.5f;

	private float sleepFraction;

	[Header("PersonalSpace")]
	[Tooltip("How far (in metres) the animal looks for herd mates. Herd identity rather than personal space: the regroup, the flee centroid and the distress broadcast all use it.")]
	public float SocialSearchRadius = 20f;

	[Tooltip("How many herd mates of the same species within Social Search Radius the animal is content surrounded by. Past this the herd is too big to keep producing at full rate.")]
	public int ComfortableHerdSize = 8;

	[Tooltip("How many herd mates it takes for the herd load pressure to bottom out. Herd size alone never reaches the neglect band, so a big provisioned herd stops growing rather than dying.")]
	public int CrowdedHerdSize = 12;

	[Tooltip("How close (in metres) another animal has to stand to count as packed against this one. Measured flat, with a height filter, so a herd on the floor above is not in here.")]
	public float PackingRadius = 2.5f;

	[Tooltip("How far apart (in metres) two animals have to be vertically before neither is packed against the other. Under a storey, so a barn's two floors are separate spaces.")]
	public float PackingHeight = 2f;

	[Tooltip("How much room this animal takes up, in adult equivalents. A calf takes up less than a cow.")]
	public float AdultEquivalent = 1f;

	[Tooltip("How many adult equivalents can stand inside Packing Radius before it starts to bite.")]
	public float ComfortablePacking = 2f;

	[Tooltip("The packing at which the pressure has fallen as far as a merely oversized herd ever takes it.")]
	public float CrowdedPacking = 5f;

	[Tooltip("The packing at which the pressure bottoms out, which is deep enough to be neglect.")]
	public float SeverePacking = 9f;

	[FormerlySerializedAs("SocialChangeRate")]
	[FormerlySerializedAs("CrowdingChangeRate")]
	[Tooltip("How much personal space moves per second, towards whichever of the two pressures is worse. Zero disables the need.")]
	public float PersonalSpaceChangeRate = 0.01f;

	[Tooltip("How far a hurt animal's distress carries to the rest of its herd. Deliberately wider than SocialSearchRadius: that one is about who counts as company, this is about how far away a bull should still answer for one of his being shot. Only queried on a hit, so it can afford to be generous.")]
	public float DistressRadius = 40f;

	[Tooltip("How far a strayed animal looks for its herd when walking back to it. Must be wider than the FSM's strayDistance, or the search can never find anyone far enough away to count as strayed.")]
	public float RegroupSearchRadius = 40f;

	[Tooltip("How close (in metres) a player this animal already holds a grudge against has to stand to a calf of ours, and stay, before its mother treats it as a threat to the calf. Zero leaves only hurting it winding her up.")]
	public float CalfThreatDistance = 6f;

	[Tooltip("How close (in metres) a calf has to be to a mother who is standing her ground before it shelters behind her instead of bolting. Keep it under the FSM's strayDistance, or a calf can shelter from further away than it is willing to stand. Zero has calves always run.")]
	public float MotherShelterDistance = 12f;

	private bool playerLingeringOverCalf;

	private EntityRef<LivestockAnimal> lastShielded;

	private const int ShieldSlots = 3;

	private const float ShieldSlotLifetime = 5f;

	private EntityRef<LivestockAnimal>[] shieldSlotHolders;

	private TimeSince[] shieldSlotClaimed;

	private const float RestingSearchRadius = 30f;

	private const float RestingGap = 0.25f;

	private RustNavMeshAgent restingAgent;

	private const float AbreastSearchRadius = 10f;

	private const float InLineTolerance = 0.01f;

	private const float HerdFleeHeadingLifetime = 10f;

	private Vector3 herdFleeHeading;

	private TimeSince herdFleeHeadingSet;

	private bool hasHerdFleeHeading;

	public const float HerdLoadFloor = 0.3f;

	public const float PackingFloor = 0f;

	private float lastHerdLoadTarget = 1f;

	private float lastPackingTarget = 1f;

	private static readonly Dictionary<string, LivestockAnimal> livingSpecials = new Dictionary<string, LivestockAnimal>();

	private static readonly HashSet<string> discoveredSpecials = new HashSet<string>();

	private static LivestockSpecial queuedSpecial;

	private static bool[] eligibleSpecials;

	private float saleValueScale = 1f;

	public const Flags Leading = Flags.Reserved7;

	public const Flags Grazing = Flags.Reserved8;

	public const Flags Pregnant = Flags.Reserved10;

	public const Flags Sleeping = Flags.Reserved11;

	public const Flags Mating = Flags.Reserved12;

	public const Flags Drinking = Flags.Reserved13;

	public const Flags Resting = Flags.Reserved14;

	public const Flags Frisky = Flags.Reserved15;

	public const Flags Leadable = Flags.Reserved16;

	public const Flags LyingIn = Flags.Reserved17;

	public const int AgeTimerId = 1;

	public const int PregnancyTimerId = 2;

	public const int BreedCooldownTimerId = 3;

	public const int DungTimerId = 4;

	public AgeStage Age = AgeStage.Adult;

	public LivestockSize MountType = LivestockSize.Small;

	[Header("Aging")]
	[Tooltip("How long (in seconds) this animal stays at its current age stage. When it elapses an infant grows into the adult named by its Species asset and an adult dies of old age. Zero disables aging.")]
	public float TimeToGrow;

	[Tooltip("Random variance applied to TimeToGrow when the animal first spawns, as a fraction of it. 0.1 means +/-10%, so a herd doesn't all grow up in lockstep.")]
	[Range(0f, 1f)]
	public float GrowTimeVariance = 0.1f;

	[Tooltip("Optional effect played on the new animal when it grows into the next stage.")]
	public GameObjectRef GrowEffect;

	[Tooltip("How long (in seconds) this animal takes to give birth once pregnant")]
	public float PregnantDuration;

	[Tooltip("How long (in seconds) before the birth she stops what she is doing and lies down. The rest of the pregnancy she spends grazing like any other animal, so raising Pregnant Duration does not turn into a lie down of the same length.")]
	public float CalvingLieDownSeconds = 45f;

	[Tooltip("How far from the mother the newborn is placed, so it doesn't spawn inside her.")]
	public float ChildSpawnDistance = 1f;

	[Header("Sex")]
	[Tooltip("Which sex this prefab represents. Infants and species that use one prefab for both sexes stay at Either and roll for it; a prefab that is specifically the male or female form of its species says so here, and must be the one its Species asset names.")]
	public SexForm Form;

	[Tooltip("The species this animal belongs to. Shared by every prefab of the species - both sexes and every age stage - so it identifies mates and tells an infant which adult to grow into.")]
	public LivestockSpecies Species;

	[Header("Needs")]
	[Tooltip("How much fullness a second of grazing puts back. Deliberately slow: grazing is meant to be what the animal spends its day doing, not a top up it finishes in a few seconds.")]
	public float FullnessFillRate = 0.004f;

	[Tooltip("How much fullness drains per second while the animal is up and about in daylight. Against FullnessFillRate this sets how much of its standing time a well fed animal spends eating: 0.001 against 0.004 is a quarter of it. Slow, because the dip below ConsumeThreshold while the animal walks over is what a keeper sees. Lying down and sleeping drain at SleepingDecayScale.")]
	public float FullnessDecayRate = 0.001f;

	[Tooltip("Where a freshly spawned animal's fullness starts. At the trough ceiling, so a new animal in a well kept pen reads right straight away instead of spending minutes climbing to it.")]
	[Range(0f, 1f)]
	public float StartingFullness = 0.8f;

	[Tooltip("Scales the drain while the animal is asleep or the sun is down. Feeding is daytime work, so a full night at the waking rate would be a debt the day could not pay off.")]
	[Range(0f, 1f)]
	public float SleepingDecayScale = 0.25f;

	public float NeedTickFrequency = 5f;

	[Tooltip("How far (in metres) the animal looks for somewhere to feed itself - a trough, a water barrel, open grass or a river bank.")]
	public float ConsumeSearchRadius = 25f;

	[Tooltip("A need at or below this share of the most it can get sends the animal off to top it up. Fullness is measured against the trough ceiling while a trough is what feeds it, and against full otherwise. High on purpose: a keeper reads the bar as whether the pen is set up right, so a well kept animal has to sit near the top of it: about 0.75 to 0.8 on troughs and 0.95 to 1 on grass. The gap below the top sets how often it goes to eat, not how much of its day it spends eating.")]
	[Range(0f, 1f)]
	public float ConsumeThreshold = 0.95f;

	[Tooltip("The most fullness a trough will ever put back. Grass still fills an animal completely, so a herd living out of a trough sits permanently a little short and everything reading Condition pays for it.")]
	[Range(0f, 1f)]
	public float TroughFullnessCeiling = 0.8f;

	[Tooltip("How much fullness each calorie of trough food is worth. The animal grazes a trough item down before it takes the next, so richer food lasts longer, the way it does for a horse.")]
	public float TroughFullnessPerCalorie = 0.0021f;

	[Tooltip("How long (in seconds) the animal takes to raise its head once it stops grazing or drinking. Clearing the flag only STARTS the head coming up, so for this long it is still visibly head down. The cow's cow_eat_out is 1.7s and the sheep's sheep_eat_exit 1.5s.")]
	public float RaiseHeadDuration = 1.7f;

	public const float StartingPersonalSpaceNeed = 1f;

	private TimeSince poseHeldFor;

	private TimeUntil onItsFeetIn;

	private TimeSince headComingUpFor;

	private TickableNeed[] needs = Array.Empty<TickableNeed>();

	private TimeSince lastLoudNoise;

	private TimeSince lastNeedsTick;

	protected PersistentTimerSet timers;

	private PersistentTimer pregnancyTimer;

	private PersistentTimer breedCooldown;

	private EntityRef<LivestockAnimal> Father;

	private EntityRef<LivestockAnimal> Mother;

	private bool findingItsFeet;

	private EntityRef<LivestockAnimal> newbornCalf;

	public static LivestockNeedsQueue NeedsQueue = new LivestockNeedsQueue();

	private bool startled;

	private EntityRef<BasePlayer> leaderBeforeMount;

	private const float LeadTautDistance = 3.5f;

	private const float LeadFullSlowDistance = 7f;

	private const float LeadMaxSlow = 0.9f;

	private static readonly List<ModifierDefintion> leadModifier = new List<ModifierDefintion>
	{
		new ModifierDefintion
		{
			type = Modifier.ModifierType.MoveSpeed,
			source = Modifier.ModifierSource.Interaction,
			value = 0f,
			duration = 0f
		}
	};

	public const int MaxAnimalNameLength = 24;

	private Vector3 lastLoudNoisePosition;

	private EntityRef<BaseCombatEntity> aggressor;

	private TimeSince lastAggression;

	private float penaltyCharged;

	private TimeSince lastHerdDistress;

	private EntityRef<BaseCombatEntity> herdDistressAttacker;

	private TimeSince lastCalfThreat;

	private bool calfEverThreatened;

	private const float NeedFlashPrecision = 0.01f;

	private const float AgeFlashPrecision = 0.01f;

	private const float FamiliarityFlashPrecision = 1f;

	private FlashedValue[] flashedValues;

	private HitchTrough grazingTrough;

	private float troughFullnessStored;

	private bool hasCalved;

	private const int BirthCandidates = 8;

	private bool lastHelpingFromTrough;

	private bool __sync_IsMale;

	private EntityRef<LivestockAnimal> __sync_PregnantPartner;

	private string __sync_AnimalName;

	private EntityRef<BasePlayer> __sync_LeadingPlayer;

	private EntityRef<BuildingPrivlidge> __sync_HomeTc;

	private int __sync_Genes;

	private int __sync_PregnantPartnerGenes;

	public bool AgingEnabled
	{
		get
		{
			if (TimeToGrow > 0f)
			{
				return Livestock.ageScale > 0f;
			}
			return false;
		}
	}

	public bool IsAging
	{
		get
		{
			if (AgingEnabled)
			{
				return DecayStarted;
			}
			return false;
		}
	}

	private float AgeLifetime => TimeToGrow * LifespanScale * Livestock.ageScale;

	private float LifespanScale
	{
		get
		{
			if (!IsAdult())
			{
				return 1f;
			}
			return GeneScale(LivestockGene.Longevity);
		}
	}

	public float AgeFraction
	{
		get
		{
			if (!AgingEnabled || !IsAdult() || !ageTimer.IsRunning || !(AgeLifetime > 0f))
			{
				return 0f;
			}
			return Mathf.Clamp01(1f - ageTimer.Remaining / AgeLifetime);
		}
	}

	public float AgeSeconds
	{
		get
		{
			if (!AgingEnabled || !ageTimer.IsRunning)
			{
				return 0f;
			}
			return Mathf.Max(0f, AgeLifetime - ageTimer.Remaining);
		}
	}

	private GameObjectRef AdultForm
	{
		get
		{
			if (!(Species != null))
			{
				return null;
			}
			return Species.AdultFor(IsMale);
		}
	}

	private bool HasAdultForm => AdultForm?.isValid ?? false;

	public LivestockPattern PatternSet
	{
		get
		{
			if (!(pattern != null))
			{
				return pattern = GetComponent<LivestockPattern>();
			}
			return pattern;
		}
	}

	public virtual int ShornFleece => 0;

	public LivestockSexParts SexPartSet
	{
		get
		{
			if (!(sexParts != null))
			{
				return sexParts = GetComponent<LivestockSexParts>();
			}
			return sexParts;
		}
	}

	public string CoatName
	{
		get
		{
			if (PatternSet == null)
			{
				return "none";
			}
			return ((PatternSet.Range != null) ? PatternSet.Range.ShadeNameOf(Genes) : null) ?? $"pattern {LivestockPattern.SeedFor(AnimalName):X8}";
		}
	}

	public float Condition
	{
		get
		{
			float num = MaxHealth();
			float num2 = Health() + conditionUnsentHealth;
			if (!(num > 0f))
			{
				return 0f;
			}
			return Mathf.Clamp01(num2 / num);
		}
	}

	public bool BreedingPaused => breedingPaused;

	public bool DungEnabled
	{
		get
		{
			if (DungItem != null)
			{
				return DungInterval > 0f;
			}
			return false;
		}
	}

	public bool CanDung
	{
		get
		{
			if (IsLeadable && !IsStarving())
			{
				return Condition >= Livestock.dungConditionFloor;
			}
			return false;
		}
	}

	public bool IsCurious { get; private set; }

	public bool ActsCurious
	{
		get
		{
			if (IsCurious)
			{
				return IsDependent;
			}
			return false;
		}
	}

	public bool IsLeadable => AnyAcquaintanceAt(Livestock.trustToLead);

	public bool IsFullyBonded => AnyAcquaintanceAt(Livestock.maxTrust);

	public bool DecayStarted { get; private set; }

	private static float TrustedNearbyTimeout => SenseComponent.maxRefreshIntervalSeconds * 2f;

	public BasePlayer NearbyTrustedPlayer
	{
		get
		{
			if (!((float)trustedNearbySeen > TrustedNearbyTimeout))
			{
				return trustedNearby.Get(base.isServer);
			}
			return null;
		}
	}

	public BasePlayer CuriousAbout
	{
		get
		{
			BasePlayer basePlayer = curiousAbout.Get(base.isServer);
			if (!WouldFollowOutOfCuriosity(basePlayer))
			{
				return null;
			}
			return basePlayer;
		}
	}

	public int AgeGaitPenalty
	{
		get
		{
			if (IsInfant())
			{
				return -1;
			}
			float ageFraction = AgeFraction;
			if (ageFraction >= Mathf.Max(OldAgeSlowFraction, OldAgeVerySlowFraction))
			{
				return 2;
			}
			if (!(ageFraction >= OldAgeSlowFraction))
			{
				return 0;
			}
			return 1;
		}
	}

	public float AgeRestDurationScale => Mathf.Lerp(1f, Mathf.Max(1f, OldAgeRestDurationScale), AgeFraction);

	public bool IsInbred => LivestockGenome.IsInbred(Genes);

	public float GeneQuality => LivestockGenome.ExpressedQuality(Genes);

	public bool IsGodClone => LivestockGenome.IsGodGenome(Genes);

	public Vector3 HomePosition
	{
		get
		{
			if (!HomeTc.TryGet(base.isServer, out var entity) || !(entity != null))
			{
				return homePosition;
			}
			return entity.transform.position;
		}
	}

	public int Generation { get; set; }

	public string NameLocale { get; set; }

	public string LineStem { get; set; }

	public bool HeirNamed { get; set; }

	public bool SpeciesAtCap
	{
		get
		{
			int maxPerSpecies = Livestock.maxPerSpecies;
			if (maxPerSpecies > 0)
			{
				return PopulationOf(Species) >= maxPerSpecies;
			}
			return false;
		}
	}

	public float SleepOffset => sleepFraction * Mathf.Max(0f, SleepTimeVariance);

	public bool IsDependent
	{
		get
		{
			if (IsMale)
			{
				return !IsAdult();
			}
			return true;
		}
	}

	private RustNavMeshAgent RestingAgent
	{
		get
		{
			if (!(restingAgent != null))
			{
				return restingAgent = GetComponent<RustNavMeshAgent>();
			}
			return restingAgent;
		}
	}

	public float LastHerdLoadTarget => lastHerdLoadTarget;

	public float LastPackingTarget => lastPackingTarget;

	public float SaleValueScale
	{
		get
		{
			if (base.isServer)
			{
				return SpeciesSaleScale() * GeneSaleScale() * ConditionSaleScale() * AgeSaleScale() * OriginSaleScale();
			}
			return saleValueScale;
		}
	}

	public float SaleQuality => Mathf.Clamp01(SaleValueScale / Mathf.Max(0.01f, Livestock.priceFullQualityScale));

	public bool IsBred => Mother.IsSet;

	[Sync(Autosave = true)]
	public bool IsMale
	{
		[CompilerGenerated]
		get
		{
			return __sync_IsMale;
		}
		[CompilerGenerated]
		set
		{
			if (!IsSyncVarEqual(__sync_IsMale, value))
			{
				__sync_IsMale = value;
				byte nameID = __GetWeaverID("IsMale");
				QueueSyncVar(nameID);
			}
		}
	}

	[Sync(Autosave = true)]
	public EntityRef<LivestockAnimal> PregnantPartner
	{
		[CompilerGenerated]
		get
		{
			return __sync_PregnantPartner;
		}
		[CompilerGenerated]
		set
		{
			if (!IsSyncVarEqual(__sync_PregnantPartner, value))
			{
				__sync_PregnantPartner = value;
				byte nameID = __GetWeaverID("PregnantPartner");
				QueueSyncVar(nameID);
			}
		}
	}

	[Sync(Autosave = true)]
	public string AnimalName
	{
		[CompilerGenerated]
		get
		{
			return __sync_AnimalName;
		}
		[CompilerGenerated]
		set
		{
			if (!IsSyncVarEqual(__sync_AnimalName, value))
			{
				__sync_AnimalName = value;
				byte nameID = __GetWeaverID("AnimalName");
				QueueSyncVar(nameID);
			}
		}
	}

	[Sync(Autosave = true)]
	public EntityRef<BasePlayer> LeadingPlayer
	{
		[CompilerGenerated]
		get
		{
			return __sync_LeadingPlayer;
		}
		[CompilerGenerated]
		set
		{
			if (!IsSyncVarEqual(__sync_LeadingPlayer, value))
			{
				__sync_LeadingPlayer = value;
				byte nameID = __GetWeaverID("LeadingPlayer");
				QueueSyncVar(nameID);
			}
		}
	}

	[Sync(Autosave = true)]
	public EntityRef<BuildingPrivlidge> HomeTc
	{
		[CompilerGenerated]
		get
		{
			return __sync_HomeTc;
		}
		[CompilerGenerated]
		set
		{
			if (!IsSyncVarEqual(__sync_HomeTc, value))
			{
				__sync_HomeTc = value;
				byte nameID = __GetWeaverID("HomeTc");
				QueueSyncVar(nameID);
			}
		}
	}

	[Sync(Autosave = true)]
	public int Genes
	{
		[CompilerGenerated]
		get
		{
			return __sync_Genes;
		}
		[CompilerGenerated]
		set
		{
			if (!IsSyncVarEqual(__sync_Genes, value))
			{
				__sync_Genes = value;
				byte nameID = __GetWeaverID("Genes");
				QueueSyncVar(nameID);
			}
		}
	}

	[Sync(Autosave = true)]
	public int PregnantPartnerGenes
	{
		[CompilerGenerated]
		get
		{
			return __sync_PregnantPartnerGenes;
		}
		[CompilerGenerated]
		set
		{
			if (!IsSyncVarEqual(__sync_PregnantPartnerGenes, value))
			{
				__sync_PregnantPartnerGenes = value;
				byte nameID = __GetWeaverID("PregnantPartnerGenes");
				QueueSyncVar(nameID);
			}
		}
	}

	public bool IsFemale => !IsMale;

	public TickableNeed Fullness => GetNeed(Need.Fullness);

	public TickableNeed PersonalSpace => GetNeed(Need.PersonalSpace);

	public TickableNeed Hydration => GetNeed(Need.Hydration);

	public bool IsHappy
	{
		get
		{
			if (Fullness.NeedValue > 0.5f && PersonalSpace.NeedValue > 0.5f)
			{
				return Hydration.NeedValue > 0.5f;
			}
			return false;
		}
	}

	public float Contentment => Mathf.Min(Fullness.NeedValue, Mathf.Min(PersonalSpace.NeedValue, Hydration.NeedValue));

	public bool RecentlyHeardLoudNoise => (float)lastLoudNoise < 10f;

	public bool IsVeryHappy => Contentment >= Livestock.friskyThreshold;

	public bool IsFindingItsFeet => findingItsFeet;

	public LivestockAnimal MotherAnimal => Mother.Get(base.isServer);

	public bool IsBeingPurchased { get; set; }

	public bool StandsItsGround
	{
		get
		{
			if (IsAdult())
			{
				if (!IsMale)
				{
					return IsProtectiveOfCalf;
				}
				return true;
			}
			return false;
		}
	}

	public bool HerdMateRecentlyHurt => (float)lastHerdDistress < 10f;

	public bool IsProtectiveOfCalf
	{
		get
		{
			float motherlyDuration = Livestock.motherlyDuration;
			if (motherlyDuration > 0f && calfEverThreatened && (float)lastCalfThreat < motherlyDuration)
			{
				return HasDependentsNearby();
			}
			return false;
		}
	}

	public bool WantsToBeAlone
	{
		get
		{
			if (!IsPregnant())
			{
				if (TryGetComponent<LivestockAnimalFSM>(out var component))
				{
					return component.WantsToBeAlone;
				}
				return false;
			}
			return true;
		}
	}

	public bool IsHoldingPose => IsLyingDown();

	public bool IsGrazingFromTrough => grazingTrough != null;

	public bool HasTroughFoodLeft
	{
		get
		{
			Item foundItem;
			if (!(troughFullnessStored > 0f))
			{
				return CanEatFrom(grazingTrough, out foundItem);
			}
			return true;
		}
	}

	public bool OnBreedingCooldown => breedCooldown.IsRunning;

	private bool MayBreed
	{
		get
		{
			if (IsFullyBonded && !BreedingPaused && !SpeciesAtCap)
			{
				BuildingPrivlidge cupboard;
				if (Livestock.breedOnlyAtHome)
				{
					return TryGetHomeCupboard(out cupboard);
				}
				return true;
			}
			return false;
		}
	}

	public bool HasCalved => hasCalved;

	public bool IsAboutToCalve
	{
		get
		{
			if (pregnancyTimer.IsRunning)
			{
				return pregnancyTimer.Remaining <= CalvingLieDownSeconds;
			}
			return false;
		}
	}

	public bool IsCarrying => pregnancyTimer.IsRunning;

	public bool IsCalving
	{
		get
		{
			if (HasFlag(Flags.Reserved10))
			{
				if (!IsAboutToCalve)
				{
					return !IsCarrying;
				}
				return true;
			}
			return false;
		}
	}

	public float ExpectedFullnessCeiling
	{
		get
		{
			if (!lastHelpingFromTrough)
			{
				return 1f;
			}
			return Mathf.Clamp01(TroughFullnessCeiling);
		}
	}

	public override bool OnRpcMessage(BasePlayer player, uint rpc, Message msg)
	{
		using (TimeWarning.New("LivestockAnimal.OnRpcMessage"))
		{
			if (rpc == 3418655327u && player != null)
			{
				Assert.IsTrue(player.isServer, "SV_RPC Message is using a clientside player!");
				if (ConVar.Global.developer > 2)
				{
					Debug.Log("SV_RPCMessage: " + player?.ToString() + " - RequestAnimalStats");
				}
				using (TimeWarning.New("RequestAnimalStats"))
				{
					using (TimeWarning.New("Conditions"))
					{
						if (!RPC_Server.CallsPerSecond.Test(3418655327u, "RequestAnimalStats", this, player, 2uL))
						{
							return true;
						}
						if (!RPC_Server.IsVisible.Test(3418655327u, "RequestAnimalStats", this, player, 3f))
						{
							return true;
						}
					}
					try
					{
						using (TimeWarning.New("Call"))
						{
							RPCMessage rPCMessage = default(RPCMessage);
							rPCMessage.connection = msg.connection;
							rPCMessage.player = player;
							rPCMessage.read = msg.read;
							RPCMessage msg2 = rPCMessage;
							RequestAnimalStats(msg2);
						}
					}
					catch (Exception exception)
					{
						Debug.LogException(exception);
						player.Kick("RPC Error in RequestAnimalStats");
					}
				}
				return true;
			}
			if (rpc == 3653170552u && player != null)
			{
				Assert.IsTrue(player.isServer, "SV_RPC Message is using a clientside player!");
				if (ConVar.Global.developer > 2)
				{
					Debug.Log("SV_RPCMessage: " + player?.ToString() + " - RPC_Lead");
				}
				using (TimeWarning.New("RPC_Lead"))
				{
					using (TimeWarning.New("Conditions"))
					{
						if (!RPC_Server.CallsPerSecond.Test(3653170552u, "RPC_Lead", this, player, 5uL))
						{
							return true;
						}
						if (!RPC_Server.MaxDistance.Test(3653170552u, "RPC_Lead", this, player, 3f))
						{
							return true;
						}
					}
					try
					{
						using (TimeWarning.New("Call"))
						{
							RPCMessage rPCMessage = default(RPCMessage);
							rPCMessage.connection = msg.connection;
							rPCMessage.player = player;
							rPCMessage.read = msg.read;
							RPCMessage msg3 = rPCMessage;
							RPC_Lead(msg3);
						}
					}
					catch (Exception exception2)
					{
						Debug.LogException(exception2);
						player.Kick("RPC Error in RPC_Lead");
					}
				}
				return true;
			}
		}
		return base.OnRpcMessage(player, rpc, msg);
	}

	private void InitAging()
	{
		ageTimer = new PersistentTimer(timers, 1)
		{
			onElapsed = OnAgeTimerElapsed,
			onStarted = OnAgeTimerStarted,
			carryOnReplace = false
		};
		if (IsAging)
		{
			StartAgeTimer();
		}
	}

	private void StartAgeTimerIfIdle()
	{
		if (IsAging && !ageTimer.IsRunning)
		{
			StartAgeTimer();
		}
	}

	private void StartAgeTimer()
	{
		ageTimer.Start(TimeToGrow * LifespanScale * UnityEngine.Random.Range(1f - GrowTimeVariance, 1f + GrowTimeVariance) * Livestock.ageScale);
	}

	private void RestartLifespanForGenes()
	{
		if (IsAging && IsAdult())
		{
			StartAgeTimer();
		}
	}

	private void OnAgeTimerStarted()
	{
		DecayStarted = true;
	}

	private void OnAgeTimerElapsed()
	{
		DecayStarted = true;
		if (!IsDead() && AgingEnabled && !AdvanceAgeStage() && !HasAdultForm)
		{
			Debug.LogWarning(Categorize() + " (" + base.PrefabName + ") is an Infant whose Species asset names no adult form; it will not age.");
		}
	}

	public bool ForceAgeUp()
	{
		if (IsDead())
		{
			return false;
		}
		ageTimer.Stop();
		return AdvanceAgeStage();
	}

	private bool AdvanceAgeStage()
	{
		if (IsInfant())
		{
			if (HasAdultForm)
			{
				return GrowIntoAdult() != null;
			}
			return false;
		}
		DieOfOldAge();
		return true;
	}

	public LivestockAnimal GrowIntoAdult()
	{
		GameObjectRef adultForm = AdultForm;
		if (adultForm == null || !adultForm.isValid)
		{
			return null;
		}
		return ReplaceWith(adultForm.resourcePath, GrowEffect);
	}

	public LivestockAnimal ReplaceWith(string prefabPath, GameObjectRef effect = null)
	{
		LivestockAnimal livestockAnimal = GameManager.server.CreateEntity(prefabPath, base.transform.position, base.transform.rotation) as LivestockAnimal;
		if (livestockAnimal == null)
		{
			Debug.LogError(Categorize() + " '" + prefabPath + "' is not a LivestockAnimal; aborting replacement.");
			return null;
		}
		if (HasParent())
		{
			livestockAnimal.SetParent(GetParentEntity(), worldPositionStays: true);
		}
		livestockAnimal.OwnerID = base.OwnerID;
		livestockAnimal.skinID = skinID;
		livestockAnimal.Spawn();
		TransferStateTo(livestockAnimal);
		livestockAnimal.SendNetworkUpdateImmediate();
		if (effect != null && effect.isValid)
		{
			Effect.server.Run(effect.resourcePath, livestockAnimal.transform.position);
		}
		Kill();
		return livestockAnimal;
	}

	protected virtual void TransferStateTo(LivestockAnimal next)
	{
		next.health = next.MaxHealth() * base.healthFraction;
		if (next.Form == SexForm.Either)
		{
			next.IsMale = IsMale;
		}
		next.SetLeading(IsLeading());
		next.LeadingPlayer = LeadingPlayer;
		next.leaderBeforeMount = leaderBeforeMount;
		leaderBeforeMount = default(EntityRef<BasePlayer>);
		HandOverMountTo(next);
		timers.CopyTo(next.timers);
		next.PregnantPartner = PregnantPartner;
		next.sleepFraction = sleepFraction;
		next.AnimalName = AnimalName;
		next.TakeLineageFrom(this);
		next.HeirNamed = HeirNamed;
		next.Mother = Mother;
		next.Father = Father;
		next.DecayStarted = DecayStarted;
		TransferFamiliarityTo(next);
		next.Genes = Genes;
		next.RestartLifespanForGenes();
		next.RestartDungForGenes();
		for (int i = 0; i < needs.Length && i < next.needs.Length; i++)
		{
			next.needs[i] = needs[i];
		}
		TransferConditionTo(next);
		next.TakeHomeFrom(this);
	}

	private void DieOfOldAge()
	{
		LogEntry(RustLog.EntryType.Combat, 2, "died of old age");
		Die();
	}

	private void InitCondition()
	{
		if (!Application.isLoadingSave)
		{
			breedingPaused = false;
		}
	}

	private float ConditionTarget()
	{
		float b = Mathf.Max(0.01f, Livestock.conditionFullContentment / GeneScale(LivestockGene.Hardiness));
		float num = Mathf.InverseLerp(0f, b, Contentment);
		if (!DecayStarted)
		{
			num = Mathf.Max(num, Mathf.Clamp01(Livestock.wildConditionFloor));
		}
		return num;
	}

	private void TickCondition(float deltaTime)
	{
		if (deltaTime <= 0f || IsDead())
		{
			return;
		}
		UpdateBreedingPause();
		if (TOD_Sky.Instance.IsNight || IsSleeping())
		{
			conditionSecondsSkipped += deltaTime;
			return;
		}
		float num = MaxHealth();
		if (num <= 0f)
		{
			conditionSecondsSkipped += deltaTime;
			return;
		}
		float num2 = ConditionTarget() * num;
		float num3 = Health() + conditionUnsentHealth;
		if (Mathf.Approximately(num2, num3))
		{
			conditionSecondsSkipped += deltaTime;
			return;
		}
		float num4 = ((num2 < num3) ? Livestock.conditionFallSeconds : Livestock.conditionRiseSeconds);
		if (num4 <= 0f)
		{
			conditionSecondsSkipped += deltaTime;
			return;
		}
		conditionSecondsSpent += deltaTime;
		num3 = Mathf.MoveTowards(num3, num2, num / num4 * deltaTime);
		conditionUnsentHealth = num3 - Health();
		if (!(Mathf.Abs(conditionUnsentHealth) < num * 0.05f) || !(num3 > 0f) || !(num3 < num))
		{
			conditionUnsentHealth = 0f;
			SetHealth(num3);
			if (Health() <= 0f)
			{
				DieOfNeglect();
			}
		}
	}

	private void DieOfNeglect()
	{
		HitInfo hitInfo = new HitInfo();
		hitInfo.damageTypes.Set(DamageType.Hunger, MaxHealth());
		Die(hitInfo);
	}

	private void UpdateBreedingPause()
	{
		float condition = Condition;
		if (condition <= Livestock.breedPauseCondition)
		{
			breedingPaused = true;
		}
		else if (condition >= Livestock.breedResumeCondition)
		{
			breedingPaused = false;
		}
	}

	public void SetCondition(float value)
	{
		conditionUnsentHealth = 0f;
		SetHealth(Mathf.Clamp01(value) * MaxHealth());
		UpdateBreedingPause();
	}

	private void SaveCondition(ProtoBuf.LivestockAnimal msg)
	{
		msg.breedingPaused = breedingPaused;
	}

	private void LoadCondition(ProtoBuf.LivestockAnimal msg)
	{
		breedingPaused = msg.breedingPaused;
	}

	private void TransferConditionTo(LivestockAnimal next)
	{
		next.breedingPaused = breedingPaused;
	}

	public string GetConditionDebugInfo()
	{
		return string.Format("cond {0:0.00} -> {1:0.00}{2}", Condition, ConditionTarget(), breedingPaused ? " paused" : string.Empty) + $" ({conditionSecondsSpent:0}s spent, {conditionSecondsSkipped:0}s skipped)";
	}

	private void InitDayBudget()
	{
		activityWeights = new float[Enum.GetValues(typeof(DayActivity)).Length];
	}

	public void TickDayBudget(DayActivity activity, float deltaTime)
	{
		if (activityWeights != null && !(deltaTime <= 0f) && !TOD_Sky.Instance.IsNight && !IsSleeping())
		{
			float num = Mathf.Max(1f, DayBudgetWindow);
			float num2 = Mathf.Exp((0f - deltaTime) / num);
			for (int i = 0; i < activityWeights.Length; i++)
			{
				activityWeights[i] *= num2;
			}
			activityWeights[(int)activity] += deltaTime;
		}
	}

	public float ShareOfDay(DayActivity activity)
	{
		if (activityWeights == null)
		{
			return 0f;
		}
		float num = 0f;
		for (int i = 0; i < activityWeights.Length; i++)
		{
			num += activityWeights[i];
		}
		if (!(num > 0f))
		{
			return 0f;
		}
		return activityWeights[(int)activity] / num;
	}

	public bool IsStarving()
	{
		return Fullness.NeedValue <= StarvingThreshold;
	}

	public string GetDayBudgetDebugInfo()
	{
		return $"graze {ShareOfDay(DayActivity.Grazing):P0}" + $" rest {ShareOfDay(DayActivity.Resting):P0}" + $" idle {ShareOfDay(DayActivity.Idling):P0}";
	}

	private void InitDung()
	{
		dungTimer = new PersistentTimer(timers, 4)
		{
			onElapsed = DropDung
		};
		if (DungEnabled)
		{
			StartDungTimer();
		}
	}

	public void RestartDungForGenes()
	{
		if (DungEnabled)
		{
			StartDungTimer();
		}
	}

	private void StartDungTimer()
	{
		dungTimer.Start(DungInterval / GeneScale(LivestockGene.Dung) * DungIntervalConditionScale() * UnityEngine.Random.Range(1f - DungIntervalVariance, 1f + DungIntervalVariance));
	}

	private float DungIntervalConditionScale()
	{
		float dungConditionFloor = Livestock.dungConditionFloor;
		float b = Mathf.Max(dungConditionFloor + 0.01f, Livestock.dungConditionFull);
		return Mathf.Lerp(Mathf.Max(1f, Livestock.dungSlowestScale), 1f, Mathf.InverseLerp(dungConditionFloor, b, Condition));
	}

	private void DropDung()
	{
		if (!IsDead())
		{
			if (CanDung)
			{
				SpawnDung();
			}
			StartDungTimer();
		}
	}

	public bool TryDropDung()
	{
		if (IsDead() || !DungEnabled || !CanDung)
		{
			return false;
		}
		SpawnDung();
		StartDungTimer();
		return true;
	}

	public float NextDungInterval()
	{
		return DungInterval / GeneScale(LivestockGene.Dung) * DungIntervalConditionScale();
	}

	public float DungBestOfBreed()
	{
		float num = DungInterval / Mathf.Max(0.01f, Livestock.geneDungGood);
		float num2 = NextDungInterval();
		if (!(num2 > 0f))
		{
			return 0f;
		}
		return Mathf.Clamp01(num / num2);
	}

	public void SpawnDung()
	{
		if (!(DungItem == null))
		{
			Item item = ItemManager.Create(DungItem, 1, 0uL, isServerSide: true, 0uL);
			if (item != null)
			{
				item.SetItemOwnership(string.IsNullOrEmpty(AnimalName) ? Categorize() : AnimalName, ItemOwnershipPhrases.Pooped);
				Vector3 vector = ((DungSpawnPoint != null) ? DungSpawnPoint.position : base.transform.position);
				item.Drop(vVelocity: new Vector3(UnityEngine.Random.Range(-0.5f, 0.5f), UnityEngine.Random.Range(-3f, -1f), UnityEngine.Random.Range(-0.5f, 0.5f)), vPos: vector + UnityEngine.Random.insideUnitSphere * 0.1f, rotation: UnityEngine.Random.rotationUniform);
				LivestockCensus.RecordDung(vector);
			}
		}
	}

	private void InitFamiliarity()
	{
		probationCredited = float.MaxValue;
		trustedNearbySeen = float.MaxValue;
		lastFamiliarityTick = 0f;
		if (!Application.isLoadingSave)
		{
			IsCurious = UnityEngine.Random.value < Livestock.curiousChance;
		}
		ValidateFamiliarityTuning();
	}

	public void SetCurious(bool curious)
	{
		IsCurious = curious;
	}

	private void ValidateFamiliarityTuning()
	{
	}

	private bool AnyAcquaintanceAt(float seconds)
	{
		if (acquaintances == null)
		{
			return false;
		}
		for (int i = 0; i < acquaintances.Length; i++)
		{
			if (acquaintances[i].userId != 0L && acquaintances[i].seconds >= seconds)
			{
				return true;
			}
		}
		return false;
	}

	public bool TrustsToHandle(BasePlayer player)
	{
		if (player != null)
		{
			return TrustOf(player) >= Livestock.trustToLead;
		}
		return false;
	}

	private void RefreshHusbandryClocks()
	{
		bool isLeadable = IsLeadable;
		SetNetworkedFlag(Flags.Reserved16, isLeadable);
		if (isLeadable)
		{
			DecayStarted = true;
			StartAgeTimerIfIdle();
		}
	}

	public float TrustOf(BasePlayer player)
	{
		if (player == null)
		{
			return 0f;
		}
		if (IsSoreAt(player))
		{
			return 0f;
		}
		return Mathf.Max(StoredSecondsFor(player.userID), PartialCreditFor(player));
	}

	public bool IsSoreAt(BaseEntity entity)
	{
		if (entity == null)
		{
			return false;
		}
		if (TryGetRememberedAggressor(out var attacker) && attacker == entity)
		{
			return true;
		}
		if (TryGetHerdDistressAttacker(out var attacker2))
		{
			return attacker2 == entity;
		}
		return false;
	}

	private float PartialCreditFor(BasePlayer player)
	{
		if (player == null)
		{
			return 0f;
		}
		return Mathf.Max(HousemateCreditFor(player), HandlingCreditFor(player));
	}

	private float HandlingCreditFor(BasePlayer player)
	{
		float calmedTrustFloor = Livestock.calmedTrustFloor;
		if (calmedTrustFloor <= 0f || player.modifiers == null)
		{
			return 0f;
		}
		if (!(player.modifiers.GetValue(Modifier.ModifierType.LivestockHandling) > 0f))
		{
			return 0f;
		}
		return calmedTrustFloor;
	}

	private float HousemateCreditFor(BasePlayer player)
	{
		float trustFloor = Livestock.trustFloor;
		if (trustFloor <= 0f || acquaintances == null || player == null)
		{
			return 0f;
		}
		float trustToLead = Livestock.trustToLead;
		if (TryGetHomeCupboard(out var cupboard))
		{
			if (cupboard.IsAuthed(player.userID))
			{
				return trustFloor;
			}
			if (cupboard.recentGroupMembers.ContainsKey(player.userID))
			{
				return trustFloor;
			}
		}
		RelationshipManager serverInstance = RelationshipManager.ServerInstance;
		if (serverInstance == null || player.currentTeam == 0L)
		{
			return 0f;
		}
		for (int i = 0; i < acquaintances.Length; i++)
		{
			if (acquaintances[i].userId != 0L && !(acquaintances[i].seconds < trustToLead))
			{
				RelationshipManager.PlayerTeam playerTeam = serverInstance.FindPlayersTeam(acquaintances[i].userId);
				if (playerTeam != null && playerTeam.teamID == player.currentTeam)
				{
					return trustFloor;
				}
			}
		}
		return 0f;
	}

	public float StoredSecondsFor(ulong userId)
	{
		if (acquaintances == null || userId == 0L)
		{
			return 0f;
		}
		for (int i = 0; i < acquaintances.Length; i++)
		{
			if (acquaintances[i].userId == userId)
			{
				return acquaintances[i].seconds;
			}
		}
		return 0f;
	}

	public void AddFamiliarity(ulong userId, float seconds, FamiliarityReason reason = FamiliarityReason.Handled)
	{
		bool isLeadable = IsLeadable;
		CreditFamiliarity(userId, seconds);
		RefreshHusbandryClocks();
		if (!isLeadable && IsLeadable && (reason == FamiliarityReason.Handled || reason == FamiliarityReason.Purchased))
		{
			Analytics.Azure.OnLivestockAcquired(userId, this, reason);
		}
	}

	private void CreditFamiliarity(ulong userId, float seconds)
	{
		if (userId == 0L || Mathf.Approximately(seconds, 0f))
		{
			return;
		}
		float maxTrust = Livestock.maxTrust;
		if (acquaintances == null)
		{
			acquaintances = new Acquaintance[8];
		}
		int num = -1;
		int num2 = 0;
		for (int i = 0; i < acquaintances.Length; i++)
		{
			if (acquaintances[i].userId == userId)
			{
				acquaintances[i].seconds = Mathf.Clamp(acquaintances[i].seconds + seconds, 0f, maxTrust);
				if (seconds > 0f)
				{
					acquaintances[i].sinceCredited = 0f;
				}
				return;
			}
			if (acquaintances[i].userId == 0L)
			{
				if (num < 0)
				{
					num = i;
				}
			}
			else if (acquaintances[i].seconds < acquaintances[num2].seconds)
			{
				num2 = i;
			}
		}
		if (seconds < 0f)
		{
			return;
		}
		if (num >= 0)
		{
			acquaintances[num] = new Acquaintance
			{
				userId = userId,
				seconds = Mathf.Min(seconds, maxTrust),
				sinceCredited = 0f
			};
			return;
		}
		if (probation.userId != userId)
		{
			if (probation.userId != 0L && (float)probationCredited < 60f)
			{
				return;
			}
			probation = new Acquaintance
			{
				userId = userId
			};
		}
		probationCredited = 0f;
		probation.seconds = Mathf.Min(probation.seconds + seconds, maxTrust);
		if (probation.seconds > acquaintances[num2].seconds)
		{
			acquaintances[num2] = probation;
			acquaintances[num2].sinceCredited = 0f;
			probation = default(Acquaintance);
			probationCredited = float.MaxValue;
		}
	}

	public void SetFamiliarity(ulong userId, float seconds, FamiliarityReason reason = FamiliarityReason.Handled)
	{
		if (userId != 0L)
		{
			AddFamiliarity(userId, seconds - StoredSecondsFor(userId), reason);
		}
	}

	public float BestFamiliarity(out ulong userId)
	{
		userId = 0uL;
		float num = 0f;
		if (acquaintances == null)
		{
			return 0f;
		}
		for (int i = 0; i < acquaintances.Length; i++)
		{
			if (acquaintances[i].userId != 0L && acquaintances[i].seconds > num)
			{
				num = acquaintances[i].seconds;
				userId = acquaintances[i].userId;
			}
		}
		return num;
	}

	private void TickFamiliarity()
	{
		float num = lastFamiliarityTick;
		lastFamiliarityTick = 0f;
		float familiarityForgetScale = Livestock.familiarityForgetScale;
		float maxTrust = Livestock.maxTrust;
		if (acquaintances == null || num <= 0f || familiarityForgetScale <= 0f)
		{
			return;
		}
		float num2 = num / familiarityForgetScale;
		float trustedNearbyTimeout = TrustedNearbyTimeout;
		bool flag = false;
		for (int i = 0; i < acquaintances.Length; i++)
		{
			ref Acquaintance reference = ref acquaintances[i];
			if (reference.userId != 0L && !(reference.seconds >= maxTrust) && !((float)reference.sinceCredited <= trustedNearbyTimeout))
			{
				reference.seconds -= num2;
				flag = true;
				if (reference.seconds <= 0f)
				{
					reference = default(Acquaintance);
				}
			}
		}
		if (flag)
		{
			RefreshHusbandryClocks();
		}
		if (probation.userId != 0L && !(probation.seconds >= maxTrust) && !((float)probationCredited <= trustedNearbyTimeout))
		{
			probation.seconds -= num2;
			if (probation.seconds <= 0f)
			{
				probation = default(Acquaintance);
				probationCredited = float.MaxValue;
			}
		}
	}

	public void OnPlayerSensed(BasePlayer player, float deltaTime)
	{
		if (player == null || player.IsDead() || deltaTime <= 0f)
		{
			return;
		}
		TryDefendSensedPlayer(player);
		if (player.IsSleeping() || IsSoreAt(player))
		{
			return;
		}
		float familiarityRadius = Livestock.familiarityRadius;
		if (familiarityRadius <= 0f)
		{
			return;
		}
		float num = Vector3.SqrMagnitude(player.transform.position - base.transform.position);
		if (!(num > familiarityRadius * familiarityRadius))
		{
			float num2 = Mathf.Max(0f, Livestock.familiarityRate);
			if (player.modifiers != null)
			{
				num2 *= 1f + Mathf.Max(0f, player.modifiers.GetValue(Modifier.ModifierType.LivestockHandling));
			}
			AddFamiliarity(player.userID, deltaTime * num2);
			TrackTrustedNearby(player, num);
			TryAdoptHome(player);
		}
	}

	private void TrackTrustedNearby(BasePlayer player, float distanceSqr)
	{
		if (!(TrustOf(player) < Livestock.trustToFollow) && (!((float)trustedNearbySeen <= TrustedNearbyTimeout) || !(trustedNearby.Get(base.isServer) != player) || !(distanceSqr >= trustedNearbyDistanceSqr)))
		{
			trustedNearby.Set(player);
			trustedNearbyDistanceSqr = distanceSqr;
			trustedNearbySeen = 0f;
		}
	}

	public BasePlayer StartCuriousFollow()
	{
		BasePlayer nearbyTrustedPlayer = NearbyTrustedPlayer;
		curiousAbout.Set(nearbyTrustedPlayer);
		return nearbyTrustedPlayer;
	}

	public bool WouldFollowOutOfCuriosity(BasePlayer player)
	{
		if (player == null || player.IsDead() || player.IsSleeping() || player.IsDestroyed)
		{
			return false;
		}
		if (IsDead() || IsLeading() || IsCalving || IsSleeping() || TOD_Sky.Instance.IsNight)
		{
			return false;
		}
		if (Fullness.NeedValue < 0.7f)
		{
			return false;
		}
		if (TrustOf(player) < Livestock.trustToFollow)
		{
			return false;
		}
		float curiousBreakDistance = Livestock.curiousBreakDistance;
		if (!(curiousBreakDistance <= 0f))
		{
			return Vector3.SqrMagnitude(player.transform.position - base.transform.position) <= curiousBreakDistance * curiousBreakDistance;
		}
		return true;
	}

	public void StopCuriousFollow()
	{
		curiousAbout = default(EntityRef<BasePlayer>);
	}

	private void TryDefendSensedPlayer(BasePlayer player)
	{
		if (!IsMale || !IsAdult() || IsDead())
		{
			return;
		}
		float maxTrust = Livestock.maxTrust;
		if (!(maxTrust <= 0f) && !(TrustOf(player) < maxTrust))
		{
			float defendReactionTime = Livestock.defendReactionTime;
			if (!(defendReactionTime <= 0f) && !(player.SecondsSinceAttacked > defendReactionTime) && !(player.lastAttackedTime <= lastDefendedAttack))
			{
				OnHerdMateHurt(player.lastAttacker, null);
				lastDefendedAttack = player.lastAttackedTime;
			}
		}
	}

	public bool RefusesToTarget(BaseEntity entity)
	{
		if (!(entity is BasePlayer player))
		{
			return !IsSoreAt(entity);
		}
		if (!IsMale || !IsAdult())
		{
			return false;
		}
		float maxTrust = Livestock.maxTrust;
		if (maxTrust > 0f)
		{
			return TrustOf(player) >= maxTrust;
		}
		return false;
	}

	private void SaveFamiliarity(ProtoBuf.LivestockAnimal save)
	{
		save.curious = IsCurious;
		save.decayStarted = DecayStarted;
		if (acquaintances == null)
		{
			return;
		}
		save.acquaintances = Facepunch.Pool.Get<List<LivestockAcquaintance>>();
		for (int i = 0; i < acquaintances.Length; i++)
		{
			if (acquaintances[i].userId != 0L)
			{
				LivestockAcquaintance livestockAcquaintance = Facepunch.Pool.Get<LivestockAcquaintance>();
				livestockAcquaintance.userId = acquaintances[i].userId;
				livestockAcquaintance.seconds = acquaintances[i].seconds;
				save.acquaintances.Add(livestockAcquaintance);
			}
		}
	}

	private void LoadFamiliarity(ProtoBuf.LivestockAnimal save)
	{
		IsCurious = save.curious;
		DecayStarted = DecayStarted || save.decayStarted;
		List<LivestockAcquaintance> list = save.acquaintances;
		if (list != null && list.Count > 0)
		{
			acquaintances = new Acquaintance[8];
			for (int i = 0; i < acquaintances.Length && i < list.Count; i++)
			{
				acquaintances[i].userId = list[i].userId;
				acquaintances[i].seconds = list[i].seconds;
			}
		}
		RefreshHusbandryClocks();
	}

	private void TransferFamiliarityTo(LivestockAnimal grown)
	{
		grown.IsCurious = IsCurious;
		CopyAcquaintancesTo(grown);
	}

	private void CopyAcquaintancesTo(LivestockAnimal other)
	{
		if (!(other == null) && acquaintances != null)
		{
			other.acquaintances = new Acquaintance[8];
			Array.Copy(acquaintances, other.acquaintances, 8);
			other.RefreshHusbandryClocks();
		}
	}

	private void PassBondsTo(LivestockAnimal calf)
	{
		if (calf == null || acquaintances == null)
		{
			return;
		}
		float trustToLead = Livestock.trustToLead;
		int num = 0;
		for (int i = 0; i < acquaintances.Length; i++)
		{
			if (acquaintances[i].userId != 0L)
			{
				if (num == 0)
				{
					calf.acquaintances = new Acquaintance[8];
				}
				calf.acquaintances[num++] = new Acquaintance
				{
					userId = acquaintances[i].userId,
					seconds = Mathf.Min(acquaintances[i].seconds, trustToLead)
				};
			}
		}
		calf.RefreshHusbandryClocks();
	}

	public static float ThresholdFor(TrustTier tier)
	{
		return tier switch
		{
			TrustTier.Known => Livestock.trustToFollow, 
			TrustTier.Tolerated => Livestock.trustToTolerate, 
			TrustTier.Bonded => Livestock.trustToLead, 
			TrustTier.Herd => Livestock.maxTrust, 
			_ => 0f, 
		};
	}

	public static float ProgressInTier(float seconds)
	{
		TrustTier trustTier = TierFor(seconds);
		if (trustTier == TrustTier.Herd)
		{
			return 1f;
		}
		float num = ThresholdFor(trustTier);
		float num2 = ThresholdFor(trustTier + 1);
		if (!(num2 > num))
		{
			return 0f;
		}
		return Mathf.Clamp01((seconds - num) / (num2 - num));
	}

	public static TrustTier TierFor(float seconds)
	{
		if (seconds >= Livestock.maxTrust)
		{
			return TrustTier.Herd;
		}
		if (seconds >= Livestock.trustToLead)
		{
			return TrustTier.Bonded;
		}
		if (seconds >= Livestock.trustToTolerate)
		{
			return TrustTier.Tolerated;
		}
		if (seconds >= Livestock.trustToFollow)
		{
			return TrustTier.Known;
		}
		return TrustTier.Stranger;
	}

	public static string TrustTierName(float seconds)
	{
		if (seconds >= Livestock.maxTrust)
		{
			return "herd";
		}
		if (seconds >= Livestock.trustToLead)
		{
			return "bonded";
		}
		if (seconds >= Livestock.trustToTolerate)
		{
			return "tolerated";
		}
		if (seconds >= Livestock.trustToFollow)
		{
			return "known";
		}
		return "stranger";
	}

	public string GetFamiliarityDebugInfo()
	{
		ulong userId;
		float num = BestFamiliarity(out userId);
		string text = (ActsCurious ? " curious" : string.Empty);
		if (userId != 0L)
		{
			return $"fam {num:0}s {TrustTierName(num)}{text}";
		}
		return "fam none stranger" + text;
	}

	private void InitFrailty()
	{
		TryGetComponent<LivestockAnimalFSM>(out frailtyFsm);
		TryGetComponent<RustNavMeshAgent>(out frailtyAgent);
		TickFrailty();
	}

	private void TickFrailty()
	{
		if (frailtyAgent != null)
		{
			frailtyAgent.gaitPenalty = AgeGaitPenalty;
		}
		if (frailtyFsm != null)
		{
			frailtyFsm.roam.SetHomeRadiusScale(Mathf.Lerp(1f, OldAgeRangeFactor, AgeFraction));
		}
	}

	public LivestockAllele Expressed(LivestockGene gene)
	{
		return LivestockGenome.Expressed(Genes, gene);
	}

	public LivestockAllele Carried(LivestockGene gene)
	{
		return LivestockGenome.Carried(Genes, gene);
	}

	public LivestockAllele AlleleFor(LivestockGene gene, int copy)
	{
		return LivestockGenome.GetAllele(Genes, gene, copy);
	}

	public bool IsPurebred(LivestockGene gene)
	{
		return LivestockGenome.IsPurebred(Genes, gene);
	}

	public int LineageMarker(int copy)
	{
		return LivestockGenome.GetMarker(Genes, copy);
	}

	public float GeneScale(LivestockGene gene)
	{
		if (!Livestock.genesEnabled)
		{
			return 1f;
		}
		return GeneScaleOf(Genes, gene);
	}

	public static float GeneScaleOf(int genome, LivestockGene gene)
	{
		float num = LivestockGeneRange.TraitFor(genome, gene);
		float b = ((num < 0.5f) ? Mathf.Lerp(BadScaleFor(gene), 1f, num * 2f) : Mathf.Lerp(1f, GoodScaleFor(gene), num * 2f - 1f));
		return Mathf.Max(0.01f, b);
	}

	private static float BadScaleFor(LivestockGene gene)
	{
		return gene switch
		{
			LivestockGene.Dung => Livestock.geneDungBad, 
			LivestockGene.Longevity => Livestock.geneLongevityBad, 
			LivestockGene.Yield => Livestock.geneYieldBad, 
			LivestockGene.Fertility => Livestock.geneFertilityBad, 
			_ => Livestock.geneHardinessBad, 
		};
	}

	private static float GoodScaleFor(LivestockGene gene)
	{
		return gene switch
		{
			LivestockGene.Dung => Livestock.geneDungGood, 
			LivestockGene.Longevity => Livestock.geneLongevityGood, 
			LivestockGene.Yield => Livestock.geneYieldGood, 
			LivestockGene.Fertility => Livestock.geneFertilityGood, 
			_ => Livestock.geneHardinessGood, 
		};
	}

	private void InitGenes()
	{
		Genes = RollGenome();
	}

	public static int RollGenome()
	{
		return LivestockGenome.Roll();
	}

	private int InheritedGenome(int fatherGenes)
	{
		int father = ((fatherGenes != 0) ? fatherGenes : Genes);
		return LivestockGenome.Combine(Genes, father, Livestock.geneMutationChance);
	}

	public int LitterSize()
	{
		float num = Mathf.Max(0f, GeneScale(LivestockGene.Fertility) - 1f);
		int num2 = Mathf.FloorToInt(num);
		if (UnityEngine.Random.value < num - (float)num2)
		{
			num2++;
		}
		return Mathf.Clamp(1 + num2, 1, Mathf.Max(1, Livestock.maxLitterSize));
	}

	public void SetGene(LivestockGene gene, LivestockAllele first, LivestockAllele second)
	{
		Genes = LivestockGenome.WithPair(Genes, gene, first, second);
	}

	public void SetLineageMarkers(int first, int second)
	{
		Genes = LivestockGenome.WithMarkers(Genes, first, second);
	}

	public string GetGenesDebugInfo()
	{
		StringBuilder obj = Facepunch.Pool.Get<StringBuilder>();
		obj.Clear();
		LivestockGene[] allGenes = LivestockGenome.AllGenes;
		foreach (LivestockGene livestockGene in allGenes)
		{
			if (obj.Length > 0)
			{
				obj.Append(", ");
			}
			obj.Append(livestockGene).Append(' ').Append(AlleleFor(livestockGene, 0).ToString()[0])
				.Append(AlleleFor(livestockGene, 1).ToString()[0])
				.Append(" (")
				.Append(GeneScale(livestockGene).ToString("0.00"))
				.Append(')');
		}
		obj.Append(", lineage ").Append(LivestockGenome.GetMarker(Genes, 0)).Append('/')
			.Append(LivestockGenome.GetMarker(Genes, 1));
		if (IsInbred)
		{
			obj.Append(" INBRED");
		}
		string result = obj.ToString();
		Facepunch.Pool.FreeUnmanaged(ref obj);
		return result;
	}

	public void SetHomePosition(Vector3 position)
	{
		homePosition = position;
	}

	public bool TryGetHomeCupboard(out BuildingPrivlidge cupboard)
	{
		cupboard = HomeTc.Get(base.isServer);
		if (cupboard != null)
		{
			return !cupboard.IsDestroyed;
		}
		return false;
	}

	private void InitHome()
	{
		lastAdoptionCheck = float.MaxValue;
		if (!Application.isLoadingSave)
		{
			homePosition = base.transform.position;
		}
	}

	public void TakeHomeFrom(LivestockAnimal other)
	{
		if (!(other == null))
		{
			homePosition = other.homePosition;
			HomeTc = other.HomeTc;
		}
	}

	private void TryAdoptHome(BasePlayer player)
	{
		if (!(TrustOf(player) < Livestock.maxTrust) && !((float)lastAdoptionCheck < 5f))
		{
			lastAdoptionCheck = 0f;
			BuildingPrivlidge buildingPrivilege = GetBuildingPrivilege(cached: true);
			if (!(buildingPrivilege == null) && buildingPrivilege.IsAuthed(player))
			{
				HomeTc = new EntityRef<BuildingPrivlidge>(buildingPrivilege.net.ID);
			}
		}
	}

	private void SaveHome(ProtoBuf.LivestockAnimal save)
	{
		save.homePosition = homePosition;
	}

	private void LoadHome(ProtoBuf.LivestockAnimal save)
	{
		if (save.homePosition != Vector3.zero)
		{
			homePosition = save.homePosition;
		}
	}

	public bool TryGetBedtimeHome(out Vector3 home)
	{
		home = HomePosition;
		float num = Mathf.Max(0f, HerdGatherDistance);
		return (home - base.transform.position).sqrMagnitude > num * num;
	}

	public string GetHomeDebugInfo()
	{
		float num = Vector3.Distance(base.transform.position, HomePosition);
		if (!TryGetHomeCupboard(out var _))
		{
			return $"home wild {num:0}m";
		}
		return $"home tc {num:0}m";
	}

	public bool NeedsWater()
	{
		return Hydration.NeedValue <= ConsumeThreshold;
	}

	public void FindDrinkBarrels(List<BaseEntity> results)
	{
		if (ConsumeSearchRadius <= 0f)
		{
			return;
		}
		using PooledList<LiquidContainer> pooledList = Facepunch.Pool.Get<PooledList<LiquidContainer>>();
		Query.Server.GetInSphere(base.transform.position, ConsumeSearchRadius, pooledList);
		foreach (LiquidContainer item in pooledList)
		{
			if (CanDrinkFrom(item))
			{
				results.Add(item);
			}
		}
	}

	public LiquidContainer FindDrinkBarrel()
	{
		using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
		FindDrinkBarrels(pooledList);
		float distanceSqr;
		return Eqs.NearestBeyond(pooledList, base.transform.position, -1f, out distanceSqr) as LiquidContainer;
	}

	public bool CanDrinkFrom(LiquidContainer container)
	{
		if (container == null || container.IsDestroyed || container.IsDead())
		{
			return false;
		}
		Item liquidItem = container.GetLiquidItem();
		if (liquidItem != null && liquidItem.info == WaterTypes.WaterItemDef)
		{
			return liquidItem.amount >= WaterForADrink();
		}
		return false;
	}

	public int WaterForADrink()
	{
		float num = 1f - Hydration.NeedValue;
		return Mathf.Max(1, Mathf.RoundToInt((float)BarrelWaterPerDrink * num));
	}

	public void ConsumeFromBarrel(LiquidContainer container)
	{
		if (CanDrinkFrom(container))
		{
			container.GetLiquidItem().UseItem(WaterForADrink());
		}
	}

	public bool IsDrinkableShore(Vector3 position)
	{
		if (!WaterResource.IsFreshWater(position))
		{
			return false;
		}
		float waterDepth = WaterLevel.GetWaterDepth(position, waves: false, volumes: false);
		if (waterDepth >= MinStandingWaterDepth)
		{
			return waterDepth <= MaxStandingWaterDepth;
		}
		return false;
	}

	private void InitName()
	{
		if (!Application.isLoadingSave && string.IsNullOrEmpty(AnimalName))
		{
			AnimalName = RollAnimalName(IsMale, out var locale);
			FoundLine(locale);
		}
	}

	public static string RollAnimalName(bool male, out string locale)
	{
		if (UnityEngine.Random.value < Livestock.devNameChance && LivestockNames.HasPack("devs"))
		{
			locale = "devs";
			return DrawFrom(male, locale);
		}
		locale = LivestockLocaleWeights.Roll();
		return DrawFrom(male, locale);
	}

	public static string RollAnimalName(bool male, string locale)
	{
		return DrawFrom(male, locale);
	}

	private static string DrawFrom(bool male, string locale)
	{
		string[] array = LivestockNames.PoolFor(male, locale);
		if (array.Length == 0)
		{
			return string.Empty;
		}
		string value = array[UnityEngine.Random.Range(0, array.Length)];
		if (string.IsNullOrEmpty(value))
		{
			return string.Empty;
		}
		value = LivestockNames.Capitalise(value, locale);
		if (value.Length <= 24)
		{
			return value;
		}
		return value.Substring(0, 24);
	}

	public bool TryInheritName(LivestockAnimal mother, LivestockAnimal father)
	{
		if (!Livestock.dynasticNames)
		{
			return false;
		}
		LivestockAnimal livestockAnimal = (IsMale ? father : mother);
		if (livestockAnimal == null || string.IsNullOrEmpty(livestockAnimal.AnimalName))
		{
			return false;
		}
		if (livestockAnimal.HeirNamed)
		{
			return false;
		}
		if (livestockAnimal.NameLocale == "devs")
		{
			return false;
		}
		int num = Mathf.Max(1, livestockAnimal.Generation) + 1;
		if (num > 20)
		{
			return false;
		}
		string text = DynastyName(livestockAnimal.LineageStem(), num);
		if (text.Length > 24)
		{
			ReportTooLong(text);
			return false;
		}
		AnimalName = text;
		livestockAnimal.HeirNamed = true;
		TakeLineageFrom(livestockAnimal);
		Generation = num;
		return true;
	}

	public string LineageStem()
	{
		if (Generation < 2)
		{
			return AnimalName;
		}
		if (!string.IsNullOrEmpty(LineStem))
		{
			return LineStem;
		}
		return StripPattern(AnimalName, DynastyPattern(Generation)) ?? AnimalName;
	}

	public void FoundLine(string locale)
	{
		LineStem = AnimalName;
		NameLocale = locale;
		Generation = 0;
		HeirNamed = false;
	}

	public void TakeLineageFrom(LivestockAnimal other)
	{
		LineStem = other.LineageStem();
		NameLocale = other.NameLocale;
		Generation = other.Generation;
	}

	private static string StripPattern(string name, string pattern)
	{
		if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(name))
		{
			return null;
		}
		int num = pattern.IndexOf("{0}", StringComparison.Ordinal);
		if (num < 0)
		{
			return null;
		}
		string text = pattern.Substring(0, num);
		string text2 = pattern.Substring(num + "{0}".Length);
		if (name.Length <= text.Length + text2.Length || !name.StartsWith(text, StringComparison.Ordinal) || !name.EndsWith(text2, StringComparison.Ordinal))
		{
			return null;
		}
		return name.Substring(text.Length, name.Length - text.Length - text2.Length);
	}

	public static string DynastyName(string stem, int generation)
	{
		if (generation < 2 || string.IsNullOrEmpty(stem))
		{
			return stem;
		}
		if (generation == 2)
		{
			return $"{stem} Junior";
		}
		return $"{stem} the {Ordinal(generation)}";
	}

	private static string DynastyPattern(int generation)
	{
		if (generation < 2)
		{
			return null;
		}
		if (generation != 2)
		{
			return string.Format("{0} the {1}", "{0}", Ordinal(generation));
		}
		return "{0} Junior";
	}

	private static void ReportTooLong(string name)
	{
		if (reportedLongNames.Add(name))
		{
			Debug.LogWarning($"The livestock dynasty name \"{name}\" is longer than the {24} characters a name may be, so the calf founds its own line instead.");
		}
	}

	public static string Ordinal(int number)
	{
		int num = number % 100;
		if (num >= 11 && num <= 13)
		{
			return number + "th";
		}
		return (number % 10) switch
		{
			1 => number + "st", 
			2 => number + "nd", 
			3 => number + "rd", 
			_ => number + "th", 
		};
	}

	private void SaveName(ProtoBuf.LivestockAnimal save)
	{
		save.generation = Generation;
		save.nameLocale = NameLocale;
		save.lineStem = LineStem;
		save.heirNamed = HeirNamed;
	}

	private void LoadName(ProtoBuf.LivestockAnimal save)
	{
		Generation = save.generation;
		NameLocale = save.nameLocale;
		LineStem = save.lineStem;
		HeirNamed = save.heirNamed;
	}

	public static int PopulationOf(LivestockSpecies species)
	{
		if (species == null || !populationBySpecies.TryGetValue(species, out var value))
		{
			return 0;
		}
		return value;
	}

	private void JoinPopulation()
	{
		if (!(Species == null))
		{
			populationBySpecies.TryGetValue(Species, out var value);
			populationBySpecies[Species] = value + 1;
		}
	}

	private void LeavePopulation()
	{
		if (!(Species == null) && populationBySpecies.TryGetValue(Species, out var value))
		{
			populationBySpecies[Species] = Mathf.Max(0, value - 1);
		}
	}

	public static int CullPast(int max)
	{
		if (max < 0)
		{
			return 0;
		}
		using PooledList<CullCandidate> pooledList = Facepunch.Pool.Get<PooledList<CullCandidate>>();
		LivestockAnimal[] array = Util.FindAll<LivestockAnimal>();
		foreach (LivestockAnimal livestockAnimal in array)
		{
			if (!(livestockAnimal == null) && !livestockAnimal.IsDestroyed && !livestockAnimal.IsDead() && livestockAnimal.isServer && !(livestockAnimal.Species == null))
			{
				pooledList.Add(new CullCandidate(livestockAnimal));
			}
		}
		pooledList.Sort();
		int num = 0;
		for (int j = 0; j < pooledList.Count; j++)
		{
			LivestockAnimal animal = pooledList[j].Animal;
			if (PopulationOf(animal.Species) > max)
			{
				animal.Kill();
				num++;
			}
		}
		return num;
	}

	public void SetSleepFraction(float fraction)
	{
		sleepFraction = Mathf.Clamp01(fraction);
	}

	private void InitSleep()
	{
		sleepFraction = UnityEngine.Random.value;
		poseHeldFor = float.MaxValue;
	}

	public LivestockAnimal FindSleepingCompanion()
	{
		LivestockAnimal livestockAnimal = (IsInfant() ? MotherAnimal : null);
		LivestockAnimal livestockAnimal2 = ((livestockAnimal != null && !livestockAnimal.IsDead()) ? livestockAnimal : FindHerdToReturnTo());
		if (livestockAnimal2 == null || livestockAnimal2.IsDead())
		{
			return null;
		}
		float num = Mathf.Max(0f, HerdGatherDistance);
		if (!((livestockAnimal2.transform.position - base.transform.position).sqrMagnitude > num * num))
		{
			return null;
		}
		return livestockAnimal2;
	}

	public bool IsHerdMate(LivestockAnimal candidate)
	{
		if (candidate == null || candidate == this || candidate.IsDestroyed || candidate.IsDead())
		{
			return false;
		}
		return candidate.IsSameSpeciesAs(this);
	}

	public bool IsMyDependent(LivestockAnimal candidate)
	{
		if (!IsHerdMate(candidate) || !candidate.IsDependent || IsInfant() || IsLeading())
		{
			return false;
		}
		if (IsMale)
		{
			return true;
		}
		if (candidate.IsInfant())
		{
			return candidate.MotherAnimal == this;
		}
		return false;
	}

	public int CountHerdMates()
	{
		if (SocialSearchRadius <= 0f)
		{
			return 0;
		}
		using PooledList<LivestockAnimal> pooledList = Facepunch.Pool.Get<PooledList<LivestockAnimal>>();
		Query.Server.GetBrainsInSphere(base.transform.position, SocialSearchRadius, pooledList);
		int num = 0;
		LivestockAnimal livestockAnimal = null;
		float num2 = float.MaxValue;
		foreach (LivestockAnimal item in pooledList)
		{
			if (!IsHerdMate(item))
			{
				continue;
			}
			num++;
			if (IsMyDependent(item))
			{
				float sqrMagnitude = (item.transform.position - base.transform.position).sqrMagnitude;
				if (!(sqrMagnitude >= num2))
				{
					livestockAnimal = item;
					num2 = sqrMagnitude;
				}
			}
		}
		if (livestockAnimal != null)
		{
			RememberShielded(livestockAnimal);
		}
		TickCalfProximity(livestockAnimal);
		return num;
	}

	private void TickCalfProximity(LivestockAnimal dependent)
	{
		if (!IsMale)
		{
			BaseCombatEntity attacker;
			bool flag = dependent != null && CalfThreatDistance > 0f && TryGetRememberedAggressor(out attacker) && Vector3.Distance(attacker.transform.position, dependent.transform.position) <= CalfThreatDistance;
			if (flag && playerLingeringOverCalf)
			{
				OnCalfThreatened();
			}
			playerLingeringOverCalf = flag;
		}
	}

	public bool HasProtectiveMotherNearby()
	{
		if (MotherShelterDistance <= 0f)
		{
			return false;
		}
		LivestockAnimal motherAnimal = MotherAnimal;
		if (motherAnimal != null && !motherAnimal.IsDead() && motherAnimal.IsProtectiveOfCalf)
		{
			return Vector3.Distance(base.transform.position, motherAnimal.transform.position) <= MotherShelterDistance;
		}
		return false;
	}

	public bool HasDependentsNearby()
	{
		return FindNearestDependent(SocialSearchRadius) != null;
	}

	public LivestockAnimal FindNearestDependent(float radius)
	{
		return FindNearestDependent(base.transform.position, radius);
	}

	public LivestockAnimal FindNearestDependent(Vector3 centre, float radius)
	{
		if (radius <= 0f)
		{
			return null;
		}
		using PooledList<LivestockAnimal> pooledList = Facepunch.Pool.Get<PooledList<LivestockAnimal>>();
		Query.Server.GetBrainsInSphere(centre, radius, pooledList);
		LivestockAnimal result = null;
		float num = float.MaxValue;
		foreach (LivestockAnimal item in pooledList)
		{
			if (IsMyDependent(item))
			{
				float sqrMagnitude = (item.transform.position - centre).sqrMagnitude;
				if (!(sqrMagnitude >= num))
				{
					result = item;
					num = sqrMagnitude;
				}
			}
		}
		return result;
	}

	public void RememberShielded(LivestockAnimal animal)
	{
		if (animal != null)
		{
			lastShielded.Set(animal);
		}
	}

	public int ClaimShieldSlot(LivestockAnimal defender)
	{
		if (defender == null)
		{
			return 0;
		}
		if (shieldSlotHolders == null)
		{
			shieldSlotHolders = new EntityRef<LivestockAnimal>[3];
		}
		if (shieldSlotClaimed == null)
		{
			shieldSlotClaimed = new TimeSince[3];
		}
		int num = -1;
		for (int i = 0; i < 3; i++)
		{
			LivestockAnimal livestockAnimal = shieldSlotHolders[i].Get(serverside: true);
			if (livestockAnimal == defender)
			{
				shieldSlotClaimed[i] = 0f;
				return i;
			}
			if (num < 0 && (livestockAnimal == null || livestockAnimal.IsDead() || (float)shieldSlotClaimed[i] > 5f))
			{
				num = i;
			}
		}
		if (num < 0)
		{
			return 0;
		}
		shieldSlotHolders[num].Set(defender);
		shieldSlotClaimed[num] = 0f;
		return num;
	}

	public LivestockAnimal FindHerdToReturnTo()
	{
		if (IsInfant())
		{
			return FindNearestHerdMate(RegroupSearchRadius);
		}
		LivestockAnimal livestockAnimal = FindMyLedDependent();
		if (livestockAnimal != null)
		{
			return livestockAnimal;
		}
		LivestockAnimal livestockAnimal2 = lastShielded.Get(serverside: true);
		if (livestockAnimal2 != null && !livestockAnimal2.IsDead() && !livestockAnimal2.IsDestroyed && IsMyDependent(livestockAnimal2))
		{
			return livestockAnimal2;
		}
		LivestockAnimal livestockAnimal3 = FindRememberedDependent();
		if (livestockAnimal3 != null)
		{
			return livestockAnimal3;
		}
		return FindNearestHerdMate(RegroupSearchRadius);
	}

	public LivestockAnimal FindMyLedDependent()
	{
		if (RegroupSearchRadius <= 0f)
		{
			return null;
		}
		using PooledList<LivestockAnimal> pooledList = Facepunch.Pool.Get<PooledList<LivestockAnimal>>();
		Query.Server.GetBrainsInSphere(base.transform.position, RegroupSearchRadius, pooledList);
		LivestockAnimal result = null;
		float num = float.MaxValue;
		foreach (LivestockAnimal item in pooledList)
		{
			if (item.IsLeading() && IsMyDependent(item))
			{
				float sqrMagnitude = (item.transform.position - base.transform.position).sqrMagnitude;
				if (!(sqrMagnitude >= num))
				{
					num = sqrMagnitude;
					result = item;
				}
			}
		}
		return result;
	}

	public LivestockAnimal FindNearestHerdMate(float radius)
	{
		if (radius <= 0f)
		{
			return null;
		}
		using PooledList<LivestockAnimal> pooledList = Facepunch.Pool.Get<PooledList<LivestockAnimal>>();
		Query.Server.GetBrainsInSphere(base.transform.position, radius, pooledList);
		LivestockAnimal result = null;
		float num = float.MaxValue;
		foreach (LivestockAnimal item in pooledList)
		{
			if (IsHerdMate(item) && !item.WantsToBeAlone)
			{
				float sqrMagnitude = (item.transform.position - base.transform.position).sqrMagnitude;
				if (!(sqrMagnitude >= num))
				{
					result = item;
					num = sqrMagnitude;
				}
			}
		}
		return result;
	}

	public void GatherRestingFootprints(Vector3 around, PooledList<LivestockFootprint> footprints)
	{
		using PooledList<LivestockAnimal> pooledList = Facepunch.Pool.Get<PooledList<LivestockAnimal>>();
		Query.Server.GetBrainsInSphere(around, 30f, pooledList);
		foreach (LivestockAnimal item in pooledList)
		{
			if (!(item == this) && !item.IsDead())
			{
				footprints.Add(item.RestingFootprint());
			}
		}
	}

	public LivestockFootprint RestingFootprint()
	{
		RustNavMeshAgent rustNavMeshAgent = RestingAgent;
		if (rustNavMeshAgent == null || !rustNavMeshAgent.hasPath)
		{
			return LivestockFootprint.Of(this);
		}
		return RestingFootprintFor(rustNavMeshAgent.destinationWS, rustNavMeshAgent.currentStoppingDistance);
	}

	public LivestockFootprint RestingFootprintFor(Vector3 destination, float stoppingDistance)
	{
		RustNavMeshAgent rustNavMeshAgent = RestingAgent;
		Vector3 vector = (destination - base.transform.position).WithY(0f);
		float magnitude = vector.magnitude;
		float num = ((rustNavMeshAgent != null) ? rustNavMeshAgent.RestingDistance(stoppingDistance) : stoppingDistance);
		if (magnitude <= num)
		{
			return LivestockFootprint.Of(this);
		}
		return LivestockFootprint.Of(this, destination - vector * (num / magnitude), vector);
	}

	public static float ClearanceOf(PooledList<LivestockFootprint> footprints, in LivestockFootprint footprint)
	{
		float num = float.MaxValue;
		foreach (LivestockFootprint footprint2 in footprints)
		{
			num = Mathf.Min(num, 0f - LivestockFootprint.Penetration(in footprint, footprint2));
		}
		return num;
	}

	public static bool IsClear(float clearance)
	{
		return clearance >= 0.25f;
	}

	public bool IsWalkingAfter(BasePlayer player)
	{
		if (player != null)
		{
			if (!IsFollowing(player))
			{
				return CuriousAbout == player;
			}
			return true;
		}
		return false;
	}

	public void CountWalkingAbreast(BasePlayer player, out int onLeft, out int onRight)
	{
		onLeft = 0;
		onRight = 0;
		if (player == null)
		{
			return;
		}
		Vector3 position = player.transform.position;
		Vector3 position2 = base.transform.position;
		Vector3 lhs = (position - position2).WithY(0f);
		using PooledList<LivestockAnimal> pooledList = Facepunch.Pool.Get<PooledList<LivestockAnimal>>();
		Query.Server.GetBrainsInSphere(position, 10f, pooledList);
		foreach (LivestockAnimal item in pooledList)
		{
			if (item == this || !item.IsWalkingAfter(player))
			{
				continue;
			}
			Vector3 position3 = item.transform.position;
			if (!(Vector3.Dot((position3 - position).WithY(0f), (position2 - position).WithY(0f)) <= 0f))
			{
				float num = Vector3.Cross(lhs, (position3 - position2).WithY(0f)).y;
				if (Mathf.Abs(num) < 0.01f)
				{
					num = ((item.net.ID.Value < net.ID.Value) ? (-1f) : 1f);
				}
				if (num < 0f)
				{
					onLeft++;
				}
				else
				{
					onRight++;
				}
			}
		}
	}

	public bool TryFindSecludedSpot(RustNavMeshAgent agent, LivestockAnimal partner, float minDistance, float maxDistance, float clearRadius, out Vector3 spot)
	{
		spot = default(Vector3);
		if (agent == null || maxDistance <= 0f)
		{
			return false;
		}
		using PooledList<LivestockAnimal> pooledList = Facepunch.Pool.Get<PooledList<LivestockAnimal>>();
		Query.Server.GetBrainsInSphere(base.transform.position, maxDistance + clearRadius, pooledList);
		using PooledList<Vector3> pooledList2 = Facepunch.Pool.Get<PooledList<Vector3>>();
		foreach (LivestockAnimal item in pooledList)
		{
			if (IsHerdMate(item) && !(item == partner))
			{
				pooledList2.Add(item.transform.position);
			}
		}
		using PooledList<NavVector3> pooledList3 = Facepunch.Pool.Get<PooledList<NavVector3>>();
		Eqs.SampleNavigablePositions(agent, agent.nextPosition, pooledList3, maxDistance, minDistance, 8);
		TryGetHerdCentroid(out var centroid);
		bool flag = false;
		float num = float.MinValue;
		NavVector3 locationNS = default(NavVector3);
		foreach (NavVector3 item2 in pooledList3)
		{
			if (agent.SamplePosition(item2, out var hitNS, 5f) && !IsCrowded(pooledList2, hitNS.position.Value, clearRadius))
			{
				float sqrMagnitude = (hitNS.position.Value - centroid).sqrMagnitude;
				if (!(sqrMagnitude <= num))
				{
					num = sqrMagnitude;
					locationNS = hitNS.position;
					flag = true;
				}
			}
		}
		if (!flag || !agent.CanReach(locationNS))
		{
			return false;
		}
		spot = locationNS.Value;
		return true;
	}

	private static bool IsCrowded(PooledList<Vector3> crowd, Vector3 point, float clearRadius)
	{
		float num = clearRadius * clearRadius;
		foreach (Vector3 item in crowd)
		{
			if ((item - point).sqrMagnitude < num)
			{
				return true;
			}
		}
		return false;
	}

	private LivestockAnimal FindRememberedDependent()
	{
		if (!TryGetComponent<SenseComponent>(out var component))
		{
			return null;
		}
		using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
		component.GetOncePerceivedEntities(pooledList);
		LivestockAnimal result = null;
		float num = float.MaxValue;
		foreach (BaseEntity item in pooledList)
		{
			if (item is LivestockAnimal livestockAnimal && IsMyDependent(livestockAnimal) && component.FindLKP(livestockAnimal, out var lkp))
			{
				float sqrMagnitude = (lkp - base.transform.position).sqrMagnitude;
				if (!(sqrMagnitude >= num))
				{
					result = livestockAnimal;
					num = sqrMagnitude;
				}
			}
		}
		return result;
	}

	public bool TryGetHerdCentroid(out Vector3 centroid)
	{
		centroid = base.transform.position;
		if (SocialSearchRadius <= 0f)
		{
			return false;
		}
		using PooledList<LivestockAnimal> pooledList = Facepunch.Pool.Get<PooledList<LivestockAnimal>>();
		Query.Server.GetBrainsInSphere(base.transform.position, SocialSearchRadius, pooledList);
		Vector3 position = base.transform.position;
		int num = 1;
		foreach (LivestockAnimal item in pooledList)
		{
			if (IsHerdMate(item) && !item.WantsToBeAlone)
			{
				position += item.transform.position;
				num++;
			}
		}
		if (num <= 1)
		{
			return false;
		}
		centroid = position / num;
		return true;
	}

	public bool TryGetHerdFleeHeading(out Vector3 heading)
	{
		heading = herdFleeHeading;
		if (hasHerdFleeHeading)
		{
			return (float)herdFleeHeadingSet < 10f;
		}
		return false;
	}

	public void PublishHerdFleeHeading(Vector3 heading)
	{
		heading = heading.WithY(0f);
		if (heading.sqrMagnitude < 0.0001f)
		{
			return;
		}
		heading = heading.normalized;
		AdoptHerdFleeHeading(heading);
		if (SocialSearchRadius <= 0f)
		{
			return;
		}
		using PooledList<LivestockAnimal> pooledList = Facepunch.Pool.Get<PooledList<LivestockAnimal>>();
		Query.Server.GetBrainsInSphere(base.transform.position, SocialSearchRadius, pooledList);
		foreach (LivestockAnimal item in pooledList)
		{
			if (IsHerdMate(item))
			{
				item.AdoptHerdFleeHeading(heading);
			}
		}
	}

	private void AdoptHerdFleeHeading(Vector3 heading)
	{
		herdFleeHeading = heading;
		herdFleeHeadingSet = 0f;
		hasHerdFleeHeading = true;
	}

	public float HerdLoadTarget(int herdMates)
	{
		int num = Mathf.Max(0, ComfortableHerdSize);
		int num2 = Mathf.Max(num + 1, CrowdedHerdSize);
		if (herdMates <= num)
		{
			return 1f;
		}
		return Mathf.Lerp(1f, 0.3f, Mathf.InverseLerp(num, num2, herdMates));
	}

	public float PackingTarget(float neighbours)
	{
		float num = Mathf.Max(0f, ComfortablePacking);
		float num2 = Mathf.Max(num + 0.01f, CrowdedPacking);
		float b = Mathf.Max(num2 + 0.01f, SeverePacking);
		if (neighbours <= num)
		{
			return 1f;
		}
		if (neighbours <= num2)
		{
			return Mathf.Lerp(1f, 0.3f, Mathf.InverseLerp(num, num2, neighbours));
		}
		return Mathf.Lerp(0.3f, 0f, Mathf.InverseLerp(num2, b, neighbours));
	}

	public float CountPackingNeighbours()
	{
		if (PackingRadius <= 0f)
		{
			return 0f;
		}
		using PooledList<LivestockAnimal> pooledList = Facepunch.Pool.Get<PooledList<LivestockAnimal>>();
		Query.Server.GetBrainsInSphere(base.transform.position, PackingRadius, pooledList);
		float num = PackingRadius * PackingRadius;
		float num2 = 0f;
		foreach (LivestockAnimal item in pooledList)
		{
			if (!(item == null) && !(item == this) && !item.IsDestroyed && !item.IsDead())
			{
				Vector3 v = item.transform.position - base.transform.position;
				if (!(Mathf.Abs(v.y) > PackingHeight) && !(v.WithY(0f).sqrMagnitude > num))
				{
					num2 += Mathf.Max(0f, item.AdultEquivalent);
				}
			}
		}
		return num2;
	}

	private void TickPersonalSpace(ref TickableNeed need, float seconds)
	{
		int herdMates = CountHerdMates();
		if (!(PersonalSpaceChangeRate <= 0f) && !TOD_Sky.Instance.IsNight && !IsSleeping())
		{
			lastHerdLoadTarget = HerdLoadTarget(herdMates);
			lastPackingTarget = PackingTarget(CountPackingNeighbours());
			float target = Mathf.Min(lastHerdLoadTarget, lastPackingTarget);
			need.NeedValue = Mathf.MoveTowards(need.NeedValue, target, PersonalSpaceChangeRate * seconds);
		}
	}

	public LivestockSpecial SpecialIdentity()
	{
		LivestockSpecialTable instance = LivestockSpecialTable.Instance;
		if (!(instance != null))
		{
			return null;
		}
		return instance.Find(AnimalName);
	}

	public static LivestockAnimal FindLivingSpecial(string name)
	{
		if (string.IsNullOrEmpty(name) || !livingSpecials.TryGetValue(name, out var value))
		{
			return null;
		}
		if (!(value != null) || value.IsDestroyed || value.IsDead())
		{
			return null;
		}
		return value;
	}

	public static void QueueNext(LivestockSpecial special)
	{
		queuedSpecial = special;
	}

	public static bool IsSpecialDiscovered(string name)
	{
		if (!string.IsNullOrEmpty(name))
		{
			return discoveredSpecials.Contains(name);
		}
		return false;
	}

	public static string[] DiscoveredSpecialNames()
	{
		if (discoveredSpecials.Count == 0)
		{
			return Array.Empty<string>();
		}
		string[] array = new string[discoveredSpecials.Count];
		discoveredSpecials.CopyTo(array);
		return array;
	}

	public static void LoadDiscoveredSpecials(string[] names)
	{
		discoveredSpecials.Clear();
		if (names == null)
		{
			return;
		}
		for (int i = 0; i < names.Length; i++)
		{
			if (!string.IsNullOrEmpty(names[i]))
			{
				discoveredSpecials.Add(names[i]);
			}
		}
	}

	private void RememberSpecial(string name, bool discovered)
	{
		LivestockSpecialTable instance = LivestockSpecialTable.Instance;
		if (!string.IsNullOrEmpty(name) && !(instance == null) && instance.Find(name) != null)
		{
			livingSpecials[name] = this;
			if (discovered)
			{
				discoveredSpecials.Add(name);
			}
		}
	}

	private void ForgetSpecial()
	{
		ForgetSpecial(AnimalName);
	}

	private void ForgetSpecial(string name)
	{
		if (!string.IsNullOrEmpty(name) && livingSpecials.TryGetValue(name, out var value) && value == this)
		{
			livingSpecials.Remove(name);
		}
	}

	public bool IsPopulationSpawn()
	{
		if (TryGetComponent<Spawnable>(out var component))
		{
			return component.Population != null;
		}
		return false;
	}

	private void TryGrantSpecial()
	{
		if (Application.isLoadingSave)
		{
			return;
		}
		LivestockSpecialTable instance = LivestockSpecialTable.Instance;
		if (instance == null || instance.Entries == null || instance.Entries.Length == 0)
		{
			return;
		}
		LivestockSpecial livestockSpecial = TakeQueued();
		bool byCommand = livestockSpecial != null;
		if (livestockSpecial == null)
		{
			if (!IsPopulationSpawn() || UnityEngine.Random.value >= Livestock.specialChance)
			{
				return;
			}
			livestockSpecial = PickSpecial(instance);
		}
		if (livestockSpecial != null)
		{
			Apply(livestockSpecial, byCommand);
			if (Livestock.debugSpecials)
			{
				Debug.Log($"[Livestock] {base.ShortPrefabName} spawned as {livestockSpecial.Name} at {base.transform.position}.");
			}
		}
	}

	private LivestockSpecial TakeQueued()
	{
		LivestockSpecial livestockSpecial = queuedSpecial;
		if (livestockSpecial == null)
		{
			return null;
		}
		queuedSpecial = null;
		if (!Fits(livestockSpecial) || !(FindLivingSpecial(livestockSpecial.Name) == null))
		{
			return null;
		}
		return livestockSpecial;
	}

	public bool Fits(LivestockSpecial special)
	{
		if (special == null || string.IsNullOrEmpty(special.Name))
		{
			return false;
		}
		if (special.Sex != 0 && special.Sex == SexForm.Male != IsMale)
		{
			return false;
		}
		if (special.Species == null || special.Species.Length == 0)
		{
			return true;
		}
		LivestockSpecies[] species = special.Species;
		foreach (LivestockSpecies livestockSpecies in species)
		{
			if (livestockSpecies != null && livestockSpecies == Species)
			{
				return true;
			}
		}
		return false;
	}

	private LivestockSpecial PickSpecial(LivestockSpecialTable table)
	{
		LivestockSpecial[] entries = table.Entries;
		if (eligibleSpecials == null || eligibleSpecials.Length < entries.Length)
		{
			eligibleSpecials = new bool[entries.Length];
		}
		float num = 0f;
		LivestockSpecial result = null;
		for (int i = 0; i < entries.Length; i++)
		{
			LivestockSpecial livestockSpecial = entries[i];
			eligibleSpecials[i] = livestockSpecial != null && livestockSpecial.Weight > 0f && Fits(livestockSpecial) && FindLivingSpecial(livestockSpecial.Name) == null;
			if (eligibleSpecials[i])
			{
				num += livestockSpecial.Weight;
				result = livestockSpecial;
			}
		}
		if (num <= 0f)
		{
			return null;
		}
		float num2 = UnityEngine.Random.value * num;
		for (int j = 0; j < entries.Length; j++)
		{
			if (eligibleSpecials[j])
			{
				num2 -= entries[j].Weight;
				if (num2 <= 0f)
				{
					return entries[j];
				}
			}
		}
		return result;
	}

	private void Apply(LivestockSpecial special, bool byCommand)
	{
		AnimalName = special.Name;
		FoundLine(LivestockLocaleWeights.HouseLocale());
		RememberSpecial(special.Name, !byCommand);
		ApplyGenes(special);
	}

	private void ApplyGenes(LivestockSpecial special)
	{
		if (special.Genes != null)
		{
			ForcedGene[] genes = special.Genes;
			foreach (ForcedGene forcedGene in genes)
			{
				if (forcedGene != null)
				{
					Genes = LivestockGenome.WithPair(Genes, forcedGene.Gene, forcedGene.First, forcedGene.Second);
				}
			}
		}
		int num = LivestockGenome.RollMarker();
		int num2 = LivestockGenome.RollMarker();
		if (num2 == num)
		{
			num2 = (num + 1 + UnityEngine.Random.Range(0, 15)) % 16;
		}
		Genes = LivestockGenome.WithMarkers(Genes, num, num2);
	}

	private bool TryNameFromCross(LivestockAnimal mother, LivestockAnimal father)
	{
		if (mother == null || father == null)
		{
			return false;
		}
		LivestockSpecialTable instance = LivestockSpecialTable.Instance;
		if (instance == null)
		{
			return false;
		}
		LivestockSpecial livestockSpecial = instance.Find(mother.AnimalName);
		LivestockSpecial livestockSpecial2 = instance.Find(father.AnimalName);
		if (livestockSpecial == null || livestockSpecial2 == null)
		{
			return false;
		}
		LivestockCross livestockCross = instance.CrossFor(livestockSpecial.Name, livestockSpecial2.Name);
		if (livestockCross == null)
		{
			return false;
		}
		AnimalName = livestockCross.Name;
		FoundLine(LivestockLocaleWeights.HouseLocale());
		return true;
	}

	private float SpeciesSaleScale()
	{
		if (!(Species != null))
		{
			return 1f;
		}
		return Mathf.Max(0f, Species.SaleValueScale);
	}

	private float GeneSaleScale()
	{
		float num = 0f;
		LivestockGene[] allGenes = LivestockGenome.AllGenes;
		foreach (LivestockGene gene in allGenes)
		{
			num += GeneScale(gene);
		}
		float num2 = num / 5f;
		return Mathf.Max(0f, 1f + (num2 - 1f) * Livestock.priceGeneWeight);
	}

	private float ConditionSaleScale()
	{
		float priceConditionFloor = Livestock.priceConditionFloor;
		float b = Mathf.Max(priceConditionFloor + 0.01f, Livestock.priceConditionFull);
		return Mathf.Lerp(Mathf.Clamp01(Livestock.priceWorstScale), 1f, Mathf.InverseLerp(priceConditionFloor, b, Condition));
	}

	private float AgeSaleScale()
	{
		float a = Mathf.Clamp01(Livestock.priceAgePrime);
		return Mathf.Lerp(1f, Mathf.Clamp01(Livestock.priceOldestScale), Mathf.InverseLerp(a, 1f, AgeFraction));
	}

	private float OriginSaleScale()
	{
		if (!IsBred)
		{
			return Mathf.Clamp01(1f - Livestock.priceWildPenalty);
		}
		return 1f;
	}

	private void SaveSaleValue(ProtoBuf.LivestockAnimal msg)
	{
		msg.saleValueScale = SaleValueScale;
	}

	public float StartingValueFor(Need need)
	{
		return need switch
		{
			Need.Fullness => Mathf.Clamp01(StartingFullness), 
			Need.Hydration => Mathf.Clamp01(StartingHydration), 
			_ => 1f, 
		};
	}

	public float DecayRatePerSecond(Need need)
	{
		return need switch
		{
			Need.Fullness => Mathf.Max(0f, FullnessDecayRate), 
			Need.Hydration => Mathf.Max(0f, HydrationDecayRate), 
			_ => 0f, 
		};
	}

	public bool IsInfant()
	{
		return Age == AgeStage.Infant;
	}

	public bool IsAdult()
	{
		return Age == AgeStage.Adult;
	}

	public bool IsLeading()
	{
		return HasFlag(Flags.Reserved7);
	}

	public bool IsPregnant()
	{
		return HasFlag(Flags.Reserved10);
	}

	public bool IsMating()
	{
		return HasFlag(Flags.Reserved12);
	}

	public bool IsGrazing()
	{
		return HasFlag(Flags.Reserved8);
	}

	public bool IsDrinking()
	{
		return HasFlag(Flags.Reserved13);
	}

	public bool IsSleeping()
	{
		return HasFlag(Flags.Reserved11);
	}

	public bool IsResting()
	{
		return HasFlag(Flags.Reserved14);
	}

	public bool IsLyingIn()
	{
		return HasFlag(Flags.Reserved17);
	}

	public bool IsFrisky()
	{
		return HasFlag(Flags.Reserved15);
	}

	public bool IsLyingDown()
	{
		if (IsSleeping())
		{
			return (float)poseHeldFor >= Mathf.Max(0f, BedDownDuration);
		}
		if (IsResting() || IsLyingIn())
		{
			return (float)poseHeldFor >= Mathf.Max(0f, LieDownDuration);
		}
		return false;
	}

	public bool IsSettlingDown()
	{
		if (IsHoldingAPoseFlag())
		{
			return !IsLyingDown();
		}
		return false;
	}

	private bool IsHoldingAPoseFlag()
	{
		if (!IsSleeping() && !IsResting())
		{
			return IsLyingIn();
		}
		return true;
	}

	public bool IsOffItsFeet()
	{
		if (!IsHoldingAPoseFlag())
		{
			return (float)onItsFeetIn > 0f;
		}
		return true;
	}

	public void MarkOnItsFeet()
	{
		onItsFeetIn = 0f;
	}

	private void StartGettingUp(float settleDuration, float riseDuration)
	{
		float num = Mathf.Max(0f, Mathf.Max(0f, settleDuration) - (float)poseHeldFor);
		onItsFeetIn = num + Mathf.Max(0f, riseDuration);
	}

	public bool IsHeadDown()
	{
		if (!IsGrazing() && !IsDrinking())
		{
			return (float)headComingUpFor < Mathf.Max(0f, RaiseHeadDuration);
		}
		return true;
	}

	public bool IsFollowing(BasePlayer player)
	{
		return LeadingPlayer.Get(base.isServer) == player;
	}

	public bool IsSameSpeciesAs(LivestockAnimal other)
	{
		if (other == null)
		{
			return false;
		}
		if (Species != null || other.Species != null)
		{
			return Species == other.Species;
		}
		return prefabID == other.prefabID;
	}

	public GameObjectRef FormForSex(bool male)
	{
		if (!(Species != null))
		{
			return null;
		}
		return Species.For(Age, male);
	}

	public TickableNeed GetNeed(Need need)
	{
		if ((int)need >= needs.Length)
		{
			return default(TickableNeed);
		}
		return needs[(int)need];
	}

	public override void InitShared()
	{
		base.InitShared();
		needs = new TickableNeed[3]
		{
			new TickableNeed(StartingValueFor(Need.Fullness)),
			new TickableNeed(StartingValueFor(Need.PersonalSpace)),
			new TickableNeed(StartingValueFor(Need.Hydration))
		};
	}

	public bool CanStopLead(BasePlayer player)
	{
		if (player != null && IsLeading())
		{
			return LeadingPlayer.Get(base.isServer) == player;
		}
		return false;
	}

	public void MarkNewborn()
	{
		findingItsFeet = true;
	}

	public void FinishFindingItsFeet()
	{
		findingItsFeet = false;
		LivestockAnimal motherAnimal = MotherAnimal;
		if (motherAnimal != null)
		{
			motherAnimal.newbornCalf = default(EntityRef<LivestockAnimal>);
		}
	}

	public bool TryGetNewbornCalf(out LivestockAnimal calf)
	{
		calf = newbornCalf.Get(base.isServer);
		if (calf == null || calf.IsDead() || !calf.IsFindingItsFeet)
		{
			calf = null;
			return false;
		}
		return true;
	}

	public override void ServerInit()
	{
		base.ServerInit();
		timers = new PersistentTimerSet(this);
		pregnancyTimer = new PersistentTimer(timers, 2)
		{
			onElapsed = delegate
			{
				GiveBirth();
			},
			onStarted = delegate
			{
				SetNetworkedFlag(Flags.Reserved10, value: true);
			}
		};
		breedCooldown = new PersistentTimer(timers, 3);
		NeedsQueue.Add(this);
		JoinPopulation();
		lastLoudNoise = 100f;
		lastHerdDistress = 100f;
		lastAggression = float.MaxValue;
		if (Form != 0)
		{
			IsMale = Form == SexForm.Male;
		}
		else if (!Application.isLoadingSave && !IsBeingPurchased)
		{
			IsMale = UnityEngine.Random.Range(0f, 1f) > 0.5f;
		}
		ValidateSexForm();
		InitGenes();
		TryGrantSpecial();
		InitName();
		InitAging();
		InitDung();
		InitSleep();
		InitFrailty();
		InitDayBudget();
		InitFamiliarity();
		InitHome();
		InitCondition();
		lastNeedsTick = 0f;
		headComingUpFor = float.MaxValue;
		IsBeingPurchased = false;
	}

	internal override void DoServerDestroy()
	{
		if (IsLeading())
		{
			ReleaseLeaderSpeed();
		}
		base.DoServerDestroy();
		NeedsQueue.Remove(this);
		LeavePopulation();
		ForgetSpecial();
	}

	private void ValidateSexForm()
	{
	}

	public override void Save(SaveInfo info)
	{
		base.Save(info);
		if (info.forDisk)
		{
			info.msg.livestockAnimal = Facepunch.Pool.Get<ProtoBuf.LivestockAnimal>();
			info.msg.livestockAnimal.timers = Facepunch.Pool.Get<List<ProtoBuf.PersistentTimer>>();
			timers.Save(info.msg.livestockAnimal.timers, info.cachedTime.Time);
			info.msg.livestockAnimal.needs = Facepunch.Pool.Get<List<LivestockNeed>>();
			TickableNeed[] array = needs;
			foreach (TickableNeed tickableNeed in array)
			{
				info.msg.livestockAnimal.needs.Add(tickableNeed.ToSavedData());
			}
			info.msg.livestockAnimal.mother = Mother.uid;
			info.msg.livestockAnimal.father = Father.uid;
			SaveName(info.msg.livestockAnimal);
			SaveFamiliarity(info.msg.livestockAnimal);
			SaveHome(info.msg.livestockAnimal);
			SaveCondition(info.msg.livestockAnimal);
		}
	}

	public override void Load(LoadInfo info)
	{
		base.Load(info);
		if (info.msg.livestockAnimal != null && base.isServer && info.fromDisk)
		{
			timers.Load(info.msg.livestockAnimal.timers);
			List<LivestockNeed> list = info.msg.livestockAnimal.needs;
			if (list != null)
			{
				for (int i = 0; i < needs.Length && i < list.Count; i++)
				{
					needs[i].LoadFrom(list[i]);
				}
			}
			Mother.uid = info.msg.livestockAnimal.mother;
			Father.uid = info.msg.livestockAnimal.father;
			LoadName(info.msg.livestockAnimal);
			LoadFamiliarity(info.msg.livestockAnimal);
			LoadHome(info.msg.livestockAnimal);
			LoadCondition(info.msg.livestockAnimal);
		}
		if (info.fromDisk && IsLeading())
		{
			StopLeading();
		}
	}

	public bool CanLead(BasePlayer player)
	{
		if (player != null && !player.IsDead() && !IsDead() && !IsSoreAt(player) && !base.IsMounted)
		{
			return TrustsToHandle(player);
		}
		return false;
	}

	public bool TakeStartle()
	{
		bool result = startled;
		startled = false;
		return result;
	}

	public void OnGrabbedBy(BasePlayer player)
	{
		if (player == null || player.IsDead())
		{
			return;
		}
		if (IsInfant())
		{
			LivestockAnimal motherAnimal = MotherAnimal;
			if (motherAnimal != null && !motherAnimal.IsDead())
			{
				motherAnimal.OnGrabbedAtDependent(player);
			}
		}
		if (TryGetComponent<SenseComponent>(out var component) && component.CanTarget(player))
		{
			component.TrySetTarget(player);
		}
		if (StandsItsGround)
		{
			Provoke(player);
			return;
		}
		startled = true;
		Spook();
	}

	private void OnGrabbedAtDependent(BasePlayer player)
	{
		OnCalfThreatened();
		if (TryGetComponent<SenseComponent>(out var component) && component.CanTarget(player))
		{
			component.TrySetTarget(player);
		}
		Provoke(player);
	}

	private void Provoke(BasePlayer player)
	{
		lastHerdDistress = 0f;
		herdDistressAttacker.Set(player);
		ClearSpook();
	}

	public void StopLeading()
	{
		ReleaseLeaderSpeed();
		SetLeading(leading: false);
		LeadingPlayer = default(EntityRef<BasePlayer>);
	}

	private void StashLeadForMount()
	{
		if (IsLeading())
		{
			leaderBeforeMount = LeadingPlayer;
		}
		StopLeading();
	}

	private void RestoreLeadAfterMount()
	{
		BasePlayer basePlayer = leaderBeforeMount.Get(serverside: true);
		leaderBeforeMount = default(EntityRef<BasePlayer>);
		if (!(basePlayer == null) && CanLead(basePlayer))
		{
			SetLeading(leading: true);
			LeadingPlayer = RefTo(basePlayer);
		}
	}

	private void TickLead()
	{
		if (!IsLeading())
		{
			return;
		}
		BasePlayer basePlayer = LeadingPlayer.Get(base.isServer);
		if (basePlayer == null || basePlayer.IsDestroyed || basePlayer.IsDead() || basePlayer.IsSleeping())
		{
			StopLeading();
			return;
		}
		float num = Vector3.Distance(basePlayer.transform.position, base.transform.position);
		if (Livestock.leadBreakDistance > 0f && num > Livestock.leadBreakDistance)
		{
			StopLeading();
		}
		else
		{
			SlowLeaderWhenTaut(basePlayer, num);
		}
	}

	private void SlowLeaderWhenTaut(BasePlayer leader, float distance)
	{
		if (!(leader.modifiers == null))
		{
			float num = 0f;
			if (distance > 3.5f && IsPulledAwayBy(leader))
			{
				num = 0f - Mathf.Lerp(0f, 0.9f, Mathf.InverseLerp(3.5f, 7f, distance));
			}
			if (!TryGetLeaderSpeed(leader, out var value))
			{
				PlayerModifiers.AddToPlayer(leader, leadModifier);
				value = 0f;
			}
			if (Mathf.Abs(value - num) > 0.01f)
			{
				leader.modifiers.SetValue(Modifier.ModifierSource.Interaction, Modifier.ModifierType.MoveSpeed, num);
			}
		}
	}

	public bool IsPulledAwayBy(BasePlayer leader)
	{
		return Vector3.Dot((leader.transform.position - base.transform.position).NormalizeXZ(), leader.estimatedVelocity.NormalizeXZ()) > 0.5f;
	}

	private static bool TryGetLeaderSpeed(BasePlayer leader, out float value)
	{
		foreach (Modifier item in leader.modifiers.All)
		{
			if (item != null && item.Source == Modifier.ModifierSource.Interaction && item.Type == Modifier.ModifierType.MoveSpeed)
			{
				value = item.Value;
				return true;
			}
		}
		value = 0f;
		return false;
	}

	private void ReleaseLeaderSpeed()
	{
		BasePlayer basePlayer = LeadingPlayer.Get(base.isServer);
		if (basePlayer != null && !basePlayer.IsDestroyed && basePlayer.modifiers != null)
		{
			basePlayer.modifiers.RemoveFromSource(Modifier.ModifierSource.Interaction);
		}
	}

	[RPC_Server]
	[RPC_Server.MaxDistance(3f)]
	[RPC_Server.CallsPerSecond(5uL)]
	private void RPC_Lead(RPCMessage msg)
	{
		if (!(msg.player == null))
		{
			bool wantsLead = msg.read.Bit();
			TryLead(msg.player, wantsLead);
		}
	}

	public bool TryLead(BasePlayer player, bool wantsLead)
	{
		if (player == null)
		{
			return false;
		}
		if (wantsLead && !CanLead(player))
		{
			if (!IsLeading() && !base.IsMounted)
			{
				OnGrabbedBy(player);
			}
			return false;
		}
		if (!wantsLead && !CanStopLead(player))
		{
			return false;
		}
		if (!wantsLead)
		{
			StopLeading();
			return true;
		}
		if (IsLeading() && LeadingPlayer.Get(base.isServer) != player)
		{
			ReleaseLeaderSpeed();
		}
		SetLeading(leading: true);
		LeadingPlayer = RefTo(player);
		return true;
	}

	public override void OnFlagsChanged(Flags old, Flags next)
	{
		base.OnFlagsChanged(old, next);
		if (base.isServer && (old & Flags.Busy) != (next & Flags.Busy))
		{
			if ((next & Flags.Busy) != 0)
			{
				StashLeadForMount();
			}
			else
			{
				RestoreLeadAfterMount();
			}
		}
	}

	public virtual bool TryGetYield(out ItemDefinition item, out int amount, out float interval, out float bestOfBreed)
	{
		item = null;
		amount = 0;
		interval = 0f;
		bestOfBreed = 0f;
		return false;
	}

	public virtual void AddCorpseYield(ResourceDispenser dispenser)
	{
	}

	[RPC_Server]
	[RPC_Server.IsVisible(3f)]
	[RPC_Server.CallsPerSecond(2uL)]
	private void RequestAnimalStats(RPCMessage msg)
	{
		if (msg.player == null)
		{
			return;
		}
		using ProtoBuf.LivestockAnimal livestockAnimal = Facepunch.Pool.Get<ProtoBuf.LivestockAnimal>();
		livestockAnimal.needs = Facepunch.Pool.Get<List<LivestockNeed>>();
		TickableNeed[] array = needs;
		foreach (TickableNeed tickableNeed in array)
		{
			livestockAnimal.needs.Add(tickableNeed.ToSavedData());
		}
		livestockAnimal.overgrazed = OvergrazedArea.IsOvergrazed(base.transform.position);
		float seconds = TrustOf(msg.player);
		livestockAnimal.trustTier = (int)TierFor(seconds);
		livestockAnimal.trustProgress = ProgressInTier(seconds);
		livestockAnimal.ageSeconds = AgeSeconds;
		livestockAnimal.ageFraction = AgeFraction;
		if (TryGetYield(out var item, out var amount, out var interval, out var bestOfBreed))
		{
			livestockAnimal.yieldItemId = item.itemid;
			livestockAnimal.yieldAmount = amount;
			livestockAnimal.yieldInterval = interval;
			livestockAnimal.yieldFraction = bestOfBreed;
		}
		livestockAnimal.dungInterval = (DungEnabled ? NextDungInterval() : 0f);
		livestockAnimal.dungFraction = (DungEnabled ? DungBestOfBreed() : 0f);
		livestockAnimal.dungItemId = (DungEnabled ? DungItem.itemid : 0);
		SaveSaleValue(livestockAnimal);
		ClientRPC(RpcTarget.Player("OnReceivedAnimalStats", msg.player), livestockAnimal);
	}

	public override void ScaleDamage(HitInfo info)
	{
		base.ScaleDamage(info);
		if (info.Initiator != null && info.Initiator != this && InSafeZone())
		{
			info.damageTypes.ScaleAll(0f);
		}
	}

	public override void Hurt(HitInfo info)
	{
		base.Hurt(info);
		if (!InSafeZone())
		{
			if (IsLeading())
			{
				StopLeading();
			}
			StopCuriousFollow();
			RememberAggressor(info?.Initiator);
			ChargeFamiliarityPenalty(info?.InitiatorPlayer, Livestock.familiarityHurtPenalty);
			if (info?.Initiator != null && !IsMale && HasDependentsNearby())
			{
				OnCalfThreatened();
			}
			AlertHerdOfDistress(info);
		}
	}

	public void OnHeardNoise(NpcNoiseEvent noise)
	{
		if (noise.Intensity >= NpcNoiseIntensity.Medium)
		{
			lastLoudNoise = 0f;
			lastLoudNoisePosition = noise.NoisePosition;
			if (noise.Initiator != null && noise.Initiator.ToNonNpcPlayer(out var player) && TryGetComponent<SenseComponent>(out var component) && component.CanTarget(player))
			{
				component.TrySetTarget(player);
			}
		}
	}

	public bool HeardLoudNoiseWithin(float range)
	{
		if (RecentlyHeardLoudNoise)
		{
			return Vector3.Distance(base.transform.position, lastLoudNoisePosition) <= range;
		}
		return false;
	}

	public void Spook()
	{
		lastLoudNoise = 0f;
	}

	public void ClearSpook()
	{
		lastLoudNoise = 100f;
	}

	private bool CanBeAnsweredFor(BaseEntity attacker, out BaseCombatEntity combatAttacker)
	{
		combatAttacker = attacker as BaseCombatEntity;
		if (combatAttacker == null || combatAttacker.IsDead() || combatAttacker == this)
		{
			return false;
		}
		if (combatAttacker is LivestockAnimal candidate)
		{
			return !IsHerdMate(candidate);
		}
		return true;
	}

	public void RememberAggressor(BaseEntity attacker)
	{
		if (CanBeAnsweredFor(attacker, out var combatAttacker))
		{
			if (!IsSoreAt(combatAttacker))
			{
				penaltyCharged = 0f;
			}
			aggressor.Set(combatAttacker);
			lastAggression = 0f;
		}
	}

	private void ChargeFamiliarityPenalty(BasePlayer player, float penalty)
	{
		if (!(player == null) && !(penalty <= penaltyCharged))
		{
			AddFamiliarity(player.userID, 0f - (penalty - penaltyCharged));
			penaltyCharged = penalty;
		}
	}

	public bool TryGetRememberedAggressor(out BaseCombatEntity attacker)
	{
		attacker = null;
		float grudgeDuration = Livestock.grudgeDuration;
		if (grudgeDuration <= 0f || (float)lastAggression > grudgeDuration)
		{
			return false;
		}
		BaseCombatEntity baseCombatEntity = aggressor.Get(base.isServer);
		if (baseCombatEntity == null || baseCombatEntity.IsDead())
		{
			return false;
		}
		attacker = baseCombatEntity;
		return true;
	}

	public bool TryGetHerdDistressAttacker(out BaseCombatEntity attacker)
	{
		attacker = null;
		if (!HerdMateRecentlyHurt)
		{
			return false;
		}
		BaseCombatEntity baseCombatEntity = herdDistressAttacker.Get(base.isServer);
		if (baseCombatEntity == null || baseCombatEntity.IsDead())
		{
			return false;
		}
		attacker = baseCombatEntity;
		return true;
	}

	public void OnCalfThreatened()
	{
		lastCalfThreat = 0f;
		calfEverThreatened = true;
		ClearSpook();
	}

	public void ForgetCalfThreat()
	{
		calfEverThreatened = false;
	}

	public void OnHerdMateHurt(BaseEntity attacker, LivestockAnimal victim)
	{
		if (CanBeAnsweredFor(attacker, out var combatAttacker))
		{
			lastHerdDistress = 0f;
			herdDistressAttacker.Set(combatAttacker);
			RememberAggressor(attacker);
			ChargeFamiliarityPenalty(attacker as BasePlayer, Livestock.familiarityDistressPenalty);
			if (IsMyDependent(victim))
			{
				OnCalfThreatened();
			}
			if (TryGetComponent<SenseComponent>(out var component) && component.CanTarget(attacker))
			{
				component.TrySetTarget(attacker);
			}
		}
	}

	public void ClearHerdDistress()
	{
		lastHerdDistress = 100f;
	}

	private void AlertHerdOfDistress(HitInfo info)
	{
		if (DistressRadius <= 0f || (IsMale && IsAdult()))
		{
			return;
		}
		using PooledList<LivestockAnimal> pooledList = Facepunch.Pool.Get<PooledList<LivestockAnimal>>();
		Query.Server.GetBrainsInSphere(base.transform.position, DistressRadius, pooledList);
		foreach (LivestockAnimal item in pooledList)
		{
			if (IsHerdMate(item))
			{
				item.OnHerdMateHurt(info?.Initiator, this);
				if (item.IsDependent && !item.IsMyDependent(this))
				{
					item.Spook();
				}
			}
		}
	}

	private void TickNeeds()
	{
		TimeSince timeSince = lastNeedsTick;
		lastNeedsTick = 0f;
		TickCondition(timeSince);
		float num = ((TOD_Sky.Instance.IsNight || IsSleeping() || IsResting()) ? SleepingDecayScale : 1f);
		for (int i = 0; i < needs.Length; i++)
		{
			ref TickableNeed reference = ref needs[i];
			reference.NextTick -= timeSince;
			if (!(reference.NextTick > 0f))
			{
				float num2 = NeedTickFrequency - reference.NextTick;
				reference.NextTick = NeedTickFrequency;
				if (i == 1)
				{
					TickPersonalSpace(ref reference, num2);
				}
				else
				{
					reference.NeedValue = Mathf.MoveTowards(reference.NeedValue, 0f, DecayRatePerSecond((Need)i) * num2 * num);
				}
			}
		}
		if (IsGrazing() && needs.Length != 0)
		{
			ref TickableNeed reference2 = ref needs[0];
			float num3 = (IsGrazingFromTrough ? Mathf.Clamp01(TroughFullnessCeiling) : 1f);
			if (reference2.NeedValue < num3)
			{
				float num4 = Mathf.Min(FullnessFillRate * (float)timeSince, num3 - reference2.NeedValue);
				if (IsGrazingFromTrough)
				{
					num4 = TakeTroughFullness(num4);
				}
				reference2.NeedValue += num4;
			}
		}
		if (IsDrinking() && needs.Length > 2)
		{
			ref TickableNeed reference3 = ref needs[2];
			reference3.NeedValue = Mathf.MoveTowards(reference3.NeedValue, 1f, HydrationFillRate * (float)timeSince);
		}
	}

	public void EnterNaturalDeathPose()
	{
		if (!IsLyingDown())
		{
			SetNetworkedFlag(Flags.Reserved11, value: true);
		}
	}

	private string DebugLine(DebugValue value, float number, float precision, string text)
	{
		return FSMDebugInfo.Mark(text, flashedValues[(int)value].Sample(number, precision));
	}

	public string GetDebugInfo()
	{
		if (flashedValues == null)
		{
			flashedValues = new FlashedValue[6];
		}
		float needValue = Fullness.NeedValue;
		float needValue2 = Hydration.NeedValue;
		float needValue3 = PersonalSpace.NeedValue;
		float contentment = Contentment;
		ulong userId;
		string text = DebugLine(DebugValue.Age, AgeFraction, 0.01f, $"age {AgeFraction:0.00}") + "\n" + DebugLine(DebugValue.Fullness, needValue, 0.01f, $"full {needValue:0.00}") + "\n" + DebugLine(DebugValue.Hydration, needValue2, 0.01f, $"hyd {needValue2:0.00}") + "\n" + DebugLine(DebugValue.PersonalSpace, needValue3, 0.01f, $"room {needValue3:0.00}") + "\n" + DebugLine(DebugValue.Mood, contentment, 0.01f, $"mood {contentment:0.00}") + "\n" + DebugLine(DebugValue.Familiarity, BestFamiliarity(out userId), 1f, GetFamiliarityDebugInfo()) + "\n" + GetHomeDebugInfo();
		if (Livestock.debugCondition)
		{
			text = text + "\n" + GetConditionDebugInfo();
		}
		if (Livestock.debugDayBudget)
		{
			text = text + "\n" + GetDayBudgetDebugInfo();
		}
		if (Livestock.debugGenes)
		{
			text = text + "\n" + GetGenesDebugInfo();
		}
		if (Livestock.debugGait && TryGetComponent<RustNavMeshAgent>(out var component))
		{
			text += $"\nwalk {component.walkSpeed:0.0} gait {component.gaitPenalty:+0;-0;0}";
		}
		return text;
	}

	private void SetLeading(bool leading)
	{
		SetNetworkedFlag(Flags.Reserved7, leading);
	}

	public void SetGrazing(bool grazing)
	{
		SetHeadDownFlag(Flags.Reserved8, grazing);
	}

	public void SetGrazingFromTrough(HitchTrough trough)
	{
		grazingTrough = trough;
	}

	public void SetDrinking(bool drinking)
	{
		SetHeadDownFlag(Flags.Reserved13, drinking);
	}

	private void SetHeadDownFlag(Flags flag, bool down)
	{
		if (HasFlag(flag) != down)
		{
			if (!down)
			{
				headComingUpFor = 0f;
			}
			SetNetworkedFlag(flag, down);
		}
	}

	public void SetMating(bool mating)
	{
		SetNetworkedFlag(Flags.Reserved12, mating);
	}

	public void SetResting(bool resting)
	{
		if (IsResting() != resting)
		{
			if (resting)
			{
				poseHeldFor = 0f;
			}
			else
			{
				StartGettingUp(LieDownDuration, StandUpDuration);
			}
			SetNetworkedFlag(Flags.Reserved14, resting);
		}
	}

	public void SetLyingIn(bool lyingIn)
	{
		if (IsLyingIn() != lyingIn)
		{
			if (lyingIn)
			{
				poseHeldFor = 0f;
			}
			else
			{
				StartGettingUp(LieDownDuration, StandUpDuration);
			}
			SetNetworkedFlag(Flags.Reserved17, lyingIn);
		}
	}

	public void SetFrisky(bool frisky)
	{
		if (IsFrisky() != frisky)
		{
			SetNetworkedFlag(Flags.Reserved15, frisky);
		}
	}

	public void SetSleeping(bool sleeping)
	{
		if (IsSleeping() != sleeping)
		{
			if (sleeping)
			{
				poseHeldFor = 0f;
			}
			else
			{
				StartGettingUp(BedDownDuration, WakeUpDuration);
			}
			SetNetworkedFlag(Flags.Reserved11, sleeping);
		}
	}

	public void SetNeedValue(Need need, float value)
	{
		if ((int)need < needs.Length)
		{
			needs[(int)need].NeedValue = Mathf.Clamp01(value);
		}
	}

	private void SetNetworkedFlag(Flags flag, bool value)
	{
		using FlagsUpdateScope flagsUpdateScope = StartSetFlags(FlagsUpdateMode.SendNetworkUpdate);
		flagsUpdateScope.Set(flag, value);
	}

	public void StartBreedingCooldown()
	{
		breedCooldown.Start(Livestock.breedCooldown / GeneScale(LivestockGene.Fertility));
	}

	public float CalvingCooldown()
	{
		float num = ((Species != null) ? Species.ChildTimeToGrow : 0f);
		if (num <= 0f)
		{
			return Livestock.breedCooldown;
		}
		return num * Mathf.Max(0f, Livestock.ageScale) * (1f + Mathf.Clamp01(GrowTimeVariance)) * Mathf.Max(0f, Livestock.calvingCooldownScale) / GeneScale(LivestockGene.Fertility);
	}

	public void StartCalvingCooldown()
	{
		breedCooldown.Start(CalvingCooldown());
	}

	public bool CanBePregnant()
	{
		if (IsAdult() && IsFemale && !IsDead() && !IsPregnant() && !OnBreedingCooldown)
		{
			return MayBreed;
		}
		return false;
	}

	public bool CanBreed()
	{
		if (IsAdult() && IsMale && !IsDead() && !IsLeading() && !OnBreedingCooldown)
		{
			return MayBreed;
		}
		return false;
	}

	private static EntityRef<T> RefTo<T>(T entity) where T : BaseEntity
	{
		EntityRef<T> result = default(EntityRef<T>);
		result.Set(entity);
		return result;
	}

	public bool MakePregnant(LivestockAnimal partner)
	{
		if (!CanBePregnant())
		{
			return false;
		}
		PregnantPartner = RefTo(partner);
		PregnantPartnerGenes = ((partner != null) ? partner.Genes : 0);
		pregnancyTimer.Start(PregnantDuration);
		if (partner != null)
		{
			partner.StartBreedingCooldown();
		}
		return true;
	}

	public bool ForceGiveBirth()
	{
		if (IsDead() || !IsPregnant())
		{
			return false;
		}
		pregnancyTimer.Stop();
		return GiveBirth();
	}

	public void FinishPregnancy()
	{
		hasCalved = false;
		SetLyingIn(lyingIn: false);
		SetNetworkedFlag(Flags.Reserved10, value: false);
	}

	private bool GiveBirth()
	{
		hasCalved = true;
		LivestockAnimal partner = PregnantPartner.Get(serverside: true);
		int pregnantPartnerGenes = PregnantPartnerGenes;
		PregnantPartner = default(EntityRef<LivestockAnimal>);
		PregnantPartnerGenes = 0;
		StartCalvingCooldown();
		int num = 0;
		for (int i = 0; i < LitterSize(); i++)
		{
			if (BirthOne(partner, pregnantPartnerGenes))
			{
				num++;
			}
		}
		return num > 0;
	}

	private bool BirthOne(LivestockAnimal partner, int partnerGenes)
	{
		bool flag = UnityEngine.Random.Range(0f, 1f) > 0.5f;
		GameObjectRef gameObjectRef = ((Species != null) ? Species.ChildFor(flag) : null);
		if (gameObjectRef == null || !gameObjectRef.isValid)
		{
			Debug.LogWarning(Categorize() + " (" + base.PrefabName + ") gave birth but its Species asset names no child prefab.");
			return false;
		}
		BaseEntity baseEntity = base.gameManager.CreateEntity(gameObjectRef.resourcePath, GetBirthPosition());
		if (baseEntity == null)
		{
			return false;
		}
		if (baseEntity is LivestockAnimal livestockAnimal)
		{
			livestockAnimal.MarkNewborn();
			livestockAnimal.SetSleeping(sleeping: true);
		}
		baseEntity.Spawn();
		if (baseEntity is LivestockAnimal livestockAnimal2)
		{
			if (livestockAnimal2.Form == SexForm.Either)
			{
				livestockAnimal2.IsMale = flag;
				livestockAnimal2.AnimalName = RollAnimalName(flag, out var locale);
				livestockAnimal2.FoundLine(locale);
			}
			livestockAnimal2.Genes = InheritedGenome(partnerGenes);
			livestockAnimal2.TakeHomeFrom(this);
			PassBondsTo(livestockAnimal2);
			livestockAnimal2.RegisterParentage(this, partner);
			if (!livestockAnimal2.TryNameFromCross(this, partner))
			{
				livestockAnimal2.TryInheritName(this, partner);
			}
			newbornCalf.Set(livestockAnimal2);
			livestockAnimal2.SendNetworkUpdateImmediate();
		}
		LivestockCensus.RecordBirth(base.transform.position);
		BuildingPrivlidge cupboard;
		if (BestFamiliarity(out var userId) > 0f && userId != 0)
		{
			Analytics.Azure.OnLivestockBirth("best_familiarity", baseEntity, userId);
		}
		else if (TryGetHomeCupboard(out cupboard))
		{
			Analytics.Azure.OnLivestockBirth("home_cupboard", baseEntity, cupboard.authorizedPlayers);
		}
		else
		{
			Analytics.Azure.OnLivestockBirth("none", baseEntity, (ulong?)null);
		}
		return true;
	}

	private void RegisterParentage(LivestockAnimal mother, LivestockAnimal father)
	{
		Father.Set(father);
		Mother.Set(mother);
	}

	private Vector3 GetBirthPosition()
	{
		if (!TryGetComponent<RustNavMeshAgent>(out var component))
		{
			return base.transform.position;
		}
		using PooledList<NavVector3> pooledList = Facepunch.Pool.Get<PooledList<NavVector3>>();
		bool flag = Eqs.SampleNavigablePositions(component, component.nextPosition, pooledList, ChildSpawnDistance, ChildSpawnDistance, 8);
		foreach (NavVector3 item in pooledList)
		{
			if (flag)
			{
				return component.NavToWorldSpace(item);
			}
			if (component.SamplePosition(item, out var hitNS, ChildSpawnDistance) && component.CanReach(hitNS.position))
			{
				return component.NavToWorldSpace(hitNS.position);
			}
		}
		return base.transform.position;
	}

	public bool NeedsFood()
	{
		return Fullness.NeedValue <= ConsumeThreshold * ExpectedFullnessCeiling;
	}

	public void RecordHelpingSource(bool fromTrough)
	{
		lastHelpingFromTrough = fromTrough;
	}

	public void FindFoodTroughs(List<BaseEntity> results)
	{
		if (ConsumeSearchRadius <= 0f)
		{
			return;
		}
		using PooledList<HitchTrough> pooledList = Facepunch.Pool.Get<PooledList<HitchTrough>>();
		Query.Server.GetInSphere(base.transform.position, ConsumeSearchRadius, pooledList);
		foreach (HitchTrough item in pooledList)
		{
			if (CanEatFrom(item, out var _))
			{
				results.Add(item);
			}
		}
	}

	public bool CanEatFrom(HitchTrough trough, out Item foundItem)
	{
		foundItem = null;
		if (trough == null || trough.IsDestroyed || trough.IsDead())
		{
			return false;
		}
		foreach (Item item in trough.inventory.itemList)
		{
			if (item.info.TryGetComponent<ItemModConsumable>(out var component) && component.chickenCoopFood && TroughFullnessOf(component) > 0f)
			{
				foundItem = item;
				return true;
			}
		}
		return false;
	}

	private float TroughFullnessOf(ItemModConsumable food)
	{
		return food.GetIfType(MetabolismAttribute.Type.Calories) * TroughFullnessPerCalorie;
	}

	public bool ConsumeFromTrough(HitchTrough trough)
	{
		if (!CanEatFrom(trough, out var foundItem))
		{
			return false;
		}
		troughFullnessStored += TroughFullnessOf(foundItem.info.GetComponent<ItemModConsumable>());
		foundItem.UseItem();
		LivestockCensus.RecordTroughItemEaten(base.transform.position);
		return true;
	}

	public float TakeTroughFullness(float wanted)
	{
		while (troughFullnessStored < wanted && ConsumeFromTrough(grazingTrough))
		{
		}
		float num = Mathf.Min(wanted, troughFullnessStored);
		troughFullnessStored -= num;
		return num;
	}

	private void OnSyncVar_IsMale(bool? oldValue, bool newValue)
	{
	}

	private void OnSyncVar_AnimalName(string? oldValue, string newValue)
	{
		if (base.isServer)
		{
			ForgetSpecial(oldValue);
			RememberSpecial(newValue, discovered: false);
		}
	}

	private void OnSyncVar_LeadingPlayer(EntityRef<BasePlayer>? oldValue, EntityRef<BasePlayer> newValue)
	{
	}

	private void OnSyncVar_Genes(int? oldValue, int newValue)
	{
	}

	protected override bool WriteSyncVar(byte id, NetWrite writer)
	{
		switch (id)
		{
		case 0:
			if (ConVar.Global.developer > 2)
			{
				NetworkableId iD = net.ID;
				Debug.Log("SyncVar Writing: IsMale for " + iD.ToString());
			}
			SyncVarNetWrite(writer, __sync_IsMale);
			return true;
		case 1:
			if (ConVar.Global.developer > 2)
			{
				NetworkableId iD = net.ID;
				Debug.Log("SyncVar Writing: PregnantPartner for " + iD.ToString());
			}
			SyncVarNetWrite(writer, __sync_PregnantPartner);
			return true;
		case 2:
			if (ConVar.Global.developer > 2)
			{
				NetworkableId iD = net.ID;
				Debug.Log("SyncVar Writing: AnimalName for " + iD.ToString());
			}
			SyncVarNetWrite(writer, __sync_AnimalName);
			return true;
		case 3:
			if (ConVar.Global.developer > 2)
			{
				NetworkableId iD = net.ID;
				Debug.Log("SyncVar Writing: LeadingPlayer for " + iD.ToString());
			}
			SyncVarNetWrite(writer, __sync_LeadingPlayer);
			return true;
		case 4:
			if (ConVar.Global.developer > 2)
			{
				NetworkableId iD = net.ID;
				Debug.Log("SyncVar Writing: HomeTc for " + iD.ToString());
			}
			SyncVarNetWrite(writer, __sync_HomeTc);
			return true;
		case 5:
			if (ConVar.Global.developer > 2)
			{
				NetworkableId iD = net.ID;
				Debug.Log("SyncVar Writing: Genes for " + iD.ToString());
			}
			SyncVarNetWrite(writer, __sync_Genes);
			return true;
		case 6:
			if (ConVar.Global.developer > 2)
			{
				NetworkableId iD = net.ID;
				Debug.Log("SyncVar Writing: PregnantPartnerGenes for " + iD.ToString());
			}
			SyncVarNetWrite(writer, __sync_PregnantPartnerGenes);
			return true;
		default:
			return base.WriteSyncVar(id, writer);
		}
	}

	protected override bool OnSyncVar(byte id, NetRead reader, bool fromAutoSave = false)
	{
		switch (id)
		{
		case 0:
			try
			{
				bool? oldValue3 = __sync_IsMale;
				bool newValue3 = (__sync_IsMale = reader.Bool());
				if (fromAutoSave)
				{
					oldValue3 = null;
				}
				OnSyncVar_IsMale(oldValue3, newValue3);
			}
			catch (Exception exception6)
			{
				Debug.LogException(exception6);
			}
			return true;
		case 1:
			try
			{
				_ = __sync_PregnantPartner;
				EntityRef<LivestockAnimal> _sync_PregnantPartner = reader.EntityRef<LivestockAnimal>();
				__sync_PregnantPartner = _sync_PregnantPartner;
			}
			catch (Exception exception2)
			{
				Debug.LogException(exception2);
			}
			return true;
		case 2:
			try
			{
				string oldValue2 = __sync_AnimalName;
				string newValue2 = (__sync_AnimalName = reader.String());
				if (fromAutoSave)
				{
					oldValue2 = null;
				}
				OnSyncVar_AnimalName(oldValue2, newValue2);
			}
			catch (Exception exception4)
			{
				Debug.LogException(exception4);
			}
			return true;
		case 3:
			try
			{
				EntityRef<BasePlayer>? oldValue4 = __sync_LeadingPlayer;
				EntityRef<BasePlayer> newValue4 = (__sync_LeadingPlayer = reader.EntityRef<BasePlayer>());
				if (fromAutoSave)
				{
					oldValue4 = null;
				}
				OnSyncVar_LeadingPlayer(oldValue4, newValue4);
			}
			catch (Exception exception7)
			{
				Debug.LogException(exception7);
			}
			return true;
		case 4:
			try
			{
				_ = __sync_HomeTc;
				EntityRef<BuildingPrivlidge> _sync_HomeTc = reader.EntityRef<BuildingPrivlidge>();
				__sync_HomeTc = _sync_HomeTc;
			}
			catch (Exception exception5)
			{
				Debug.LogException(exception5);
			}
			return true;
		case 5:
			try
			{
				int? oldValue = __sync_Genes;
				int newValue = (__sync_Genes = reader.Int32());
				if (fromAutoSave)
				{
					oldValue = null;
				}
				OnSyncVar_Genes(oldValue, newValue);
			}
			catch (Exception exception3)
			{
				Debug.LogException(exception3);
			}
			return true;
		case 6:
			try
			{
				_ = __sync_PregnantPartnerGenes;
				int _sync_PregnantPartnerGenes = reader.Int32();
				__sync_PregnantPartnerGenes = _sync_PregnantPartnerGenes;
			}
			catch (Exception exception)
			{
				Debug.LogException(exception);
			}
			return true;
		default:
			return base.OnSyncVar(id, reader, fromAutoSave);
		}
	}

	private byte __GetWeaverID(string propertyName)
	{
		return propertyName switch
		{
			"IsMale" => 0, 
			"PregnantPartner" => 1, 
			"AnimalName" => 2, 
			"LeadingPlayer" => 3, 
			"HomeTc" => 4, 
			"Genes" => 5, 
			"PregnantPartnerGenes" => 6, 
			_ => byte.MaxValue, 
		};
	}

	protected override void WriteAutoSaveSyncVars(NetWrite writer)
	{
		base.WriteAutoSaveSyncVars(writer);
		WriteSyncVar(0, writer);
		WriteSyncVar(1, writer);
		WriteSyncVar(2, writer);
		WriteSyncVar(3, writer);
		WriteSyncVar(4, writer);
		WriteSyncVar(5, writer);
		WriteSyncVar(6, writer);
	}

	protected override void ReadAutoSaveSyncVars(NetRead reader)
	{
		base.ReadAutoSaveSyncVars(reader);
		OnSyncVar(0, reader, fromAutoSave: true);
		OnSyncVar(1, reader, fromAutoSave: true);
		OnSyncVar(2, reader, fromAutoSave: true);
		OnSyncVar(3, reader, fromAutoSave: true);
		OnSyncVar(4, reader, fromAutoSave: true);
		OnSyncVar(5, reader, fromAutoSave: true);
		OnSyncVar(6, reader, fromAutoSave: true);
	}

	protected override bool AutoSaveSyncVars(SaveInfo save)
	{
		NetWrite obj = Network.Net.sv.StartWrite();
		WriteAutoSaveSyncVars(obj);
		(byte[] Buffer, int Length) buffer = obj.GetBuffer();
		byte[] item = buffer.Buffer;
		int item2 = buffer.Length;
		byte[] array = _autosaveBuffer;
		if (array == null || array.Length < item2)
		{
			byte[] array2 = BaseEntity._autosaveBufferPool.Rent(item2);
			while (array == null || array.Length < item2)
			{
				byte[] array3 = Interlocked.CompareExchange(ref _autosaveBuffer, array2, array);
				if (array3 == array)
				{
					if (array3 != null)
					{
						BaseEntity._autosaveBufferPool.Return(array3);
					}
					array = array2;
					break;
				}
				array = array3;
			}
			if (array != array2)
			{
				BaseEntity._autosaveBufferPool.Return(array2);
			}
		}
		Buffer.BlockCopy(item, 0, array, 0, item2);
		save.msg.baseEntity.syncVars = array;
		Facepunch.Pool.Free(ref obj);
		return true;
	}

	protected override bool AutoLoadSyncVars(LoadInfo load)
	{
		if (load.msg.baseEntity != null && load.msg.baseEntity.syncVars != null)
		{
			NetRead obj = Facepunch.Pool.Get<NetRead>();
			obj.Init(load.msg.baseEntity.syncVars.AsSpan());
			ReadAutoSaveSyncVars(obj);
			Facepunch.Pool.Free(ref obj);
		}
		return true;
	}

	protected override void ResetSyncVars()
	{
		base.ResetSyncVars();
		__sync_IsMale = false;
		__sync_PregnantPartner = default(EntityRef<LivestockAnimal>);
		__sync_AnimalName = null;
		__sync_LeadingPlayer = default(EntityRef<BasePlayer>);
		__sync_HomeTc = default(EntityRef<BuildingPrivlidge>);
		__sync_Genes = 0;
		__sync_PregnantPartnerGenes = 0;
	}

	protected override bool ShouldInvalidateCache(byte id)
	{
		return id switch
		{
			0 => true, 
			1 => true, 
			2 => true, 
			3 => true, 
			4 => true, 
			5 => true, 
			6 => true, 
			_ => base.ShouldInvalidateCache(id), 
		};
	}
}

