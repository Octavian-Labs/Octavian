using System;
using System.Collections.Generic;
using ConVar;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using UnityEngine;
using UnityEngine.Serialization;

namespace Rust.Ai.Gen2;

public class LivestockAnimalFSM : FSMComponent
{
	[Serializable]
	public class State_Follow : FSMStateBase
	{
		[SerializeField]
		protected RustNavMeshAgent.Speeds minSpeed;

		[SerializeField]
		protected RustNavMeshAgent.Speeds maxSpeed = RustNavMeshAgent.Speeds.Sprint;

		[SerializeField]
		protected float idealDistance = 3f;

		[SerializeField]
		protected float laneWidth = 1.5f;

		public const float StandingSpeed = 0.5f;

		private const float ArrivedDistance = 1f;

		private const float LaneSearchInterval = 0.5f;

		private float laneOffset;

		private TimeUntil nextLaneSearch;

		public virtual BasePlayer GetTarget()
		{
			if (!(Owner is LivestockAnimal { LeadingPlayer: var leadingPlayer }))
			{
				return null;
			}
			return leadingPlayer.Get(Owner.isServer);
		}

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			nextLaneSearch = 0f;
			if (!KeepUp())
			{
				return EFSMStateStatus.Failure;
			}
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!KeepUp())
			{
				return EFSMStateStatus.Failure;
			}
			return base.OnStateUpdate(deltaTime);
		}

		private bool KeepUp()
		{
			BasePlayer target = GetTarget();
			if (target == null)
			{
				return true;
			}
			Vector3 position = target.transform.position;
			Vector3 vector = LanePosition(target);
			if (!KeepUpWith(target, vector))
			{
				if (vector != position)
				{
					return KeepUpWith(target, position);
				}
				return false;
			}
			return true;
		}

		private Vector3 LanePosition(BasePlayer target)
		{
			if ((float)nextLaneSearch <= 0f)
			{
				nextLaneSearch = 0.5f;
				laneOffset = 0f;
				if (Owner is LivestockAnimal livestockAnimal)
				{
					livestockAnimal.CountWalkingAbreast(target, out var onLeft, out var onRight);
					laneOffset = (float)(onLeft - onRight) * 0.5f * laneWidth;
				}
			}
			Vector3 position = target.transform.position;
			Vector3 rhs = (position - Owner.transform.position).NormalizeXZ();
			Vector3 vector = position + Vector3.Cross(Vector3.up, rhs) * laneOffset;
			if (laneOffset != 0f && base.Agent.Raycast(base.Agent.WorldToNavSpace(position), base.Agent.WorldToNavSpace(vector), out var hitNS))
			{
				return base.Agent.NavToWorldSpace(hitNS.position);
			}
			return vector;
		}

		private bool KeepUpWith(BasePlayer target, Vector3 position)
		{
			float num = Vector3.Distance(Owner.transform.position, position);
			float estimatedSpeed2D = target.estimatedSpeed2D;
			if (num <= 1f)
			{
				return true;
			}
			if (estimatedSpeed2D < 0.5f)
			{
				if (num > idealDistance)
				{
					RustNavMeshAgent agent = base.Agent;
					RustNavMeshAgent.Speeds? gait = maxSpeed;
					float? stoppingDistance = idealDistance;
					return agent.SetDestinationWithParams(position, autoBraking: true, gait, null, null, null, null, stoppingDistance);
				}
				if (base.Agent.hasPath)
				{
					RustNavMeshAgent agent2 = base.Agent;
					RustNavMeshAgent.Speeds? gait2 = minSpeed;
					float? stoppingDistance = base.Agent.emergencyDeceleration;
					return agent2.SetDestinationWithParams(position, autoBraking: true, gait2, null, stoppingDistance, null, null, null);
				}
				return true;
			}
			RustNavMeshAgent.Speeds value = ((num > idealDistance) ? maxSpeed : ClosestGait(estimatedSpeed2D * num / idealDistance));
			return base.Agent.SetDestinationWithParams(position, autoBraking: false, value, null, null, null, null, null);
		}

		private RustNavMeshAgent.Speeds ClosestGait(float speed)
		{
			RustNavMeshAgent.Speeds result = minSpeed;
			float num = float.MaxValue;
			for (RustNavMeshAgent.Speeds speeds = minSpeed; speeds <= maxSpeed; speeds++)
			{
				float num2 = Mathf.Abs(base.Agent.GetSpeedForGait(base.Agent.PenalisedGait(speeds)) - speed);
				if (num2 < num)
				{
					result = speeds;
					num = num2;
				}
			}
			return result;
		}
	}

	[Serializable]
	public class Trans_IsFollowingPlayer : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				return livestockAnimal.IsLeading();
			}
			return false;
		}
	}

	[Serializable]
	public class State_TurnToLeader : State_TurnToTarget
	{
		protected override bool FindTurnTarget(out Vector3 position)
		{
			position = Vector3.zero;
			if (Owner is LivestockAnimal { LeadingPlayer: var leadingPlayer })
			{
				BasePlayer basePlayer = leadingPlayer.Get(Owner.isServer);
				if ((object)basePlayer != null)
				{
					position = basePlayer.transform.position;
					return true;
				}
			}
			return false;
		}
	}

	[Serializable]
	public class Trans_LeaderBehind : FSMTransitionBase
	{
		private const float BehindAngle = 120f;

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner is LivestockAnimal { LeadingPlayer: var leadingPlayer } livestockAnimal)
			{
				BasePlayer basePlayer = leadingPlayer.Get(Owner.isServer);
				if ((object)basePlayer != null)
				{
					if ((base.Agent.hasPath ? base.Agent.velocity.magnitude : 0f) >= base.Agent.jogSpeed || basePlayer.estimatedSpeed2D < 0.5f || !livestockAnimal.IsPulledAwayBy(basePlayer))
					{
						return false;
					}
					Vector3 to = (basePlayer.transform.position - Owner.transform.position).NormalizeXZ();
					return Vector3.Angle(Owner.transform.forward.NormalizeXZ(), to) > 120f;
				}
			}
			return false;
		}
	}

	[Serializable]
	public class State_LivestockRoam : State_Roam
	{
		protected override Vector3 HomePosition
		{
			get
			{
				if (!(Owner is LivestockAnimal livestockAnimal))
				{
					return base.HomePosition;
				}
				return livestockAnimal.HomePosition;
			}
		}

		protected override bool HealsWhenUndisturbed => false;
	}

	[Serializable]
	public class Trans_Stranded : Trans_Dead
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (AI.useUnityNavmesh)
			{
				NavHit hitNS;
				if (SingletonComponent<DynamicNavMesh>.Instance != null && !SingletonComponent<DynamicNavMesh>.Instance.IsBuilding)
				{
					return !base.Agent.SamplePosition(base.Agent.WorldToNavSpace(Owner.transform.position), out hitNS, 2f);
				}
				return false;
			}
			if (base.Agent.IsNavMeshBuilt)
			{
				return !base.Agent.isOnNavMesh;
			}
			return false;
		}

		public override void OnTransitionTaken(FSMStateBase from, FSMStateBase to)
		{
			base.OnTransitionTaken(from, to);
			if (Owner is BaseNPC2 baseNPC)
			{
				baseNPC.DieOffNavmesh();
			}
		}
	}

	[Serializable]
	public class State_CuriousFollow : State_Follow
	{
		[Tooltip("How long (in seconds) after a walk that could not be finished before she sets off after somebody again.")]
		[SerializeField]
		private float retryDelay = 15f;

		private bool walkFailed;

		public State_CuriousFollow()
		{
			minSpeed = RustNavMeshAgent.Speeds.Sneak;
			maxSpeed = RustNavMeshAgent.Speeds.Walk;
		}

		public override BasePlayer GetTarget()
		{
			if (!(Owner is LivestockAnimal livestockAnimal))
			{
				return null;
			}
			return livestockAnimal.CuriousAbout;
		}

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			walkFailed = false;
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.StartCuriousFollow() == null)
			{
				return Record(EFSMStateStatus.Failure);
			}
			return Record(base.OnStateEnter(payload));
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			return Record(base.OnStateUpdate(deltaTime));
		}

		public override void OnStateExit()
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.StopCuriousFollow();
			}
			if (walkFailed && Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				component.NextCuriousFollow = retryDelay;
			}
			base.OnStateExit();
		}

		private EFSMStateStatus Record(EFSMStateStatus status)
		{
			walkFailed = status == EFSMStateStatus.Failure;
			return status;
		}
	}

	[Serializable]
	public class Trans_IsCuriousAboutPlayer : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal { ActsCurious: not false } livestockAnimal))
			{
				return false;
			}
			if (Owner.TryGetComponent<LivestockAnimalFSM>(out var component) && (float)component.NextCuriousFollow > 0f)
			{
				return false;
			}
			return livestockAnimal.WouldFollowOutOfCuriosity(livestockAnimal.NearbyTrustedPlayer);
		}
	}

	[Serializable]
	public class Trans_HasKick : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				return component.kick.HasAnimation;
			}
			return false;
		}
	}

	[Serializable]
	public class Trans_HasCanterTurn : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				return component.turnCantering.HasTurnAnimation;
			}
			return false;
		}
	}

	[Serializable]
	public class Trans_StillCuriousAboutPlayer : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				return livestockAnimal.CuriousAbout != null;
			}
			return false;
		}
	}

	[Serializable]
	public class Trans_RecentlyHurt : FSMTransitionBase
	{
		[SerializeField]
		private float thresholdSeconds = 5f;

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner is BaseCombatEntity { lastAttackedTime: >0f } baseCombatEntity)
			{
				return UnityEngine.Time.time - baseCombatEntity.lastAttackedTime < thresholdSeconds;
			}
			return false;
		}
	}

	[Serializable]
	public class Trans_LoudNoiseNearby : FSMTransitionBase
	{
		[Tooltip("How close the noise has to have been. 0 is anywhere within earshot, which is what a cow runs from; the bull answers only for shots near his herd.")]
		[SerializeField]
		public float range;

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal))
			{
				return false;
			}
			if (!(range > 0f))
			{
				return livestockAnimal.RecentlyHeardLoudNoise;
			}
			return livestockAnimal.HeardLoudNoiseWithin(range);
		}
	}

	[Serializable]
	public class Trans_ChasedFromBehind : FSMTransitionBase
	{
		[Tooltip("How much clear air there may be between them and our body. Measured off the animal's bounds rather than its origin, so this is the gap you actually see.")]
		[SerializeField]
		public float range = 4f;

		[Tooltip("Past this many degrees off our forward counts as being on our back.")]
		[SerializeField]
		public float rearAngle = 100f;

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.IsInfant() || range <= 0f)
			{
				return false;
			}
			if (!Owner.TryGetComponent<LivestockAnimalFSM>(out var component) || !component.kickBehind.HasAnimation)
			{
				return false;
			}
			if (!livestockAnimal.TryGetRememberedAggressor(out var attacker))
			{
				return false;
			}
			if (Trans_IsTargetProtectedByMount.IsProtected(attacker))
			{
				return false;
			}
			if (livestockAnimal.InSafeZone())
			{
				return false;
			}
			Vector3 position = attacker.transform.position;
			if (Vector3.Distance(position, Owner.ClosestPoint(position)) > range)
			{
				return false;
			}
			Vector3 to = (position - Owner.transform.position).WithY(0f).NormalizeXZ();
			if (to.sqrMagnitude > 0.001f && Vector3.Angle(Owner.transform.forward, to) < rearAngle)
			{
				return false;
			}
			base.Senses.TrySetTarget(attacker);
			return true;
		}
	}

	[Serializable]
	public class Trans_AggressorNearby : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || !livestockAnimal.TryGetRememberedAggressor(out var attacker))
			{
				return false;
			}
			float grudgeRange = Livestock.grudgeRange;
			if (grudgeRange <= 0f || Vector3.Distance(livestockAnimal.transform.position, attacker.transform.position) > grudgeRange)
			{
				return false;
			}
			base.Senses.TrySetTarget(attacker);
			return true;
		}
	}

	[Serializable]
	public abstract class Trans_IsConsumeLocationInRange : FSMTransitionBase
	{
		private enum DeployableSearch
		{
			None,
			Found,
			NothingToGain,
			Busy
		}

		[FormerlySerializedAs("troughStandoff")]
		[FormerlySerializedAs("barrelStandoff")]
		[SerializeField]
		private float standoff = 1.5f;

		private const float MuzzleHeight = 0.8f;

		private const int ConsumeBlockers = 2162688;

		[FormerlySerializedAs("grassRetryDelay")]
		[FormerlySerializedAs("waterRetryDelay")]
		[SerializeField]
		private float retryDelay = 30f;

		[FormerlySerializedAs("grazeTopology")]
		[FormerlySerializedAs("drinkTopology")]
		[SerializeField]
		private TerrainTopology.Enum topology;

		private TimeUntil nextTerrainSearch;

		private static readonly float[] StandingBearings = new float[13]
		{
			0f, 15f, -15f, 30f, -30f, 45f, -45f, 60f, -60f, 75f,
			-75f, 90f, -90f
		};

		private static BaseEntity RootOf(BaseEntity entity)
		{
			BaseEntity baseEntity = entity;
			while (baseEntity != null)
			{
				BaseEntity parentEntity = baseEntity.GetParentEntity();
				if (parentEntity == null)
				{
					break;
				}
				baseEntity = parentEntity;
			}
			return baseEntity;
		}

		protected Trans_IsConsumeLocationInRange(TerrainTopology.Enum defaultTopology)
		{
			topology = defaultTopology;
		}

		protected abstract bool IsInNeed(LivestockAnimal self);

		protected abstract void FindDeployables(LivestockAnimal self, List<BaseEntity> results);

		protected virtual bool IsUsableTerrain(LivestockAnimal self, Vector3 position)
		{
			return true;
		}

		protected virtual void OnTerrainSearchFailed(LivestockAnimalFSM fsm)
		{
		}

		protected virtual float TerrainSearchRadius(LivestockAnimal self)
		{
			return self.ConsumeSearchRadius;
		}

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal self))
			{
				return false;
			}
			if (TOD_Sky.Instance.IsNight)
			{
				return false;
			}
			if (!IsInNeed(self))
			{
				return false;
			}
			if (!Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				return false;
			}
			if (TryFindTerrain(self, component, out var searched))
			{
				return true;
			}
			switch (TryFindDeployable(self, component))
			{
			case DeployableSearch.Found:
				return true;
			case DeployableSearch.NothingToGain:
				OnNothingToGainFromDeployables(self);
				return false;
			case DeployableSearch.Busy:
				return false;
			default:
				if (searched)
				{
					OnTerrainSearchFailed(component);
				}
				return false;
			}
		}

		protected virtual bool CanGainFromDeployables(LivestockAnimal self)
		{
			return true;
		}

		protected virtual void OnNothingToGainFromDeployables(LivestockAnimal self)
		{
		}

		private DeployableSearch TryFindDeployable(LivestockAnimal self, LivestockAnimalFSM fsm)
		{
			using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
			FindDeployables(self, pooledList);
			if (pooledList.Count > 0 && !CanGainFromDeployables(self))
			{
				return DeployableSearch.NothingToGain;
			}
			using PooledList<LivestockFootprint> pooledList2 = Facepunch.Pool.Get<PooledList<LivestockFootprint>>();
			self.GatherRestingFootprints(self.transform.position, pooledList2);
			float distanceSqr = -1f;
			BaseEntity baseEntity = null;
			Vector3 spot = default(Vector3);
			float num = float.MinValue;
			for (int i = 0; i < 3; i++)
			{
				BaseEntity baseEntity2 = Eqs.NearestBeyond(pooledList, self.transform.position, distanceSqr, out distanceSqr);
				if (baseEntity2 == null)
				{
					break;
				}
				if (TryFindStandingSpot(self, baseEntity2, pooledList2, out var spot2, out var clearance))
				{
					if (LivestockAnimal.IsClear(clearance))
					{
						return Found(fsm, baseEntity2, spot2);
					}
					if (clearance > num)
					{
						baseEntity = baseEntity2;
						spot = spot2;
						num = clearance;
					}
				}
			}
			if (baseEntity == null)
			{
				return DeployableSearch.None;
			}
			return (num < 0f) ? DeployableSearch.Busy : Found(fsm, baseEntity, spot);
		}

		private bool TryFindStandingSpot(LivestockAnimal self, BaseEntity deployable, PooledList<LivestockFootprint> taken, out Vector3 spot, out float clearance)
		{
			spot = default(Vector3);
			clearance = float.MinValue;
			Vector3 vector = (self.transform.position - deployable.transform.position).WithY(0f);
			Vector3 vector2 = ((vector.sqrMagnitude > 0.01f) ? vector.normalized : self.transform.forward);
			using PooledList<Vector3> pooledList = Facepunch.Pool.Get<PooledList<Vector3>>();
			using PooledList<float> pooledList2 = Facepunch.Pool.Get<PooledList<float>>();
			float[] standingBearings = StandingBearings;
			foreach (float y in standingBearings)
			{
				Vector3 positionWS = deployable.transform.position + Quaternion.Euler(0f, y, 0f) * vector2 * standoff;
				if (base.Agent.SamplePosition(positionWS, out var hitWS, 2f))
				{
					pooledList.Add(hitWS.position);
					pooledList2.Add(LivestockAnimal.ClearanceOf(taken, self.RestingFootprintFor(hitWS.position, base.Agent.stoppingDistance)));
				}
			}
			for (int j = 0; j < pooledList.Count; j++)
			{
				if (LivestockAnimal.IsClear(pooledList2[j]))
				{
					if (CanUseFrom(deployable, pooledList[j]))
					{
						spot = pooledList[j];
						clearance = pooledList2[j];
						return true;
					}
					pooledList2[j] = float.MinValue;
				}
			}
			int num;
			while (true)
			{
				num = -1;
				for (int k = 0; k < pooledList.Count; k++)
				{
					if (pooledList2[k] > float.MinValue && (num < 0 || pooledList2[k] > pooledList2[num]))
					{
						num = k;
					}
				}
				if (num < 0)
				{
					return false;
				}
				if (CanUseFrom(deployable, pooledList[num]))
				{
					break;
				}
				pooledList2[num] = float.MinValue;
			}
			spot = pooledList[num];
			clearance = pooledList2[num];
			return true;
		}

		private bool CanUseFrom(BaseEntity deployable, Vector3 spot)
		{
			if (!base.Agent.CanReach(spot))
			{
				return false;
			}
			BaseEntity baseEntity = RootOf(deployable);
			return GamePhysics.LineOfSight(spot + Vector3.up * 0.8f, baseEntity.CenterPoint(), 2162688, baseEntity);
		}

		private static DeployableSearch Found(LivestockAnimalFSM fsm, BaseEntity deployable, Vector3 spot)
		{
			fsm.ConsumeLocation = spot;
			fsm.ConsumeSource.Set(deployable);
			return DeployableSearch.Found;
		}

		private bool TryFindTerrain(LivestockAnimal self, LivestockAnimalFSM fsm, out bool searched)
		{
			searched = false;
			if ((float)nextTerrainSearch > 0f)
			{
				return false;
			}
			searched = true;
			NavVector3 nextPosition = base.Agent.nextPosition;
			if (self.IsInfant() && self.MotherAnimal != null)
			{
				nextPosition = self.MotherAnimal.GetComponent<RustNavMeshAgent>().nextPosition;
			}
			float num = TerrainSearchRadius(self);
			using PooledList<NavVector3> pooledList = Facepunch.Pool.Get<PooledList<NavVector3>>();
			Eqs.SampleNavigablePositions(base.Agent, nextPosition, pooledList, num, num * 0.25f, 16);
			using PooledList<LivestockFootprint> footprints = Facepunch.Pool.Get<PooledList<LivestockFootprint>>();
			self.GatherRestingFootprints(nextPosition.Value, footprints);
			bool flag = false;
			bool flag2 = false;
			float num2 = float.MaxValue;
			float num3 = float.MinValue;
			foreach (NavVector3 item in pooledList)
			{
				if (base.Agent.SamplePosition(item, out var hitNS, 10f) && base.Agent.IsPositionAtTopologyRequirement(hitNS.position, topology) && IsOnOpenGround(hitNS.position.Value) && IsUsableTerrain(self, hitNS.position.Value))
				{
					float num4 = LivestockAnimal.ClearanceOf(footprints, self.RestingFootprintFor(hitNS.position.Value, base.Agent.stoppingDistance));
					bool flag3 = LivestockAnimal.IsClear(num4);
					float sqrMagnitude = (hitNS.position.Value - nextPosition.Value).sqrMagnitude;
					if ((flag3 != flag2) ? flag3 : (flag3 ? (sqrMagnitude < num2) : (num4 > num3)))
					{
						num2 = sqrMagnitude;
						num3 = num4;
						flag2 = flag3;
						fsm.ConsumeLocation = hitNS.position.Value;
						fsm.ConsumeSource = default(EntityRef<BaseEntity>);
						flag = true;
					}
				}
			}
			if (flag && base.Agent.CanReach(fsm.ConsumeLocation.Value))
			{
				return true;
			}
			fsm.ConsumeLocation = null;
			nextTerrainSearch = retryDelay;
			return false;
		}

		private static bool IsOnOpenGround(Vector3 position)
		{
			return !GamePhysics.CheckSphere(GamePhysics.Realm.Server, position - Vector3.up * 0.2f, 0.25f, 2097408, QueryTriggerInteraction.Ignore);
		}
	}

	[Serializable]
	public class Trans_IsGrazeLocationInRange : Trans_IsConsumeLocationInRange
	{
		[SerializeField]
		private float searchRadius = 10f;

		[Tooltip("Ground that counts as grass, compared with the splat that dominates the spot. Field topology alone covers snow, sand and bare dirt.")]
		[InspectorFlags]
		[SerializeField]
		private TerrainSplat.Enum grazingSplats = (TerrainSplat.Enum)48;

		[Tooltip("Biomes with grass to graze. Anywhere else the herd has to be fed at a trough.")]
		[SerializeField]
		private TerrainBiome.Enum grazingBiomes = TerrainBiome.Enum.Arid | TerrainBiome.Enum.Temperate | TerrainBiome.Enum.Tundra | TerrainBiome.Enum.Jungle;

		public Trans_IsGrazeLocationInRange()
			: base(TerrainTopology.Enum.Field | TerrainTopology.Enum.Forest | TerrainTopology.Enum.Forestside)
		{
		}

		protected override bool IsInNeed(LivestockAnimal self)
		{
			return self.NeedsFood();
		}

		protected override void FindDeployables(LivestockAnimal self, List<BaseEntity> results)
		{
			self.FindFoodTroughs(results);
		}

		protected override bool CanGainFromDeployables(LivestockAnimal self)
		{
			return self.Fullness.NeedValue < self.TroughFullnessCeiling;
		}

		protected override void OnNothingToGainFromDeployables(LivestockAnimal self)
		{
			self.RecordHelpingSource(fromTrough: true);
		}

		protected override bool IsUsableTerrain(LivestockAnimal self, Vector3 position)
		{
			if (base.Agent.IsPositionASplatRequirement(position, grazingSplats) && base.Agent.IsPositionABiomeRequirement(position, grazingBiomes))
			{
				return !OvergrazedArea.IsOvergrazed(position);
			}
			return false;
		}

		protected override float TerrainSearchRadius(LivestockAnimal self)
		{
			return searchRadius;
		}

		protected override void OnTerrainSearchFailed(LivestockAnimalFSM fsm)
		{
			fsm.LastFoodSearchFailed = 0f;
		}
	}

	[Serializable]
	public abstract class State_ConsumeAtLocation : FSMStateBase
	{
		private enum Phase
		{
			Moving,
			Consuming
		}

		[SerializeField]
		private Vector2 distanceRange = new Vector2(10f, 20f);

		[SerializeField]
		private RustNavMeshAgent.Speeds maxSpeed = RustNavMeshAgent.Speeds.Sprint;

		[FormerlySerializedAs("grazeDurationRange")]
		[FormerlySerializedAs("drinkDurationRange")]
		[SerializeField]
		private Vector2 durationRange = new Vector2(5f, 15f);

		[SerializeField]
		private float postAnimationDuration = 3f;

		[SerializeField]
		private RustNavMeshAgent.Speeds minSpeed;

		private Phase phase;

		private TimeUntil endTime;

		private EntityRef<BaseEntity> source;

		public float MaxDuration => durationRange.y;

		protected abstract bool CanConsumeFrom(LivestockAnimal self, BaseEntity source);

		protected abstract void Consume(LivestockAnimal self, BaseEntity source);

		protected abstract void SetConsuming(LivestockAnimal self, bool consuming);

		protected virtual bool HasNothingLeftToGain(LivestockAnimal self)
		{
			return false;
		}

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			if (!Owner.TryGetComponent<LivestockAnimalFSM>(out var component) || !component.ConsumeLocation.HasValue)
			{
				return EFSMStateStatus.Failure;
			}
			Vector3 value = component.ConsumeLocation.Value;
			source = component.ConsumeSource;
			component.ConsumeLocation = null;
			component.ConsumeSource = default(EntityRef<BaseEntity>);
			if (!base.Agent.SamplePosition(value, out var hitWS, 10f))
			{
				return EFSMStateStatus.Failure;
			}
			if (!MoveWithRampedSpeed(base.Agent, hitWS.position, distanceRange.y, minSpeed, maxSpeed))
			{
				return EFSMStateStatus.Failure;
			}
			phase = Phase.Moving;
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!(Owner is LivestockAnimal self))
			{
				return EFSMStateStatus.Failure;
			}
			switch (phase)
			{
			case Phase.Moving:
			{
				if (base.Agent.hasPath)
				{
					return base.OnStateUpdate(deltaTime);
				}
				BaseEntity baseEntity = source.Get(serverside: true);
				if (baseEntity != null && !CanConsumeFrom(self, baseEntity))
				{
					return EFSMStateStatus.Failure;
				}
				Consume(self, baseEntity);
				phase = Phase.Consuming;
				endTime = UnityEngine.Random.Range(durationRange.x, durationRange.y);
				SetConsuming(self, consuming: true);
				return base.OnStateUpdate(deltaTime);
			}
			case Phase.Consuming:
				if (HasNothingLeftToGain(self))
				{
					SetConsuming(self, consuming: false);
					return EFSMStateStatus.Success;
				}
				if ((float)endTime <= postAnimationDuration)
				{
					SetConsuming(self, consuming: false);
				}
				if (!((float)endTime <= 0f))
				{
					return EFSMStateStatus.None;
				}
				return EFSMStateStatus.Success;
			default:
				return base.OnStateUpdate(deltaTime);
			}
		}

		public override void OnStateExit()
		{
			base.Agent.ResetPath();
			source = default(EntityRef<BaseEntity>);
			if (Owner is LivestockAnimal self)
			{
				SetConsuming(self, consuming: false);
			}
			base.OnStateExit();
		}
	}

	[Serializable]
	public class State_Graze : State_ConsumeAtLocation
	{
		protected override bool CanConsumeFrom(LivestockAnimal self, BaseEntity source)
		{
			if (source is HitchTrough trough)
			{
				Item foundItem;
				return self.CanEatFrom(trough, out foundItem);
			}
			return false;
		}

		protected override void Consume(LivestockAnimal self, BaseEntity source)
		{
			HitchTrough hitchTrough = source as HitchTrough;
			self.SetGrazingFromTrough(hitchTrough);
			self.RecordHelpingSource(hitchTrough != null);
			if (hitchTrough == null)
			{
				OvergrazedArea.RegisterGraze(self.transform.position);
			}
		}

		protected override void SetConsuming(LivestockAnimal self, bool consuming)
		{
			self.SetGrazing(consuming);
			if (!consuming)
			{
				self.SetGrazingFromTrough(null);
			}
		}

		protected override bool HasNothingLeftToGain(LivestockAnimal self)
		{
			if (!self.IsGrazingFromTrough)
			{
				return self.Fullness.NeedValue >= 1f;
			}
			if (!(self.Fullness.NeedValue >= self.TroughFullnessCeiling))
			{
				return !self.HasTroughFoodLeft;
			}
			return true;
		}
	}

	[Serializable]
	public class Trans_IsDrinkSourceInRange : Trans_IsConsumeLocationInRange
	{
		public Trans_IsDrinkSourceInRange()
			: base(TerrainTopology.Enum.River | TerrainTopology.Enum.Riverside | TerrainTopology.Enum.Lake | TerrainTopology.Enum.Lakeside)
		{
		}

		protected override bool IsInNeed(LivestockAnimal self)
		{
			return self.NeedsWater();
		}

		protected override void FindDeployables(LivestockAnimal self, List<BaseEntity> results)
		{
			self.FindDrinkBarrels(results);
		}

		protected override bool IsUsableTerrain(LivestockAnimal self, Vector3 position)
		{
			return self.IsDrinkableShore(position);
		}
	}

	[Serializable]
	public class State_Drink : State_ConsumeAtLocation
	{
		protected override bool CanConsumeFrom(LivestockAnimal self, BaseEntity source)
		{
			if (source is LiquidContainer container)
			{
				return self.CanDrinkFrom(container);
			}
			return false;
		}

		protected override void Consume(LivestockAnimal self, BaseEntity source)
		{
			if (source is LiquidContainer container)
			{
				self.ConsumeFromBarrel(container);
			}
		}

		protected override void SetConsuming(LivestockAnimal self, bool consuming)
		{
			self.SetDrinking(consuming);
		}
	}

	[Serializable]
	public class State_Patrol_Points : FSMStateBase
	{
		public bool Enabled;

		[SerializeField]
		private Transform[] TargetPoints;

		[Tooltip("Speeds are cycled through, one every 'speedCycleInterval' seconds, to preview movement animations")]
		[SerializeField]
		private RustNavMeshAgent.Speeds[] speeds = new RustNavMeshAgent.Speeds[4]
		{
			RustNavMeshAgent.Speeds.Walk,
			RustNavMeshAgent.Speeds.Jog,
			RustNavMeshAgent.Speeds.Run,
			RustNavMeshAgent.Speeds.Sprint
		};

		[Tooltip("How often (in seconds) to advance to the next speed in the array")]
		[SerializeField]
		private float speedCycleInterval = 10f;

		private int currentPointIndex;

		private int currentSpeedIndex;

		private double nextSpeedChangeTime;

		private RustNavMeshAgent.Speeds CurrentSpeed
		{
			get
			{
				if (speeds == null || speeds.Length == 0)
				{
					return RustNavMeshAgent.Speeds.Walk;
				}
				return speeds[currentSpeedIndex];
			}
		}

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			if (TargetPoints == null || TargetPoints.Length == 0)
			{
				if (AI.logIssues)
				{
					Debug.LogError($"[FSM] {base.Name} has no patrol points set on {Owner}", Owner);
				}
				return EFSMStateStatus.Failure;
			}
			currentPointIndex = 0;
			currentSpeedIndex = 0;
			nextSpeedChangeTime = UnityEngine.Time.timeAsDouble + (double)speedCycleInterval;
			if (!MoveToCurrentPoint())
			{
				return EFSMStateStatus.Failure;
			}
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (speeds != null && speeds.Length != 0 && UnityEngine.Time.timeAsDouble >= nextSpeedChangeTime)
			{
				currentSpeedIndex = (currentSpeedIndex + 1) % speeds.Length;
				nextSpeedChangeTime = UnityEngine.Time.timeAsDouble + (double)speedCycleInterval;
				base.Agent.SetGait(speeds[currentSpeedIndex]);
			}
			if (!base.Agent.hasPath)
			{
				currentPointIndex = (currentPointIndex + 1) % TargetPoints.Length;
				if (!MoveToCurrentPoint())
				{
					return EFSMStateStatus.Failure;
				}
			}
			return base.OnStateUpdate(deltaTime);
		}

		public override void OnStateExit()
		{
			base.Agent.ResetPath();
			base.OnStateExit();
		}

		private bool MoveToCurrentPoint()
		{
			Transform transform = TargetPoints[currentPointIndex];
			if (transform == null)
			{
				return false;
			}
			if (!base.Agent.SamplePosition(transform.position, out var hitWS, 10f))
			{
				return false;
			}
			return base.Agent.SetDestinationWithParams(hitWS.position, autoBraking: true, CurrentSpeed, null, null, null, null, null);
		}
	}

	[Serializable]
	public class Trans_CanBreed : FSMSlowTransitionBase
	{
		[Tooltip("How long (in seconds) before searching again after finding nobody worth walking to")]
		[SerializeField]
		private float retryDelay = 15f;

		protected override bool EvaluateAtInterval(ref FSMPayload payload)
		{
			using (TimeWarning.New("Trans_CanBreed"))
			{
				if (!Livestock.breedingEnabled)
				{
					return false;
				}
				if (!(Owner is LivestockAnimal livestockAnimal) || !livestockAnimal.CanBreed())
				{
					return false;
				}
				if (!livestockAnimal.IsHappy)
				{
					return false;
				}
				if (!Owner.TryGetComponent<LivestockAnimalFSM>(out var component) || (float)component.NextMateSearch > 0f)
				{
					return false;
				}
				using PooledList<LivestockAnimal> pooledList = Facepunch.Pool.Get<PooledList<LivestockAnimal>>();
				BaseEntity.Query.Server.GetBrainsInSphere(Owner.transform.position, Livestock.breedSearchRadius, pooledList);
				for (int num = pooledList.Count - 1; num >= 0; num--)
				{
					if (!IsValidMate(livestockAnimal, pooledList[num]))
					{
						pooledList.RemoveAt(num);
					}
				}
				float distanceSqr = -1f;
				for (int i = 0; i < 3; i++)
				{
					LivestockAnimal livestockAnimal2 = Eqs.NearestBeyond(pooledList, Owner.transform.position, distanceSqr, out distanceSqr);
					if (livestockAnimal2 == null)
					{
						break;
					}
					if (base.Agent.CanReach(livestockAnimal2.transform.position))
					{
						component.MateTarget.Set(livestockAnimal2);
						return true;
					}
				}
				component.NextMateSearch = retryDelay;
				return false;
			}
		}

		private static bool IsValidMate(LivestockAnimal self, LivestockAnimal candidate)
		{
			if (!self.IsHerdMate(candidate))
			{
				return false;
			}
			if (!candidate.CanBePregnant() || candidate.IsLeading())
			{
				return false;
			}
			if (!candidate.IsHappy || !self.IsHappy)
			{
				return false;
			}
			if (!candidate.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				return false;
			}
			LivestockAnimal livestockAnimal = component.MateSuitor.Get(serverside: true);
			if (!(livestockAnimal == null))
			{
				return livestockAnimal == self;
			}
			return true;
		}
	}

	[Serializable]
	public class Trans_HasMateRequest : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			using (TimeWarning.New("Trans_HasMateRequest"))
			{
				if (!Owner.TryGetComponent<LivestockAnimalFSM>(out var component) || (float)component.MateRequestExpiry <= 0f)
				{
					return false;
				}
				return component.MateSuitor.Get(serverside: true) != null;
			}
		}
	}

	[Serializable]
	public class State_SeekMate : FSMStateBase
	{
		private enum Phase
		{
			Approaching,
			Withdrawing
		}

		[SerializeField]
		private RustNavMeshAgent.Speeds minSpeed = RustNavMeshAgent.Speeds.Walk;

		[SerializeField]
		private RustNavMeshAgent.Speeds maxSpeed = RustNavMeshAgent.Speeds.Jog;

		[Tooltip("Distance at which the male moves at maxSpeed; he eases off as he closes in")]
		[SerializeField]
		private float fullSpeedDistance = 20f;

		[Tooltip("How close the male has to get before he has met her and leads her away")]
		[SerializeField]
		private float breedDistance = 2f;

		[Tooltip("Give up if the walk over takes longer than this")]
		[SerializeField]
		private float approachTimeout = 30f;

		[Tooltip("How far off the pair look for somewhere quiet, once he has reached her. Zero has them stay where they met.")]
		[SerializeField]
		private Vector2 withdrawRange = new Vector2(12f, 25f);

		[Tooltip("How much room the quiet spot needs, with no other herd mate standing inside it.")]
		[SerializeField]
		private float withdrawClearance = 10f;

		[Tooltip("Give up on the walk out and settle where they got to after this long. Counted in the 45 seconds a pair can be away from the herd before their Social runs down.")]
		[SerializeField]
		private float withdrawTimeout = 20f;

		[Tooltip("How long (in seconds) after leaving this state before looking for a mate again")]
		[SerializeField]
		private float retryDelay = 15f;

		private Phase phase;

		private EntityRef<LivestockAnimal> mate;

		private TimeUntil approachDeadline;

		private TimeUntil withdrawDeadline;

		private Vector3 quietSpot;

		private bool handingOver;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			mate = default(EntityRef<LivestockAnimal>);
			handingOver = false;
			if (!(Owner is LivestockAnimal self) || !Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				return EFSMStateStatus.Failure;
			}
			LivestockAnimal livestockAnimal = component.MateTarget.Get(serverside: true);
			component.MateTarget = default(EntityRef<LivestockAnimal>);
			if (livestockAnimal == null || !livestockAnimal.CanBePregnant() || !TryClaim(self, livestockAnimal))
			{
				return EFSMStateStatus.Failure;
			}
			mate.Set(livestockAnimal);
			component.MatePartner.Set(livestockAnimal);
			phase = Phase.Approaching;
			approachDeadline = approachTimeout;
			base.Senses.TrySetTarget(livestockAnimal);
			if (Vector3.Distance(Owner.transform.position, livestockAnimal.transform.position) <= breedDistance)
			{
				return StartWithdrawing(self, livestockAnimal);
			}
			if (!MoveToMate(livestockAnimal))
			{
				return EFSMStateStatus.Failure;
			}
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.IsLeading())
			{
				return EFSMStateStatus.Failure;
			}
			LivestockAnimal livestockAnimal2 = mate.Get(serverside: true);
			if (livestockAnimal2 == null || !livestockAnimal2.CanBePregnant())
			{
				return EFSMStateStatus.Failure;
			}
			if (!livestockAnimal2.TryGetComponent<LivestockAnimalFSM>(out var component) || component.MateSuitor.Get(serverside: true) != livestockAnimal)
			{
				return EFSMStateStatus.Failure;
			}
			switch (phase)
			{
			case Phase.Approaching:
			{
				if ((float)approachDeadline <= 0f)
				{
					return EFSMStateStatus.Failure;
				}
				Vector3 position = livestockAnimal2.transform.position;
				if (Vector3.Distance(Owner.transform.position, position) <= breedDistance)
				{
					return StartWithdrawing(livestockAnimal, livestockAnimal2);
				}
				if (Vector3.Distance(base.Agent.destination.Value, position) > breedDistance * 3f && !MoveToMate(livestockAnimal2))
				{
					return EFSMStateStatus.Failure;
				}
				if (!base.Agent.hasPath)
				{
					return EFSMStateStatus.Failure;
				}
				return base.OnStateUpdate(deltaTime);
			}
			case Phase.Withdrawing:
				if ((float)withdrawDeadline <= 0f || Vector3.Distance(Owner.transform.position, quietSpot) <= breedDistance || !base.Agent.hasPath)
				{
					return HandOver();
				}
				return base.OnStateUpdate(deltaTime);
			default:
				return base.OnStateUpdate(deltaTime);
			}
		}

		private EFSMStateStatus StartWithdrawing(LivestockAnimal self, LivestockAnimal target)
		{
			base.Agent.ResetPath();
			if (withdrawRange.y <= 0f || !self.TryFindSecludedSpot(base.Agent, target, withdrawRange.x, withdrawRange.y, withdrawClearance, out quietSpot))
			{
				return HandOver();
			}
			if (!MoveWithRampedSpeed(base.Agent, quietSpot, fullSpeedDistance, minSpeed, maxSpeed))
			{
				return HandOver();
			}
			phase = Phase.Withdrawing;
			withdrawDeadline = withdrawTimeout;
			return EFSMStateStatus.None;
		}

		private EFSMStateStatus HandOver()
		{
			handingOver = true;
			return EFSMStateStatus.Success;
		}

		public override void OnStateExit()
		{
			base.Agent.ResetPath();
			if (Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.SetMating(mating: false);
				LivestockAnimal livestockAnimal2 = mate.Get(serverside: true);
				if (!handingOver && livestockAnimal2 != null && livestockAnimal2.TryGetComponent<LivestockAnimalFSM>(out var component) && component.MateSuitor.Get(serverside: true) == livestockAnimal)
				{
					component.MateSuitor = default(EntityRef<LivestockAnimal>);
					livestockAnimal2.SetMating(mating: false);
				}
			}
			mate = default(EntityRef<LivestockAnimal>);
			if (Owner.TryGetComponent<LivestockAnimalFSM>(out var component2))
			{
				component2.NextMateSearch = retryDelay;
				if (!handingOver)
				{
					component2.MatePartner = default(EntityRef<LivestockAnimal>);
				}
			}
			base.OnStateExit();
		}

		private bool TryClaim(LivestockAnimal self, LivestockAnimal target)
		{
			if (!target.TryGetComponent<LivestockAnimalFSM>(out var component) || !self.TryGetComponent<LivestockAnimalFSM>(out var component2))
			{
				return false;
			}
			LivestockAnimal livestockAnimal = component.MateSuitor.Get(serverside: true);
			if (livestockAnimal != null && livestockAnimal != self)
			{
				return false;
			}
			component.MateSuitor.Set(self);
			component.MateRequestExpiry = approachTimeout + withdrawTimeout + component2.frolic.MaxDuration + component2.mate.MaxDuration + 5f;
			return true;
		}

		private bool MoveToMate(LivestockAnimal target)
		{
			return MoveWithRampedSpeed(base.Agent, target.transform.position, fullSpeedDistance, minSpeed, maxSpeed);
		}
	}

	[Serializable]
	public class State_AwaitMate : FSMStateBase
	{
		[SerializeField]
		private RustNavMeshAgent.Speeds minSpeed = RustNavMeshAgent.Speeds.Walk;

		[SerializeField]
		private RustNavMeshAgent.Speeds maxSpeed = RustNavMeshAgent.Speeds.Jog;

		[Tooltip("How far she lets him get before she follows. Inside it she stands and waits.")]
		[SerializeField]
		private float courtDistance = 6f;

		[Tooltip("How far he has to move from where she was already headed before she re-paths.")]
		[SerializeField]
		private float repathTolerance = 3f;

		[Tooltip("Distance at which she moves at maxSpeed; she eases off as she closes in")]
		[SerializeField]
		private float fullSpeedDistance = 15f;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			if (!Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				return EFSMStateStatus.Failure;
			}
			LivestockAnimal livestockAnimal = component.MateSuitor.Get(serverside: true);
			if (livestockAnimal == null)
			{
				return EFSMStateStatus.Failure;
			}
			component.MatePartner.Set(livestockAnimal);
			base.Agent.ResetPath();
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!(Owner is LivestockAnimal livestockAnimal))
			{
				return EFSMStateStatus.Failure;
			}
			if (livestockAnimal.IsPregnant())
			{
				return EFSMStateStatus.Success;
			}
			if (livestockAnimal.IsLeading())
			{
				return EFSMStateStatus.Failure;
			}
			if (!Owner.TryGetComponent<LivestockAnimalFSM>(out var component) || (float)component.MateRequestExpiry <= 0f)
			{
				return EFSMStateStatus.Failure;
			}
			LivestockAnimal livestockAnimal2 = component.MateSuitor.Get(serverside: true);
			if (livestockAnimal2 == null || livestockAnimal2.IsDead())
			{
				return EFSMStateStatus.Failure;
			}
			KeepUpWith(livestockAnimal2);
			return base.OnStateUpdate(deltaTime);
		}

		private void KeepUpWith(LivestockAnimal suitor)
		{
			Vector3 position = suitor.transform.position;
			if (Vector3.Distance(Owner.transform.position, position) <= courtDistance)
			{
				if (base.Agent.hasPath)
				{
					base.Agent.ResetPath();
				}
			}
			else if (!base.Agent.hasPath || !(Vector3.Distance(base.Agent.destination.Value, position) <= repathTolerance))
			{
				MoveWithRampedSpeed(base.Agent, position, fullSpeedDistance, minSpeed, maxSpeed);
			}
		}

		public override void OnStateExit()
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.SetMating(mating: false);
			}
			base.OnStateExit();
		}
	}

	[Serializable]
	public class Trans_SuitorIsFrolicking : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				return false;
			}
			LivestockAnimal livestockAnimal = component.MateSuitor.Get(serverside: true);
			if (livestockAnimal != null && livestockAnimal.TryGetComponent<LivestockAnimalFSM>(out var component2))
			{
				return component2.CurrentState == component2.frolic;
			}
			return false;
		}
	}

	[Serializable]
	public class Trans_IsMale : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				return livestockAnimal.IsMale;
			}
			return false;
		}
	}

	[Serializable]
	public class Trans_IsCourting : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				return component.WantsToBeAlone;
			}
			return false;
		}
	}

	[Serializable]
	public class State_Frolic : State_Circle
	{
		[Tooltip("How long (in seconds) the pair circle each other before they settle. Counted in the 45 seconds a pair can be away from the herd before their Social runs down.")]
		[SerializeField]
		private float duration = 12f;

		[Tooltip("The gait for the stretches between canters. Circling at one speed for the whole frolic reads as a loop rather than as play.")]
		[SerializeField]
		private RustNavMeshAgent.Speeds walkGait = RustNavMeshAgent.Speeds.Walk;

		[Tooltip("How long a stretch of cantering, or of walking, lasts before it swaps to the other. Rolled fresh each time so the two of them fall in and out of step.")]
		[SerializeField]
		private Vector2 gaitStretchRange = new Vector2(1.5f, 3.5f);

		private TimeUntil frolicEnd;

		private RustNavMeshAgent.Speeds canterGait;

		private TimeUntil nextGaitStretch;

		private bool cantering;

		public float MaxDuration => duration;

		public State_Frolic()
		{
			radius = 5f;
			speed = RustNavMeshAgent.Speeds.Run;
		}

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			canterGait = speed;
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.IsLeading() || !Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				return EFSMStateStatus.Failure;
			}
			if (component.MatePartner.Get(serverside: true) == null)
			{
				return EFSMStateStatus.Failure;
			}
			frolicEnd = duration;
			EFSMStateStatus eFSMStateStatus = base.OnStateEnter(payload);
			if (eFSMStateStatus == EFSMStateStatus.Failure)
			{
				return eFSMStateStatus;
			}
			StartStretch(livestockAnimal, component, canter: true);
			return eFSMStateStatus;
		}

		private void StartStretch(LivestockAnimal self, LivestockAnimalFSM fsm, bool canter)
		{
			cantering = canter;
			speed = (canter ? canterGait : walkGait);
			nextGaitStretch = UnityEngine.Random.Range(gaitStretchRange.x, gaitStretchRange.y);
			self.SetFrisky(canter && fsm.friskyCanter.HasFriskyCanter);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.IsLeading())
			{
				return EFSMStateStatus.Failure;
			}
			if ((float)frolicEnd <= 0f)
			{
				return EFSMStateStatus.Success;
			}
			if (GetPartner() == null)
			{
				return EFSMStateStatus.Failure;
			}
			if ((float)nextGaitStretch <= 0f && Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				StartStretch(livestockAnimal, component, !cantering);
			}
			EFSMStateStatus eFSMStateStatus = base.OnStateUpdate(deltaTime);
			if (eFSMStateStatus != EFSMStateStatus.Failure)
			{
				return eFSMStateStatus;
			}
			return EFSMStateStatus.None;
		}

		public override void OnStateExit()
		{
			speed = canterGait;
			if (Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.SetFrisky(frisky: false);
			}
			base.OnStateExit();
		}

		protected override bool GetCircleOrigin(out Vector3 origin)
		{
			origin = default(Vector3);
			LivestockAnimal partner = GetPartner();
			if (partner == null)
			{
				return false;
			}
			origin = partner.transform.position;
			return true;
		}

		private LivestockAnimal GetPartner()
		{
			if (!Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				return null;
			}
			return component.MatePartner.Get(serverside: true);
		}
	}

	[Serializable]
	public class State_Mate : FSMStateBase
	{
		private enum Phase
		{
			Closing,
			Mating,
			Lingering
		}

		[SerializeField]
		private RustNavMeshAgent.Speeds minSpeed = RustNavMeshAgent.Speeds.Walk;

		[SerializeField]
		private RustNavMeshAgent.Speeds maxSpeed = RustNavMeshAgent.Speeds.Jog;

		[Tooltip("Distance at which he moves at maxSpeed; he eases off as he closes in")]
		[SerializeField]
		private float fullSpeedDistance = 10f;

		[Tooltip("How close he has to get before mating starts")]
		[SerializeField]
		private float breedDistance = 2f;

		[Tooltip("Longest he spends closing the last of the distance to her")]
		[SerializeField]
		private float closeTimeout = 10f;

		[Tooltip("How long (in seconds) the pair stand together before she becomes pregnant")]
		[SerializeField]
		private float matingDuration = 3f;

		[Tooltip("How long he stays with her afterwards, watching her go down, before he gets on with his day. Counted in the 45 seconds a pair can be away from the herd.")]
		[SerializeField]
		private float afterMatingLinger = 8f;

		[Tooltip("How long (in seconds) after leaving this state before looking for a mate again")]
		[SerializeField]
		private float retryDelay = 15f;

		private Phase phase;

		private EntityRef<LivestockAnimal> mate;

		private TimeUntil closeDeadline;

		private TimeUntil matingEndTime;

		private TimeUntil lingerEndTime;

		public float MaxDuration => closeTimeout + matingDuration + afterMatingLinger;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			mate = default(EntityRef<LivestockAnimal>);
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.IsLeading() || !Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				return EFSMStateStatus.Failure;
			}
			LivestockAnimal livestockAnimal2 = component.MatePartner.Get(serverside: true);
			if (livestockAnimal2 == null || !livestockAnimal2.CanBePregnant() || !StillClaimedBy(livestockAnimal, livestockAnimal2))
			{
				return EFSMStateStatus.Failure;
			}
			mate.Set(livestockAnimal2);
			phase = Phase.Closing;
			closeDeadline = closeTimeout;
			base.Senses.TrySetTarget(livestockAnimal2);
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.IsLeading())
			{
				return EFSMStateStatus.Failure;
			}
			LivestockAnimal livestockAnimal2 = mate.Get(serverside: true);
			if (livestockAnimal2 == null || !StillClaimedBy(livestockAnimal, livestockAnimal2))
			{
				return EFSMStateStatus.Failure;
			}
			switch (phase)
			{
			case Phase.Closing:
				if (!livestockAnimal2.CanBePregnant())
				{
					return EFSMStateStatus.Failure;
				}
				if (Vector3.Distance(Owner.transform.position, livestockAnimal2.transform.position) <= breedDistance)
				{
					base.Agent.ResetPath();
					phase = Phase.Mating;
					matingEndTime = matingDuration;
					livestockAnimal.SetMating(mating: true);
					livestockAnimal2.SetMating(mating: true);
					return EFSMStateStatus.None;
				}
				if ((float)closeDeadline <= 0f)
				{
					return EFSMStateStatus.Failure;
				}
				MoveWithRampedSpeed(base.Agent, livestockAnimal2.transform.position, fullSpeedDistance, minSpeed, maxSpeed);
				return base.OnStateUpdate(deltaTime);
			case Phase.Mating:
				if ((float)matingEndTime > 0f)
				{
					return EFSMStateStatus.None;
				}
				if (!livestockAnimal2.MakePregnant(livestockAnimal))
				{
					return EFSMStateStatus.Failure;
				}
				livestockAnimal.SetMating(mating: false);
				livestockAnimal2.SetMating(mating: false);
				phase = Phase.Lingering;
				lingerEndTime = afterMatingLinger;
				return EFSMStateStatus.None;
			case Phase.Lingering:
				if (!((float)lingerEndTime > 0f))
				{
					return EFSMStateStatus.Success;
				}
				return EFSMStateStatus.None;
			default:
				return base.OnStateUpdate(deltaTime);
			}
		}

		public override void OnStateExit()
		{
			base.Agent.ResetPath();
			if (Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.SetMating(mating: false);
				LivestockAnimal livestockAnimal2 = mate.Get(serverside: true);
				if (livestockAnimal2 != null && livestockAnimal2.TryGetComponent<LivestockAnimalFSM>(out var component) && component.MateSuitor.Get(serverside: true) == livestockAnimal)
				{
					component.MateSuitor = default(EntityRef<LivestockAnimal>);
					component.MatePartner = default(EntityRef<LivestockAnimal>);
					livestockAnimal2.SetMating(mating: false);
				}
			}
			mate = default(EntityRef<LivestockAnimal>);
			if (Owner.TryGetComponent<LivestockAnimalFSM>(out var component2))
			{
				component2.MatePartner = default(EntityRef<LivestockAnimal>);
				component2.NextMateSearch = retryDelay;
			}
			base.OnStateExit();
		}

		private static bool StillClaimedBy(LivestockAnimal self, LivestockAnimal target)
		{
			if (target.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				return component.MateSuitor.Get(serverside: true) == self;
			}
			return false;
		}
	}

	[Serializable]
	public class State_Pregnant : FSMStateBase
	{
		[Tooltip("How long (in seconds) she stays sat after giving birth, so the animation has time to play out before she walks off.")]
		public float StandUpDuration = 5f;

		[Tooltip("How long (in seconds) she stays lying down after calving, before she gets up. Standing the instant the calf lands looks like nothing happened to her.")]
		[SerializeField]
		private float afterBirthLieDown = 8f;

		[Tooltip("Longest (in seconds) she waits on a calf that is still down, on top of the stand up. A calf killed or stuck mid sequence would otherwise hold her forever.")]
		[SerializeField]
		private float waitForCalfTimeout = 30f;

		private float standUpTime;

		private TimeUntil giveUpOnCalf;

		private TimeUntil stayDownUntil;

		private bool lyingIn;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			base.Agent.ResetPath();
			standUpTime = 0f;
			lyingIn = false;
			giveUpOnCalf = StandUpDuration + waitForCalfTimeout;
			if (Owner is LivestockAnimal livestockAnimal && livestockAnimal.IsPregnant())
			{
				livestockAnimal.SetLyingIn(lyingIn: true);
			}
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!(Owner is LivestockAnimal livestockAnimal))
			{
				return EFSMStateStatus.Failure;
			}
			if (livestockAnimal.IsLeading())
			{
				return EFSMStateStatus.Failure;
			}
			if (livestockAnimal.IsCarrying)
			{
				return EFSMStateStatus.None;
			}
			if (livestockAnimal.IsPregnant())
			{
				if (!lyingIn)
				{
					lyingIn = true;
					stayDownUntil = afterBirthLieDown;
				}
				if ((float)stayDownUntil > 0f)
				{
					return EFSMStateStatus.None;
				}
				livestockAnimal.FinishPregnancy();
			}
			standUpTime += deltaTime;
			if (standUpTime <= StandUpDuration)
			{
				return EFSMStateStatus.None;
			}
			if (livestockAnimal.TryGetNewbornCalf(out var calf) && (float)giveUpOnCalf > 0f)
			{
				base.Senses.TrySetTarget(calf);
				return EFSMStateStatus.None;
			}
			return EFSMStateStatus.Success;
		}

		public override void OnStateExit()
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				if (livestockAnimal.HasCalved)
				{
					livestockAnimal.FinishPregnancy();
				}
				livestockAnimal.SetLyingIn(lyingIn: false);
			}
			if (base.Senses.FindTarget(out var target) && target is LivestockAnimal)
			{
				base.Senses.TrySetTarget(null);
			}
			base.OnStateExit();
		}
	}

	[Serializable]
	public class Trans_IsPregnant : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				return livestockAnimal.IsCalving;
			}
			return false;
		}
	}

	[Serializable]
	public class State_Newborn : FSMStateBase
	{
		private enum Phase
		{
			Asleep,
			HeadUp,
			Standing
		}

		[Tooltip("How long (in seconds) it lies there before lifting its head.")]
		[SerializeField]
		private float sleepDuration = 6f;

		[Tooltip("How long (in seconds) it sits up with its head about before trying to stand.")]
		[SerializeField]
		private float restDuration = 8f;

		[Tooltip("How long (in seconds) to let the getting up play out before the state ends.")]
		[SerializeField]
		private float standUpDuration = 2.5f;

		private Phase phase;

		private TimeUntil nextPhase;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal))
			{
				return EFSMStateStatus.Failure;
			}
			phase = Phase.Asleep;
			nextPhase = sleepDuration;
			base.Agent.ResetPath();
			livestockAnimal.SetSleeping(sleeping: true);
			livestockAnimal.SetResting(resting: false);
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!(Owner is LivestockAnimal livestockAnimal))
			{
				return EFSMStateStatus.Failure;
			}
			if ((float)nextPhase > 0f)
			{
				return EFSMStateStatus.None;
			}
			switch (phase)
			{
			case Phase.Asleep:
				livestockAnimal.SetSleeping(sleeping: false);
				livestockAnimal.SetResting(resting: true);
				phase = Phase.HeadUp;
				nextPhase = restDuration;
				return EFSMStateStatus.None;
			case Phase.HeadUp:
				livestockAnimal.SetResting(resting: false);
				phase = Phase.Standing;
				nextPhase = standUpDuration;
				return EFSMStateStatus.None;
			default:
				return EFSMStateStatus.Success;
			}
		}

		public override void OnStateExit()
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.SetSleeping(sleeping: false);
				livestockAnimal.SetResting(resting: false);
				livestockAnimal.FinishFindingItsFeet();
			}
			base.OnStateExit();
		}
	}

	[Serializable]
	public class Trans_IsNewborn : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				return livestockAnimal.IsFindingItsFeet;
			}
			return false;
		}
	}

	[Serializable]
	public class State_FriskyCanter : State_MoveToTarget
	{
		[Tooltip("Whether this species has the happy canter on its animator. Without it the flag would be networked to a controller that does nothing with it, and the animal would just canter about for no visible reason.")]
		[SerializeField]
		private bool hasFriskyCanter;

		[Tooltip("How far short of whoever she is running to she pulls up. She is coming over to say hello, not to barge into them.")]
		[SerializeField]
		private float standoff = 4f;

		[Tooltip("How far off she runs when there is nobody to run to, picked in this range.")]
		[SerializeField]
		private Vector2 wanderRange = new Vector2(12f, 22f);

		[Tooltip("Longest (in seconds) the canter lasts, however far the destination turned out to be. Stops an unreachable one keeping her skipping about all day.")]
		[SerializeField]
		private float maxDuration = 8f;

		private TimeUntil canterTimeout;

		private Vector3? wanderTo;

		public bool HasFriskyCanter => hasFriskyCanter;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			wanderTo = null;
			canterTimeout = maxDuration;
			EFSMStateStatus eFSMStateStatus = base.OnStateEnter(payload);
			if (eFSMStateStatus == EFSMStateStatus.Failure)
			{
				return eFSMStateStatus;
			}
			if (Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.SetFrisky(frisky: true);
			}
			return eFSMStateStatus;
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if ((float)canterTimeout <= 0f)
			{
				return EFSMStateStatus.Success;
			}
			return base.OnStateUpdate(deltaTime);
		}

		public override void OnStateExit()
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.SetFrisky(frisky: false);
			}
			base.OnStateExit();
		}

		protected override bool GetMoveDestination(out NavVector3 destination)
		{
			destination = default(NavVector3);
			BasePlayer basePlayer = ((Owner is LivestockAnimal livestockAnimal) ? livestockAnimal.NearbyTrustedPlayer : null);
			Vector3 targetPosition = ((basePlayer != null) ? basePlayer.transform.position : Vector3.zero);
			if (basePlayer != null || base.Senses.FindTargetPosition(out targetPosition))
			{
				Vector3 vector = (Owner.transform.position - targetPosition).NormalizeXZ();
				Vector3 positionWS = ((vector.sqrMagnitude < 0.001f) ? targetPosition : (targetPosition + vector * standoff));
				if (base.Agent.SamplePosition(positionWS, out var hitWS, 3f) && base.Agent.CanReach(hitWS.position))
				{
					destination = base.Agent.WorldToNavSpace(hitWS.position);
					return true;
				}
			}
			if (!wanderTo.HasValue)
			{
				Vector3 vector2 = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) * Vector3.forward;
				Vector3 positionWS2 = Owner.transform.position + vector2 * UnityEngine.Random.Range(wanderRange.x, wanderRange.y);
				if (!base.Agent.SamplePosition(positionWS2, out var hitWS2, 5f))
				{
					return false;
				}
				wanderTo = hitWS2.position;
			}
			destination = base.Agent.WorldToNavSpace(wanderTo.Value);
			return true;
		}
	}

	[Serializable]
	public class Trans_IsVeryHappy : FSMTransitionBase
	{
		[Tooltip("Chance of going for a canter, each time an idling animal is content enough for one.")]
		[Range(0f, 1f)]
		[SerializeField]
		private float chance = 0.15f;

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.IsLeading())
			{
				return false;
			}
			if (!Owner.TryGetComponent<LivestockAnimalFSM>(out var component) || !component.friskyCanter.HasFriskyCanter)
			{
				return false;
			}
			if (TOD_Sky.Instance.IsNight)
			{
				return false;
			}
			if (!livestockAnimal.IsVeryHappy)
			{
				return false;
			}
			if (livestockAnimal.TryGetRememberedAggressor(out var _))
			{
				return false;
			}
			return UnityEngine.Random.value < chance;
		}
	}

	[Serializable]
	public class State_StayNearMother : FSMStateBase
	{
		[SerializeField]
		private RustNavMeshAgent.Speeds minSpeed = RustNavMeshAgent.Speeds.Walk;

		[SerializeField]
		private RustNavMeshAgent.Speeds maxSpeed = RustNavMeshAgent.Speeds.Run;

		[Tooltip("Fastest gait an animal that is not answering for the herd will use to get back. A bull crossing a field at a run reads as urgency; a cow doing it reads as panic.")]
		[SerializeField]
		private RustNavMeshAgent.Speeds dependentMaxSpeed = RustNavMeshAgent.Speeds.Walk;

		[Tooltip("Distance at which the infant moves at maxSpeed; it eases off as it closes in")]
		[SerializeField]
		private float fullSpeedDistance = 25f;

		[Tooltip("How close the infant has to get before it settles down beside her again")]
		[SerializeField]
		private float reunitedDistance = 4f;

		[Tooltip("Give up if the walk back takes longer than this")]
		[SerializeField]
		private float returnTimeout = 30f;

		[Tooltip("How long (in seconds) after a failed walk back before trying again")]
		[SerializeField]
		private float retryDelay = 15f;

		private EntityRef<LivestockAnimal> companion;

		private TimeUntil returnDeadline;

		private bool walkFailed;

		protected virtual LivestockAnimal FindCompanion(LivestockAnimal self)
		{
			return self.MotherAnimal;
		}

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			companion = default(EntityRef<LivestockAnimal>);
			walkFailed = false;
			if (!(Owner is LivestockAnimal self))
			{
				return EFSMStateStatus.Failure;
			}
			LivestockAnimal livestockAnimal = FindCompanion(self);
			if (livestockAnimal == null || livestockAnimal.IsDead())
			{
				return EFSMStateStatus.Failure;
			}
			companion.Set(livestockAnimal);
			returnDeadline = returnTimeout;
			if (!MoveToCompanion(livestockAnimal))
			{
				return GiveUp();
			}
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.IsLeading())
			{
				return EFSMStateStatus.Failure;
			}
			LivestockAnimal livestockAnimal2 = companion.Get(serverside: true);
			if (livestockAnimal2 == null || livestockAnimal2.IsDead())
			{
				return EFSMStateStatus.Failure;
			}
			if ((float)returnDeadline <= 0f)
			{
				return GiveUp();
			}
			Vector3 position = livestockAnimal2.transform.position;
			if (Vector3.Distance(Owner.transform.position, position) <= reunitedDistance)
			{
				return EFSMStateStatus.Success;
			}
			if (Vector3.Distance(base.Agent.destination.Value, position) > reunitedDistance && !MoveToCompanion(livestockAnimal2))
			{
				return GiveUp();
			}
			if (!base.Agent.hasPath)
			{
				return GiveUp();
			}
			return base.OnStateUpdate(deltaTime);
		}

		public override void OnStateExit()
		{
			base.Agent.ResetPath();
			companion = default(EntityRef<LivestockAnimal>);
			if (walkFailed && Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				component.NextCompanionSearch = retryDelay;
			}
			base.OnStateExit();
		}

		private EFSMStateStatus GiveUp()
		{
			walkFailed = true;
			return EFSMStateStatus.Failure;
		}

		private bool MoveToCompanion(LivestockAnimal target)
		{
			RustNavMeshAgent.Speeds speeds = ((Owner is LivestockAnimal { IsDependent: not false }) ? dependentMaxSpeed : maxSpeed);
			return MoveWithRampedSpeed(base.Agent, target.transform.position, fullSpeedDistance, minSpeed, speeds);
		}
	}

	[Serializable]
	public class State_ReturnToHerd : State_StayNearMother
	{
		protected override LivestockAnimal FindCompanion(LivestockAnimal self)
		{
			return self.FindHerdToReturnTo();
		}
	}

	[Serializable]
	public class Trans_StrayedFromHerd : FSMSlowTransitionBase
	{
		[Tooltip("How far a bull can get from the nearest cow or calf before he heads back.")]
		[SerializeField]
		public float strayDistance = 20f;

		protected override bool EvaluateAtInterval(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.IsDead())
			{
				return false;
			}
			if (livestockAnimal.IsLeading() || livestockAnimal.IsInfant())
			{
				return false;
			}
			if (livestockAnimal.IsPregnant())
			{
				return false;
			}
			if (Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				if ((float)component.NextCompanionSearch > 0f)
				{
					return false;
				}
				if (component.CurrentState == component.rest)
				{
					return false;
				}
			}
			LivestockAnimal livestockAnimal2 = livestockAnimal.FindHerdToReturnTo();
			if (livestockAnimal2 == null)
			{
				return false;
			}
			return Vector3.Distance(livestockAnimal.transform.position, livestockAnimal2.transform.position) > strayDistance;
		}
	}

	[Serializable]
	public class Trans_StrayedFromMother : FSMTransitionBase
	{
		[Tooltip("How far (in metres) an infant can get from its mother before it heads back to her.")]
		[SerializeField]
		private float strayDistance = 15f;

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || !livestockAnimal.IsInfant())
			{
				return false;
			}
			if (livestockAnimal.IsSleeping())
			{
				return false;
			}
			if (Owner.TryGetComponent<LivestockAnimalFSM>(out var component) && (float)component.NextCompanionSearch > 0f)
			{
				return false;
			}
			LivestockAnimal motherAnimal = livestockAnimal.MotherAnimal;
			if (motherAnimal == null || motherAnimal.IsDead())
			{
				return false;
			}
			return Vector3.Distance(livestockAnimal.transform.position, motherAnimal.transform.position) > strayDistance;
		}
	}

	[Serializable]
	public class State_Sleep : FSMStateBase
	{
		private enum Phase
		{
			Asleep,
			WalkingHome,
			Gathering
		}

		public float WakeDuration = 10f;

		[Tooltip("How long (in seconds) the animal spends walking in to the herd before giving up and lying down where it stands. Stops one animal on the wrong side of a wall keeping the whole night awake.")]
		[SerializeField]
		private float gatherTimeout = 20f;

		[Tooltip("How long (in seconds) the walk home at dusk gets before the animal gives up and beds down where it stands. Longer than the herd gather, because home can be the whole roaming radius away while a companion is only metres off.")]
		[SerializeField]
		private float homeTimeout = 90f;

		private float wakeTime;

		[SerializeField]
		private RustNavMeshAgent.Speeds gatherMinSpeed = RustNavMeshAgent.Speeds.Walk;

		[SerializeField]
		private RustNavMeshAgent.Speeds gatherMaxSpeed = RustNavMeshAgent.Speeds.Walk;

		[Tooltip("How far out the walk in to the herd runs at full speed before easing down.")]
		[SerializeField]
		private float gatherFullSpeedDistance = 10f;

		private Phase phase;

		private TimeUntil gatherDeadline;

		private EntityRef<LivestockAnimal> gatherCompanion;

		private Vector3 gatherAim;

		private TimeSince dawnSeen;

		private bool sawDawn;

		public bool IsGathering => phase == Phase.Gathering;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			wakeTime = 0f;
			sawDawn = false;
			gatherCompanion = default(EntityRef<LivestockAnimal>);
			Vector3 home;
			if (!(Owner is LivestockAnimal livestockAnimal))
			{
				LieDown();
			}
			else if (livestockAnimal.TryGetBedtimeHome(out home) && MoveToBedtimeAim(home))
			{
				phase = Phase.WalkingHome;
				gatherDeadline = homeTimeout;
			}
			else if (!StartGathering(livestockAnimal))
			{
				LieDown();
			}
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!(Owner is LivestockAnimal livestockAnimal))
			{
				return EFSMStateStatus.Failure;
			}
			if (phase == Phase.WalkingHome)
			{
				if (livestockAnimal.TryGetBedtimeHome(out var home) && (float)gatherDeadline > 0f)
				{
					if (Vector3.Distance(gatherAim, home) > livestockAnimal.HerdGatherDistance)
					{
						MoveToBedtimeAim(home);
					}
					return base.OnStateUpdate(deltaTime);
				}
				if (!StartGathering(livestockAnimal))
				{
					LieDown();
				}
			}
			if (phase == Phase.Gathering)
			{
				LivestockAnimal livestockAnimal2 = gatherCompanion.Get(serverside: true);
				if (!(livestockAnimal2 == null) && !livestockAnimal2.IsDead() && !(Vector3.Distance(Owner.transform.position, livestockAnimal2.transform.position) <= livestockAnimal.HerdGatherDistance) && (float)gatherDeadline > 0f)
				{
					if (Vector3.Distance(gatherAim, livestockAnimal2.transform.position) > livestockAnimal.HerdGatherDistance)
					{
						MoveToCompanion(livestockAnimal2);
					}
					return base.OnStateUpdate(deltaTime);
				}
				LieDown();
			}
			if (TOD_Sky.Instance.IsNight)
			{
				sawDawn = false;
				return EFSMStateStatus.None;
			}
			if (!sawDawn)
			{
				sawDawn = true;
				dawnSeen = 0f;
			}
			if ((float)dawnSeen < livestockAnimal.SleepOffset)
			{
				return EFSMStateStatus.None;
			}
			SetSleeping(sleeping: false);
			wakeTime += deltaTime;
			if (!(wakeTime > WakeDuration))
			{
				return EFSMStateStatus.None;
			}
			return EFSMStateStatus.Success;
		}

		private bool MoveToCompanion(LivestockAnimal companion)
		{
			return MoveToBedtimeAim(companion.transform.position);
		}

		private bool MoveToBedtimeAim(Vector3 aim)
		{
			gatherAim = aim;
			return MoveWithRampedSpeed(base.Agent, gatherAim, gatherFullSpeedDistance, gatherMinSpeed, gatherMaxSpeed);
		}

		private bool StartGathering(LivestockAnimal animal)
		{
			LivestockAnimal livestockAnimal = animal.FindSleepingCompanion();
			if (livestockAnimal == null || !MoveToCompanion(livestockAnimal))
			{
				return false;
			}
			phase = Phase.Gathering;
			gatherCompanion.Set(livestockAnimal);
			gatherDeadline = gatherTimeout;
			return true;
		}

		private void LieDown()
		{
			phase = Phase.Asleep;
			gatherCompanion = default(EntityRef<LivestockAnimal>);
			base.Agent.ResetPath();
			SetSleeping(sleeping: true);
		}

		public override void OnStateExit()
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || !livestockAnimal.IsDead())
			{
				SetSleeping(sleeping: false);
			}
			base.OnStateExit();
		}

		private void SetSleeping(bool sleeping)
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.SetSleeping(sleeping);
			}
		}
	}

	[Serializable]
	public class Trans_WantsBedtimeRest : FSMTransitionBase
	{
		[Tooltip("Chance an animal dozes before dropping off rather than going straight to sleep.")]
		[Range(0f, 1f)]
		[SerializeField]
		private float chance = 0.5f;

		private bool rolled;

		private bool dozing;

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.IsSleeping())
			{
				return false;
			}
			if (!TOD_Sky.Instance.IsNight)
			{
				rolled = false;
				return false;
			}
			if (!Owner.TryGetComponent<LivestockAnimalFSM>(out var component) || !component.rest.HasRestingPose)
			{
				return false;
			}
			if (!rolled)
			{
				rolled = true;
				dozing = UnityEngine.Random.value <= chance;
			}
			return dozing;
		}

		public override void OnTransitionTaken(FSMStateBase from, FSMStateBase to)
		{
			dozing = false;
		}
	}

	[Serializable]
	public abstract class Trans_AfterFailedFoodSearch : FSMTransitionBase
	{
		[Tooltip("How soon (in seconds) after a failed food search the animal still reacts to it, so the reaction is tied to the search that just failed rather than to a long tail.")]
		[SerializeField]
		protected float window;

		[Tooltip("Shortest gap (in seconds) between two reactions. Without it the animal repeats this every time idle ends for as long as there is nothing to eat.")]
		[SerializeField]
		protected float minimumInterval;

		private double? lastTakenTime;

		protected Trans_AfterFailedFoodSearch(float window, float minimumInterval)
		{
			this.window = window;
			this.minimumInterval = minimumInterval;
		}

		protected abstract bool StillWants(LivestockAnimal animal, LivestockAnimalFSM fsm);

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal animal))
			{
				return false;
			}
			if (lastTakenTime.HasValue && UnityEngine.Time.timeAsDouble - lastTakenTime.Value < (double)minimumInterval)
			{
				return false;
			}
			if (Owner.TryGetComponent<LivestockAnimalFSM>(out var component) && (float)component.LastFoodSearchFailed < window)
			{
				return StillWants(animal, component);
			}
			return false;
		}

		public override void OnTransitionTaken(FSMStateBase from, FSMStateBase to)
		{
			lastTakenTime = UnityEngine.Time.timeAsDouble;
		}
	}

	[Serializable]
	public class Trans_FoodSearchFailed : Trans_AfterFailedFoodSearch
	{
		[Tooltip("Fullness has to be at or under this, not merely under the consume threshold. The animal is meant to look like it cannot find food, not merely peckish.")]
		[Range(0f, 1f)]
		[SerializeField]
		private float starvingBelow = 0.2f;

		public Trans_FoodSearchFailed()
			: base(5f, 60f)
		{
		}

		protected override bool StillWants(LivestockAnimal animal, LivestockAnimalFSM fsm)
		{
			if (animal.Fullness.NeedValue <= starvingBelow)
			{
				return fsm.foodSearch.HasAnimation;
			}
			return false;
		}
	}

	[Serializable]
	public class Trans_LooksElsewhereForFood : Trans_AfterFailedFoodSearch
	{
		public Trans_LooksElsewhereForFood()
			: base(20f, 30f)
		{
		}

		protected override bool StillWants(LivestockAnimal animal, LivestockAnimalFSM fsm)
		{
			return animal.NeedsFood();
		}
	}

	[Serializable]
	public class State_Rest : FSMStateBase
	{
		[Tooltip("Whether this species has the lying down pose set up on its animator. Without it the flag would be networked to a controller that does nothing with it.")]
		[SerializeField]
		private bool hasRestingPose;

		[Tooltip("How long (in seconds) the animal stays down, picked in this range. Long enough that the standing gap between two rests still leaves the day's resting share reachable - short lie downs with a fixed gap between them cannot add up to it.")]
		[SerializeField]
		private Vector2 durationRange = new Vector2(40f, 70f);

		[Tooltip("Scales the lie down after dark, where it is a doze on the way to sleep rather than a rest in its own right.")]
		[SerializeField]
		private float nightScale = 0.35f;

		[Tooltip("How long (in seconds) to let the getting up play out before the state ends.")]
		[SerializeField]
		private float standUpDuration = 2.5f;

		private TimeUntil stayDownUntil;

		private float standingUpTime;

		private bool gettingUp;

		private TimeSince stoodUp;

		private bool hasStoodUp;

		public bool HasRestingPose => hasRestingPose;

		public float SinceStoodUp
		{
			get
			{
				if (!hasStoodUp)
				{
					return float.MaxValue;
				}
				return stoodUp;
			}
		}

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			gettingUp = false;
			standingUpTime = 0f;
			float num = UnityEngine.Random.Range(durationRange.x, durationRange.y);
			if (Owner is LivestockAnimal livestockAnimal)
			{
				num *= (TOD_Sky.Instance.IsNight ? nightScale : livestockAnimal.AgeRestDurationScale);
				livestockAnimal.SetResting(resting: true);
			}
			stayDownUntil = num;
			base.Agent.ResetPath();
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!(Owner is LivestockAnimal livestockAnimal))
			{
				return EFSMStateStatus.Failure;
			}
			if (!gettingUp)
			{
				if ((float)stayDownUntil > 0f && !livestockAnimal.IsStarving())
				{
					return EFSMStateStatus.None;
				}
				gettingUp = true;
				livestockAnimal.SetResting(resting: false);
			}
			standingUpTime += deltaTime;
			if (!(standingUpTime > standUpDuration))
			{
				return EFSMStateStatus.None;
			}
			return EFSMStateStatus.Success;
		}

		public override void OnStateExit()
		{
			if (Owner is LivestockAnimal livestockAnimal && !livestockAnimal.IsDead())
			{
				livestockAnimal.SetResting(resting: false);
			}
			stoodUp = 0f;
			hasStoodUp = true;
			base.OnStateExit();
		}
	}

	[Serializable]
	public class Trans_WantsRest : FSMTransitionBase
	{
		[Tooltip("How long (in seconds) after getting up before the animal will lie down again.")]
		[SerializeField]
		private float minimumTimeStanding = 30f;

		[Tooltip("How long (in seconds) a food search that found nothing lets a hungry animal lie down anyway. A hungry animal with food in reach tops up first, one with none rests rather than pacing all day.")]
		[SerializeField]
		private float foodSearchFailedMemory = 60f;

		[Tooltip("Chance that an animal on its way to bed, or just woken up, lies down for a while first instead. Stops a herd going from standing to asleep in one step.")]
		[Range(0f, 1f)]
		[SerializeField]
		private float aroundBedtimeChance = 0.5f;

		[Tooltip("How long (in seconds) that lie down around bedtime lasts, picked in this range.")]
		[SerializeField]
		private Vector2 aroundBedtimeDuration = new Vector2(10f, 20f);

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal))
			{
				return false;
			}
			if (TOD_Sky.Instance.IsNight)
			{
				return false;
			}
			if (!Owner.TryGetComponent<LivestockAnimalFSM>(out var component) || !component.rest.HasRestingPose)
			{
				return false;
			}
			if (component.rest.SinceStoodUp < minimumTimeStanding)
			{
				return false;
			}
			if (livestockAnimal.NeedsFood() && (float)component.LastFoodSearchFailed >= foodSearchFailedMemory)
			{
				return false;
			}
			return true;
		}

		public bool RollBedtimeRest(out float duration)
		{
			duration = UnityEngine.Random.Range(aroundBedtimeDuration.x, aroundBedtimeDuration.y);
			return UnityEngine.Random.value <= aroundBedtimeChance;
		}
	}

	[Serializable]
	public class Trans_WantsSleep : FSMTransitionBase
	{
		private TimeSince duskSeen;

		private bool sawDusk;

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.IsSleeping())
			{
				return false;
			}
			if (!TOD_Sky.Instance.IsNight)
			{
				sawDusk = false;
				return false;
			}
			if (!sawDusk)
			{
				sawDusk = true;
				duskSeen = 0f;
			}
			return (float)duskSeen >= livestockAnimal.SleepOffset;
		}
	}

	[Serializable]
	public class State_ShovingAttack : State_Attack
	{
		[Tooltip("How hard the hit shoves a player. x lifts them off the ground enough for it to read, y drives them away from the animal.")]
		[SerializeField]
		private Vector2 pushForce = new Vector2(4f, 16f);

		[Tooltip("How far the blow reaches when it lands. Further than this and it whiffs, which is what makes the charge sidesteppable.")]
		[SerializeField]
		private float hitRange = 2.5f;

		[Tooltip("How wide the blow is, in degrees either side of our forward at the moment it lands. 180 removes the check, for an attack that does not face what it hits.")]
		[SerializeField]
		private float hitAngle = 60f;

		protected override void DoDamage()
		{
			if (!base.Senses.FindTarget(out var target) || !(target is BaseCombatEntity baseCombatEntity))
			{
				return;
			}
			Vector3 v = target.transform.position - Owner.transform.position;
			if (!(v.MagnitudeXZ() > hitRange) && (!(hitAngle < 180f) || !(Vector3.Angle(Owner.transform.forward, v.NormalizeXZ()) > hitAngle)))
			{
				baseCombatEntity.OnAttacked(Damage, DamageType, Owner, ignoreShield: false);
				if (baseCombatEntity.ToNonNpcPlayer(out var player))
				{
					player.DoPush(v.NormalizeXZ() * pushForce.y + Vector3.up * pushForce.x);
				}
			}
		}
	}

	[Serializable]
	public class State_HoldGround : FSMStateBase
	{
		[Tooltip("Gait once he is in position between the threat and whoever he is shielding.")]
		[SerializeField]
		public RustNavMeshAgent.Speeds speed = RustNavMeshAgent.Speeds.Walk;

		[Tooltip("How close to the threat a cow or calf has to be for him to put himself in front of it. Zero makes him only stand his ground.")]
		[SerializeField]
		private float shieldSearchRadius = 20f;

		[Tooltip("How often the search for someone to shield runs, in seconds. This state ticks every frame and the search is a sphere query, so it is not run every one.")]
		[SerializeField]
		private float shieldSearchInterval = 1f;

		[Tooltip("How far along the line from the threat to the animal he is shielding he tries to stand. 0.5 is squarely between the two of them.")]
		[Range(0f, 1f)]
		[SerializeField]
		private float interposeFraction = 0.5f;

		[Tooltip("He will not close on the threat past this, so the halfway point gets pushed back out when it would put him too close. Keep it above the range the charge triggers at or he will charge instead of ever settling.")]
		[SerializeField]
		private float minStandoff = 6f;

		[Tooltip("Gait used while he is still getting into position. Once he is there he drops to the state's own speed.")]
		[SerializeField]
		private RustNavMeshAgent.Speeds repositionSpeed = RustNavMeshAgent.Speeds.Run;

		[Tooltip("How far off his shielding spot counts as out of position, and worth hurrying.")]
		[SerializeField]
		private float inPositionTolerance = 1f;

		[Tooltip("How much room he leaves in front of the animal he is guarding, so he stands off her shoulder rather than in her.")]
		[SerializeField]
		private float shieldClearance = 3f;

		[Tooltip("How far to either side of the shielding spot a defender may stand, so two of them answering the same threat line up shoulder to shoulder instead of in each other. Wants to be about a body width. Zero puts them all on the same point.")]
		[SerializeField]
		private float shieldSlotSpread = 1.5f;

		private EntityRef<LivestockAnimal> shielded;

		private TimeUntil nextShieldSearch;

		private float shieldSlotOffset;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			if (payload.entity != null)
			{
				base.Senses.TrySetTarget(payload.entity);
			}
			shielded = default(EntityRef<LivestockAnimal>);
			nextShieldSearch = 0f;
			shieldSlotOffset = 0f;
			return base.OnStateEnter(payload);
		}

		public override void OnStateExit()
		{
			base.Agent.ResetPath();
			base.OnStateExit();
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!base.Senses.FindTargetPosition(out var targetPosition) || !TryGetShieldPosition(targetPosition, out var destination) || base.Agent.Raycast(destination, out var _))
			{
				return StandHisGround();
			}
			RustNavMeshAgent.Speeds value = ((Vector3.Distance(Owner.transform.position, destination) > inPositionTolerance) ? repositionSpeed : speed);
			if (!base.Agent.SetDestinationWithParams(base.Agent.WorldToNavSpace(destination), autoBraking: false, value, null, null, null, null, null))
			{
				return StandHisGround();
			}
			return EFSMStateStatus.None;
		}

		private EFSMStateStatus StandHisGround()
		{
			if (base.Agent.hasPath)
			{
				base.Agent.ResetPath();
			}
			return EFSMStateStatus.None;
		}

		private bool TryGetShieldPosition(Vector3 threatPosition, out Vector3 destination)
		{
			destination = default(Vector3);
			LivestockAnimal livestockAnimal = FindAnimalToShield(threatPosition);
			if (livestockAnimal == null)
			{
				return false;
			}
			Vector3 v = livestockAnimal.transform.position - threatPosition;
			float num = v.MagnitudeXZ();
			Vector3 vector = v.NormalizeXZ();
			if (vector.sqrMagnitude < 0.001f)
			{
				return false;
			}
			float a = Mathf.Max(num * interposeFraction, minStandoff);
			a = Mathf.Min(a, Mathf.Max(0f, num - shieldClearance));
			a = Mathf.Min(a, (Owner.transform.position - threatPosition).MagnitudeXZ());
			Vector3 vector2 = new Vector3(0f - vector.z, 0f, vector.x) * shieldSlotOffset;
			destination = threatPosition + vector * a + vector2;
			return true;
		}

		private float SlotToOffset(int slot)
		{
			if (slot <= 0)
			{
				return 0f;
			}
			int num = (slot + 1) / 2;
			return (float)((slot % 2 == 1) ? num : (-num)) * shieldSlotSpread;
		}

		private LivestockAnimal FindAnimalToShield(Vector3 threatPosition)
		{
			if (shieldSearchRadius <= 0f)
			{
				return null;
			}
			if ((float)nextShieldSearch > 0f)
			{
				LivestockAnimal livestockAnimal = shielded.Get(serverside: true);
				if (livestockAnimal != null && !livestockAnimal.IsDead())
				{
					return livestockAnimal;
				}
			}
			nextShieldSearch = shieldSearchInterval;
			if (!(Owner is LivestockAnimal livestockAnimal2))
			{
				return null;
			}
			LivestockAnimal livestockAnimal3 = livestockAnimal2.FindNearestDependent(threatPosition, shieldSearchRadius);
			shieldSlotOffset = ((livestockAnimal3 == null) ? 0f : SlotToOffset(livestockAnimal3.ClaimShieldSlot(livestockAnimal2)));
			shielded.Set(livestockAnimal3);
			livestockAnimal2.RememberShielded(livestockAnimal3);
			return livestockAnimal3;
		}
	}

	[Serializable]
	public class State_Stomp : State_PlayAnimationBase
	{
		[Tooltip("Preferred. A stomp with extracted root motion, so the step in it carries him a little way towards whoever earned it.")]
		[SerializeField]
		public RootMotionData RootMotionAnimation;

		[Tooltip("Fallback for a species with no extracted stomp. Plays on the spot.")]
		[SerializeField]
		public AnimationClip Animation;

		public bool HasAnimation
		{
			get
			{
				if (!(RootMotionAnimation != null))
				{
					return Animation != null;
				}
				return true;
			}
		}

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			FaceTarget = false;
			EFSMStateStatus result = base.OnStateEnter(payload);
			animState = ((RootMotionAnimation != null) ? base.AnimPlayer.PlayServerAndTakeFromPool(RootMotionAnimation) : base.AnimPlayer.PlayServerAndTakeFromPool(Animation));
			return result;
		}
	}

	[Serializable]
	public class State_SquareUpApproach : State_MoveToTarget
	{
		[Tooltip("Hand over once the target is within this many degrees of our forward.")]
		[SerializeField]
		private float faceAngle = 25f;

		[Tooltip("Longest he spends walking in. Someone circling him would never let him start otherwise, and the walk is only ever meant to be the turn.")]
		[SerializeField]
		private float maxDuration = 2f;

		private TimeUntil walkTimeout;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			if (IsTargetAhead())
			{
				return EFSMStateStatus.Success;
			}
			walkTimeout = maxDuration;
			EFSMStateStatus eFSMStateStatus = base.OnStateEnter(payload);
			if (eFSMStateStatus != EFSMStateStatus.Failure)
			{
				return eFSMStateStatus;
			}
			return EFSMStateStatus.Success;
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (IsTargetAhead() || (float)walkTimeout <= 0f)
			{
				return EFSMStateStatus.Success;
			}
			EFSMStateStatus eFSMStateStatus = base.OnStateUpdate(deltaTime);
			if (eFSMStateStatus != EFSMStateStatus.Failure)
			{
				return eFSMStateStatus;
			}
			return EFSMStateStatus.Success;
		}

		private bool IsTargetAhead()
		{
			if (!base.Senses.FindTargetPosition(out var targetPosition))
			{
				return true;
			}
			Vector3 to = (targetPosition - Owner.transform.position).NormalizeXZ();
			if (to.sqrMagnitude < 0.001f)
			{
				return true;
			}
			return Vector3.Angle(Owner.transform.forward, to) <= faceAngle;
		}
	}

	[Serializable]
	public class State_HerdFlee : State_Flee
	{
		[Tooltip("Keep running while whatever we are fleeing is still this close, rather than giving up on it. 0 leaves the base behaviour alone.")]
		[SerializeField]
		private float keepRunningWithin = 12f;

		[Tooltip("Longest (in seconds) that persistence lasts, so an animal with nowhere left to run stops rather than scrabbling at a corner forever.")]
		[SerializeField]
		private float maxPersistence = 20f;

		[Tooltip("How far a startled animal moves off, instead of the full flee distance. Somebody grabbing at an animal that will not have them is a shove, not a gunshot.")]
		[SerializeField]
		private float startleDistance = 6f;

		private bool ran;

		private TimeSince fleeingFor;

		private float authoredDistance;

		private float authoredDesiredDistance;

		private bool shortened;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			fleeingFor = 0f;
			shortened = Owner is LivestockAnimal livestockAnimal && livestockAnimal.TakeStartle();
			if (shortened)
			{
				authoredDistance = distance;
				authoredDesiredDistance = desiredDistance;
				distance = startleDistance;
				desiredDistance = startleDistance;
			}
			EFSMStateStatus eFSMStateStatus = base.OnStateEnter(payload);
			ran = eFSMStateStatus != EFSMStateStatus.Success;
			return eFSMStateStatus;
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			EFSMStateStatus eFSMStateStatus = base.OnStateUpdate(deltaTime);
			if (eFSMStateStatus != EFSMStateStatus.Success)
			{
				return eFSMStateStatus;
			}
			if (keepRunningWithin > 0f && (float)fleeingFor < maxPersistence && base.Senses.FindTargetPosition(out var targetPosition) && Vector3.Distance(Owner.transform.position, targetPosition) <= keepRunningWithin)
			{
				return MoveAwayFromTarget();
			}
			return eFSMStateStatus;
		}

		public override void OnStateExit()
		{
			if (ran && Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.ClearSpook();
			}
			if (shortened)
			{
				distance = authoredDistance;
				desiredDistance = authoredDesiredDistance;
				shortened = false;
			}
			base.OnStateExit();
		}

		protected override NavVector3 GetFleeDirection(NavVector3 posNS, Vector3 targetPosition)
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				if (livestockAnimal.TryGetHerdFleeHeading(out var heading))
				{
					livestockAnimal.PublishHerdFleeHeading(heading);
					return ToNavSpace(heading);
				}
				if (livestockAnimal.TryGetHerdCentroid(out var centroid))
				{
					Vector3 heading2 = (centroid - targetPosition).WithY(0f);
					if (heading2.sqrMagnitude > 1E-06f)
					{
						livestockAnimal.PublishHerdFleeHeading(heading2);
						return ToNavSpace(heading2);
					}
				}
				livestockAnimal.PublishHerdFleeHeading(Owner.transform.position - targetPosition);
			}
			return base.GetFleeDirection(posNS, targetPosition);
		}

		private NavVector3 ToNavSpace(Vector3 heading)
		{
			Vector3 position = Owner.transform.position;
			return (base.Agent.WorldToNavSpace(position + heading) - base.Agent.WorldToNavSpace(position)).NormalizeXZ();
		}
	}

	[Serializable]
	public class Trans_HerdMateHurt : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner is LivestockAnimal livestockAnimal)
			{
				return livestockAnimal.HerdMateRecentlyHurt;
			}
			return false;
		}

		public override void OnTransitionTaken(FSMStateBase from, FSMStateBase to)
		{
			base.OnTransitionTaken(from, to);
			if (Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.ClearHerdDistress();
			}
		}
	}

	[Serializable]
	public class Trans_WouldRunFromAHit : FSMSlowTransitionBase
	{
		[Tooltip("A bull runs once his health drops below this fraction of his maximum. Cows and calves run from any hit at all.")]
		[SerializeField]
		public float bullStandsGroundAbove = 0.8f;

		protected override bool EvaluateAtInterval(ref FSMPayload payload)
		{
			return Evaluate(Owner);
		}

		public bool Evaluate(BaseEntity owner)
		{
			if (!(owner is LivestockAnimal livestockAnimal))
			{
				return true;
			}
			if (livestockAnimal.InSafeZone())
			{
				return false;
			}
			if (livestockAnimal.IsLeading())
			{
				return true;
			}
			if (livestockAnimal.IsInfant())
			{
				return !livestockAnimal.HasProtectiveMotherNearby();
			}
			if (livestockAnimal.IsMale ? livestockAnimal.HasDependentsNearby() : livestockAnimal.IsProtectiveOfCalf)
			{
				return false;
			}
			if (livestockAnimal.IsMale && !(livestockAnimal.healthFraction < bullStandsGroundAbove))
			{
				return IsTargetOutOfReach(livestockAnimal);
			}
			return true;
		}

		private static bool IsTargetOutOfReach(LivestockAnimal animal)
		{
			if (animal.TryGetComponent<SenseComponent>(out var component) && component.FindTargetPosition(out var targetPosition) && animal.TryGetComponent<RustNavMeshAgent>(out var component2))
			{
				return !component2.CanReach(targetPosition);
			}
			return false;
		}
	}

	[Serializable]
	public class Trans_TargetNearHerd : FSMSlowTransitionBase
	{
		[Tooltip("How close the target has to get to a cow or calf to count as threatening it.")]
		[SerializeField]
		public float threatDistance = 10f;

		protected override bool EvaluateAtInterval(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || threatDistance <= 0f)
			{
				return false;
			}
			if (!base.Senses.FindTargetPosition(out var targetPosition))
			{
				return false;
			}
			if (livestockAnimal.FindNearestDependent(targetPosition, threatDistance) != null)
			{
				return true;
			}
			if (livestockAnimal.IsMale && livestockAnimal.IsAdult())
			{
				return AnyAdoptedPlayerNear(livestockAnimal, targetPosition, threatDistance);
			}
			return false;
		}

		private static bool AnyAdoptedPlayerNear(LivestockAnimal self, Vector3 targetPosition, float threatDistance)
		{
			float maxTrust = Livestock.maxTrust;
			if (maxTrust <= 0f)
			{
				return false;
			}
			using PooledList<BasePlayer> pooledList = Facepunch.Pool.Get<PooledList<BasePlayer>>();
			BaseEntity.Query.Server.GetPlayersInSphere(targetPosition, threatDistance, pooledList);
			foreach (BasePlayer item in pooledList)
			{
				if (!(item == null) && !item.IsDead() && !item.IsNpc && self.TrustOf(item) >= maxTrust)
				{
					return true;
				}
			}
			return false;
		}
	}

	[Serializable]
	public class Trans_LeadOutranksTarget : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || !livestockAnimal.IsLeading())
			{
				return false;
			}
			BasePlayer basePlayer = livestockAnimal.LeadingPlayer.Get(serverside: true);
			if (basePlayer == null || livestockAnimal.TrustOf(basePlayer) < Livestock.trustToLead)
			{
				return false;
			}
			if (base.Senses.FindTarget(out var target))
			{
				return !(target == basePlayer);
			}
			return true;
		}
	}

	[Serializable]
	public class Trans_HasThreatTarget : FSMTransitionBase
	{
		[Tooltip("Asks for a threat sat in a car or anything else that protects them from animals, the one to run from, instead of refusing them.")]
		[SerializeField]
		public bool targetProtectedByMount;

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || livestockAnimal.IsDead())
			{
				return false;
			}
			if (livestockAnimal.IsInfant() || livestockAnimal.IsLeading())
			{
				return false;
			}
			if (!base.Senses.FindTarget(out var target))
			{
				return false;
			}
			if (Trans_IsTargetProtectedByMount.IsProtected(target) != targetProtectedByMount)
			{
				return false;
			}
			if (target.InSafeZone() || livestockAnimal.InSafeZone())
			{
				return false;
			}
			if (target.ToNonNpcPlayer(out var _) && base.Senses.GetVisibilityStatus(target, out var status) && status.isInWaterCached)
			{
				return false;
			}
			if (WaterLevel.GetWaterDepth(livestockAnimal.transform.position, waves: false, volumes: false) >= 1f)
			{
				return false;
			}
			if (target is BasePlayer player2)
			{
				if (livestockAnimal.TrustOf(player2) >= Livestock.trustToTolerate)
				{
					return false;
				}
			}
			else if (!livestockAnimal.IsSoreAt(target))
			{
				return false;
			}
			return IsRightSort(livestockAnimal);
		}

		protected virtual bool IsRightSort(LivestockAnimal animal)
		{
			return true;
		}
	}

	[Serializable]
	public class Trans_IsProtective : Trans_HasThreatTarget
	{
		protected override bool IsRightSort(LivestockAnimal animal)
		{
			if (!animal.IsMale)
			{
				return animal.IsProtectiveOfCalf;
			}
			return true;
		}
	}

	[Serializable]
	public class Trans_IsLyingDown : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || !livestockAnimal.IsLyingDown())
			{
				return false;
			}
			if (Owner.TryGetComponent<LivestockAnimalFSM>(out var component))
			{
				component.StartledOutOfSleep = livestockAnimal.IsSleeping();
			}
			return true;
		}
	}

	[Serializable]
	public class State_GetUp : State_PlayAnimation
	{
		[Tooltip("Scrambles straight from the sleeping pose to standing, replacing the animator's slower wake and stand. Must be authored from the sleeping pose.")]
		[SerializeField]
		private AnimationClip scrambleFromSleep;

		[Tooltip("The same for a daytime lie down. Must be authored from the lying pose, not the sleeping one - the two are a long way apart.")]
		[SerializeField]
		private AnimationClip scrambleFromRest;

		public bool HasSleepScramble => scrambleFromSleep != null;

		public bool HasRestScramble => scrambleFromRest != null;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			base.Agent.ResetPath();
			LivestockAnimalFSM component;
			bool flag = Owner.TryGetComponent<LivestockAnimalFSM>(out component) && component.StartledOutOfSleep;
			Animation = (flag ? scrambleFromSleep : scrambleFromRest);
			if (Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.SetResting(resting: false);
				livestockAnimal.SetSleeping(sleeping: false);
				livestockAnimal.SetLyingIn(lyingIn: false);
			}
			if (!base.HasAnimation)
			{
				return EFSMStateStatus.None;
			}
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!(Owner is LivestockAnimal livestockAnimal))
			{
				return EFSMStateStatus.Success;
			}
			if (!base.HasAnimation)
			{
				if (!livestockAnimal.IsOffItsFeet())
				{
					return EFSMStateStatus.Success;
				}
				return EFSMStateStatus.None;
			}
			EFSMStateStatus num = base.OnStateUpdate(deltaTime);
			if (num != 0)
			{
				livestockAnimal.MarkOnItsFeet();
			}
			return num;
		}
	}

	[Serializable]
	public class State_RaiseHead : FSMStateBase
	{
		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			base.Agent.ResetPath();
			if (Owner is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.SetGrazing(grazing: false);
				livestockAnimal.SetDrinking(drinking: false);
			}
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!(Owner is LivestockAnimal livestockAnimal) || !livestockAnimal.IsHeadDown())
			{
				return EFSMStateStatus.Success;
			}
			return base.OnStateUpdate(deltaTime);
		}
	}

	[Serializable]
	public class State_LivestockHurt : State_HurtWithAdditive
	{
		[Tooltip("Knocked down from a standstill.")]
		[SerializeField]
		private RootMotionData StandKnockdown;

		[Tooltip("Knocked down mid stride, for an animal already running.")]
		[SerializeField]
		private RootMotionData CanterKnockdown;

		[Tooltip("The gait from which a knockdown reads as being taken off its feet mid stride rather than dropping where it stood.")]
		[SerializeField]
		private RustNavMeshAgent.Speeds canterFrom = RustNavMeshAgent.Speeds.Jog;

		public override bool HasStaggerAnimation
		{
			get
			{
				if (!(StandKnockdown != null))
				{
					return CanterKnockdown != null;
				}
				return true;
			}
		}

		protected override RootMotionData PickStrongHit(HitInfo hitInfo)
		{
			object obj;
			if (!(base.Agent != null) || !(base.Agent.velocity.magnitude >= base.Agent.GetSpeedForGait(canterFrom)))
			{
				obj = StandKnockdown;
				if (obj == null)
				{
					return CanterKnockdown;
				}
			}
			else
			{
				obj = CanterKnockdown ?? StandKnockdown;
			}
			return (RootMotionData)obj;
		}
	}

	[Serializable]
	public class State_TurnAndRun : State_PlayAnimationRM
	{
		[Tooltip("Turns away to the animal's left, for a threat on its right.")]
		[SerializeField]
		private RootMotionData TurnLeft;

		[Tooltip("Turns away to the animal's right, for a threat on its left.")]
		[SerializeField]
		private RootMotionData TurnRight;

		[Tooltip("The full about-turn, for a threat dead ahead.")]
		[SerializeField]
		private RootMotionData TurnAbout;

		[Tooltip("Only bridges from a standstill. Already moving faster than this and the animal can just run, a turn montage would snap it back to a stop.")]
		[SerializeField]
		private float maxSpeedToTurn = 1f;

		[Tooltip("How far off dead ahead a hit can land and still read as frontal, as a dot product against the animal's forward. 0 is straight to the side.")]
		[Range(0f, 1f)]
		[SerializeField]
		private float frontalDot = 0.35f;

		public bool HasTurnAnimation
		{
			get
			{
				if (!(TurnAbout != null) && !(TurnLeft != null))
				{
					return TurnRight != null;
				}
				return true;
			}
		}

		public bool ShouldBridgeToFlee(BaseEntity owner, RustNavMeshAgent agent, HitInfo hitInfo)
		{
			if (!HasTurnAnimation || hitInfo == null || owner == null || agent == null)
			{
				return false;
			}
			if (agent.velocity.magnitude > maxSpeedToTurn)
			{
				return false;
			}
			return Vector3.Dot(hitInfo.attackNormal, owner.transform.forward) < 0f - frontalDot;
		}

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			Animation = PickTurn(payload.hitInfo);
			return base.OnStateEnter(payload);
		}

		private RootMotionData PickTurn(HitInfo hitInfo)
		{
			if (hitInfo == null)
			{
				return TurnAbout ?? TurnLeft ?? TurnRight;
			}
			float num = Vector3.Dot(hitInfo.attackNormal, Owner.transform.right);
			if (num < 0f - frontalDot)
			{
				return TurnLeft ?? TurnAbout ?? TurnRight;
			}
			if (num > frontalDot)
			{
				return TurnRight ?? TurnAbout ?? TurnLeft;
			}
			return TurnAbout ?? TurnLeft ?? TurnRight;
		}
	}

	public const float WadingDepth = 1f;

	public const float StompDistance = 10.4f;

	public const float ChargeDistance = 4.8f;

	public State_PlayRandomAnimation randomIdle = new State_PlayRandomAnimation();

	public State_LivestockRoam roam = new State_LivestockRoam();

	public Trans_LooksElsewhereForFood foodRoamTransition = new Trans_LooksElsewhereForFood();

	public State_FriskyCanter friskyCanter = new State_FriskyCanter();

	public Trans_IsVeryHappy veryHappyTransition = new Trans_IsVeryHappy();

	public State_Follow follow = new State_Follow();

	public State_TurnToLeader turnToLeader = new State_TurnToLeader();

	public State_CuriousFollow curiousFollow = new State_CuriousFollow();

	public Trans_IsCuriousAboutPlayer curiousTransition = new Trans_IsCuriousAboutPlayer();

	public State_Patrol_Points patrol = new State_Patrol_Points();

	public State_Graze graze = new State_Graze();

	public Trans_IsGrazeLocationInRange grazeTransition = new Trans_IsGrazeLocationInRange();

	public State_Drink drink = new State_Drink();

	public Trans_IsDrinkSourceInRange drinkTransition = new Trans_IsDrinkSourceInRange();

	public State_HerdFlee flee = new State_HerdFlee();

	public State_Sleep sleep = new State_Sleep();

	public State_Rest rest = new State_Rest();

	public State_PlayAnimation foodSearch = new State_PlayAnimation();

	public Trans_FoodSearchFailed foodSearchFailedTransition = new Trans_FoodSearchFailed();

	public Trans_WantsBedtimeRest bedtimeRestTransition = new Trans_WantsBedtimeRest();

	public Trans_WantsRest restTransition = new Trans_WantsRest();

	public State_SeekMate seekMate = new State_SeekMate();

	public State_AwaitMate awaitMate = new State_AwaitMate();

	public State_Frolic frolic = new State_Frolic();

	public State_Mate mate = new State_Mate();

	public State_Pregnant pregnant = new State_Pregnant();

	public State_Newborn newborn = new State_Newborn();

	public Trans_IsNewborn newbornTransition = new Trans_IsNewborn();

	public State_StayNearMother stayNearMother = new State_StayNearMother();

	public Trans_CanBreed canBreedTransition = new Trans_CanBreed();

	public Trans_HasMateRequest mateRequestTransition = new Trans_HasMateRequest();

	public Trans_SuitorIsFrolicking suitorFrolickingTransition = new Trans_SuitorIsFrolicking();

	public Trans_IsMale maleTransition = new Trans_IsMale();

	public Trans_StrayedFromMother strayedFromMotherTransition = new Trans_StrayedFromMother();

	public State_LivestockHurt hurt = new State_LivestockHurt();

	public State_TurnAndRun turnAndRun = new State_TurnAndRun();

	public State_GetUp getUp = new State_GetUp();

	public State_RaiseHead raiseHead = new State_RaiseHead();

	public Trans_IsLyingDown lyingDownTransition = new Trans_IsLyingDown();

	public State_SquareUpApproach warnApproach = new State_SquareUpApproach();

	public State_Stomp warn = new State_Stomp();

	public State_MoveToTarget chargeRun = new State_MoveToTarget();

	public State_AttackWithTracking headbutt = new State_AttackWithTracking();

	public State_ShovingAttack kick = new State_ShovingAttack();

	public State_ShovingAttack kickBehind = new State_ShovingAttack();

	public State_TurnToTarget turnToFace = new State_TurnToTarget();

	public State_TurnToTarget turnCantering = new State_TurnToTarget();

	public State_HoldGround observe = new State_HoldGround();

	[FormerlySerializedAs("protectiveMaleTransition")]
	public Trans_IsProtective protectiveTransition = new Trans_IsProtective();

	public Trans_TargetNearHerd herdThreatenedTransition = new Trans_TargetNearHerd();

	public Trans_WouldRunFromAHit wouldRunFromAHitTransition = new Trans_WouldRunFromAHit();

	public Trans_HerdMateHurt herdMateHurtTransition = new Trans_HerdMateHurt();

	public State_ReturnToHerd returnToHerd = new State_ReturnToHerd();

	public Trans_StrayedFromHerd strayedFromHerdTransition = new Trans_StrayedFromHerd();

	public State_Dead dead = new State_Dead();

	[NonSerialized]
	public Vector3? ConsumeLocation;

	[NonSerialized]
	public EntityRef<BaseEntity> ConsumeSource;

	[NonSerialized]
	public EntityRef<LivestockAnimal> MateTarget;

	[NonSerialized]
	public EntityRef<LivestockAnimal> MateSuitor;

	[NonSerialized]
	public TimeUntil MateRequestExpiry;

	[NonSerialized]
	public EntityRef<LivestockAnimal> MatePartner;

	[NonSerialized]
	public TimeUntil NextMateSearch;

	[NonSerialized]
	public TimeUntil NextCompanionSearch;

	[NonSerialized]
	public TimeUntil NextCuriousFollow;

	[NonSerialized]
	public TimeSince LastFoodSearchFailed;

	[NonSerialized]
	public bool StartledOutOfSleep;

	private Trans_Triggerable_HitInfo DeathTrans;

	private Trans_Triggerable_HitInfo HurtTrans;

	private Trans_Triggerable_HitInfo TurnAndRunTrans;

	public LivestockAnimal.DayActivity CurrentDayActivity
	{
		get
		{
			FSMStateBase currentState = base.CurrentState;
			if (currentState == graze)
			{
				if (!(base.baseEntity is LivestockAnimal livestockAnimal) || !livestockAnimal.IsGrazing())
				{
					return LivestockAnimal.DayActivity.Idling;
				}
				return LivestockAnimal.DayActivity.Grazing;
			}
			if (currentState == rest)
			{
				return LivestockAnimal.DayActivity.Resting;
			}
			if (currentState == randomIdle || currentState == roam || currentState == foodSearch)
			{
				return LivestockAnimal.DayActivity.Idling;
			}
			return LivestockAnimal.DayActivity.Other;
		}
	}

	public bool WantsToBeAlone
	{
		get
		{
			if (base.CurrentState != seekMate && base.CurrentState != awaitMate && base.CurrentState != frolic)
			{
				return base.CurrentState == mate;
			}
			return true;
		}
	}

	public override void InitShared()
	{
		base.InitShared();
		if (base.baseEntity.isServer)
		{
			LastFoodSearchFailed = float.MaxValue;
			State_Nothing obj = new State_Nothing
			{
				Name = "Root"
			};
			State_Nothing state_Nothing = new State_Nothing
			{
				Name = "Alive"
			};
			State_Nothing state_Nothing2 = new State_Nothing
			{
				Name = "WaitForNavMesh"
			};
			State_Nothing state_Nothing3 = new State_Nothing
			{
				Name = "Can flee"
			};
			State_Nothing state_Nothing4 = new State_Nothing
			{
				Name = "Can follow"
			};
			State_Nothing state_Nothing5 = new State_Nothing
			{
				Name = "Not hurt"
			};
			State_Nothing state_Nothing6 = new State_Nothing
			{
				Name = "Square up"
			};
			State_Nothing state_Nothing7 = new State_Nothing
			{
				Name = "Not engaged"
			};
			Trans_Cooldown trans_Cooldown = new Trans_Cooldown
			{
				cooldown = 3f,
				onlyWhenStarted = true
			};
			Trans_And trans_And = new Trans_TargetInRange
			{
				Range = 3f
			} & ~new Trans_TargetInFront
			{
				Angle = 150f
			};
			Trans_And trans_And2 = new Trans_TargetInRange
			{
				Range = 3f,
				MaxHeightDifference = 1.5f
			} & ~new Trans_TargetInFront
			{
				Angle = 100f
			};
			Trans_And trans_And3 = new Trans_CanReachTarget_Slow() & new Trans_HasStraightPathToTarget
			{
				ProjectTarget = true
			};
			Trans_And trans_And4 = trans_And & new Trans_HasKick() & trans_Cooldown & trans_And3;
			Trans_And transition = trans_And2 & new Trans_HasKick() & trans_Cooldown & trans_And3;
			Trans_And trans_And5 = trans_And & new Trans_HasKick
			{
				Inverted = true
			} & new Trans_HasCanterTurn() & trans_And3;
			Trans_And transition2 = trans_And2 & new Trans_HasKick
			{
				Inverted = true
			} & new Trans_HasCanterTurn() & trans_And3;
			Trans_And transition3 = new Trans_ChasedFromBehind
			{
				range = 4f
			} & new Trans_Cooldown
			{
				cooldown = 3f
			} & trans_And3;
			Trans_Or trans_Or = new Trans_RecentlyHurt() | new Trans_LoudNoiseNearby() | new Trans_AggressorNearby() | (protectiveTransition & herdThreatenedTransition);
			DeathTrans = new Trans_Triggerable_HitInfo();
			HurtTrans = new Trans_Triggerable_HitInfo();
			TurnAndRunTrans = new Trans_Triggerable_HitInfo();
			obj.AddChildren(state_Nothing.AddTickTransition(dead, DeathTrans), dead);
			if (patrol.Enabled)
			{
				state_Nothing.AddChildren(state_Nothing2.AddTickTransition(patrol, new Trans_IsNavmeshReady()), patrol);
			}
			else
			{
				state_Nothing.AddChildren(state_Nothing5.AddTickTransition(getUp, lyingDownTransition & trans_Or).AddTickTransition(hurt, HurtTrans).AddTickTransition(turnAndRun, TurnAndRunTrans)
					.AddTickTransition(turnToLeader, new Trans_LeaderBehind())
					.AddChildren(state_Nothing2.AddTickTransition(newborn, newbornTransition).AddTickTransition(randomIdle, new Trans_IsNavmeshReady()), state_Nothing3.AddTickTransition(follow, new Trans_LeadOutranksTarget()).AddTickTransition(flee, (new Trans_RecentlyHurt() | new Trans_LoudNoiseNearby() | new Trans_AggressorNearby()) & wouldRunFromAHitTransition & new Trans_IsFollowingPlayer
					{
						Inverted = true
					} & new Trans_Cooldown
					{
						cooldown = 5f
					}).AddTickTransition(flee, new Trans_IsProtective
					{
						targetProtectedByMount = true
					} & (new Trans_TargetInRange
					{
						Range = 10.4f
					} | herdThreatenedTransition))
						.AddChildren(state_Nothing7.AddTickTransition(kick, protectiveTransition & trans_And4).AddTickTransition(turnCantering, protectiveTransition & trans_And5).AddTickTransition(kickBehind, transition3)
							.AddTickTransition(chargeRun, protectiveTransition & (new Trans_RecentlyHurt() | herdMateHurtTransition | new Trans_LoudNoiseNearby
							{
								range = 20f
							}) & new Trans_CanReachTarget_Slow())
							.AddTickTransition(chargeRun, protectiveTransition & new Trans_IsCourting
							{
								Inverted = true
							} & new Trans_TargetInRange
							{
								Range = 4.8f
							} & (new Trans_TargetInFront
							{
								Angle = 150f
							} | herdThreatenedTransition) & new Trans_CanReachTarget_Slow())
							.AddTickTransition(warnApproach, protectiveTransition & new Trans_IsCourting
							{
								Inverted = true
							} & ((new Trans_TargetInRange
							{
								Range = 10.4f
							} & new Trans_TargetInFront
							{
								Angle = 150f
							}) | herdThreatenedTransition) & new Trans_Cooldown
							{
								cooldown = 4f
							} & new Trans_CanReachTarget_Slow())
							.AddChildren(state_Nothing4.AddTickTransition(newborn, newbornTransition).AddTickTransition(follow, new Trans_IsFollowingPlayer()).AddTickTransition(stayNearMother, strayedFromMotherTransition)
								.AddTickTransition(pregnant, new Trans_IsPregnant())
								.AddTickTransition(seekMate, canBreedTransition)
								.AddTickTransition(awaitMate, mateRequestTransition)
								.AddTickTransition(curiousFollow, curiousTransition)
								.AddTickTransition(returnToHerd, strayedFromHerdTransition)
								.AddChildren(randomIdle.AddTickTransition(graze, grazeTransition).AddEndTransition(rest, bedtimeRestTransition).AddEndTransition(sleep, new Trans_WantsSleep())
									.AddEndTransition(drink, drinkTransition)
									.AddEndTransition(graze, grazeTransition)
									.AddEndTransition(friskyCanter, veryHappyTransition)
									.AddEndTransition(foodSearch, foodSearchFailedTransition)
									.AddEndTransition(roam, foodRoamTransition)
									.AddEndTransition(rest, restTransition)
									.AddEndTransition(randomIdle), graze.AddEndTransition(randomIdle), drink.AddEndTransition(randomIdle), roam.AddFailureTransition(dead, new Trans_Stranded()).AddEndTransition(randomIdle), friskyCanter.AddEndTransition(randomIdle), rest.AddEndTransition(randomIdle), foodSearch.AddEndTransition(randomIdle), sleep.AddEndTransition(randomIdle)), follow.AddTickTransition(randomIdle, new Trans_IsFollowingPlayer
							{
								Inverted = true
							}), curiousFollow.AddTickTransition(randomIdle, new Trans_StillCuriousAboutPlayer
							{
								Inverted = true
							}).AddFailureTransition(randomIdle), stayNearMother.AddEndTransition(randomIdle), newborn.AddEndTransition(randomIdle), seekMate.AddFailureTransition(randomIdle).AddEndTransition(frolic), awaitMate.AddTickTransition(frolic, suitorFrolickingTransition).AddEndTransition(randomIdle), frolic.AddEndTransition(mate, maleTransition).AddEndTransition(randomIdle), mate.AddFailureTransition(randomIdle).AddEndTransition(randomIdle), pregnant.AddEndTransition(randomIdle)), returnToHerd.AddTickTransition(kick, protectiveTransition & trans_And4).AddTickTransition(turnCantering, protectiveTransition & trans_And5).AddTickTransition(chargeRun, protectiveTransition & (new Trans_RecentlyHurt() | herdMateHurtTransition) & new Trans_TargetInRange
						{
							Range = 15f
						} & new Trans_CanReachTarget_Slow())
							.AddTickTransition(chargeRun, protectiveTransition & herdThreatenedTransition & new Trans_TargetInRange
							{
								Range = 4.8f
							} & new Trans_CanReachTarget_Slow())
							.AddTickTransition(warnApproach, protectiveTransition & herdThreatenedTransition & new Trans_Cooldown
							{
								cooldown = 4f
							} & new Trans_CanReachTarget_Slow())
							.AddFailureTransition(randomIdle)
							.AddEndTransition(randomIdle), state_Nothing6.AddTickTransition(randomIdle, new Trans_HasTarget
						{
							Inverted = true
						}).AddTickTransition(randomIdle, new Trans_TargetIsInSafeZone() | new Trans_IsInSafeZone()).AddTickTransition(randomIdle, new Trans_IsInWater_Slow
						{
							minDepth = 1f
						} | new Trans_IsTargetInWater())
							.AddChildren(warnApproach.AddTickTransition(kick, trans_And4).AddTickTransition(turnCantering, trans_And5).AddTickTransition(chargeRun, new Trans_RecentlyHurt() | herdMateHurtTransition)
								.AddTickTransition(chargeRun, new Trans_TargetInRange
								{
									Range = 4.8f
								} & new Trans_CanReachTarget_Slow())
								.AddTickTransition(observe, ~new Trans_TargetInRange
								{
									Range = 10.4f
								} & new Trans_TargetNearHerd
								{
									Inverted = true,
									threatDistance = 12.5f
								})
								.AddEndTransition(warn), warn.AddTickTransition(kick, trans_And4).AddTickTransition(turnCantering, trans_And5).AddTickTransition(chargeRun, new Trans_RecentlyHurt() | herdMateHurtTransition)
								.AddTickTransition(chargeRun, new Trans_TargetInRange
								{
									Range = 4.8f
								} & new Trans_CanReachTarget_Slow())
								.AddEndTransition(chargeRun, herdThreatenedTransition & new Trans_TargetInRange
								{
									Range = 10.4f
								} & new Trans_CanReachTarget_Slow())
								.AddEndTransition(observe), chargeRun.AddTickTransition(headbutt, new Trans_TargetInRange
							{
								Range = 4.7f,
								TimeToPredict = 0.35f,
								MaxHeightDifference = 1.5f
							} & new Trans_TargetInFront
							{
								Angle = 100f
							} & new Trans_HasStraightPathToTarget
							{
								ProjectTarget = true
							}).AddTickTransition(kick, transition).AddTickTransition(turnCantering, transition2)
								.AddTickTransition(observe, new Trans_ElapsedTime
								{
									Duration = 6.0
								})
								.AddFailureTransition(observe)
								.AddEndTransition(observe), headbutt.AddEndTransition(turnCantering, ~new Trans_TargetInRange
							{
								Range = 8f
							}).AddEndTransition(turnToFace, ~new Trans_TargetInFront
							{
								Angle = 60f
							}).AddFailureTransition(observe)
								.AddEndTransition(observe), turnCantering.AddFailureTransition(observe).AddEndTransition(chargeRun, new Trans_TargetInRange
							{
								Range = 4.8f
							} & new Trans_CanReachTarget_Slow()).AddEndTransition(warnApproach, new Trans_TargetInRange
							{
								Range = 10.4f
							} & new Trans_CanReachTarget_Slow())
								.AddEndTransition(observe), turnToFace.AddFailureTransition(observe).AddEndTransition(chargeRun, new Trans_TargetInRange
							{
								Range = 4.8f
							} & new Trans_CanReachTarget_Slow()).AddEndTransition(warnApproach, new Trans_TargetInRange
							{
								Range = 10.4f
							} & new Trans_CanReachTarget_Slow())
								.AddEndTransition(observe), observe.AddTickTransition(kick, trans_And4).AddTickTransition(turnCantering, trans_And5).AddTickTransition(chargeRun, (new Trans_RecentlyHurt() | herdMateHurtTransition) & new Trans_CanReachTarget_Slow())
								.AddTickTransition(chargeRun, new Trans_TargetInRange
								{
									Range = 4.8f
								} & new Trans_CanReachTarget_Slow())
								.AddTickTransition(warnApproach, new Trans_TargetEntersRange
								{
									Range = 10.4f
								} & new Trans_CanReachTarget_Slow())
								.AddTickTransition(randomIdle, new Trans_ElapsedTime
								{
									Duration = 4.0
								} & new Trans_CanReachTarget_Slow
								{
									Inverted = true
								})
								.AddTickTransition(randomIdle, new Trans_ElapsedTime
								{
									Duration = 2.0
								} & ~new Trans_TargetInRange
								{
									Range = 12f
								} & new Trans_TargetNearHerd
								{
									Inverted = true,
									threatDistance = 12.5f
								})
								.AddFailureTransition(randomIdle))), kick.AddTickTransition(randomIdle, new Trans_HasTarget
					{
						Inverted = true
					}).AddFailureTransition(observe).AddEndTransition(turnToFace), kickBehind.AddEndTransition(flee), flee.AddTickTransition(kickBehind, transition3).AddTickTransition(chargeRun, protectiveTransition & (new Trans_RecentlyHurt() | herdMateHurtTransition) & new Trans_CanReachTarget_Slow()).AddEndTransition(chargeRun, new Trans_IsProtective() & new Trans_TargetInRange
					{
						Range = 8f
					} & new Trans_CanReachTarget_Slow())
						.AddEndTransition(randomIdle), raiseHead.AddEndTransition(flee, trans_Or & wouldRunFromAHitTransition & new Trans_IsFollowingPlayer
					{
						Inverted = true
					}).AddEndTransition(randomIdle)), hurt.AddEndTransition(flee, wouldRunFromAHitTransition).AddEndTransition(randomIdle), turnAndRun.AddEndTransition(flee), turnToLeader.AddTickTransition(randomIdle, new Trans_IsFollowingPlayer
				{
					Inverted = true
				}).AddEndTransition(follow), getUp.AddEndTransition(flee, trans_Or & wouldRunFromAHitTransition & new Trans_IsFollowingPlayer
				{
					Inverted = true
				}).AddEndTransition(randomIdle));
			}
			RegisterDebugMoveTo(state_Nothing);
			RegisterMounting(state_Nothing, randomIdle);
			SetState(state_Nothing2);
			SetFsmActive(newActive: true);
		}
	}

	protected override FSMStateBase RedirectStateChange(FSMStateBase newState)
	{
		if (base.CurrentState == null)
		{
			return newState;
		}
		if (!(base.baseEntity is LivestockAnimal livestockAnimal))
		{
			return newState;
		}
		if (!livestockAnimal.IsOffItsFeet())
		{
			return RedirectHeadDown(livestockAnimal, newState);
		}
		if (base.CurrentState == getUp)
		{
			return newState;
		}
		if (newState == getUp || newState == dead || newState == rest || newState == newborn || newState == pregnant)
		{
			return newState;
		}
		if (livestockAnimal.IsSettlingDown() && (newState == hurt || newState == turnAndRun))
		{
			return newState;
		}
		StartledOutOfSleep = livestockAnimal.IsSleeping();
		return getUp;
	}

	private FSMStateBase RedirectHeadDown(LivestockAnimal animal, FSMStateBase newState)
	{
		if (!animal.IsHeadDown() || base.CurrentState == raiseHead)
		{
			return newState;
		}
		if (newState == raiseHead || newState == dead || newState == hurt || newState == turnAndRun || newState == mountVehicle)
		{
			return newState;
		}
		return raiseHead;
	}

	protected override void OnTicked(float deltaTime)
	{
		using (LivestockProfiler.Sample(LivestockProfiler.Section.Fsm))
		{
			base.OnTicked(deltaTime);
			if (base.baseEntity is LivestockAnimal livestockAnimal)
			{
				livestockAnimal.TickDayBudget(CurrentDayActivity, deltaTime);
			}
		}
	}

	public override bool OnDied(HitInfo hitInfo)
	{
		LivestockCensus.RecordDeath(hitInfo, base.baseEntity.transform.position);
		DeathTrans.Trigger(hitInfo);
		return false;
	}

	public override void Hurt(HitInfo hitInfo)
	{
		if (base.CurrentState == dead)
		{
			return;
		}
		if (hurt.WeakHitAdditive != null)
		{
			GetComponent<RootMotionPlayer>().PlayServerAdditive(hurt.WeakHitAdditive);
		}
		if (base.CurrentState == hurt || base.CurrentState == turnAndRun || base.CurrentState == getUp)
		{
			return;
		}
		if (base.baseEntity is LivestockAnimal livestockAnimal && livestockAnimal.IsOffItsFeet() && !livestockAnimal.IsSettlingDown())
		{
			ForceTickOnTheNextUpdate();
		}
		else if (!base.baseEntity.InSafeZone())
		{
			if (hurt.HasStaggerAnimation && hurt.ShouldStagger(base.baseEntity, hitInfo))
			{
				HurtTrans.Trigger(hitInfo);
				ForceTickOnTheNextUpdate();
			}
			else if (turnAndRun.ShouldBridgeToFlee(base.baseEntity, GetComponent<RustNavMeshAgent>(), hitInfo) && wouldRunFromAHitTransition.Evaluate(base.baseEntity))
			{
				TurnAndRunTrans.Trigger(hitInfo);
				ForceTickOnTheNextUpdate();
			}
		}
	}

	private static bool MoveWithRampedSpeed(RustNavMeshAgent agent, Vector3 destinationWS, float fullSpeedDistance, RustNavMeshAgent.Speeds minSpeed, RustNavMeshAgent.Speeds maxSpeed)
	{
		if (!agent.SetDestinationWithParams(destinationWS, autoBraking: true, null, null, null, null, null, null))
		{
			return false;
		}
		float ratio = Mathf.InverseLerp(0f, fullSpeedDistance, Vector3.Distance(agent.transform.position, destinationWS));
		agent.SetSpeedRatio(ratio, minSpeed, maxSpeed);
		return true;
	}
}

