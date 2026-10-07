using System;
using System.Collections.Generic;
using ConVar;
using Development.Attributes;
using Facepunch;
using UnityEngine;
using UnityEngine.Events;

namespace Rust.Ai.Gen2;

public class SenseComponent : EntityComponent<BaseEntity>, IServerComponent
{
	[Serializable]
	public struct Cone
	{
		public float halfAngle;

		public float range;

		public Cone(float halfAngle = 80f, float range = 10f)
		{
			this.halfAngle = halfAngle;
			this.range = range;
		}
	}

	public class VisibilityStatus : Facepunch.Pool.IPooled
	{
		private const float maxPredictionTime = 1f;

		private bool isFirstAware;

		private BaseEntity baseEntity;

		private BaseEntity targetEntity;

		public Vector3 lastKnownPosition;

		public Vector3 predictedPosition;

		private const float maxClarity = 2f;

		private const float waterCheckInterval = 1f;

		private double? lastTimeInWaterUpdated;

		private double? lastTimeSurprised;

		public float clarity { get; private set; }

		public bool IsAware => clarity >= 1f;

		public float Accuracy
		{
			get
			{
				if (!IsAware)
				{
					return 0f;
				}
				return Mathx.RemapValClamped(clarity, 1f, 2f, 0f, 1f);
			}
		}

		public float timeVisible { get; private set; }

		public float timeNotVisible { get; private set; }

		public bool IsVisible => timeVisible > 0f;

		public float timeAwareAndVisible { get; private set; }

		public float timeNotAwareAndVisible { get; private set; }

		public float timeWatched { get; private set; }

		public float timeNotWatched { get; private set; }

		public float timeAimedAt { get; private set; }

		public float timeNotAimedAt { get; private set; }

		public WaterLevel.WaterInfo? lastWaterInfo { get; private set; }

		public bool isInWaterCached
		{
			get
			{
				if (!targetEntity.ToNonNpcPlayer(out var player))
				{
					return false;
				}
				if (!lastWaterInfo.HasValue || !lastTimeInWaterUpdated.HasValue || UnityEngine.Time.timeAsDouble - lastTimeInWaterUpdated > 1.0)
				{
					BaseMountable castedUnityObject;
					Vector3 vector = (player.GetMounted().Is<BaseMountable>(out castedUnityObject) ? (Vector3.down * 0.5f) : Vector3.zero);
					lastWaterInfo = WaterLevel.GetWaterInfo(targetEntity.transform.position + vector, waves: false, volumes: false);
					lastTimeInWaterUpdated = UnityEngine.Time.timeAsDouble;
				}
				return lastWaterInfo.Value.currentDepth >= 0.3f;
			}
		}

		public bool IsCamping { get; private set; }

		public bool TryConsumeSurprise()
		{
			if (!lastTimeSurprised.HasValue)
			{
				return false;
			}
			lastTimeSurprised = null;
			return true;
		}

		private void Reset()
		{
			isFirstAware = true;
			targetEntity = null;
			baseEntity = null;
			timeAwareAndVisible = 0f;
			timeNotAwareAndVisible = 100f;
			timeWatched = 0f;
			timeNotWatched = 100f;
			timeAimedAt = 0f;
			timeNotAimedAt = 100f;
			timeVisible = 0f;
			timeNotVisible = 100f;
			lastKnownPosition = Vector3.zero;
			predictedPosition = Vector3.zero;
			lastWaterInfo = null;
			lastTimeInWaterUpdated = null;
			lastTimeSurprised = null;
			IsCamping = false;
			clarity = 0f;
		}

		public void EnterPool()
		{
			Reset();
		}

		public void LeavePool()
		{
			Reset();
		}

		[PoolAnalyzerGetWrapper]
		public static VisibilityStatus GetFromPool(BaseEntity baseEntity, BaseEntity targetEntity, bool isVisible, float deltaTime, float clarityGainSpeed, Vector3? lastKnownPositionOverride = null, float? minClarity = null)
		{
			VisibilityStatus visibilityStatus = Facepunch.Pool.Get<VisibilityStatus>();
			visibilityStatus.baseEntity = baseEntity;
			visibilityStatus.targetEntity = targetEntity;
			visibilityStatus.UpdateVisibility(isVisible, deltaTime, clarityGainSpeed, lastKnownPositionOverride, minClarity);
			return visibilityStatus;
		}

		private bool CheckValid()
		{
			if (!baseEntity.IsValid() || !targetEntity.IsValid())
			{
				if (AI.logIssues)
				{
					Debug.LogError($"SenseComponent:UpdateVisibility NRE: {baseEntity} {targetEntity}");
				}
				return false;
			}
			return true;
		}

