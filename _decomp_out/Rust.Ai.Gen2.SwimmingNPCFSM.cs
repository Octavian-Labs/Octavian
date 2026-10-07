using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

public abstract class SwimmingNPCFSM : FSMComponent
{
	[Serializable]
	public class Trans_NoPlayerConnectionsInRange : FSMTransitionBase
	{
		private bool noPlayersInRange;

		private TimeSince timeSinceLastCheck;

		private float checkTimeout = 10f;

		public override void Init(BaseEntity owner)
		{
			timeSinceLastCheck = 999f;
			base.Init(owner);
		}

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if ((float)timeSinceLastCheck >= checkTimeout)
			{
				noPlayersInRange = !BaseNetworkable.HasCloseConnections(Owner.transform.position, 128f);
				timeSinceLastCheck = 0f;
			}
			return noPlayersInRange;
		}
	}

	[Serializable]
	public class State_Deactivate : FSMStateBase
	{
		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			SetDeactivated(toggle: true);
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			return EFSMStateStatus.None;
		}

		public override void OnStateExit()
		{
			SetDeactivated(toggle: false);
			base.OnStateExit();
		}

		private void SetDeactivated(bool toggle)
		{
			if (!(Owner is SwimmingNPC swimmingNPC))
			{
				Debug.LogWarning("[SwimmingNPC] Deactivate state owner is not a SwimmingNPC, flag not set", Owner);
			}
			else
			{
				swimmingNPC.SetDeactivatedFlag(toggle);
			}
		}
	}

	[Serializable]
	public abstract class State_SwimToPoint : FSMStateBase
	{
		protected virtual bool IsUrgent => false;

		protected abstract bool TryPickDestination(SwimmingNPC swimmer, out Vector3 destination);

		protected virtual void SetUrgent(SwimmingNPC swimmer, bool urgent)
		{
		}

		protected EFSMStateStatus SetDestination()
		{
			if (!Owner.TryGetComponent<SwimmingNPC>(out var component))
			{
				return EFSMStateStatus.Failure;
			}
			if (!TryPickDestination(component, out var destination))
			{
				return EFSMStateStatus.Success;
			}
			component.destination = component.WaterClamp(destination);
			if (IsUrgent)
			{
				SetUrgent(component, urgent: true);
			}
			return EFSMStateStatus.None;
		}

		public override void OnStateExit()
		{
			if (Owner.TryGetComponent<SwimmingNPC>(out var component))
			{
				SetUrgent(component, urgent: false);
				component.destination = component.transform.position;
			}
			base.OnStateExit();
		}
	}

	[Serializable]
	public class State_RoamWater : State_SwimToPoint
	{
		[SerializeField]
		private Vector2 distanceRange = new Vector2(10f, 25f);

		[Tooltip("How far the swimmer will drift from home before its next leg is aimed back towards it.")]
		[SerializeField]
		private float homeRadius = 50f;

		[Tooltip("How close counts as arrived. The swimmer steers rather than paths, so it never lands exactly on the point.")]
		[SerializeField]
		private float arriveDistance = 3f;

		[Tooltip("Water this shallow is not worth swimming to. What passes for navigable without a navmesh.")]
		[SerializeField]
		private float minWaterDepth = 3f;

		[Tooltip("How far down the swimmer aims to swim, as a fraction of the water depth at its destination. 0 hugs the surface and 1 hugs the floor. Without this the destination inherits whatever height the swimmer already had, so it never recovers depth once it sinks.")]
		[Range(0f, 1f)]
		[SerializeField]
		private float targetDepthFraction = 0.3f;

		[Tooltip("How much each leg's depth varies around the target, as a fraction of local depth, so the swimmer does not swim at one fixed height. WaterClamp keeps it in bounds.")]
		[Range(0f, 0.5f)]
		[SerializeField]
		private float depthJitterFraction = 0.15f;

		[SerializeField]
		private int maxAttempts = 4;

		[Tooltip("How long a single swim \"path\" can take before the swimmer gives up and picks another.")]
		[SerializeField]
		private float legTimeout = 30f;

		private Vector3? spawnPosition;

		private TimeSince timeSinceLegStarted;

		protected virtual Vector3 HomePosition => spawnPosition ?? Owner.transform.position;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			Vector3 valueOrDefault = spawnPosition.GetValueOrDefault();
			if (!spawnPosition.HasValue)
			{
				valueOrDefault = Owner.transform.position;
				spawnPosition = valueOrDefault;
			}
			timeSinceLegStarted = 0f;
			EFSMStateStatus eFSMStateStatus = SetDestination();
			if (eFSMStateStatus != 0)
			{
				return eFSMStateStatus;
			}
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if (!Owner.TryGetComponent<SwimmingNPC>(out var component))
			{
				return EFSMStateStatus.Failure;
			}
			if (Vector3.Distance(component.transform.position, component.destination) <= arriveDistance)
			{
				return EFSMStateStatus.Success;
			}
			if ((float)timeSinceLegStarted > legTimeout)
			{
				return EFSMStateStatus.Success;
			}
			return base.OnStateUpdate(deltaTime);
		}

		protected override bool TryPickDestination(SwimmingNPC swimmer, out Vector3 destination)
		{
			Vector3 position = swimmer.transform.position;
			Vector3 homePosition = HomePosition;
			bool flag = Vector3.Distance(homePosition, position) > homeRadius;
			Vector3? vector = null;
			for (int i = 0; i < maxAttempts; i++)
			{
				float num = Mathf.Lerp(distanceRange.x, distanceRange.y, (float)i / (float)maxAttempts);
				Vector3 vector2 = (vector.HasValue ? (Quaternion.Euler(0f, UnityEngine.Random.Range(-45f, 45f), 0f) * vector.Value) : (flag ? (Quaternion.Euler(0f, UnityEngine.Random.Range(-60f, 60f), 0f) * (homePosition - position).NormalizeXZ()) : UnityEngine.Random.insideUnitCircle.normalized.XZ3D()));
				Vector3 vector3 = position + vector2 * num;
				float overallWaterDepth = WaterLevel.GetOverallWaterDepth(vector3, waves: false, volumes: false);
				if (overallWaterDepth < minWaterDepth)
				{
					Vector3? vector4 = vector;
					if (!vector4.HasValue)
					{
						vector = GetAwayFromShore(position);
					}
					continue;
				}
				float num2 = overallWaterDepth * depthJitterFraction;
				vector3.y = GetTargetAltitude(vector3) + UnityEngine.Random.Range(0f - num2, num2);
				destination = vector3;
				return true;
			}
			destination = homePosition;
			return false;
		}

		private float GetTargetAltitude(Vector3 point)
		{
			var (a, b) = WaterLevel.GetWaterAndTerrainSurface(point, waves: false, volumes: false);
			return Mathf.Lerp(a, b, targetDepthFraction);
		}

		private static Vector3? GetAwayFromShore(Vector3 pos)
		{
			TerrainTexturing instance = TerrainTexturing.Instance;
			if (instance == null)
			{
				return null;
			}
			(Vector3 shoreDir, float shoreDist) coarseVectorToShore = instance.GetCoarseVectorToShore(pos);
			Vector3 item = coarseVectorToShore.shoreDir;
			Vector3 v = ((coarseVectorToShore.shoreDist >= 0f) ? (-item) : item);
			if (v.sqrMagnitude < 0.001f)
			{
				return null;
			}
			return v.NormalizeXZ();
		}
	}

	protected override TickFSMWorkQueue workQueue => CritterAnimalFSM.critterWorkQueue;
}

