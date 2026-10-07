using System;
using System.Collections.Generic;
using ConVar;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Jobs;

namespace Rust.Ai.Gen2;

public class RustNavMeshAgent : EntityComponent<BaseEntity>, IServerComponent
{
	internal struct PathFollowingSpeed
	{
		public float current;

		public float desired;

		public float acceleration;

		public float deceleration;

		public float emergencyDeceleration;

		public float walkSpeed;

		public float brakeFloor;

		public float slowDownRadius;

		public float remaining;

		public float stopping;

		public byte unityBraking;

		public byte autoBraking;

		public float Advance(float dt, out byte adjustment)
		{
			adjustment = 0;
			bool flag = autoBraking != 0;
			if (unityBraking != 0)
			{
				if (flag && remaining < slowDownRadius && remaining * desired < slowDownRadius * current)
				{
					adjustment = 1;
					return Mathf.Max(0f, current - dt * current * current / (2f * Mathf.Max(remaining, 0.001f)));
				}
				flag = false;
			}
			if (flag && remaining - stopping < GetBrakingDistance(current, deceleration))
			{
				adjustment = 1;
				return Mathf.Max(brakeFloor, current - deceleration * dt);
			}
			if (current > desired)
			{
				adjustment = 2;
				float num = (current - desired) / deceleration;
				float num2 = ((current > walkSpeed && num > 1f) ? emergencyDeceleration : deceleration);
				return Mathf.Max(desired, current - num2 * dt);
			}
			if (current < desired)
			{
				adjustment = 3;
				return Mathf.Min(desired, current + acceleration * dt);
			}
			return current;
		}
	}

	internal struct PathFollowingUpdate
	{
		public IntPtr navmesh;

		public IntPtr corridor;

		public Vector3 position;

		public Vector3 waypoint;

		public Vector3 destination;

		public Vector3 optimizeTarget;

		public Vector3 forward;

		public Vector3 velocity;

		public PathFollowingSpeed speed;

		public float deviation;

		public float turnRadius;

		public float baseOffset;

		public int moved;

		public byte flags;
	}

	public enum Speeds
	{
		Sneak,
		Walk,
		Jog,
		Run,
		Sprint,
		FullSprint
	}

	internal struct MovementTransformUpdate
	{
		public Vector3 position;

		public Quaternion rotation;

		public float maxDegreesDelta;

		public byte flags;

		public PathFollowingUpdate path;
	}

	[BurstCompile(CompileSynchronously = true)]
	internal struct MovementTransformJob : IJobParallelForTransform
	{
		public NativeArray<MovementTransformUpdate> updates;

		[ReadOnly]
		[NativeDisableParallelForRestriction]
		public NativeArray<IntPtr> queries;

		public float deltaTime;

		[NativeSetThreadIndex]
		private int threadIndex;

		public void Execute(int index, TransformAccess transform)
		{
			MovementTransformUpdate update = updates[index];
			if (update.flags == 0 || !transform.isValid)
			{
				return;
			}
			if ((update.flags & 8) != 0)
			{
				CalculatePathFollowing(ref update, queries[threadIndex], transform.rotation, deltaTime);
				updates[index] = update;
			}
			Quaternion quaternion = update.rotation;
			if ((update.flags & 4) != 0)
			{
				quaternion q = transform.rotation;
				quaternion q2 = quaternion;
				float num = math.min(math.abs(math.dot(q.value, q2.value)), 1f);
				if (num <= 0.999999f)
				{
					float num2 = 2f * math.acos(num) * 57.29578f;
					quaternion = math.slerp(q, q2, math.min(1f, update.maxDegreesDelta / num2));
				}
			}
			if ((update.flags & 1) != 0)
			{
				if ((update.flags & 2) != 0)
				{
					transform.SetPositionAndRotation(update.position, quaternion);
				}
				else
				{
					transform.position = update.position;
				}
			}
			else if ((update.flags & 2) != 0)
			{
				transform.rotation = quaternion;
			}
		}
	}

	public bool letUnityMoveAgentIfPossible = true;

	private static ListHashSet<RustNavMeshAgent> enabledComponents = new ListHashSet<RustNavMeshAgent>();

	private static RustNavMeshPath tempPath = new RustNavMeshPath();

	private static RustNavMeshPath scratchPath = new RustNavMeshPath();

	private RustNavMeshPath CurPathNS = new RustNavMeshPath();

	private NavMeshAgent _agent;

	private Transform cachedTransform;

	private NavVector3 previousPositionNS;

	private IntPtr corridor;

	private bool followingPath;

	private readonly List<NavVector3> corners = new List<NavVector3>();

	private float remainingCornerTailDistance;

	private bool lastCornerIsEnd;

	private bool cornersDirty = true;

	private const float CorridorOptimizationRange = 20f;

	private const float CornerRepullDistance = 0.25f;

	private const float SteeringCornerRepullMoveDistance = 0.35f;

	private const float SteeringCornerRepullMaxInterval = 0.25f;

	private NavVector3 lastCornerPullPositionNS;

	private float lastCornerPullTime;

	private int lastSeenTileVersion = -1;

	private int anchorCheckedTileDataVersion = -1;

	private uint lastCheckedFlagStamp;

	private bool lastCheckedNpcDoors;

	private NavVector3 pendingOptimizeTargetNS;

	private bool hasPendingOptimize;

	private RustNavmesh boundNavmesh;

	[NonSerialized]
	public readonly List<NavVector3> lastValidPath = new List<NavVector3>();

	private bool isScientist;

	private bool npcDoorsOpenForEntity;

	private SenseComponent senses;

	private const float ReachSnapRange = 1f;

	private const float ReachSnapHeight = 2f;

	private const float ReachSnapClimb = 0.75f;

	private const float ReachSnapDropRange = 0.35f;

	[Header("Base fields")]
	[SerializeField]
	private int _agentTypeID;

	[SerializeField]
	private float _baseOffset;

	[SerializeField]
	private float _desiredSpeed;

	[SerializeField]
	private float _angularSpeed;

	[SerializeField]
	public ResettableFloat _acceleration = new ResettableFloat(10f);

	[SerializeField]
	private float _stoppingDistance;

	[SerializeField]
	private bool _autoBraking;

	[SerializeField]
	private float _height;

	[SerializeField]
	private int _avoidancePriority;

	[SerializeField]
	private int _areaMask;

	[SerializeField]
	private bool _updatePosition = true;

	[SerializeField]
	private bool _updateRotation = true;

	private ObstacleAvoidanceType _obstacleAvoidanceType;

	private bool _isStopped;

	private NavVector3 _velocityNS = NavVector3.zero;

	private NavVector3 _nextPositionNS = NavVector3.zero;

	[NonSerialized]
	private float _stoppingDistanceOverride = -1f;

	private const float FindClosestEdgeMaxRadius = 10f;

	private const float MovingTargetPatchMaxDistance = 3f;

	private const float MovingTargetFullReplanInterval = 1f;

	private float lastFullPlanTime = float.NegativeInfinity;

	public Vector3? overrideDirectionWS;

	[Header("Doors")]
	public bool canOpenDoors;

	private const byte PathFollowingUpdateFlag = 8;

	private const byte PathSteering = 1;

	private const byte PathClampStep = 2;

	private const byte PathAutoBraking = 4;

	private const byte PathCachedForward = 8;

	private const byte PathOptimize = 16;

	private const byte PathNpcDoorsWillOpen = 32;

	private static IntPtr[] movementQueries;

	private static bool pendingPathFollowing;

	private HashSet<object> pausingSources = new HashSet<object>();

	[Header("Movement speed")]
	public float sneakSpeed = 0.6f;

	public float walkSpeed = 0.89f;

	public float jogSpeed = 2.45f;

	public float runSpeed = 4.4f;

	public float sprintSpeed = 6f;

	public float fullSprintSpeed = 9f;

	[Tooltip("Slowest gait this species has an animation for. A shared state may ask for something slower, but with no clip authored at that speed it reads as sliding - livestock have no sneak, for instance.")]
	public Speeds minimumGait;

	public ResettableFloat deceleration = new ResettableFloat(2f);

	public float emergencyDeceleration = 10f;

	private float currentSpeed;

	private float dampVelocity;

	private float _agentTypeRadius = -1f;

	[NonSerialized]
	[Tooltip("Gaits to shift whatever is asked for. Positive drops it, so an old animal jogs where it used to run. Negative raises it, which is how a calf keeps up: the clips are shared with its mother, so at her gait the same animation carries its shorter legs along far too slowly.")]
	public int gaitPenalty;

	[Header("Steering")]
	public bool canSteer = true;

	public float maxTurnRadius = 2f;

	[NonSerialized]
	public float currentDeviation;

	private NavVector3 cachedSteeringForwardNS;

	private bool hasCachedSteeringForward;

	[Header("Swimming")]
	public bool canSwim;

	public float swimSpeed = 0.6f;

	public float swimSprintSpeed = 0.89f;

	public ResettableFloat desiredSwimDepth = new ResettableFloat(0.7f);

	[Header("Terrain Preferences")]
	public TerrainTopology.Enum preferedTopology = TerrainTopology.Enum.Field | TerrainTopology.Enum.Forest | TerrainTopology.Enum.Forestside | TerrainTopology.Enum.Lakeside | TerrainTopology.Enum.Mainland;

	public TerrainBiome.Enum preferedBiome = TerrainBiome.Enum.Arid | TerrainBiome.Enum.Temperate | TerrainBiome.Enum.Tundra | TerrainBiome.Enum.Arctic;

	private const byte PositionUpdate = 1;

	private const byte RotationUpdate = 2;

	private const byte InterpolateRotation = 4;

	private const int MinimumTransformBatch = 32;

	private static TransformAccessArray movementTransforms;

	private static MovementTransformUpdate[] movementUpdates;

	private static RustNavMeshAgent[] pendingTransformAgents;

	private static Transform[] registeredTransforms;

	private static bool transformBatchActive;

	private static RustNavMeshAgent transformBatchAgent;

	private static int pendingTransformUpdates;

	private bool hasPendingTransform;

	private Transform AgentTransform => cachedTransform ?? (cachedTransform = base.transform);

	public IndependantNavmesh independantNavmesh { get; private set; }

	public bool HasValidIndependantNavmesh
	{
		get
		{
			if (!RustNavigation.EnsureNewNavmesh())
			{
				return false;
			}
			if (independantNavmesh != null && independantNavmesh.Navmesh != null)
			{
				return independantNavmesh.Navmesh.IsBuilt();
			}
			return false;
		}
	}

	public bool IsNavMeshBuilt
	{
		get
		{
			if (!RustNavigation.EnsureNewNavmesh())
			{
				return false;
			}
			if (boundNavmesh == null || !boundNavmesh.IsValid())
			{
				TryBindNavmesh();
			}
			if (boundNavmesh != null)
			{
				return boundNavmesh.IsBuilt();
			}
			return false;
		}
	}