		public void UpdateVisibility(bool newVisibility, float deltaTime, float clarityGainSpeed, Vector3? lastKnownPositionOverride = null, float? minClarity = null)
		{
			if (!CheckValid())
			{
				return;
			}
			bool isAware = IsAware;
			Vector3 vector = lastKnownPosition;
			if (minClarity.HasValue)
			{
				clarity = Mathf.Max(clarity, minClarity.Value);
			}
			else if (clarityGainSpeed > 0f)
			{
				clarity += clarityGainSpeed * (deltaTime / 1f);
			}
			else
			{
				clarity -= deltaTime / 3f;
			}
			clarity = Mathf.Clamp(clarity, 0f, 2f);
			bool flag = clarity >= 1f;
			if (lastKnownPositionOverride.HasValue)
			{
				lastKnownPosition = lastKnownPositionOverride.Value;
				predictedPosition = lastKnownPositionOverride.Value;
			}
			else if (newVisibility && flag)
			{
				lastKnownPosition = targetEntity.transform.position;
				predictedPosition = targetEntity.transform.position;
			}
			if (timeNotAwareAndVisible < 1f)
			{
				predictedPosition = targetEntity.transform.position;
			}
			if (isFirstAware || (!isAware && flag && ShouldBeSurprised(vector)))
			{
				lastTimeSurprised = UnityEngine.Time.timeAsDouble;
			}
			if (lastTimeSurprised.HasValue && UnityEngine.Time.timeAsDouble - lastTimeSurprised.Value > 3.0)
			{
				lastTimeSurprised = null;
			}
			if (!isFirstAware && !isAware && flag && timeNotVisible >= 2f && timeNotVisible < 15f)
			{
				float num = Vector3.Distance(vector, lastKnownPosition);
				IsCamping = num < 6f;
			}
			if (newVisibility)
			{
				timeNotVisible = 0f;
				timeVisible += deltaTime;
			}
			else
			{
				timeVisible = 0f;
				timeNotVisible += deltaTime;
				timeAwareAndVisible = 0f;
				timeNotAwareAndVisible += deltaTime;
			}
			if (flag)
			{
				if (newVisibility)
				{
					timeNotAwareAndVisible = 0f;
					timeAwareAndVisible += deltaTime;
				}
				Vector3 lhs = targetEntity.transform.forward;
				Vector3 position = targetEntity.transform.position;
				if (targetEntity.ToNonNpcPlayer(out var player))
				{
					lhs = player.eyes.HeadForward();
					position = player.eyes.position;
				}
				float num2 = Mathf.Acos(Vector3.Dot(lhs, (baseEntity.transform.position - position).normalized)) * 57.29578f * 2f;
				bool num3 = num2 < AI.watchedAngle;
				if (num3)
				{
					timeNotWatched = 0f;
					timeWatched += deltaTime;
				}
				else
				{
					timeWatched = 0f;
					timeNotWatched += deltaTime;
				}
				if (num3 && player != null && player.modelState.aiming && num2 < AI.aimedAtAngle && !(player.GetHeldEntity() is BaseMelee { canScareAiWhenAimed: false }))
				{
					timeNotAimedAt = 0f;
					timeAimedAt += deltaTime;
				}
				else
				{
					timeAimedAt = 0f;
					timeNotAimedAt += deltaTime;
				}
				isFirstAware = false;
			}
			else
			{
				timeWatched = 0f;
				timeNotWatched += deltaTime;
				timeAimedAt = 0f;
				timeNotAimedAt += deltaTime;
			}
		}

		private bool ShouldBeSurprised(Vector3 previousLastKnownPosition)
		{
			if (timeNotAwareAndVisible <= 4f)
			{
				return false;
			}
			if (Vector3.Angle(baseEntity.transform.forward, lastKnownPosition - baseEntity.transform.position) > 45f)
			{
				return true;
			}
			if (Vector3.Distance(previousLastKnownPosition, lastKnownPosition) > 20f)
			{
				return true;
			}
			return false;
		}
	}

	[SerializeField]
	private Vector3 LongRangeVisionRectangle = new Vector3(6f, 30f, 60f);

	[SerializeField]
	private Cone ShortRangeVisionCone = new Cone(100f, 30f);

	[SerializeField]
	private float touchDistance = 6f;

	[SerializeField]
	private float noiseRangeMultiplier = 1f;

	[SerializeField]
	private float hearingRange = 50f;

	[SerializeField]
	private NPCTeam team;

	public ResettableFloat timeToForgetSightings = new ResettableFloat(30f);

	private const float timeToForgetNoises = 5f;

	private static HashSet<BaseEntity> entitiesUpdatedThisFrame = new HashSet<BaseEntity>();

	[ServerVar]
	public static float minRefreshIntervalSeconds = 0.2f;

	[ServerVar]
	public static float maxRefreshIntervalSeconds = 1f;

	private double? _lastTickTime;

	private double nextRefreshTime;

	private ISenseObserver observer;

	private bool observerResolved;

	private double spawnTime;

	private Dictionary<BaseEntity, double> _alliesWeAreAwareOf = new Dictionary<BaseEntity, double>(3);

	private Dictionary<BaseEntity, VisibilityStatus> entitiesWeAreAwareOf = new Dictionary<BaseEntity, VisibilityStatus>(8);

	private Dictionary<BaseEntity, double> recentAttackers = new Dictionary<BaseEntity, double>(2);

	private BaseEntity Target;

	private RustNavMeshAgent _agent;

	private static readonly float lookDistanceThresholdSq = Mathf.Pow(15f, 2f);

	private static readonly float lookBehindDotThreshold = Mathf.Cos(MathF.PI / 3f);

	public static readonly Dictionary<NpcNoiseIntensity, float> noiseRadii = new Dictionary<NpcNoiseIntensity, float>
	{
		{
			NpcNoiseIntensity.None,
			0f
		},
		{
			NpcNoiseIntensity.Low,
			10f
		},
		{
			NpcNoiseIntensity.Medium,
			20f
		},
		{
			NpcNoiseIntensity.High,
			50f
		}
	};

	private HashSet<NpcNoiseEvent> noises = new HashSet<NpcNoiseEvent>();

	[SerializeField]
	private float foodDetectionRange = 30f;

	private BaseEntity _nearestFood;

	private HashSet<BaseEntity> ignoredFood = new HashSet<BaseEntity>();

	[SerializeField]
	private float fireDetectionRange = 20f;

	[NonSerialized]
	public UnityEvent onFireMelee = new UnityEvent();

	private BaseEntity _nearestFire;

	private double? lastMeleeTime;

	[SerializeField]
	private float TargetingCooldown = 5f;

	[SerializeField]
	private float SwitchTargetToFocusAggressorCooldown = 5f;

	private LockState lockState = new LockState();

	private double? lastTargetTime;

	private double? lastTimeSwitchedTargetToFocusAggressor;

	public float SenseRadius => LongRangeVisionRectangle.z;

	public NPCTeam Team => team;

