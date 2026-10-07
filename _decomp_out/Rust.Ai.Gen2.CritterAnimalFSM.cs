using System;
using ConVar;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class CritterAnimalFSM : FSMComponent
{
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
			return base.EvaluateInternal(ref payload);
		}
	}

	[Serializable]
	public class Trans_LoudNoiseNearby : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner is CritterAnimal critterAnimal)
			{
				return critterAnimal.RecentlyHeardLoudNoise;
			}
			return base.EvaluateInternal(ref payload);
		}
	}

	[Serializable]
	public class Trans_NoPlayerConnectionsInRange : FSMTransitionBase
	{
		private bool noPlayersInRange;

		private TimeSince timeSinceLastCheck;

		private float checkTimeout = 5f;

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
			if (Owner is CritterAnimal critterAnimal)
			{
				critterAnimal.SetDeactivatedFlag(toggle);
				if (toggle)
				{
					base.Agent.Pause(this);
				}
				else
				{
					base.Agent.Unpause(this);
				}
			}
		}
	}

	[Serializable]
	public class State_Idle : FSMStateBase
	{
		[Tooltip("How long the critter stays stopped before moving on, in seconds.")]
		[SerializeField]
		private Vector2 idleDurationRange = new Vector2(3f, 8f);

		private TimeUntil idleEndTime;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			base.Agent.ResetPath();
			idleEndTime = UnityEngine.Random.Range(idleDurationRange.x, idleDurationRange.y);
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if ((float)idleEndTime <= 0f)
			{
				return EFSMStateStatus.Success;
			}
			return EFSMStateStatus.None;
		}
	}

	[Serializable]
	public class Trans_IsInterestingLocationInRange : FSMTransitionBase
	{
		[SerializeField]
		protected float searchRadius = 10f;

		[SerializeField]
		protected TerrainTopology.Enum interestTopology = TerrainTopology.Enum.Field | TerrainTopology.Enum.Forest | TerrainTopology.Enum.Forestside;

		[SerializeField]
		protected float timeoutAfterInterest = 60f;

		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (!Owner.TryGetComponent<CritterAnimalFSM>(out var component))
			{
				return false;
			}
			if ((float)component.TimeSinceLastInterest <= timeoutAfterInterest)
			{
				return false;
			}
			NavVector3 nextPosition = base.Agent.nextPosition;
			using PooledList<NavVector3> pooledList = Facepunch.Pool.Get<PooledList<NavVector3>>();
			Eqs.SamplePositionsInDonutShape(nextPosition, pooledList, searchRadius);
			foreach (NavVector3 item in pooledList)
			{
				if (base.Agent.SamplePosition(item, out var hitNS, 10f) && base.Agent.IsPositionAtTopologyRequirement(hitNS.position, interestTopology))
				{
					component.InterestedLocation = hitNS.position.Value;
					return true;
				}
			}
			return false;
		}
	}

	[Serializable]
	public class State_Interested : FSMStateBase
	{
		protected enum Phase
		{
			MovingToInterest,
			InvestigatingInterest
		}

		[SerializeField]
		protected Vector2 distanceRange = new Vector2(10f, 20f);

		[SerializeField]
		protected RustNavMeshAgent.Speeds minSpeed;

		[SerializeField]
		protected RustNavMeshAgent.Speeds maxSpeed = RustNavMeshAgent.Speeds.Sprint;

		[SerializeField]
		protected Vector2 interestedDurationRange = new Vector2(5f, 15f);

		[SerializeField]
		protected float postAnimationDuration = 3f;

		protected Phase phase;

		protected TimeUntil interestEndTime;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			if (!Owner.TryGetComponent<CritterAnimalFSM>(out var component))
			{
				return EFSMStateStatus.Failure;
			}
			if (!component.InterestedLocation.HasValue)
			{
				return base.OnStateEnter(payload);
			}
			Vector3 value = component.InterestedLocation.Value;
			component.InterestedLocation = null;
			if (!base.Agent.SamplePosition(value, out var hitWS, 10f))
			{
				return EFSMStateStatus.Failure;
			}
			float value2 = Vector3.Distance(Owner.transform.position, hitWS.position);
			if (!base.Agent.SetDestinationWithParams(hitWS.position, autoBraking: true, null, null, null, null, null, null))
			{
				return EFSMStateStatus.Failure;
			}
			float ratio = Mathf.InverseLerp(0f, distanceRange.y, value2);
			base.Agent.SetSpeedRatio(ratio, minSpeed, maxSpeed);
			phase = Phase.MovingToInterest;
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			switch (phase)
			{
			case Phase.MovingToInterest:
				if (base.Agent.hasPath)
				{
					return base.OnStateUpdate(deltaTime);
				}
				phase = Phase.InvestigatingInterest;
				interestEndTime = UnityEngine.Random.Range(interestedDurationRange.x, interestedDurationRange.y);
				if (Owner is CritterAnimal critterAnimal2)
				{
					critterAnimal2.SetInterested(interested: true);
				}
				return base.OnStateUpdate(deltaTime);
			case Phase.InvestigatingInterest:
				if ((float)interestEndTime <= postAnimationDuration && Owner is CritterAnimal critterAnimal && critterAnimal.IsInterested())
				{
					critterAnimal.SetInterested(interested: false);
				}
				if ((float)interestEndTime <= 0f)
				{
					if (Owner.TryGetComponent<CritterAnimalFSM>(out var component))
					{
						component.TimeSinceLastInterest = 0f;
					}
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
			if (Owner is CritterAnimal critterAnimal)
			{
				critterAnimal.SetInterested(interested: false);
			}
			base.OnStateExit();
		}
	}

	public State_PlayRandomAnimation randomIdle = new State_PlayRandomAnimation();

	public State_Roam roam = new State_Roam();

	public LivestockAnimalFSM.State_Patrol_Points patrol = new LivestockAnimalFSM.State_Patrol_Points();

	public State_Flee flee = new State_Flee();

	public State_Flee startle = new State_Flee();

	public State_Deactivate deactivate = new State_Deactivate();

	public State_Dead dead = new State_Dead();

	public Trans_IsInterestingLocationInRange _interestedTransition = new Trans_IsInterestingLocationInRange();

	public State_Interested _interested = new State_Interested();

	private Trans_Triggerable_HitInfo DeathTrans;

	[NonSerialized]
	public Vector3? InterestedLocation;

	[NonSerialized]
	public TimeSince TimeSinceLastInterest;

	public static TickFSMWorkQueue critterWorkQueue = new TickFSMWorkQueue();

	public virtual FSMStateBase idle => randomIdle;

	public virtual Trans_IsInterestingLocationInRange interestedTransition => _interestedTransition;

	public virtual State_Interested interested => _interested;

	public static float critterFrameBudgetMs => AI.critters_frametime;

	protected override TickFSMWorkQueue workQueue => critterWorkQueue;

	public override bool OnDied(HitInfo hitInfo)
	{
		DeathTrans.Trigger(hitInfo);
		return false;
	}

	public override void InitShared()
	{
		base.InitShared();
		if (base.baseEntity.isServer)
		{
			TimeSinceLastInterest = 0f;
			DeathTrans = new Trans_Triggerable_HitInfo();
			State_Nothing state_Nothing = new State_Nothing();
			state_Nothing.Name = "Root";
			State_Nothing state_Nothing2 = new State_Nothing
			{
				Name = "Alive"
			};
			State_Nothing state_Nothing3 = new State_Nothing
			{
				Name = "WaitForNavMesh"
			};
			State_Nothing state_Nothing4 = new State_Nothing
			{
				Name = "Can flee"
			};
			State_Nothing state_Nothing5 = new State_Nothing
			{
				Name = "Can follow"
			};
			state_Nothing.AddChildren(state_Nothing2.AddTickTransition(dead, DeathTrans), dead);
			if (patrol.Enabled)
			{
				state_Nothing2.AddChildren(state_Nothing3.AddTickTransition(patrol, new Trans_IsNavmeshReady()), patrol);
			}
			else
			{
				state_Nothing2.AddChildren(state_Nothing3.AddTickTransition(deactivate, new Trans_IsNavmeshReady()), deactivate.AddTickTransition(idle, ~new Trans_NoPlayerConnectionsInRange()), state_Nothing4.AddTickTransition(deactivate, new Trans_NoPlayerConnectionsInRange()).AddTickTransition(flee, new Trans_RecentlyHurt() | new Trans_LoudNoiseNearby()).AddTickTransition(startle, new Trans_HasTarget())
					.AddChildren(state_Nothing5.AddChildren(idle.AddEndTransition(interested, interestedTransition).AddEndTransition(roam), interested.AddEndTransition(idle), roam.AddEndTransition(idle))), flee.AddEndTransition(idle), startle.AddTickTransition(flee, new Trans_RecentlyHurt() | new Trans_LoudNoiseNearby()).AddEndTransition(idle));
			}
			RegisterDebugMoveTo(state_Nothing2);
			SetState(state_Nothing3);
			SetFsmActive(newActive: true);
		}
	}
}