	public bool npcDoorsWillOpen
	{
		get
		{
			if (!canOpenDoors)
			{
				return npcDoorsOpenForEntity;
			}
			return true;
		}
	}

	public NavVector3 forward => WorldToNavDirection(base.baseEntity.transform.forward);

	public NavVector3 right => WorldToNavDirection(base.baseEntity.transform.right);

	public NavVector3 up => WorldToNavDirection(base.baseEntity.transform.up);

	public OffMeshLinkData currentOffMeshLinkData
	{
		get
		{
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				return agent.currentOffMeshLinkData;
			}
			return default(OffMeshLinkData);
		}
	}

	public int agentTypeID
	{
		get
		{
			return _agentTypeID;
		}
		set
		{
			_agentTypeID = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.agentTypeID = value;
			}
		}
	}

	public int areaMask
	{
		get
		{
			return _areaMask;
		}
		set
		{
			_areaMask = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.areaMask = value;
			}
		}
	}

	public float speed
	{
		get
		{
			return _desiredSpeed;
		}
		set
		{
			_desiredSpeed = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.speed = value;
			}
		}
	}

	public float angularSpeed
	{
		get
		{
			return _angularSpeed;
		}
		set
		{
			_angularSpeed = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.angularSpeed = value;
			}
		}
	}

	public float acceleration
	{
		get
		{
			return _acceleration.Value;
		}
		set
		{
			_acceleration.Value = value;
			if (letUnityMoveAgentIfPossible)
			{
				deceleration.Value = value;
			}
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.acceleration = value;
			}
		}
	}

	public bool updatePosition
	{
		get
		{
			return _updatePosition;
		}
		set
		{
			_updatePosition = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent) && letUnityMoveAgentIfPossible)
			{
				agent.updatePosition = value;
			}
		}
	}

	public bool updateRotation
	{
		get
		{
			return _updateRotation;
		}
		set
		{
			_updateRotation = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent) && letUnityMoveAgentIfPossible)
			{
				agent.updateRotation = value;
			}
		}
	}

	public float stoppingDistance
	{
		get
		{
			return _stoppingDistance;
		}
		set
		{
			_stoppingDistance = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.stoppingDistance = value;
			}
		}
	}

	public float currentStoppingDistance
	{
		get
		{
			if (!(_stoppingDistanceOverride >= 0f))
			{
				return _stoppingDistance;
			}
			return _stoppingDistanceOverride;
		}
	}

	public float height
	{
		get
		{
			return _height;
		}
		set
		{
			_height = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.height = value;
			}
		}
	}

	public ObstacleAvoidanceType obstacleAvoidanceType
	{
		get
		{
			return _obstacleAvoidanceType;
		}
		set
		{
			_obstacleAvoidanceType = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.obstacleAvoidanceType = value;
			}
		}
	}

	public int avoidancePriority
	{
		get
		{
			return _avoidancePriority;
		}
		set
		{
			_avoidancePriority = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.avoidancePriority = value;
			}
		}
	}

	public bool isStopped
	{
		get
		{
			return _isStopped;
		}
		set
		{
			_isStopped = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent) && letUnityMoveAgentIfPossible)
			{
				agent.isStopped = value;
			}
		}
	}

	public bool autoBraking
	{
		get
		{
			return _autoBraking;
		}
		set
		{
			_autoBraking = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.autoBraking = value;
			}
		}
	}

	public float baseOffset
	{
		get
		{
			return _baseOffset;
		}
		set
		{
			_baseOffset = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.baseOffset = value;
			}
		}
	}

	public Vector3 nextPositionWS => NavToWorldSpace(nextPosition);

	public NavVector3 nextPosition
	{
		get
		{
			if (AI.useUnityNavmesh)
			{
				if (TryGetAgent(out var agent))
				{
					return new NavVector3(agent.nextPosition);
				}
				if (AI.logIssues)
				{
					Debug.LogError("RustNavMeshAgent.nextPosition called with useUnityNavmesh enabled, but no NavMeshAgent was found on the entity.");
				}
				return _nextPositionNS;
			}
			if (currentPolyRef != 0L)
			{
				return _nextPositionNS;
			}
			return WorldToNavSpace(AgentTransform.position);
		}
	}

	public NavMeshPathStatus pathStatus
	{
		get
		{
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent))
				{
					return NavMeshPathStatus.PathInvalid;
				}
				return agent.pathStatus;
			}
			if (!hasPath)
			{
				return NavMeshPathStatus.PathInvalid;
			}
			return CurPathNS.status;
		}
	}

	public bool isOnOffMeshLink
	{
		get
		{
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				return agent.isOnOffMeshLink;
			}
			return false;
		}
	}

	public bool pathPending
	{
		get
		{
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				return agent.pathPending;
			}
			return false;
		}
	}

	public NavVector3 steeringTarget
	{
		get
		{
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent))
				{
					return NavVector3.zero;
				}
				return new NavVector3(agent.steeringTarget);
			}
			if (corners.Count <= 0)
			{
				return _nextPositionNS;
			}
			return corners[0];
		}
	}

	public NavVector3 velocity
	{
		get
		{
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent))
				{
					return NavVector3.zero;
				}
				return new NavVector3(agent.velocity);
			}
			return _velocityNS;
		}
		set
		{
			_velocityNS = value;
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.velocity = value.Value;
			}
			if (!AI.useUnityNavmesh && AI.logIssues)
			{
				RustNavigation.LogError("Setting velocity on RustNavMeshAgent has no effect when not using Unity NavMesh. Use Move() to move the agent instead.");
			}
		}
	}

	public Vector3 desiredVelocityWS => NavToWorldDirection(desiredVelocity);

	public NavVector3 desiredVelocity
	{
		get
		{
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent))
				{
					return NavVector3.zero;
				}
				return new NavVector3(agent.desiredVelocity);
			}
			return velocity;
		}
	}

	public bool hasPath
	{
		get
		{
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent))
				{
					return false;
				}
				return agent.hasPath;
			}
			return followingPath;
		}
	}

	public Vector3 destinationWS => NavToWorldSpace(destination);

	public NavVector3 destination
	{
		get
		{
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent))
				{
					return NavVector3.zero;
				}
				return new NavVector3(agent.destination);
			}
			if (!hasPath)
			{
				return _nextPositionNS;
			}
			return CurPathNS.GetDestinationNS();
		}
		set
		{
			if (AI.useUnityNavmesh)
			{
				if (TryGetAgent(out var agent))
				{
					agent.destination = value.Value;
				}
			}
			else
			{
				SetDestination(value);
			}
		}
	}

	public float remainingDistance
	{
		get
		{
			using (TimeWarning.New("RustNavMeshAgent.remainingDistance"))
			{
				if (AI.useUnityNavmesh)
				{
					if (!TryGetAgent(out var agent))
					{
						return 0f;
					}
					return agent.remainingDistance;
				}
				if (!hasPath)
				{
					return 0f;
				}
				if (corners.Count > 0)
				{
					return RemainingDistanceAlongCorners();
				}
				return CurPathNS.GetPathLength();
			}
		}
	}

	public bool isOnNavMesh
	{
		get
		{
			using (TimeWarning.New("RustNavMeshAgent.isOnNavMesh"))
			{
				if (AI.useUnityNavmesh)
				{
					if (!TryGetAgent(out var agent))
					{
						return false;
					}
					return agent.isOnNavMesh;
				}
				ulong num = currentPolyRef;
				if (num == 0L)
				{
					TryBindNavmesh();
				}
				else if (boundNavmesh != null && boundNavmesh.IsValidPolyRef(num))
				{
					return true;
				}
				NavHit hitNS;
				return SamplePosition(_nextPositionNS, out hitNS, 2f, debugDraw: false);
			}
		}
	}

	private ulong currentPolyRef
	{
		get
		{
			if (!(corridor != IntPtr.Zero))
			{
				return 0uL;
			}
			return RecastWrapper.CorridorGetFirstPoly(corridor);
		}
	}

	public bool IsJumping
	{
		get
		{
			return base.baseEntity.HasFlag(BaseEntity.Flags.Reserved2);
		}
		set
		{
			using BaseEntity.FlagsUpdateScope flagsUpdateScope = base.baseEntity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate);
			flagsUpdateScope.Set(BaseEntity.Flags.Reserved2, value);
		}
	}

	public bool IsPaused
	{
		get
		{
			if (pausingSources.Count == 0)
			{
				return false;
			}
			bool wasPaused = pausingSources.Count > 0;
			bool flag;
			do
			{
				flag = false;
				foreach (object pausingSource in pausingSources)
				{
					if (pausingSource.IsUnityNull())
					{
						if (AI.logIssues)
						{
							RustNavigation.LogError("Removing null pausing source from " + base.baseEntity.name + ".");
						}
						pausingSources.Remove(pausingSource);
						flag = true;
						break;
					}
				}
			}
			while (flag);
			OnChange(wasPaused);
			return pausingSources.Count > 0;
		}
	}

	private float agentTypeRadius
	{
		get
		{
			if (_agentTypeRadius < 0f)
			{
				float agentRadius = NavMesh.GetSettingsByID(_agentTypeID).agentRadius;
				_agentTypeRadius = ((agentRadius > 0f) ? agentRadius : 0.5f);
			}
			return _agentTypeRadius;
		}
	}

	public bool IsSprinting => currentSpeed >= sprintSpeed;

	public bool IsSwimming
	{
		get
		{
			return base.baseEntity.HasFlag(BaseEntity.Flags.Reserved1);
		}
		set
		{
			using BaseEntity.FlagsUpdateScope flagsUpdateScope = base.baseEntity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate);
			flagsUpdateScope.Set(BaseEntity.Flags.Reserved1, value);
		}
	}

	private bool TryGetAgent(out NavMeshAgent agent)
	{
		agent = null;
		if (!RustNavigation.EnsureUnityNavmesh())
		{
			return false;
		}
		if (_agent == null)
		{
			_agent = base.gameObject.GetComponent<NavMeshAgent>();
		}
		if (_agent == null)
		{
			if (AI.logIssues)
			{
				RustNavigation.LogError("Entity " + (base.baseEntity?.name ?? "unknown entity") + " has RustNavMeshAgent but no NavMeshAgent component. This component will not function correctly without a NavMeshAgent.");
			}
			return false;
		}
		agent = _agent;
		return true;
	}

	public NavVector3 WorldToNavSpace(Vector3 positionWS)
	{
		if (AI.useUnityNavmesh)
		{
			return new NavVector3(base.baseEntity.WorldToNavMeshSpace.MultiplyPoint3x4(positionWS));
		}
		if ((object)independantNavmesh == null)
		{
			return new NavVector3(positionWS);
		}
		if (HasValidIndependantNavmesh)
		{
			return independantNavmesh.TransformPointFromWorldSpaceToNavSpace(positionWS);
		}
		return new NavVector3(positionWS);
	}

	public Vector3 NavToWorldSpace(NavVector3 positionNS)
	{
		if (AI.useUnityNavmesh)
		{
			return base.baseEntity.NavMeshToWorldSpace.MultiplyPoint3x4(positionNS.Value);
		}
		if ((object)independantNavmesh == null)
		{
			return positionNS.Value;
		}
		if (HasValidIndependantNavmesh)
		{
			return independantNavmesh.TransformPointFromNavSpaceToWorldSpace(positionNS);
		}
		return positionNS.Value;
	}

	public Vector3 NavToWorldDirection(NavVector3 directionNS)
	{
		if (AI.useUnityNavmesh)
		{
			return base.baseEntity.NavMeshToWorldSpace.MultiplyVector(directionNS.Value);
		}
		if ((object)independantNavmesh == null)
		{
			return directionNS.Value;
		}
		if (HasValidIndependantNavmesh)
		{
			return independantNavmesh.TransformDirectionFromNavSpaceToWorldSpace(directionNS);
		}
		return directionNS.Value;
	}

	public NavVector3 WorldToNavDirection(Vector3 directionWS)
	{
		if (AI.useUnityNavmesh)
		{
			return new NavVector3(base.baseEntity.WorldToNavMeshSpace.MultiplyVector(directionWS));
		}
		if ((object)independantNavmesh == null)
		{
			return new NavVector3(directionWS);
		}
		if (HasValidIndependantNavmesh)
		{
			return independantNavmesh.TransformDirectionFromWorldSpaceToNavSpace(directionWS);
		}
		return new NavVector3(directionWS);
	}

	public void ApplySerializedSettingsToAgent(NavMeshAgent agent)
	{
		agent.baseOffset = _baseOffset;
		agent.agentTypeID = agentTypeID;
		agent.speed = _desiredSpeed;
		agent.angularSpeed = _angularSpeed;
		agent.acceleration = _acceleration.Value;
		agent.autoBraking = _autoBraking;
		agent.stoppingDistance = _stoppingDistance;
		agent.height = _height;
		agent.obstacleAvoidanceType = _obstacleAvoidanceType;
		agent.avoidancePriority = _avoidancePriority;
		agent.areaMask = _areaMask;
		agent.updatePosition = _updatePosition;
		agent.updateRotation = _updateRotation;
		agent.enabled = false;
	}

	private bool SamplePositionInternal(NavVector3 positionNS, out NavHit hitNS, float maxDistance, out ulong nearestPolyRef, bool includeGatedDoorPolys = false)
	{
		using (TimeWarning.New("RustNavMeshAgent.SamplePositionInternal"))
		{
			hitNS = default(NavHit);
			nearestPolyRef = 0uL;
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent))
				{
					return false;
				}
				if (!NavMesh.SamplePosition(positionNS.Value, out var hit, maxDistance, agent.areaMask))
				{
					return false;
				}
				hitNS = NavHit.FromUnity(in hit);
				return true;
			}
			if (boundNavmesh != null)
			{
				return boundNavmesh.SamplePositionPoly(positionNS, out hitNS, maxDistance * Vector3.one, out nearestPolyRef, includeGatedDoorPolys, npcDoorsWillOpen);
			}
			return false;
		}
	}

	public bool SamplePosition(Vector3 positionWS, out NavMeshHit hitWS, float maxDistance, bool debugDraw = true)
	{
		hitWS = default(NavMeshHit);
		if (!SamplePosition(WorldToNavSpace(positionWS), out var hitNS, maxDistance, debugDraw))
		{
			return false;
		}
		hitWS = hitNS.ToUnity();
		hitWS.position = NavToWorldSpace(hitNS.position);
		hitWS.normal = NavToWorldDirection(hitNS.normal);
		return true;
	}

	public bool SamplePosition(NavVector3 positionNS, out NavHit hitNS, float maxDistance, bool debugDraw = true)
	{
		ulong nearestPolyRef;
		return SamplePositionPoly(positionNS, out hitNS, maxDistance, out nearestPolyRef, debugDraw);
	}

	public bool SamplePositionPoly(NavVector3 positionNS, out NavHit hitNS, float maxDistance, out ulong nearestPolyRef, bool debugDraw = true, bool includeGatedDoorPolys = false)
	{
		using (TimeWarning.New("RustNavMeshAgent.SamplePosition"))
		{
			bool num = SamplePositionInternal(positionNS, out hitNS, maxDistance, out nearestPolyRef, includeGatedDoorPolys);
			float num2 = 1f;
			if (num && maxDistance > num2 && Mathf.Abs(hitNS.position.y - positionNS.y) > num2 && SampleGroundPositionWithPhysics(positionNS, out var hitInfoNS, 3.5f) && SamplePositionInternal(hitInfoNS.point, out var hitNS2, num2, out nearestPolyRef, includeGatedDoorPolys))
			{
				hitNS = hitNS2;
			}
			if (!num)
			{
				return false;
			}
			return true;
		}
	}

	public bool Raycast(NavVector3 startNS, NavVector3 endNS, out NavHit hitNS)
	{
		ulong startRef = 0uL;
		return Raycast(ref startRef, startNS, endNS, out hitNS);
	}

	private bool Raycast(ref ulong startRef, NavVector3 startNS, NavVector3 endNS, out NavHit hitNS)
	{
		using (TimeWarning.New("RustNavMeshAgent.Raycast"))
		{
			hitNS = default(NavHit);
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent))
				{
					return false;
				}
				if (!NavMesh.Raycast(startNS.Value, endNS.Value, out var hit, agent.areaMask))
				{
					return false;
				}
				hitNS = NavHit.FromUnity(in hit);
				return true;
			}
			if (boundNavmesh == null)
			{
				if (AI.logIssues)
				{
					RustNavigation.LogError(string.Format("No navmesh bound, cannot raycast for {0} from {1} to {2}", base.baseEntity?.name ?? "unknown entity", startNS, endNS));
				}
				return false;
			}
			return boundNavmesh.Raycast(ref startRef, startNS, endNS, out hitNS, npcDoorsWillOpen);
		}
	}

	public bool CalculatePath(NavVector3 startNS, NavVector3 endNS, RustNavMeshPath pathNS)
	{
		ulong startRef = 0uL;
		return CalculatePath(ref startRef, startNS, endNS, pathNS);
	}

	private bool CalculatePath(ref ulong startRef, NavVector3 startNS, NavVector3 endNS, RustNavMeshPath pathNS)
	{
		using (TimeWarning.New("RustNavMeshAgent.CalculatePath"))
		{
			pathNS.Reset();
			bool flag = false;
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent))
				{
					return false;
				}
				flag = RustNavMeshHelpers.CalculatePath(startNS.Value, endNS.Value, agent.areaMask, pathNS);
			}
			else if (boundNavmesh != null)
			{
				flag = boundNavmesh.CalculatePath(ref startRef, startNS, endNS, pathNS, npcDoorsWillOpen);
			}
			else
			{
				flag = false;
				if (AI.logIssues)
				{
					RustNavigation.LogError(string.Format("No navmesh bound, cannot calculate path for {0} from {1} to {2}", base.baseEntity?.name ?? "unknown entity", startNS, endNS));
				}
			}
			if (flag)
			{
				_ = pathNS.status;
			}
			return flag;
		}
	}

	private bool SampleReachTarget(NavVector3 locationNS, out NavHit hitNS)
	{
		if (!SamplePosition(locationNS, out hitNS, 2f))
		{
			return false;
		}
		float num = ((locationNS.y - hitNS.position.y > 0.75f) ? 0.35f : 1f);
		return NavVector3.DistanceXZ(hitNS.position, locationNS) <= num;
	}

	public bool CanReach(Vector3 locationWS, bool updateLastValidPath = false)
	{
		return CanReach(WorldToNavSpace(locationWS), updateLastValidPath);
	}

	public bool CanReach(NavVector3 locationNS, bool updateLastValidPath = false)
	{
		using (TimeWarning.New("RustNavMeshAgent.CanReach"))
		{
			if (!SampleReachTarget(locationNS, out var hitNS))
			{
				return false;
			}
			if (!CalculatePath(hitNS.position, tempPath))
			{
				return false;
			}
			bool result = tempPath.status == NavMeshPathStatus.PathComplete;
			if (updateLastValidPath)
			{
				lastValidPath.Clear();
				lastValidPath.AddRange(tempPath.corners);
			}
			return result;
		}
	}

	public bool HasStraightPathTo(Vector3 locationWS)
	{
		return HasStraightPathTo(WorldToNavSpace(locationWS));
	}

	public bool HasStraightPathTo(NavVector3 locationNS)
	{
		using (TimeWarning.New("RustNavMeshAgent.HasStraightPathTo"))
		{
			NavHit hitNS;
			NavHit hitNS2;
			return SampleReachTarget(locationNS, out hitNS) && !Raycast(hitNS.position, out hitNS2);
		}
	}

	public bool SetDestinationWithParams(Vector3 targetPositionWS, bool autoBraking = true, Speeds? gait = null, float? acceleration = null, float? deceleration = null, float? deviation = null, float? swimDepth = null, float? stoppingDistance = null)
	{
		return SetDestinationWithParams(WorldToNavSpace(targetPositionWS), autoBraking, gait, acceleration, deceleration, deviation, swimDepth, stoppingDistance);
	}

	public bool SetDestinationWithParams(NavVector3 targetPositionNS, bool autoBraking = true, Speeds? gait = null, float? acceleration = null, float? deceleration = null, float? deviation = null, float? swimDepth = null, float? stoppingDistance = null)
	{
		if (!SetDestination(targetPositionNS))
		{
			return false;
		}
		this.autoBraking = autoBraking;
		SetStoppingDistanceOverride(stoppingDistance ?? (-1f));
		if (gait.HasValue)
		{
			SetGait(gait.Value);
		}
		if (acceleration.HasValue)
		{
			this.acceleration = acceleration.Value;
		}
		if (deceleration.HasValue)
		{
			this.deceleration.Value = deceleration.Value;
		}
		if (deviation.HasValue)
		{
			currentDeviation = deviation.Value;
		}
		if (swimDepth.HasValue)
		{
			desiredSwimDepth.Value = swimDepth.Value;
		}
		return true;
	}

	private void OnEnable()
	{
		enabledComponents.TryAdd(this);
		isScientist = base.baseEntity.Is<ScientistNPC2>(out var _);
		npcDoorsOpenForEntity = base.baseEntity.IsNpcPlayer();
		TryGetComponent<SenseComponent>(out senses);
		if (AI.useUnityNavmesh)
		{
			_nextPositionNS = WorldToNavSpace(AgentTransform.position);
			if (SamplePositionPoly(_nextPositionNS, out var hitNS, 10f, out var _))
			{
				_nextPositionNS = hitNS.position;
			}
		}
		else
		{
			if (corridor == IntPtr.Zero)
			{
				corridor = RecastWrapper.CreateCorridor();
			}
			TryBindNavmesh();
		}
		previousPositionNS = _nextPositionNS;
		TrySyncWorldPosWithNavPos();
	}

	internal void TryBindNavmesh()
	{
		BindNavmeshAt(AgentTransform.position);
		_nextPositionNS = WorldToNavSpace(AgentTransform.position);
		if (SamplePositionPoly(_nextPositionNS, out var hitNS, 10f, out var nearestPolyRef, debugDraw: true, includeGatedDoorPolys: true))
		{
			_nextPositionNS = hitNS.position;
		}
		AnchorCorridor(nearestPolyRef, _nextPositionNS);
		previousPositionNS = _nextPositionNS;
	}

	private void BindNavmeshAt(Vector3 positionWS)
	{
		lastSeenTileVersion = -1;
		lastCheckedFlagStamp = 0u;
		anchorCheckedTileDataVersion = -1;
		independantNavmesh = IndependantNavmesh.FindNavmeshAtPosition(positionWS);
		if (HasValidIndependantNavmesh)
		{
			boundNavmesh = independantNavmesh.Navmesh;
		}
		else
		{
			boundNavmesh = (RustNavigation.Instance.IsDefaultNavmeshBuilt() ? RustNavigation.Instance.DefaultNavmesh : null);
		}
	}

	private void OnDestroy()
	{
		if (corridor != IntPtr.Zero)
		{
			RecastWrapper.FreeCorridor(corridor);
			corridor = IntPtr.Zero;
		}
	}

	private void RefreshCorners()
	{
		cornersDirty = false;
		lastCornerPullPositionNS = _nextPositionNS;
		lastCornerPullTime = UnityEngine.Time.time;
		boundNavmesh.CorridorFindCorners(corridor, corners, 256, out lastCornerIsEnd);
		remainingCornerTailDistance = 0f;
		for (int i = 1; i < corners.Count; i++)
		{
			remainingCornerTailDistance += NavVector3.Distance(corners[i - 1], corners[i]);
		}
	}

	private bool ShouldRepullSteeringCorners()
	{
		if (corners.Count == 0)
		{
			return true;
		}
		float num = _nextPositionNS.x - corners[0].x;
		float num2 = _nextPositionNS.z - corners[0].z;
		if (num * num + num2 * num2 <= 0.0625f)
		{
			return true;
		}
		float num3 = _nextPositionNS.x - lastCornerPullPositionNS.x;
		num2 = _nextPositionNS.z - lastCornerPullPositionNS.z;
		if (num3 * num3 + num2 * num2 >= 0.122499995f)
		{
			return true;
		}
		return UnityEngine.Time.time - lastCornerPullTime >= 0.25f;
	}

	private float RemainingDistanceAlongCorners()
	{
		using (TimeWarning.New("Tick.RemainingAlongCorners"))
		{
			return (corners.Count == 0) ? 0f : (NavVector3.Distance(_nextPositionNS, corners[0]) + remainingCornerTailDistance);
		}
	}

	private void OnDisable()
	{
		enabledComponents.Remove(this);
		if (enabledComponents.Count == 0 && !transformBatchActive)
		{
			DisposeTransformBatch();
		}
		if (AI.useUnityNavmesh && TryGetAgent(out var agent))
		{
			agent.enabled = false;
		}
		if (!AI.useUnityNavmesh)
		{
			ResetPath();
		}
	}

	public static void RebindAgentsAfterNavmeshSwap()
	{
		int count = enabledComponents.Count;
		for (int i = 0; i < count; i++)
		{
			RustNavMeshAgent rustNavMeshAgent = enabledComponents[i];
			if (!rustNavMeshAgent.IsUnityNull() && (rustNavMeshAgent.boundNavmesh == null || !rustNavMeshAgent.boundNavmesh.IsValid()))
			{
				rustNavMeshAgent.TryBindNavmesh();
				rustNavMeshAgent.ResetPath();
			}
		}
	}

	public static void TickEnabledComponents()
	{
		float deltaTime = UnityEngine.Time.deltaTime;
		int count = enabledComponents.Count;
		BeginTransformBatch(count);
		try
		{
			for (int num = count - 1; num >= 0; num--)
			{
				RustNavMeshAgent rustNavMeshAgent = enabledComponents[num];
				if (rustNavMeshAgent.IsUnityNull() || !rustNavMeshAgent.baseEntity.IsValid())
				{
					enabledComponents.RemoveAt(num);
				}
				else
				{
					transformBatchAgent = rustNavMeshAgent;
					rustNavMeshAgent.Tick(deltaTime);
				}
			}
		}
		finally
		{
			transformBatchAgent = null;
			try
			{
				FlushTransformBatch();
			}
			finally
			{
				transformBatchActive = false;
				if (enabledComponents.Count == 0)
				{
					DisposeTransformBatch();
				}
			}
		}
	}

	private void Tick(float deltaTime)
	{
		try
		{
			using (TimeWarning.New("RustNavMeshAgent.Tick"))
			{
				if (AI.useUnityNavmesh && TryGetAgent(out var agent))
				{
					if (letUnityMoveAgentIfPossible)
					{
						return;
					}
					if (agent.enabled && agent.isOnNavMesh)
					{
						agent.isStopped = true;
					}
					agent.updatePosition = false;
					agent.updateRotation = false;
				}
				if (updateRotation && (overrideDirectionWS.HasValue || (isScientist && senses != null)))
				{
					using (TimeWarning.New("Tick.Rotation"))
					{
						hasCachedSteeringForward = false;
						if (overrideDirectionWS.HasValue)
						{
							AgentTransform.rotation = Quaternion.RotateTowards(AgentTransform.rotation, Quaternion.LookRotation(overrideDirectionWS.Value.NormalizeXZ()), _angularSpeed * deltaTime);
						}
						else
						{
							AgentTransform.rotation = Quaternion.LookRotation((senses.GetEyeTransform().rotation * Vector3.forward).NormalizeXZ());
						}
					}
				}
				if (!hasPath || IsPaused || _isStopped)
				{
					if (!hasPath)
					{
						EnsureAnchorValidAfterTileRebuild();
					}
					if (currentSpeed == 0f && !(previousPositionNS != _nextPositionNS))
					{
						return;
					}
					using (TimeWarning.New("Tick.NoPath"))
					{
						currentSpeed = Mathf.SmoothDamp(currentSpeed, (_nextPositionNS - previousPositionNS).magnitude / deltaTime, ref dampVelocity, 1f, 9999f, UnityEngine.Time.smoothDeltaTime * 10f);
						if (currentSpeed < 0.005f)
						{
							currentSpeed = 0f;
						}
						return;
					}
				}
				bool flag = !letUnityMoveAgentIfPossible && canSteer;
				float num = RestingDistance(currentStoppingDistance);
				NavVector3 navVector;
				float num2;
				if (AI.useUnityNavmesh)
				{
					if (!TryGetAgent(out var agent2))
					{
						return;
					}
					navVector = new NavVector3(agent2.steeringTarget);
					num2 = remainingDistance;
					if (num2 <= num)
					{
						ResetPath();
						return;
					}
				}
				else
				{
					if (boundNavmesh == null || corridor == IntPtr.Zero)
					{
						ResetPath();
						return;
					}
					bool flag2 = npcDoorsWillOpen;
					if (lastSeenTileVersion != boundNavmesh.TileChangeVersion || flag2 != lastCheckedNpcDoors)
					{
						lastSeenTileVersion = boundNavmesh.TileChangeVersion;
						if (flag2 != lastCheckedNpcDoors)
						{
							lastCheckedNpcDoors = flag2;
							lastCheckedFlagStamp = 0u;
						}
						uint currentStamp;
						bool num3 = boundNavmesh.CorridorIsValid(corridor, int.MaxValue, flag2, lastCheckedFlagStamp, out currentStamp);
						lastCheckedFlagStamp = currentStamp;
						if (!num3)
						{
							using (TimeWarning.New("Tick.Replan"))
							{
								ulong startRef = currentPolyRef;
								if (!boundNavmesh.CalculatePath(ref startRef, _nextPositionNS, CurPathNS.GetDestinationNS(), tempPath, npcDoorsWillOpen) || tempPath.status != 0 || !SetPath(tempPath))
								{
									ResetPath();
									return;
								}
							}
						}
					}
					if (cornersDirty || (flag && ShouldRepullSteeringCorners()))
					{
						RefreshCorners();
					}
					if (corners.Count == 0)
					{
						ResetPath();
						return;
					}
					navVector = corners[0];
					num2 = RemainingDistanceAlongCorners();
					if (lastCornerIsEnd && num2 <= num)
					{
						ResetPath();
						return;
					}
					if (flag)
					{
						pendingOptimizeTargetNS = corners[Mathf.Min(1, corners.Count - 1)];
						hasPendingOptimize = true;
					}
					else
					{
						float num4 = _nextPositionNS.x - navVector.x;
						float num5 = _nextPositionNS.z - navVector.z;
						if (num4 * num4 + num5 * num5 <= 0.0625f)
						{
							cornersDirty = true;
						}
					}
				}
				bool flag3 = !AI.useUnityNavmesh;
				if (AI.useUnityNavmesh || !TryQueuePathFollowing(navVector, num2, num, flag))
				{
					NavVector3 navVector2 = navVector - _nextPositionNS;
					if (flag3)
					{
						navVector2 = navVector2.Flat();
					}
					float magnitude = navVector2.magnitude;
					AdjustCurrentSpeedFromDesiredSpeed(_nextPositionNS, _autoBraking, num2, num, deltaTime);
					float num6 = currentSpeed * deltaTime;
					if (!flag3 || (lastCornerIsEnd && corners.Count == 1))
					{
						num6 = Mathf.Clamp(num6, 0f - magnitude, magnitude);
					}
					NavVector3 navVector3 = ((magnitude > 1E-05f) ? (navVector2 * (num6 / magnitude)) : NavVector3.zero);
					MoveInternal(navVector3, num2);
					_velocityNS = navVector3 / deltaTime;
				}
			}
		}
		finally
		{
			previousPositionNS = _nextPositionNS;
		}
	}

	public void SetStoppingDistanceOverride(float distance)
	{
		_stoppingDistanceOverride = distance;
	}

	public void ActivateCurrentOffMeshLink(bool activated)
	{
		if (AI.useUnityNavmesh && TryGetAgent(out var agent))
		{
			agent.ActivateCurrentOffMeshLink(activated);
		}
	}

	public void CompleteOffMeshLink()
	{
		if (AI.useUnityNavmesh && TryGetAgent(out var agent))
		{
			agent.CompleteOffMeshLink();
		}
	}

	public bool FindClosestEdge(out NavHit hitNS)
	{
		if (AI.useUnityNavmesh && TryGetAgent(out var agent))
		{
			NavMeshHit hit;
			bool result = agent.FindClosestEdge(out hit);
			hitNS = NavHit.FromUnity(in hit);
			return result;
		}
		hitNS = default(NavHit);
		if (boundNavmesh != null)
		{
			ulong startRef = currentPolyRef;
			return boundNavmesh.FindDistanceToWall(ref startRef, _nextPositionNS, 10f, out hitNS, npcDoorsWillOpen);
		}
		return false;
	}

	public bool FindClosestEdge(NavVector3 positionNS, out NavHit hitNS)
	{
		if (AI.useUnityNavmesh)
		{
			hitNS = default(NavHit);
			if (!TryGetAgent(out var agent))
			{
				return false;
			}
			if (!NavMesh.FindClosestEdge(positionNS.Value, out var hit, agent.areaMask))
			{
				return false;
			}
			hitNS = NavHit.FromUnity(in hit);
			return true;
		}
		hitNS = default(NavHit);
		ulong startRef = 0uL;
		if (boundNavmesh != null)
		{
			return boundNavmesh.FindDistanceToWall(ref startRef, positionNS, 10f, out hitNS, npcDoorsWillOpen);
		}
		return false;
	}

	public bool SampleConnectedPositions(float maxRadius, float minRadius, int count, List<NavVector3> resultsNS, float angleOffset = -1f)
	{
		if (AI.useUnityNavmesh || boundNavmesh == null)
		{
			return false;
		}
		if (angleOffset < 0f)
		{
			angleOffset = UnityEngine.Random.Range(0f, MathF.PI * 2f);
		}
		ulong startRef = currentPolyRef;
		return boundNavmesh.FindDonutPointsInCircle(ref startRef, _nextPositionNS, maxRadius, minRadius, angleOffset, count, resultsNS, npcDoorsWillOpen);
	}

	public bool SampleConnectedPositions(NavVector3 centerNS, float maxRadius, float minRadius, int count, List<NavVector3> resultsNS, float angleOffset = -1f)
	{
		if (AI.useUnityNavmesh || boundNavmesh == null)
		{
			return false;
		}
		if (angleOffset < 0f)
		{
			angleOffset = UnityEngine.Random.Range(0f, MathF.PI * 2f);
		}
		ulong startRef = 0uL;
		if (currentPolyRef != 0L && NavVector3.Distance(centerNS, _nextPositionNS) <= maxRadius)
		{
			startRef = currentPolyRef;
		}
		return boundNavmesh.FindDonutPointsInCircle(ref startRef, centerNS, maxRadius, minRadius, angleOffset, count, resultsNS, npcDoorsWillOpen);
	}

	public bool SetDestination(Vector3 targetPositionWS)
	{
		return SetDestination(WorldToNavSpace(targetPositionWS));
	}

	public bool SetDestination(NavVector3 targetPositionNS)
	{
		using (TimeWarning.New("RustNavMeshAgent.SetDestination"))
		{
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent))
				{
					return false;
				}
				if (!TryEnableAgent(agent))
				{
					return false;
				}
				if (letUnityMoveAgentIfPossible)
				{
					return agent.SetDestination(targetPositionNS.Value);
				}
			}
			if (hasPath && CurPathNS.GetDestinationNS() == targetPositionNS)
			{
				return true;
			}
			if (TryPatchMovingDestination(targetPositionNS))
			{
				return true;
			}
			if (!CalculatePath(targetPositionNS, tempPath) || tempPath.status != 0)
			{
				return false;
			}
			if (!SetPath(tempPath))
			{
				return false;
			}
			return true;
		}
	}

	private bool TryPatchMovingDestination(NavVector3 targetPositionNS)
	{
		if (AI.useUnityNavmesh)
		{
			return false;
		}
		if (corridor == IntPtr.Zero || boundNavmesh == null)
		{
			return false;
		}
		if (UnityEngine.Time.time - lastFullPlanTime >= 1f)
		{
			return false;
		}
		bool flag = followingPath;
		if (flag)
		{
			if (CurPathNS.status != 0 || CurPathNS.corners.Count == 0)
			{
				return false;
			}
			if (NavVector3.Distance(CurPathNS.GetDestinationNS(), targetPositionNS) > 3f)
			{
				return false;
			}
		}
		else
		{
			ulong num = currentPolyRef;
			if (num == 0L)
			{
				return false;
			}
			if (NavVector3.Distance(_nextPositionNS, targetPositionNS) > 3f)
			{
				return false;
			}
			AnchorCorridor(num, _nextPositionNS);
		}
		if (!boundNavmesh.CorridorMoveTargetPosition(corridor, targetPositionNS, out var resultTargetNS, npcDoorsWillOpen))
		{
			return false;
		}
		NavVector3 navVector = resultTargetNS - targetPositionNS;
		if (Mathf.Abs(navVector.x) > 0.5f || Mathf.Abs(navVector.y) > 2f || Mathf.Abs(navVector.z) > 0.5f)
		{
			return false;
		}
		if (flag)
		{
			CurPathNS.corners[CurPathNS.corners.Count - 1] = resultTargetNS;
		}
		else
		{
			CurPathNS.corners.Clear();
			CurPathNS.corners.Add(_nextPositionNS);
			CurPathNS.corners.Add(resultTargetNS);
			CurPathNS.polyRefCount = 0;
			CurPathNS.status = NavMeshPathStatus.PathComplete;
		}
		followingPath = true;
		cornersDirty = true;
		_isStopped = false;
		return true;
	}

	public void TryEnableInternalUnityAgent()
	{
		if (AI.useUnityNavmesh && base.enabled && TryGetAgent(out var agent))
		{
			TryEnableAgent(agent);
		}
	}

	private bool TryEnableAgent(NavMeshAgent Agent)
	{
		if (Agent.enabled)
		{
			return Agent.isOnNavMesh;
		}
		Agent.enabled = true;
		if (!Agent.isOnNavMesh)
		{
			if (AI.logIssues)
			{
				RustNavigation.LogError($"{base.baseEntity} is not on navmesh at {base.baseEntity.transform.position} in {MapHelper.PositionToString(base.baseEntity.transform.position)}");
			}
			return false;
		}
		return true;
	}

	public bool SetPath(RustNavMeshPath pathNS)
	{
		using (TimeWarning.New("RustNavMeshAgent.SetPath"))
		{
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent))
				{
					return false;
				}
				if (pathNS.unityPath == null)
				{
					if (AI.logIssues)
					{
						RustNavigation.LogError("Trying to set a path on a RustNavMeshAgent with useUnityNavmesh enabled, but the provided path doesn't have a Unity NavMeshPath.");
					}
					return false;
				}
				if (!TryEnableAgent(agent))
				{
					return false;
				}
				return agent.SetPath(pathNS.unityPath);
			}
			if (pathNS.status == NavMeshPathStatus.PathInvalid)
			{
				ResetPath();
				return false;
			}
			if (pathNS.polyRefCount == 0 || pathNS.polyRefs[0] != currentPolyRef)
			{
				ulong startRef = currentPolyRef;
				if (boundNavmesh == null || !boundNavmesh.CalculatePath(ref startRef, _nextPositionNS, pathNS.GetDestinationNS(), scratchPath, npcDoorsWillOpen) || scratchPath.status != 0)
				{
					ResetPath();
					return false;
				}
				pathNS = scratchPath;
			}
			CurPathNS.CopyFrom(pathNS);
			IntPtr intPtr = corridor;
			ulong[] polyRefs = CurPathNS.polyRefs;
			int polyRefCount = CurPathNS.polyRefCount;
			NavVector3 destinationNS = CurPathNS.GetDestinationNS();
			RecastWrapper.CorridorSetPath(intPtr, polyRefs, polyRefCount, in destinationNS.Value);
			followingPath = true;
			cornersDirty = true;
			_isStopped = false;
			lastFullPlanTime = UnityEngine.Time.time;
			lastValidPath.Clear();
			lastValidPath.AddRange(CurPathNS.corners);
			return true;
		}
	}

	private void AnchorCorridor(ulong polyRef, NavVector3 positionNS)
	{
		if (corridor != IntPtr.Zero)
		{
			RecastWrapper.CorridorReset(corridor, polyRef, in positionNS.Value);
		}
	}

	public void Move(NavVector3 deltaNS)
	{
		FlushTransformBatch();
		try
		{
			MoveInternal(deltaNS, null);
		}
		finally
		{
			FlushTransformBatch();
		}
	}

	private void MoveInternal(NavVector3 deltaNS, float? remainingDistanceHint)
	{
		using (TimeWarning.New("RustNavMeshAgent.Move"))
		{
			if (AI.useUnityNavmesh && letUnityMoveAgentIfPossible)
			{
				if (TryGetAgent(out var agent) && TryEnableAgent(agent))
				{
					agent.Move(deltaNS.Value);
				}
				return;
			}
			if (!letUnityMoveAgentIfPossible && canSteer && !IsPaused)
			{
				deltaNS = AdjustMovementForSteering(deltaNS, remainingDistanceHint);
			}
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent2) || !TryEnableAgent(agent2))
				{
					return;
				}
				agent2.Move(deltaNS.Value);
				_nextPositionNS = new NavVector3(agent2.nextPosition);
			}
			else
			{
				if (boundNavmesh == null || !boundNavmesh.IsValid())
				{
					TryBindNavmesh();
				}
				if (boundNavmesh != null && corridor != IntPtr.Zero)
				{
					if ((!followingPath || lastSeenTileVersion != boundNavmesh.TileChangeVersion) && !boundNavmesh.IsValidPolyRef(currentPolyRef))
					{
						TryBindNavmesh();
						ResetPath();
						return;
					}
					bool flag;
					NavVector3 resultPosNS;
					if (hasPendingOptimize)
					{
						hasPendingOptimize = false;
						flag = boundNavmesh.CorridorOptimizeAndMove(corridor, pendingOptimizeTargetNS, 20f, _nextPositionNS + deltaNS, out resultPosNS, npcDoorsWillOpen);
					}
					else
					{
						flag = boundNavmesh.CorridorMove(corridor, _nextPositionNS + deltaNS, out resultPosNS, out var _, npcDoorsWillOpen);
					}
					if (flag)
					{
						_nextPositionNS = resultPosNS;
					}
					else if (followingPath)
					{
						ResetPath();
					}
				}
			}
			Quaternion? quaternion = null;
			bool flag2 = false;
			NavVector3 directionNS = deltaNS.Flat();
			if (_updateRotation && !IsPaused && !overrideDirectionWS.HasValue && directionNS.Value.sqrMagnitude > 1E-06f)
			{
				using (TimeWarning.New("Move.RotationWrite"))
				{
					Vector3 vector = NavToWorldDirection(directionNS);
					if (!letUnityMoveAgentIfPossible && canSteer)
					{
						quaternion = Quaternion.LookRotation(vector);
						cachedSteeringForwardNS = directionNS.NormalizeXZ();
						hasCachedSteeringForward = true;
					}
					else
					{
						quaternion = Quaternion.LookRotation(vector);
						flag2 = true;
						hasCachedSteeringForward = false;
					}
				}
			}
			else
			{
				hasCachedSteeringForward = false;
			}
			if (!TryQueueTransformUpdate(quaternion, flag2))
			{
				if (flag2)
				{
					quaternion = Quaternion.RotateTowards(AgentTransform.rotation, quaternion.Value, _angularSpeed * UnityEngine.Time.deltaTime);
				}
				SyncWorldPosWithNavPos(quaternion);
				TryOpenDoors();
			}
		}
	}

	private Vector3 GetMovementWorldPosition()
	{
		if (!canSwim)
		{
			return NavToWorldSpace(_nextPositionNS + NavVector3.up * _baseOffset);
		}
		return CalculateSwimmingWorldPosition();
	}

	private bool TrySyncWorldPosWithNavPos()
	{
		return SyncWorldPosWithNavPos(null);
	}

	private bool SyncWorldPosWithNavPos(Quaternion? newRotation)
	{
		using (TimeWarning.New("RustNavMeshAgent.TrySyncWorldPosWithNavPos"))
		{
			FlushTransformBatch();
			if (!_updatePosition)
			{
				if (newRotation.HasValue)
				{
					AgentTransform.rotation = newRotation.Value;
				}
				return false;
			}
			Vector3 movementWorldPosition = GetMovementWorldPosition();
			if (newRotation.HasValue)
			{
				AgentTransform.SetPositionAndRotation(movementWorldPosition, newRotation.Value);
			}
			else
			{
				AgentTransform.position = movementWorldPosition;
			}
			return true;
		}
	}

	public bool Warp(Vector3 newPositionWS)
	{
		return WarpToWorldPosition(newPositionWS);
	}

	public bool Warp(NavVector3 newPositionNS)
	{
		using (TimeWarning.New("RustNavMeshAgent.Warp"))
		{
			FlushTransformBatch();
			if (AI.useUnityNavmesh)
			{
				if (!TryGetAgent(out var agent))
				{
					return false;
				}
				if (!TryEnableAgent(agent))
				{
					return false;
				}
				return agent.Warp(newPositionNS.Value);
			}
			if (!SamplePositionPoly(newPositionNS, out var hitNS, 1f, out var nearestPolyRef))
			{
				return false;
			}
			_nextPositionNS = hitNS.position;
			AnchorCorridor(nearestPolyRef, _nextPositionNS);
			ResetPath();
			TrySyncWorldPosWithNavPos();
			return true;
		}
	}

	public bool WarpToWorldPosition(Vector3 newPositionWS)
	{
		FlushTransformBatch();
		if (!AI.useUnityNavmesh)
		{
			BindNavmeshAt(newPositionWS);
		}
		return Warp(WorldToNavSpace(newPositionWS));
	}

	private void EnsureAnchorValid()
	{
		if (AI.useUnityNavmesh || corridor == IntPtr.Zero)
		{
			return;
		}
		if (boundNavmesh == null || !boundNavmesh.IsValid())
		{
			TryBindNavmesh();
			return;
		}
		ulong num = currentPolyRef;
		if (num == 0L || !boundNavmesh.IsValidPolyRef(num))
		{
			if (SamplePositionPoly(_nextPositionNS, out var hitNS, 2.5f, out var nearestPolyRef, debugDraw: false, includeGatedDoorPolys: true) && nearestPolyRef != 0L)
			{
				_nextPositionNS = hitNS.position;
				AnchorCorridor(nearestPolyRef, _nextPositionNS);
				previousPositionNS = _nextPositionNS;
				ResetPath();
			}
			else
			{
				TryBindNavmesh();
				ResetPath();
			}
			if (currentPolyRef != 0L && !IsPaused)
			{
				TrySyncWorldPosWithNavPos();
			}
		}
	}

	private void EnsureAnchorValidAfterTileRebuild()
	{
		if (!AI.useUnityNavmesh && boundNavmesh != null && anchorCheckedTileDataVersion != boundNavmesh.TileDataVersion && !IsPaused)
		{
			anchorCheckedTileDataVersion = boundNavmesh.TileDataVersion;
			if (currentPolyRef != 0L)
			{
				EnsureAnchorValid();
			}
		}
	}

	public bool CalculatePath(Vector3 targetPositionWS, RustNavMeshPath pathNS)
	{
		return CalculatePath(WorldToNavSpace(targetPositionWS), pathNS);
	}

	public bool CalculatePath(NavVector3 targetPositionNS, RustNavMeshPath pathNS)
	{
		EnsureAnchorValid();
		ulong startRef = currentPolyRef;
		return CalculatePath(ref startRef, _nextPositionNS, targetPositionNS, pathNS);
	}

	public bool Raycast(Vector3 targetPositionWS, out NavMeshHit hitWS)
	{
		hitWS = default(NavMeshHit);
		if (!Raycast(WorldToNavSpace(targetPositionWS), out var hitNS))
		{
			return false;
		}
		hitWS = hitNS.ToUnity();
		hitWS.position = NavToWorldSpace(hitNS.position);
		hitWS.normal = NavToWorldDirection(hitNS.normal);
		return true;
	}

	public bool Raycast(NavVector3 targetPositionNS, out NavHit hitNS)
	{
		ulong startRef = currentPolyRef;
		return Raycast(ref startRef, _nextPositionNS, targetPositionNS, out hitNS);
	}

	public bool IsPositionOnNavmesh(Vector3 positionWS, out NavMeshHit hitWS)
	{
		hitWS = default(NavMeshHit);
		if (!IsPositionOnNavmesh(WorldToNavSpace(positionWS), out var hitNS))
		{
			return false;
		}
		hitWS = hitNS.ToUnity();
		hitWS.position = NavToWorldSpace(hitNS.position);
		hitWS.normal = NavToWorldDirection(hitNS.normal);
		return true;
	}

	public bool IsPositionOnNavmesh(NavVector3 positionNS, out NavHit hitNS, float maxDistance = 2f)
	{
		return SamplePosition(positionNS, out hitNS, maxDistance);
	}

	public void ResetPath()
	{
		using (TimeWarning.New("RustNavMeshAgent.ResetPath"))
		{
			if (AI.useUnityNavmesh && TryGetAgent(out var agent) && agent.enabled && agent.isOnNavMesh)
			{
				agent.ResetPath();
			}
			CurPathNS.Reset();
			followingPath = false;
			corners.Clear();
			cornersDirty = true;
			lastCornerIsEnd = false;
			hasPendingOptimize = false;
			AnchorCorridor(currentPolyRef, _nextPositionNS);
			autoBraking = true;
			_desiredSpeed = 0f;
			if (!letUnityMoveAgentIfPossible)
			{
				_acceleration.Reset();
				deceleration.Reset();
			}
			currentDeviation = 0f;
			_stoppingDistanceOverride = -1f;
		}
	}

	public bool TryOpenDoors()
	{
		if (!canOpenDoors)
		{
			return false;
		}
		using (TimeWarning.New("RustNavMeshAgent.TryOpenDoors"))
		{
			bool flag = false;
			using PooledList<NPCDoorTriggerBox> pooledList = Facepunch.Pool.Get<PooledList<NPCDoorTriggerBox>>();
			NPCDoorTriggerBox.AllDoors.GetNeighboors(base.baseEntity.transform.position, pooledList);
			foreach (NPCDoorTriggerBox item in pooledList)
			{
				flag |= item.TryOpenDoorFor(base.baseEntity);
			}
			return flag;
		}
	}

	private PathFollowingSpeed GetPathFollowingSpeed(bool autoBraking, float remaining, float stopping)
	{
		PathFollowingSpeed result = default(PathFollowingSpeed);
		result.current = currentSpeed;
		result.desired = AdjustDesiredSpeedWhenSwimming(_desiredSpeed, sprintSpeed);
		result.acceleration = _acceleration.Value;
		result.deceleration = deceleration.Value;
		result.emergencyDeceleration = emergencyDeceleration;
		result.walkSpeed = walkSpeed;
		result.brakeFloor = Mathf.Min(1f, GetSpeedForGait(minimumGait) * 0.5f);
		result.slowDownRadius = (letUnityMoveAgentIfPossible ? (2f * agentTypeRadius) : 0f);
		result.remaining = remaining;
		result.stopping = stopping;
		result.unityBraking = (byte)(letUnityMoveAgentIfPossible ? 1 : 0);
		result.autoBraking = (byte)(autoBraking ? 1 : 0);
		return result;
	}

	private bool TryQueuePathFollowing(NavVector3 waypoint, float remaining, float stopping, bool steering)
	{
		if (!AI.batch_navmesh_pathfollowing || !transformBatchActive || (object)transformBatchAgent != this || canOpenDoors || canSwim || hasPendingTransform || (object)independantNavmesh != null || overrideDirectionWS.HasValue || isScientist || boundNavmesh == null || !boundNavmesh.IsValid() || corridor == IntPtr.Zero)
		{
			return false;
		}
		EnsureMovementCapacity();
		byte b = 8;
		if (_updatePosition)
		{
			b |= 1;
		}
		if (_updateRotation)
		{
			b |= 2;
		}
		byte b2 = 0;
		if (steering)
		{
			b2 |= 1;
		}
		if (lastCornerIsEnd && corners.Count == 1)
		{
			b2 |= 2;
		}
		if (_autoBraking)
		{
			b2 |= 4;
		}
		if (hasCachedSteeringForward)
		{
			b2 |= 8;
		}
		if (hasPendingOptimize)
		{
			b2 |= 0x10;
		}
		if (npcDoorsWillOpen)
		{
			b2 |= 0x20;
		}
		movementUpdates[pendingTransformUpdates] = new MovementTransformUpdate
		{
			flags = b,
			maxDegreesDelta = _angularSpeed * UnityEngine.Time.deltaTime,
			path = new PathFollowingUpdate
			{
				navmesh = boundNavmesh.NavMeshHandle,
				corridor = corridor,
				position = _nextPositionNS.Value,
				waypoint = waypoint.Value,
				destination = CurPathNS.GetDestinationNS().Value,
				optimizeTarget = pendingOptimizeTargetNS.Value,
				forward = cachedSteeringForwardNS.Value,
				speed = GetPathFollowingSpeed(_autoBraking, remaining, stopping),
				deviation = currentDeviation,
				turnRadius = maxTurnRadius,
				baseOffset = _baseOffset,
				moved = -1,
				flags = b2
			}
		};
		pendingTransformAgents[pendingTransformUpdates++] = this;
		hasPendingTransform = true;
		pendingPathFollowing = true;
		return true;
	}

	private static void EnsureMovementQueries()
	{
		if (movementQueries != null)
		{
			return;
		}
		movementQueries = new IntPtr[JobsUtility.ThreadIndexCount];
		try
		{
			for (int i = 0; i < movementQueries.Length; i++)
			{
				movementQueries[i] = RecastWrapper.CreateMovementQuery();
				if (movementQueries[i] == IntPtr.Zero)
				{
					throw new OutOfMemoryException("Could not allocate movement query scratch storage.");
				}
			}
		}
		catch
		{
			DisposeMovementQueries();
			throw;
		}
	}

	private static void DisposeMovementQueries()
	{
		if (movementQueries == null)
		{
			return;
		}
		IntPtr[] array = movementQueries;
		foreach (IntPtr intPtr in array)
		{
			if (intPtr != IntPtr.Zero)
			{
				RecastWrapper.FreeMovementQuery(intPtr);
			}
		}
		movementQueries = null;
	}

	private unsafe static void CalculatePathFollowing(ref MovementTransformUpdate update, IntPtr query, Quaternion currentRotation, float dt)
	{
		ref PathFollowingUpdate path = ref update.path;
		path.speed.current = path.speed.Advance(dt, out var _);
		Vector3 vector = path.waypoint - path.position;
		vector.y = 0f;
		float magnitude = vector.magnitude;
		float num = path.speed.current * dt;
		if ((path.flags & 2) != 0)
		{
			num = Mathf.Clamp(num, 0f - magnitude, magnitude);
		}
		Vector3 vector2 = ((magnitude > 1E-05f) ? (vector * (num / magnitude)) : Vector3.zero);
		path.velocity = vector2 / dt;
		bool flag = (path.flags & 1) != 0;
		if (flag)
		{
			if (path.deviation != 0f && Mathf.Abs(path.speed.remaining - Vector3.Distance(path.position, path.destination)) < 5f)
			{
				vector2 = math.mul(quaternion.AxisAngle(math.up(), path.deviation * (MathF.PI / 180f)), vector2);
			}
			float num2 = (((path.flags & 4) != 0) ? Mathx.RemapValClamped(path.speed.remaining, path.turnRadius * 2f, 0f, path.turnRadius, 0.001f) : path.turnRadius);
			vector2 = RotateTowardsFlat(new NavVector3(((path.flags & 8) != 0) ? path.forward : (currentRotation * Vector3.forward)), new NavVector3(vector2), path.speed.current / num2 * dt).Value * vector2.magnitude;
		}
		Vector3 vector3 = path.position + vector2;
		Vector3 position = path.position;
		Vector3 optimizeTarget = path.optimizeTarget;
		path.moved = RecastWrapper.CorridorMoveWithQuery(path.navmesh, path.corridor, query, &vector3, ((path.flags & 0x10) != 0) ? (&optimizeTarget) : null, 20f, &position, ((path.flags & 0x20) != 0) ? 1 : 0);
		if (path.moved != 0)
		{
			path.position = position;
		}
		update.position = path.position + Vector3.up * path.baseOffset;
		vector2.y = 0f;
		if ((update.flags & 2) != 0 && vector2.sqrMagnitude > 1E-06f)
		{
			update.rotation = quaternion.LookRotationSafe(vector2, math.up());
			if (flag)
			{
				path.forward = vector2.normalized;
			}
			else
			{
				update.flags |= 4;
			}
		}
		else
		{
			update.flags &= 253;
		}
	}

	private void ApplyPathFollowingResult(in MovementTransformUpdate update)
	{
		currentSpeed = update.path.speed.current;
		_nextPositionNS = new NavVector3(update.path.position);
		previousPositionNS = _nextPositionNS;
		_velocityNS = new NavVector3(update.path.velocity);
		hasPendingOptimize = false;
		hasCachedSteeringForward = (update.path.flags & 1) != 0 && (update.flags & 2) != 0;
		if (hasCachedSteeringForward)
		{
			cachedSteeringForwardNS = new NavVector3(update.path.forward);
		}
		if (update.path.moved == 0)
		{
			ResetPath();
		}
	}

	public bool Pause(object source)
	{
		bool wasPaused = pausingSources.Count > 0;
		bool result = pausingSources.Add(source);
		OnChange(wasPaused);
		return result;
	}

	public bool Unpause(object source)
	{
		bool wasPaused = pausingSources.Count > 0;
		bool result = pausingSources.Remove(source);
		OnChange(wasPaused);
		return result;
	}

	private void OnChange(bool wasPaused)
	{
		if (!wasPaused && pausingSources.Count > 0)
		{
			ResetPath();
			if (AI.useUnityNavmesh && TryGetAgent(out var agent))
			{
				agent.enabled = false;
			}
		}
		else if (wasPaused && pausingSources.Count == 0)
		{
			Vector3 position = base.transform.position;
			if (IsSwimming)
			{
				position.y = WaterLevel.GetWaterInfo(position, waves: false, volumes: false).terrainHeight;
			}
			bool flag = Warp(WorldToNavSpace(position));
			if (AI.logIssues && !flag)
			{
				Debug.LogError($"Failed to reproject {base.baseEntity.name} to current position {base.transform.position} after unpausing.", this);
			}
		}
	}

	private static float GetBrakingDistance(float speed, float brakingDeceleration)
	{
		float num = speed / Mathf.Max(brakingDeceleration, 0.001f);
		return 0.5f * brakingDeceleration * num * num;
	}

	public float GetSpeedForGait(Speeds gait)
	{
		return gait switch
		{
			Speeds.Sneak => sneakSpeed, 
			Speeds.Walk => walkSpeed, 
			Speeds.Jog => jogSpeed, 
			Speeds.Run => runSpeed, 
			Speeds.Sprint => sprintSpeed, 
			Speeds.FullSprint => fullSprintSpeed, 
			_ => walkSpeed, 
		};
	}

	public Speeds PenalisedGait(Speeds gait)
	{
		return (Speeds)Mathf.Clamp((int)(gait - gaitPenalty), (int)minimumGait, 5);
	}

	public void SetGait(Speeds gait)
	{
		speed = GetSpeedForGait(PenalisedGait(gait));
	}

	public void SetSpeedRatio(float ratio, Speeds minSpeed = Speeds.Sneak, Speeds maxSpeed = Speeds.Sprint, int offset = 0)
	{
		int num = Mathf.FloorToInt(Mathf.Lerp((float)minSpeed, (float)maxSpeed, ratio));
		num = Mathf.Clamp(num + offset, (int)minSpeed, (int)maxSpeed);
		SetGait((Speeds)num);
	}

	public void AdjustCurrentSpeedFromDesiredSpeed(NavVector3 position, bool shouldStopAtDestination, float remainingDistance, float stoppingDistance, float dt)
	{
		using (TimeWarning.New("Tick.SpeedAdjust"))
		{
			currentSpeed = GetPathFollowingSpeed(shouldStopAtDestination, remainingDistance, stoppingDistance).Advance(dt, out var _);
		}
	}

	public float RestingDistance(float stoppingDistance)
	{
		if (letUnityMoveAgentIfPossible || !canSteer)
		{
			return stoppingDistance;
		}
		return Mathf.Max(maxTurnRadius, stoppingDistance);
	}

	public NavVector3 AdjustMovementForSteering(NavVector3 deltaNS)
	{
		return AdjustMovementForSteering(deltaNS, null);
	}

	public NavVector3 AdjustMovementForSteering(NavVector3 deltaNS, float? remainingDistanceHint)
	{
		using (TimeWarning.New("AdjustMovementForSteering"))
		{
			NavVector3 targetNS = deltaNS;
			float num = remainingDistanceHint ?? remainingDistance;
			if (currentDeviation != 0f && Mathf.Abs(num - NavVector3.Distance(_nextPositionNS, CurPathNS.GetDestinationNS())) < 5f)
			{
				targetNS = Quaternion.AngleAxis(currentDeviation, Vector3.up) * deltaNS;
			}
			float num2 = (_autoBraking ? Mathx.RemapValClamped(num, maxTurnRadius * 2f, 0f, maxTurnRadius, 0.001f) : maxTurnRadius);
			float num3 = currentSpeed / num2;
			return RotateTowardsFlat(hasCachedSteeringForward ? cachedSteeringForwardNS : forward, targetNS, num3 * UnityEngine.Time.deltaTime) * targetNS.magnitude;
		}
	}

	private static NavVector3 RotateTowardsFlat(NavVector3 currentNS, NavVector3 targetNS, float maxRadiansDelta)
	{
		float x = currentNS.x;
		float z = currentNS.z;
		float x2 = targetNS.x;
		float z2 = targetNS.z;
		float num = Mathf.Sqrt(x * x + z * z);
		float num2 = Mathf.Sqrt(x2 * x2 + z2 * z2);
		if (num < 1E-06f || num2 < 1E-06f)
		{
			return targetNS.NormalizeXZ();
		}
		x /= num;
		z /= num;
		x2 /= num2;
		z2 /= num2;
		if (x * x2 + z * z2 >= Mathf.Cos(maxRadiansDelta))
		{
			return new NavVector3(new Vector3(x2, 0f, z2));
		}
		float f = ((x * z2 - z * x2 >= 0f) ? maxRadiansDelta : (0f - maxRadiansDelta));
		float num3 = Mathf.Cos(f);
		float num4 = Mathf.Sin(f);
		return new NavVector3(new Vector3(x * num3 - z * num4, 0f, x * num4 + z * num3));
	}

	public Vector3 CalculateSwimmingWorldPosition()
	{
		using (TimeWarning.New("RustNavMeshAgent.CalculateSwimmingWorldPosition"))
		{
			Vector3 vector = NavToWorldSpace(_nextPositionNS);
			WaterLevel.WaterInfo waterInfo = WaterLevel.GetWaterInfo(vector, waves: false, volumes: false);
			if (IsSwimming = waterInfo.currentDepth > desiredSwimDepth.Value)
			{
				vector.y = base.baseEntity.transform.position.y;
				vector.y = Mathf.MoveTowards(vector.y, waterInfo.surfaceLevel - desiredSwimDepth.Value, 1f * UnityEngine.Time.deltaTime);
				vector.y = Mathf.Max(vector.y, waterInfo.terrainHeight);
			}
			return vector;
		}
	}

	public float AdjustDesiredSpeedWhenSwimming(float desiredGroundSpeed, float groundSprintSpeed)
	{
		if (!IsSwimming || desiredGroundSpeed <= 0f)
		{
			return desiredGroundSpeed;
		}
		if (!(desiredGroundSpeed < groundSprintSpeed))
		{
			return swimSprintSpeed;
		}
		return swimSpeed;
	}

	public bool IsPositionOnFavoredTerrain(NavVector3 positionNS)
	{
		return IsPositionOnFavoredTerrain(NavToWorldSpace(positionNS));
	}

	public bool IsPositionOnFavoredTerrain(Vector3 positionWS)
	{
		using (TimeWarning.New("IsPositionOnFavoredTerrain"))
		{
			return IsPositionAtTopologyRequirement(positionWS, preferedTopology) && IsPositionABiomeRequirement(positionWS, preferedBiome);
		}
	}

	public bool IsPositionAtTopologyRequirement(NavVector3 positionNS, TerrainTopology.Enum topologyRequirement)
	{
		return IsPositionAtTopologyRequirement(NavToWorldSpace(positionNS), topologyRequirement);
	}

	public bool IsPositionAtTopologyRequirement(Vector3 positionWS, TerrainTopology.Enum topologyRequirement)
	{
		using (TimeWarning.New("IsPositionAtTopologyRequirement"))
		{
			if (TerrainMeta.TopologyMap == null)
			{
				return false;
			}
			TerrainTopology.Enum topology = (TerrainTopology.Enum)TerrainMeta.TopologyMap.GetTopology(positionWS);
			if ((topologyRequirement & topology) == 0)
			{
				return false;
			}
			return true;
		}
	}

	public bool IsPositionABiomeRequirement(NavVector3 positionNS, TerrainBiome.Enum biomeRequirement)
	{
		return IsPositionABiomeRequirement(NavToWorldSpace(positionNS), biomeRequirement);
	}

	public bool IsPositionABiomeRequirement(Vector3 positionWS, TerrainBiome.Enum biomeRequirement)
	{
		using (TimeWarning.New("IsPositionABiomeRequirement"))
		{
			if (biomeRequirement == (TerrainBiome.Enum)0)
			{
				return true;
			}
			if (TerrainMeta.BiomeMap == null)
			{
				return false;
			}
			TerrainBiome.Enum biomeMaxType = (TerrainBiome.Enum)TerrainMeta.BiomeMap.GetBiomeMaxType(positionWS);
			if ((biomeRequirement & biomeMaxType) == 0)
			{
				return false;
			}
			return true;
		}
	}

	public bool IsPositionASplatRequirement(NavVector3 positionNS, TerrainSplat.Enum splatRequirement)
	{
		return IsPositionASplatRequirement(NavToWorldSpace(positionNS), splatRequirement);
	}

	public bool IsPositionASplatRequirement(Vector3 positionWS, TerrainSplat.Enum splatRequirement)
	{
		using (TimeWarning.New("IsPositionASplatRequirement"))
		{
			if (splatRequirement == (TerrainSplat.Enum)0)
			{
				return true;
			}
			if (TerrainMeta.SplatMap == null)
			{
				return false;
			}
			TerrainSplat.Enum splatMaxType = (TerrainSplat.Enum)TerrainMeta.SplatMap.GetSplatMaxType(positionWS);
			if ((splatRequirement & splatMaxType) == 0)
			{
				return false;
			}
			return true;
		}
	}

	public bool IsInWater(NavVector3 positionNS)
	{
		return IsInWater(NavToWorldSpace(positionNS));
	}

	public bool IsInWater(Vector3 positionWS)
	{
		using (TimeWarning.New("IsInWater"))
		{
			if (base.baseEntity.GetParentEntity() != null)
			{
				return false;
			}
			if (WaterLevel.GetWaterDepth(positionWS, waves: false, volumes: false) >= 0.3f)
			{
				return true;
			}
			return false;
		}
	}

	public bool SampleGroundPositionWithPhysics(Vector3 positionWS, out NavGroundHit hitInfoNS, float maxDistance = 2f, float radius = 0f, int layerMask = 1503731969)
	{
		return SampleGroundPositionWithPhysics(WorldToNavSpace(positionWS), out hitInfoNS, maxDistance, radius, layerMask);
	}

	public bool SampleGroundPositionWithPhysics(NavVector3 positionNS, out NavGroundHit hitInfoNS, float maxDistance = 2f, float radius = 0f, int layerMask = 1503731969)
	{
		using (TimeWarning.New("SampleGroundPositionWithPhysics"))
		{
			Vector3 origin = NavToWorldSpace(positionNS) + Vector3.up * radius * 1.5f;
			float maxDistance2 = maxDistance + radius * 1.5f;
			RaycastHit hitInfo;
			bool num = GamePhysics.TraceRealm(GamePhysics.Realm.Server, new Ray(origin, Vector3.down), radius, out hitInfo, maxDistance2, layerMask, QueryTriggerInteraction.Ignore);
			hitInfoNS = new NavGroundHit
			{
				distance = hitInfo.distance,
				collider = hitInfo.collider,
				rawHitWS = hitInfo
			};
			if (!num)
			{
				hitInfoNS.point = positionNS;
				hitInfoNS.normal = NavVector3.up;
				return false;
			}
			hitInfoNS.point = WorldToNavSpace(hitInfo.point);
			hitInfoNS.normal = WorldToNavDirection(hitInfo.normal);
			if (radius > 0f && hitInfoNS.distance <= 0f)
			{
				hitInfoNS.point = positionNS;
			}
			return true;
		}
	}

	private static void BeginTransformBatch(int count)
	{
		transformBatchActive = false;
		if (!AI.batch_navmesh_transforms || AI.useUnityNavmesh || count < 32)
		{
			return;
		}
		int num = 0;
		for (int num2 = count - 1; num2 >= 0; num2--)
		{
			RustNavMeshAgent rustNavMeshAgent = enabledComponents[num2];
			if ((object)rustNavMeshAgent != null && rustNavMeshAgent.followingPath && !rustNavMeshAgent._isStopped && rustNavMeshAgent.pausingSources.Count == 0)
			{
				if (rustNavMeshAgent.canOpenDoors)
				{
					num = 0;
				}
				else if ((rustNavMeshAgent._updatePosition || rustNavMeshAgent._updateRotation) && ++num >= 32)
				{
					transformBatchActive = true;
					break;
				}
			}
		}
	}

	private bool TryQueueTransformUpdate(Quaternion? rotation, bool interpolate)
	{
		if (!transformBatchActive || (object)transformBatchAgent != this)
		{
			return false;
		}
		if (canOpenDoors || hasPendingTransform)
		{
			FlushTransformBatch();
			return false;
		}
		byte b = (byte)(_updatePosition ? 1 : 0);
		if (rotation.HasValue)
		{
			b |= 2;
		}
		if (interpolate)
		{
			b |= 4;
		}
		if (b == 0)
		{
			return true;
		}
		EnsureMovementCapacity();
		movementUpdates[pendingTransformUpdates] = new MovementTransformUpdate
		{
			position = (_updatePosition ? GetMovementWorldPosition() : default(Vector3)),
			rotation = rotation.GetValueOrDefault(),
			maxDegreesDelta = _angularSpeed * UnityEngine.Time.deltaTime,
			flags = b
		};
		pendingTransformAgents[pendingTransformUpdates++] = this;
		hasPendingTransform = true;
		return true;
	}

	private static void EnsureMovementCapacity()
	{
		if (movementUpdates == null || pendingTransformUpdates == movementUpdates.Length)
		{
			int newSize = Mathf.NextPowerOfTwo(pendingTransformUpdates + 32);
			Array.Resize(ref movementUpdates, newSize);
			Array.Resize(ref pendingTransformAgents, newSize);
			Array.Resize(ref registeredTransforms, newSize);
		}
	}

	private static void FlushTransformBatch()
	{
		int num = pendingTransformUpdates;
		if (num == 0)
		{
			return;
		}
		using (TimeWarning.New("RustNavMeshAgent.TransformBatch"))
		{
			try
			{
				if (pendingPathFollowing)
				{
					EnsureMovementQueries();
				}
				if (num < 32)
				{
					for (int i = 0; i < num; i++)
					{
						RustNavMeshAgent rustNavMeshAgent = pendingTransformAgents[i];
						if (rustNavMeshAgent.IsUnityNull())
						{
							continue;
						}
						MovementTransformUpdate update = movementUpdates[i];
						Transform agentTransform = rustNavMeshAgent.AgentTransform;
						if ((update.flags & 8) != 0)
						{
							CalculatePathFollowing(ref update, movementQueries[0], agentTransform.rotation, UnityEngine.Time.deltaTime);
							movementUpdates[i] = update;
						}
						Quaternion quaternion = update.rotation;
						if ((update.flags & 4) != 0)
						{
							quaternion = Quaternion.RotateTowards(agentTransform.rotation, quaternion, update.maxDegreesDelta);
						}
						if ((update.flags & 1) != 0)
						{
							if ((update.flags & 2) != 0)
							{
								agentTransform.SetPositionAndRotation(update.position, quaternion);
							}
							else
							{
								agentTransform.position = update.position;
							}
						}
						else if ((update.flags & 2) != 0)
						{
							agentTransform.rotation = quaternion;
						}
					}
				}
				else
				{
					PrepareMovementTransforms(num);
					RunMovementTransformJob(num);
				}
			}
			finally
			{
				pendingTransformUpdates = 0;
				pendingPathFollowing = false;
				for (int j = 0; j < num; j++)
				{
					RustNavMeshAgent rustNavMeshAgent2 = pendingTransformAgents[j];
					rustNavMeshAgent2.hasPendingTransform = false;
					if ((movementUpdates[j].flags & 8) != 0 && movementUpdates[j].path.moved >= 0 && !rustNavMeshAgent2.IsUnityNull())
					{
						rustNavMeshAgent2.ApplyPathFollowingResult(in movementUpdates[j]);
					}
					pendingTransformAgents[j] = null;
				}
			}
		}
	}

	private static void PrepareMovementTransforms(int count)
	{
		if (!movementTransforms.isCreated)
		{
			movementTransforms = new TransformAccessArray(Mathf.NextPowerOfTwo(count));
		}
		int length = movementTransforms.length;
		for (int num = length - 1; num >= count; num--)
		{
			movementTransforms.RemoveAtSwapBack(num);
			registeredTransforms[num] = null;
		}
		for (int i = 0; i < count; i++)
		{
			RustNavMeshAgent rustNavMeshAgent = pendingTransformAgents[i];
			Transform transform = (rustNavMeshAgent.IsUnityNull() ? null : rustNavMeshAgent.AgentTransform);
			if (i >= length)
			{
				movementTransforms.Add(transform);
			}
			else if ((object)registeredTransforms[i] != transform)
			{
				movementTransforms[i] = transform;
			}
			registeredTransforms[i] = transform;
		}
	}

	private unsafe static void RunMovementTransformJob(int count)
	{
		fixed (MovementTransformUpdate* dataPointer = movementUpdates)
		{
			fixed (IntPtr* dataPointer2 = movementQueries)
			{
				NativeArray<MovementTransformUpdate> updates = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<MovementTransformUpdate>(dataPointer, count, Allocator.None);
				IntPtr[] array = movementQueries;
				NativeArray<IntPtr> queries = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<IntPtr>(dataPointer2, (array != null) ? array.Length : 0, Allocator.None);
				MovementTransformJob jobData = default(MovementTransformJob);
				jobData.updates = updates;
				jobData.queries = queries;
				jobData.deltaTime = UnityEngine.Time.deltaTime;
				jobData.Schedule(movementTransforms).Complete();
			}
		}
	}

	private static void DisposeTransformBatch()
	{
		FlushTransformBatch();
		if (movementTransforms.isCreated)
		{
			movementTransforms.Dispose();
		}
		DisposeMovementQueries();
		movementUpdates = null;
		pendingTransformAgents = null;
		registeredTransforms = null;
	}
}