	public float RefreshInterval
	{
		get
		{
			if (!ShouldRefreshFast)
			{
				return maxRefreshIntervalSeconds;
			}
			return minRefreshIntervalSeconds;
		}
	}

	private double LastTickTime
	{
		get
		{
			double valueOrDefault = _lastTickTime.GetValueOrDefault();
			if (!_lastTickTime.HasValue)
			{
				valueOrDefault = UnityEngine.Time.timeAsDouble;
				_lastTickTime = valueOrDefault;
				return valueOrDefault;
			}
			return valueOrDefault;
		}
		set
		{
			_lastTickTime = value;
		}
	}

	public bool HasPlayerInVicinity { get; private set; }

	public bool ShouldRefreshFast
	{
		get
		{
			if (!HasPlayerInVicinity)
			{
				if (Target != null)
				{
					return Target.IsNonNpcPlayer();
				}
				return false;
			}
			return true;
		}
	}

	public Vector3 EyeOffset => EyePosition - base.baseEntity.transform.position;

	private ISenseObserver Observer
	{
		get
		{
			if (!observerResolved)
			{
				observerResolved = true;
				observer = base.baseEntity as ISenseObserver;
			}
			return observer;
		}
	}

	private RustNavMeshAgent Agent => _agent ?? (_agent = base.baseEntity.GetComponent<RustNavMeshAgent>());

	public Vector3 EyePosition
	{
		get
		{
			using (TimeWarning.New("SenseComponent:ClientEyePosition"))
			{
				if (!base.baseEntity.Is<ScientistNPC2>(out var _))
				{
					return base.baseEntity.CenterPoint();
				}
				Vector3 eyeOffset = PlayerEyes.EyeOffset;
				if (base.baseEntity.HasFlag(BaseEntity.Flags.Reserved5))
				{
					eyeOffset += PlayerEyes.DuckOffset;
				}
				return base.baseEntity.transform.position + eyeOffset;
			}
		}
	}

	private bool IsInCombat => base.baseEntity.HasFlag(BaseEntity.Flags.Reserved3);

	private bool ChangedTargetRecently
	{
		get
		{
			if (Target != null && lastTargetTime.HasValue)
			{
				return UnityEngine.Time.timeAsDouble - lastTargetTime.Value < (double)TargetingCooldown;
			}
			return false;
		}
	}

	private bool SwitchedTargetToFocusAggressorRecently
	{
		get
		{
			if (Target != null && lastTimeSwitchedTargetToFocusAggressor.HasValue)
			{
				return UnityEngine.Time.timeAsDouble - lastTimeSwitchedTargetToFocusAggressor.Value < (double)SwitchTargetToFocusAggressorCooldown;
			}
			return false;
		}
	}

	public void GetPerceivedAllies(List<BaseEntity> allies)
	{
		using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
		foreach (var (baseEntity2, _) in _alliesWeAreAwareOf)
		{
			if (!baseEntity2.IsValid() || (baseEntity2.Is<BaseCombatEntity>(out var castedUnityObject) && castedUnityObject.IsDead()))
			{
				pooledList.Add(baseEntity2);
			}
			else
			{
				allies.Add(baseEntity2);
			}
		}
		foreach (BaseEntity item in pooledList)
		{
			_alliesWeAreAwareOf.Remove(item);
		}
	}

	public void GetInitialAllies(List<BaseEntity> allies)
	{
		using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
		foreach (var (baseEntity2, num2) in _alliesWeAreAwareOf)
		{
			if (!baseEntity2.IsValid() || (baseEntity2.Is<BaseCombatEntity>(out var castedUnityObject) && castedUnityObject.IsDead()))
			{
				pooledList.Add(baseEntity2);
			}
			else if (!(num2 - spawnTime > (double)(maxRefreshIntervalSeconds * 2f)))
			{
				allies.Add(baseEntity2);
			}
		}
		foreach (BaseEntity item in pooledList)
		{
			_alliesWeAreAwareOf.Remove(item);
		}
	}

	public static Vector3 GetEntityLineOfSightTestPoint(BaseEntity entity, bool ignoreCrouch = true)
	{
		return GetEntityLineOfSightTestPoint(entity, entity.transform.position, ignoreCrouch);
	}

	public static Vector3 GetEntityLineOfSightTestPoint(BaseEntity entity, Vector3 entityPosition, bool ignoreCrouch = true)
	{
		if (entity.ToNonNpcPlayer(out var player))
		{
			if (ignoreCrouch)
			{
				return entityPosition + PlayerEyes.EyeOffset.y * Vector3.up;
			}
			return entityPosition + (player.eyes.position - player.transform.position);
		}
		return entityPosition + entity.bounds.size.y * Vector3.up;
	}

	public bool FindTargetLKP(out Vector3 lkp, bool applyHeightOffset = false, bool predict = false, bool ignoreCrouch = true)
	{
		if (!FindTarget(out var target))
		{
			lkp = Vector3.zero;
			return false;
		}
		return FindLKP(target, out lkp, applyHeightOffset, predict, ignoreCrouch);
	}

	public bool FindLKP(BaseEntity entity, out Vector3 lkp, bool applyHeightOffset = false, bool predict = false, bool ignoreCrouch = true)
	{
		if (!GetVisibilityStatus(entity, out var status))
		{
			lkp = Vector3.zero;
			return false;
		}
		if (status.IsVisible && status.IsAware)
		{
			lkp = entity.transform.position;
		}
		else
		{
			lkp = (predict ? status.predictedPosition : status.lastKnownPosition);
		}
		if (applyHeightOffset)
		{
			lkp = GetEntityLineOfSightTestPoint(entity, lkp, ignoreCrouch);
		}
		return true;
	}

	public bool GetVisibilityStatus(BaseEntity entity, out VisibilityStatus status)
	{
		status = null;
		if (!CanTarget(entity))
		{
			return false;
		}
		if (!entitiesWeAreAwareOf.TryGetValue(entity, out status))
		{
			return false;
		}
		return true;
	}

	public bool Forget(BaseEntity entity)
	{
		if (!entitiesWeAreAwareOf.TryGetValue(entity, out var value))
		{
			return false;
		}
		entitiesWeAreAwareOf.Remove(entity);
		Facepunch.Pool.Free(ref value);
		return true;
	}

	public bool IsVisible(BaseEntity entity)
	{
		if (!GetVisibilityStatus(entity, out var status))
		{
			return false;
		}
		return status.IsVisible;
	}

	public void GetSeenEntities(List<BaseEntity> perceivedEntities)
	{
		using (TimeWarning.New("SenseComponent:GetSeenEntities"))
		{
			foreach (BaseEntity key in entitiesWeAreAwareOf.Keys)
			{
				if (IsVisible(key))
				{
					perceivedEntities.Add(key);
				}
			}
		}
	}

	public void GetOncePerceivedEntities(List<BaseEntity> perceivedEntities)
	{
		foreach (BaseEntity key in entitiesWeAreAwareOf.Keys)
		{
			if (GetVisibilityStatus(key, out var _))
			{
				perceivedEntities.Add(key);
			}
		}
	}

	public bool Trace(Vector3 source, Vector3 direction, out RaycastHit hitInfo, int layerMask, string debugCategory = "sight")
	{
		using (TimeWarning.New("Trace"))
		{
			return GamePhysics.Trace(new Ray(source, direction), 0f, out hitInfo, direction.magnitude, layerMask);
		}
	}

	public bool IsLineOccluded(Vector3 a, Vector3 b, int layerMask, string debugCategory = "sight")
	{
		using (TimeWarning.New("IsLineOccluded"))
		{
			RaycastHit hitInfo;
			return Trace(a, b - a, out hitInfo, layerMask, debugCategory);
		}
	}

	public bool CanSeeFromAt(Vector3 potentialLocation, Vector3 targetLocation, string debugCategory = "sight")
	{
		Vector3 vector = Vector3.Cross((targetLocation - potentialLocation).NormalizeXZ(), Vector3.up) * 0.5f * 2f;
		if (IsLineOccluded(potentialLocation, targetLocation + vector, 1218519041, debugCategory))
		{
			return !IsLineOccluded(potentialLocation, targetLocation - vector, 1218519041, debugCategory);
		}
		return true;
	}

	public bool CanBeSeenAtFrom(Vector3 potentialLocation, Vector3 targetLocation, string debugCategory = "sight")
	{
		Vector3 vector = Vector3.Cross((targetLocation - potentialLocation).NormalizeXZ(), Vector3.up) * 0.5f * 2f;
		if (IsLineOccluded(targetLocation, potentialLocation + vector, 1218519041, debugCategory))
		{
			return !IsLineOccluded(targetLocation, potentialLocation - vector, 1218519041, debugCategory);
		}
		return true;
	}

	public Matrix4x4 GetEyeTransform()
	{
		using (TimeWarning.New("SenseComponent:GetEyeTransform"))
		{
			Vector3 eyePosition = EyePosition;
			Quaternion q = base.baseEntity.transform.rotation;
			if (FindTargetLKP(out var lkp, applyHeightOffset: true, predict: false, ignoreCrouch: false) && !Agent.overrideDirectionWS.HasValue && (!base.baseEntity.Is<ScientistNPC2>(out var _) || !Agent.IsSprinting))
			{
				q = Quaternion.LookRotation(lkp - eyePosition, Vector3.up);
				float maxAngle = 90f;
				if (Vector3.Dot(q * Vector3.forward, base.baseEntity.transform.forward) < 0f - lookBehindDotThreshold)
				{
					maxAngle = ((!((lkp - base.baseEntity.transform.position).sqrMagnitude > lookDistanceThresholdSq)) ? 70f : 0f);
				}
				q = Clamp(base.baseEntity.transform.rotation, q, maxAngle);
			}
			return Matrix4x4.TRS(eyePosition, q, Vector3.one);
		}
	}

	public override void InitShared()
	{
		base.InitShared();
		spawnTime = UnityEngine.Time.timeAsDouble;
	}

	public override void Hurt(HitInfo hitInfo)
	{
		using (TimeWarning.New("SenseComponent:Hurt"))
		{
			BaseEntity initiator = hitInfo.Initiator;
			if (CanTarget(initiator))
			{
				Vector3 entityPositionGuess = initiator.transform.position + Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), Vector3.up) * Vector3.forward * 5f;
				SimulateSighting(initiator, entityPositionGuess);
				if (Target == null)
				{
					TrySetTarget(initiator, bypassCooldown: false);
				}
				else if (Target != initiator && !SwitchedTargetToFocusAggressorRecently && Vector3.Distance(base.baseEntity.transform.position, initiator.transform.position) < 50f && TrySetTarget(initiator))
				{
					lastTimeSwitchedTargetToFocusAggressor = UnityEngine.Time.timeAsDouble;
				}
			}
		}
	}

	public void SimulateSighting(BaseEntity entity, Vector3 entityPositionGuess)
	{
		if (entitiesWeAreAwareOf.TryGetValue(entity, out var value))
		{
			if (!value.IsVisible || !value.IsAware)
			{
				value.UpdateVisibility(newVisibility: false, 0.01f, 0f, entityPositionGuess, 1f);
			}
		}
		else
		{
			VisibilityStatus fromPool = VisibilityStatus.GetFromPool(base.baseEntity, entity, isVisible: false, 0.01f, 0f, entityPositionGuess, 1f);
			entitiesWeAreAwareOf.Add(entity, fromPool);
		}
	}

	public void Tick()
	{
		using (TimeWarning.New("SenseComponent:Tick"))
		{
			double timeAsDouble = UnityEngine.Time.timeAsDouble;
			if (timeAsDouble < nextRefreshTime)
			{
				return;
			}
			float deltaTime = (float)(timeAsDouble - LastTickTime);
			LastTickTime = timeAsDouble;
			HasPlayerInVicinity = false;
			entitiesUpdatedThisFrame.Clear();
			using (TimeWarning.New("SenseComponent:Tick:ProcessEntities"))
			{
				using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
				GetModifiedSenses(null, out var _, out var _, out var _, out var modLongVisionRectangle);
				BaseEntity.Query.Server.GetPlayersAndBrainsInSphere(base.baseEntity.transform.position, modLongVisionRectangle.z, pooledList, BaseEntity.Query.DistanceCheckType.None);
				foreach (BaseEntity item in pooledList)
				{
					if (item == base.baseEntity)
					{
						continue;
					}
					if (item.IsNonNpcPlayer())
					{
						HasPlayerInVicinity = true;
						if (Observer != null && item is BasePlayer player)
						{
							Observer.OnPlayerSensed(player, deltaTime);
						}
					}
					if (InSameTeam(item) && !_alliesWeAreAwareOf.ContainsKey(item))
					{
						_alliesWeAreAwareOf.Add(item, timeAsDouble);
					}
					if (CanTarget(item))
					{
						UpdateEntityVisibility(item, deltaTime);
					}
				}
			}
			using (TimeWarning.New("SenseComponent:Tick:RemoveEntities"))
			{
				using PooledList<BaseEntity> pooledList2 = Facepunch.Pool.Get<PooledList<BaseEntity>>();
				pooledList2.AddRange(entitiesWeAreAwareOf.Keys);
				foreach (BaseEntity item2 in pooledList2)
				{
					if (!entitiesWeAreAwareOf.TryGetValue(item2, out var value))
					{
						continue;
					}
					if (!CanTarget(item2))
					{
						if (Target.IsValid() && Target == item2)
						{
							ClearTarget(forget: false);
						}
						Forget(item2);
					}
					else if (!value.IsVisible && value.timeNotVisible > timeToForgetSightings.Value && !HurtUsRecently(item2))
					{
						if (Target.IsValid() && Target == item2)
						{
							ClearTarget(forget: false);
						}
						Forget(item2);
					}
					else if (!entitiesUpdatedThisFrame.Contains(item2))
					{
						if (IsInCombat)
						{
							UpdateEntityVisibility(item2, deltaTime);
						}
						else if (value.IsVisible)
						{
							entitiesWeAreAwareOf[item2].UpdateVisibility(newVisibility: false, deltaTime, 0f, null, null);
						}
					}
				}
				entitiesUpdatedThisFrame.Clear();
			}
			TickHearing(deltaTime);
			TickFoodDetection(deltaTime);
			TickFireDetection(deltaTime);
			ForgetOldAttackers();
			TickTargeting(deltaTime);
			nextRefreshTime = UnityEngine.Time.timeAsDouble + (double)RefreshInterval;
		}
	}

	private void GetModifiedSenses(BaseEntity entity, out float modTouchDistance, out float modHalfAngle, out float modShortVisionRange, out Vector3 modLongVisionRectangle)
	{
		modTouchDistance = touchDistance;
		modHalfAngle = ShortRangeVisionCone.halfAngle;
		modShortVisionRange = ShortRangeVisionCone.range;
		modLongVisionRectangle = LongRangeVisionRectangle;
		if (!(entity != null) || !entity.ToNonNpcPlayer(out var player))
		{
			return;
		}
		if (player.GetMountedVehicle().Is<BaseVehicle>(out var castedUnityObject))
		{
			switch (castedUnityObject.npcVisibilityCategory)
			{
			case BaseVehicle.NpcVisibilityCategory.QuiteObious:
				modTouchDistance = touchDistance * 6f;
				modShortVisionRange = LongRangeVisionRectangle.z;
				return;
			case BaseVehicle.NpcVisibilityCategory.VeryObvious:
				modTouchDistance = LongRangeVisionRectangle.z;
				modShortVisionRange = LongRangeVisionRectangle.z;
				return;
			case BaseVehicle.NpcVisibilityCategory.LikeNormalPlayer:
				return;
			}
			if (AI.logIssues)
			{
				Debug.LogError($"SenseComponent:GetModifiedSenses: Unknown npcVisibilityCategory {castedUnityObject.npcVisibilityCategory} for vehicle {castedUnityObject}");
			}
		}
		else if (player.IsDucked())
		{
			modTouchDistance = base.baseEntity.bounds.extents.z * 1.5f;
			modHalfAngle = ShortRangeVisionCone.halfAngle * 0.85f;
			modShortVisionRange = ShortRangeVisionCone.range * 0.5f;
			modLongVisionRectangle = Vector3.Scale(LongRangeVisionRectangle, new Vector3(3f, 0.5f, 0.5f));
		}
		else if (player.IsRunning())
		{
			modTouchDistance = touchDistance * 3f;
			modShortVisionRange = ShortRangeVisionCone.range * 1.3f;
			modLongVisionRectangle = LongRangeVisionRectangle * 1.15f;
		}
	}

	private bool IsInAnyRange(BaseEntity entity, out float clarity)
	{
		using (TimeWarning.New("IsInAnyRange"))
		{
			Vector3 position = GetEyeTransform().GetPosition();
			Vector3 vector = GetEyeTransform().rotation * Vector3.forward;
			Vector3 entityLineOfSightTestPoint = GetEntityLineOfSightTestPoint(entity, ignoreCrouch: false);
			Vector3 vector2 = entityLineOfSightTestPoint - position;
			float magnitude = vector2.magnitude;
			GetModifiedSenses(entity, out var modTouchDistance, out var modHalfAngle, out var modShortVisionRange, out var modLongVisionRectangle);
			clarity = 0f;
			float num = Vector3.Angle(vector, vector2.normalized);
			if (magnitude < 1.2f)
			{
				clarity = 999f;
			}
			else if (num < modHalfAngle)
			{
				if (magnitude < modShortVisionRange)
				{
					float num2 = Mathx.RemapValClamped(num, modHalfAngle, 0f, 0f, 1f);
					float num3 = Mathx.RemapValClamped(magnitude, modShortVisionRange, touchDistance, 0f, 1f);
					clarity = (num2 + num3) * 0.5f;
				}
				else
				{
					clarity = Mathx.RemapValClamped(magnitude, modLongVisionRectangle.z, modShortVisionRange, 0f, 0.5f);
				}
			}
			else if (magnitude < modTouchDistance)
			{
				clarity = 1f;
				if (entity.ToNonNpcPlayer(out var player))
				{
					if (player.IsRunning())
					{
						clarity = 2f;
					}
					else if (player.IsDucked())
					{
						clarity = 0.5f;
					}
				}
			}
			if (magnitude < modTouchDistance)
			{
				return true;
			}
			if (num < modHalfAngle)
			{
				if (magnitude < modShortVisionRange)
				{
					if (IsSightLineBrokenBySmoke(position, entityLineOfSightTestPoint))
					{
						clarity = 0f;
						return false;
					}
					return true;
				}
				if ((IsInCombat || (TOD_Sky.Instance.IsDay && magnitude < modLongVisionRectangle.z)) && DistToLineYZ(position, vector, entity.transform.position) < modLongVisionRectangle.y * 0.5f && DistToLineXZ(position, vector, entity.transform.position) < modLongVisionRectangle.x * 0.5f)
				{
					if (IsSightLineBrokenBySmoke(position, entityLineOfSightTestPoint))
					{
						clarity = 0f;
						return false;
					}
					return true;
				}
			}
			clarity = 0f;
			return false;
		}
	}

	private bool IsSightLineBrokenBySmoke(Vector3 eyePos, Vector3 entityTestPoint)
	{
		using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
		SingletonComponent<SmokeGrenadeManager>.Instance.GetSmokeAround(eyePos, 50f, pooledList);
		foreach (BaseEntity item in pooledList)
		{
			if (item.Is<SmokeGrenade>(out var castedUnityObject) && NpcCoverManager.SegmentSphereIntersection(eyePos, entityTestPoint, castedUnityObject.transform.position, AI.smokeGrenadeNpcRadius))
			{
				return true;
			}
		}
		return false;
	}

	private static float DistToLine(Vector3 lineStart, Vector3 lineDir, Vector3 point)
	{
		return Vector3.Cross(lineDir.normalized, point - lineStart).magnitude;
	}

	private static float DistToLineXZ(Vector3 lineStart, Vector3 lineDir, Vector3 point)
	{
		return DistToLine(lineStart.WithY(0f), lineDir.WithY(0f), point.WithY(0f));
	}

	private static float DistToLineYZ(Vector3 lineStart, Vector3 lineDir, Vector3 point)
	{
		return DistToLine(lineStart.WithX(0f), lineDir.WithX(0f), point.WithX(0f));
	}

	private void UpdateEntityVisibility(BaseEntity entity, float deltaTime)
	{
		float clarity;
		bool flag = IsInAnyRange(entity, out clarity);
		clarity *= 1f / AI.npcReactionTime;
		clarity = Mathf.Max(clarity, 0.001f);
		if (flag && entity.ToNonNpcPlayer(out var player))
		{
			Vector3 entityLineOfSightTestPoint = GetEntityLineOfSightTestPoint(player, ignoreCrouch: false);
			flag = !IsLineOccluded(EyePosition, entityLineOfSightTestPoint, 1218519041);
		}
		if (!flag)
		{
			clarity = 0f;
		}
		if (entitiesWeAreAwareOf.TryGetValue(entity, out var value))
		{
			value.UpdateVisibility(flag, deltaTime, clarity, null, null);
			entitiesUpdatedThisFrame.Add(entity);
		}
		else if (flag)
		{
			VisibilityStatus fromPool = VisibilityStatus.GetFromPool(base.baseEntity, entity, isVisible: true, deltaTime, clarity, null, null);
			entitiesWeAreAwareOf.Add(entity, fromPool);
			entitiesUpdatedThisFrame.Add(entity);
		}
	}

	public bool InSameTeam(BaseEntity other)
	{
		if (team != null && other.TryGetComponent<SenseComponent>(out var component))
		{
			return team == component.team;
		}
		return false;
	}

	public NPCStance StanceToward(BaseEntity entity)
	{
		if (team == null)
		{
			return NPCStance.Ignore;
		}
		NPCStance nPCStance = NPCStance.Ignore;
		SenseComponent component;
		if (entity.IsNonNpcPlayer())
		{
			nPCStance = team.players;
		}
		else if (entity.TryGetComponent<SenseComponent>(out component))
		{
			nPCStance = team.StanceToward(component.team);
		}
		if (nPCStance == NPCStance.Ignore && HurtUsRecently(entity))
		{
			return team.attackers;
		}
		return nPCStance;
	}

	public void NoteAttacker(BaseEntity attacker)
	{
		if (attacker.IsValid() && !(attacker == base.baseEntity) && !InSameTeam(attacker))
		{
			recentAttackers[attacker] = UnityEngine.Time.timeAsDouble;
		}
	}

	private bool HurtUsRecently(BaseEntity entity)
	{
		if (recentAttackers.TryGetValue(entity, out var value))
		{
			return UnityEngine.Time.timeAsDouble - value < (double)timeToForgetSightings.Value;
		}
		return false;
	}

	private void ForgetOldAttackers()
	{
		if (recentAttackers.Count == 0)
		{
			return;
		}
		using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
		foreach (var (baseEntity2, num2) in recentAttackers)
		{
			if (!baseEntity2.IsValid() || UnityEngine.Time.timeAsDouble - num2 >= (double)timeToForgetSightings.Value)
			{
				pooledList.Add(baseEntity2);
			}
		}
		foreach (BaseEntity item in pooledList)
		{
			recentAttackers.Remove(item);
		}
	}

	private static Quaternion Clamp(Quaternion originalForward, Quaternion targetRotation, float maxAngle)
	{
		float num = Quaternion.Angle(originalForward, targetRotation);
		if (num > maxAngle)
		{
			float t = maxAngle / num;
			return Quaternion.Slerp(originalForward, targetRotation, t);
		}
		return targetRotation;
	}

	private void TickHearing(float deltaTime)
	{
		using (TimeWarning.New("SenseComponent:TickHearing"))
		{
			if (noiseRangeMultiplier > 0f)
			{
				using PooledList<NpcNoiseEvent> pooledList = Facepunch.Pool.Get<PooledList<NpcNoiseEvent>>();
				SingletonComponent<NpcNoiseManager>.Instance.GetNoisesAround(base.baseEntity.transform.position, hearingRange, pooledList);
				foreach (NpcNoiseEvent item in pooledList)
				{
					if (noises.Contains(item) || item.Initiator == base.baseEntity || !CanTarget(item.Initiator) || UnityEngine.Time.timeAsDouble - item.EventTime > 5.0)
					{
						continue;
					}
					if (!noiseRadii.TryGetValue(item.Intensity, out var value))
					{
						Debug.LogError($"Unknown noise intensity: {item.Intensity}");
					}
					else if (!(Vector3.Distance(item.NoisePosition, base.baseEntity.transform.position) > Mathf.Min(value * noiseRangeMultiplier, hearingRange)))
					{
						noises.Add(item);
						if (base.baseEntity is ISimpleHearingReceiver simpleHearingReceiver)
						{
							simpleHearingReceiver.OnHeardNoise(item);
						}
						SimulateSighting(item.Initiator, item.GuessedInitiatorPosition);
					}
				}
			}
			using PooledList<NpcNoiseEvent> pooledList2 = Facepunch.Pool.Get<PooledList<NpcNoiseEvent>>();
			foreach (NpcNoiseEvent noise in noises)
			{
				if (!CanTarget(noise.Initiator) || UnityEngine.Time.timeAsDouble - noise.EventTime > 5.0)
				{
					pooledList2.Add(noise);
				}
			}
			foreach (NpcNoiseEvent item2 in pooledList2)
			{
				noises.Remove(item2);
			}
		}
	}

	public bool FindMostRelevantNoise(out NpcNoiseEvent mostRelevantNoise)
	{
		using (TimeWarning.New("SenseComponent:FindMostRelevantPooledNoise"))
		{
			NpcNoiseEvent? npcNoiseEvent = null;
			foreach (NpcNoiseEvent noise in noises)
			{
				if (CanTarget(noise.Initiator) && !(UnityEngine.Time.timeAsDouble - noise.EventTime > 5.0) && (!npcNoiseEvent.HasValue || noise.Intensity > npcNoiseEvent.Value.Intensity))
				{
					npcNoiseEvent = noise;
				}
			}
			if (npcNoiseEvent.HasValue)
			{
				mostRelevantNoise = npcNoiseEvent.Value;
				return true;
			}
			mostRelevantNoise = default(NpcNoiseEvent);
			return false;
		}
	}

	public void ForgetAllNoises()
	{
		noises.Clear();
	}

	public void IgnoreFood(BaseEntity food)
	{
		ignoredFood.Add(food);
		if (_nearestFood == food)
		{
			_nearestFood = null;
		}
	}

	public bool FindFood(out BaseEntity food)
	{
		if (!_nearestFood.IsValid() || _nearestFood.IsDestroyed || !SingletonComponent<NpcFoodManager>.Instance.Contains(_nearestFood))
		{
			food = null;
			return false;
		}
		food = _nearestFood;
		return true;
	}

	private void TickFoodDetection(float deltaTime)
	{
		using (TimeWarning.New("SenseComponent:TickFoodDetection"))
		{
			_nearestFood = null;
			if (foodDetectionRange <= 0f)
			{
				return;
			}
			float num = foodDetectionRange * foodDetectionRange;
			float num2 = float.MaxValue;
			using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
			SingletonComponent<NpcFoodManager>.Instance.GetFoodAround(base.baseEntity.transform.position, foodDetectionRange, pooledList);
			ForgetVanishedIgnoredFood();
			RustNavMeshAgent component = base.baseEntity.GetComponent<RustNavMeshAgent>();
			foreach (BaseEntity item in pooledList)
			{
				float sqrMagnitude = (item.transform.position - base.baseEntity.transform.position).sqrMagnitude;
				if (!(sqrMagnitude >= num2) && !(sqrMagnitude >= num) && NpcFoodManager.IsFoodImmobile(item) && !ignoredFood.Contains(item) && (!(item is BaseCorpse baseCorpse) || !baseCorpse.GetComponent<HeadDispenser>().Is<HeadDispenser>(out var castedUnityObject) || !castedUnityObject.SourceEntity.GetEntity().Is<BaseEntity>(out var castedUnityObject2) || !InSameTeam(castedUnityObject2)) && component.IsPositionOnNavmesh(item.transform.position, out var _))
				{
					_nearestFood = item;
					num2 = sqrMagnitude;
				}
			}
		}
	}

	private void ForgetVanishedIgnoredFood()
	{
		if (ignoredFood.Count == 0)
		{
			return;
		}
		using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
		foreach (BaseEntity item in ignoredFood)
		{
			if (!item.IsValid() || item.IsDestroyed)
			{
				pooledList.Add(item);
			}
		}
		foreach (BaseEntity item2 in pooledList)
		{
			ignoredFood.Remove(item2);
		}
	}

	public bool FindFire(out BaseEntity fire)
	{
		if (!_nearestFire.IsValid() || _nearestFire.IsDestroyed || !NpcFireManager.IsOnFire(_nearestFire))
		{
			_nearestFire = null;
		}
		fire = _nearestFire;
		return fire != null;
	}

	private void TickFireDetection(float deltaTime)
	{
		using (TimeWarning.New("SenseComponent:TickFireDetection"))
		{
			if (fireDetectionRange <= 0f)
			{
				return;
			}
			if (Target != null && SingletonComponent<NpcFireManager>.Instance.DidMeleeWithFireRecently(base.baseEntity, Target, out var meleeTime) && (!lastMeleeTime.HasValue || meleeTime != lastMeleeTime.Value))
			{
				lastMeleeTime = meleeTime;
				onFireMelee.Invoke();
			}
			using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
			SingletonComponent<NpcFireManager>.Instance.GetFiresAround(base.baseEntity.transform.position, fireDetectionRange, pooledList);
			BaseEntity baseEntity = null;
			float num = fireDetectionRange * fireDetectionRange;
			float num2 = float.MaxValue;
			foreach (BaseEntity item in pooledList)
			{
				float sqrMagnitude = (item.transform.position - base.baseEntity.transform.position).sqrMagnitude;
				if (sqrMagnitude < num2 && sqrMagnitude < num)
				{
					baseEntity = item;
					num2 = sqrMagnitude;
				}
			}
			if (baseEntity != null)
			{
				_nearestFire = baseEntity;
			}
		}
	}

	public LockState.LockHandle LockCurrentTarget()
	{
		return lockState.AddLock();
	}

	public bool UnlockTarget(ref LockState.LockHandle handle)
	{
		return lockState.RemoveLock(ref handle);
	}

	public bool CanTarget(BaseEntity entity)
	{
		if (!entity.IsValid())
		{
			return false;
		}
		if (entity.IsTransferProtected())
		{
			return false;
		}
		if (entity.IsDestroyed)
		{
			return false;
		}
		if (!entity.IsNonNpcPlayer() && !entity.IsNpc)
		{
			return false;
		}
		if (entity.IsNpcPlayer())
		{
			return false;
		}
		if (entity is BaseCombatEntity baseCombatEntity && baseCombatEntity.IsDead())
		{
			return false;
		}
		if (StanceToward(entity) == NPCStance.Ignore)
		{
			return false;
		}
		if (entity.IsNpc && entity.InSafeZone())
		{
			return false;
		}
		if (entity is BasePlayer item)
		{
			if (AI.ignoreplayers)
			{
				return false;
			}
			if (SimpleAIMemory.PlayerIgnoreList.Contains(item))
			{
				return false;
			}
		}
		if (Observer != null && Observer.RefusesToTarget(entity))
		{
			return false;
		}
		return true;
	}

	public bool FindTarget(out BaseEntity target)
	{
		if (!CanTarget(Target))
		{
			ClearTarget();
			target = null;
			return false;
		}
		target = Target;
		return target != null;
	}

	public bool FindTargetPosition(out Vector3 targetPosition)
	{
		if (!FindTarget(out var target))
		{
			targetPosition = Vector3.zero;
			return false;
		}
		targetPosition = target.transform.position;
		return true;
	}

	public bool FindTargetStatus(out VisibilityStatus status)
	{
		status = null;
		if (!FindTarget(out var target))
		{
			return false;
		}
		if (!GetVisibilityStatus(target, out status))
		{
			return false;
		}
		return true;
	}

	public bool TrySetTarget(BaseEntity newTarget, bool bypassCooldown = true)
	{
		if (lockState.IsLocked)
		{
			return false;
		}
		if (newTarget == null)
		{
			ClearTarget();
			return true;
		}
		if (newTarget == Target)
		{
			return true;
		}
		if (!CanTarget(newTarget))
		{
			return false;
		}
		if (Target != null && !bypassCooldown && ChangedTargetRecently)
		{
			return false;
		}
		lastTargetTime = UnityEngine.Time.timeAsDouble;
		Target = newTarget;
		return true;
	}

	public void ClearTarget(bool forget = true)
	{
		if (Target.IsValid())
		{
			if (forget)
			{
				Forget(Target);
			}
			lastTargetTime = null;
			Target = null;
		}
	}

	private void TickTargeting(float deltaTime)
	{
		using (TimeWarning.New("SenseComponent:TickTargeting"))
		{
			if (Target != null && !CanTarget(Target))
			{
				ClearTarget();
			}
			if ((Target != null && SwitchedTargetToFocusAggressorRecently) || (Target != null && ChangedTargetRecently))
			{
				return;
			}
			using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
			GetOncePerceivedEntities(pooledList);
			if (pooledList.Count == 0)
			{
				return;
			}
			BaseEntity baseEntity = null;
			float num = float.NegativeInfinity;
			foreach (BaseEntity item in pooledList)
			{
				if (GetVisibilityStatus(item, out var status) && status.IsAware && FindLKP(item, out var lkp, applyHeightOffset: false, predict: true))
				{
					float num2 = 0f;
					float num3 = base.baseEntity.Distance(lkp);
					if (status.IsVisible && num3 < 3f)
					{
						num2 += 1000f;
					}
					num2 += Mathx.RemapValClamped(status.timeNotVisible, 0f, 10f, 1f, 0f) * 100f;
					num2 += Mathx.RemapValClamped(num3, 0f, 50f, 1f, 0f);
					if (num2 > num)
					{
						num = num2;
						baseEntity = item;
					}
				}
			}
			if (baseEntity != null)
			{
				TrySetTarget(baseEntity, bypassCooldown: false);
			}
		}
	}
}

