using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class SeaTurtleFSM : SwimmingNPCFSM
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
				return Time.time - baseCombatEntity.lastAttackedTime < thresholdSeconds;
			}
			return base.EvaluateInternal(ref payload);
		}
	}

	[Serializable]
	public class Trans_LoudNoiseNearby : FSMTransitionBase
	{
		protected override bool EvaluateInternal(ref FSMPayload payload)
		{
			if (Owner is SeaTurtle seaTurtle)
			{
				return seaTurtle.RecentlyHeardLoudNoise;
			}
			return base.EvaluateInternal(ref payload);
		}
	}

	[Serializable]
	public class State_Flee : State_SwimToPoint
	{
		[SerializeField]
		public float desiredDistance = 50f;

		[SerializeField]
		public float distance = 20f;

		[SerializeField]
		protected RustNavMeshAgent.Speeds speed = RustNavMeshAgent.Speeds.Sprint;

		[SerializeField]
		private int maxAttempts = 3;

		[SerializeField]
		protected float fleeDuration = 5f;

		[Tooltip("How much of the escape direction is straight down. 0 swims away on the flat, 1 dives as hard as it swims outwards.")]
		[SerializeField]
		protected float diveBias = 0.5f;

		[Tooltip("How many shorter escape points to try when fleeing would take the turtle out of the water. Higher finds a usable point nearer the shoreline at more cost.")]
		[SerializeField]
		private int fleeRetreatSteps = 4;

		private int attempts;

		protected float startDistance;

		private TimeSince timeSinceFleeStarted;

		protected override bool IsUrgent => true;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			base.Blackboard.Remove("HitByFire");
			if (!base.Senses.FindTargetPosition(out var targetPosition))
			{
				return EFSMStateStatus.Success;
			}
			attempts = 0;
			timeSinceFleeStarted = 0f;
			startDistance = Vector3.Distance(Owner.transform.position, targetPosition);
			return SetDestination();
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			Vector3 targetPosition;
			bool flag = base.Senses.FindTargetPosition(out targetPosition);
			if (flag && Vector3.Distance(targetPosition, Owner.transform.position) > desiredDistance + startDistance)
			{
				return EFSMStateStatus.Success;
			}
			if ((float)timeSinceFleeStarted <= fleeDuration)
			{
				return base.OnStateUpdate(deltaTime);
			}
			if (!flag)
			{
				return EFSMStateStatus.Success;
			}
			attempts++;
			timeSinceFleeStarted = 0f;
			if (attempts >= maxAttempts)
			{
				return EFSMStateStatus.Success;
			}
			return SetDestination();
		}

		protected override void SetUrgent(SwimmingNPC swimmer, bool urgent)
		{
			if (swimmer is SeaTurtle seaTurtle && seaTurtle.IsFleeing() != urgent)
			{
				seaTurtle.SetFleeing(urgent);
			}
		}

		protected virtual Vector3 GetFleeDirection(Vector3 pos, Vector3 targetPosition)
		{
			Vector3 vector = (pos - targetPosition).NormalizeXZ();
			if (vector.sqrMagnitude < 0.001f)
			{
				vector = Owner.transform.forward.NormalizeXZ();
			}
			if (vector.sqrMagnitude < 0.001f)
			{
				vector = Owner.transform.right.NormalizeXZ();
			}
			return (vector + Vector3.down * diveBias).normalized;
		}

		protected override bool TryPickDestination(SwimmingNPC swimmer, out Vector3 destination)
		{
			if (!base.Senses.FindTargetPosition(out var targetPosition))
			{
				destination = default(Vector3);
				return false;
			}
			Vector3 position = swimmer.transform.position;
			Vector3 fleeDirection = GetFleeDirection(position, targetPosition);
			destination = position + fleeDirection * distance;
			if (!SwimmingNPC.CanSwimAt(destination))
			{
				for (int num = fleeRetreatSteps - 1; num >= 1; num--)
				{
					Vector3 vector = position + fleeDirection * (distance * (float)num / (float)fleeRetreatSteps);
					if (SwimmingNPC.CanSwimAt(vector))
					{
						destination = vector;
						return true;
					}
				}
				destination = position;
			}
			return true;
		}
	}

	public State_PlayRandomAnimation randomIdle = new State_PlayRandomAnimation();

	public State_RoamWater roamWater = new State_RoamWater();

	public State_Flee flee = new State_Flee();

	public State_Deactivate deactivate = new State_Deactivate();

	public State_Dead dead = new State_Dead();

	private Trans_Triggerable_HitInfo DeathTrans;

	public override void InitShared()
	{
		base.InitShared();
		if (base.baseEntity.isServer)
		{
			DeathTrans = new Trans_Triggerable_HitInfo();
			State_Nothing state_Nothing = new State_Nothing();
			state_Nothing.Name = "Root";
			State_Nothing state_Nothing2 = new State_Nothing
			{
				Name = "Alive"
			};
			State_Nothing state_Nothing3 = new State_Nothing
			{
				Name = "Can flee"
			};
			State_Nothing state_Nothing4 = new State_Nothing
			{
				Name = "Spawning"
			};
			state_Nothing.AddChildren(state_Nothing2.AddTickTransition(dead, DeathTrans), dead);
			state_Nothing2.AddChildren(state_Nothing4.AddTickTransition(randomIdle, new Trans_AlwaysValid()), deactivate.AddTickTransition(randomIdle, ~new Trans_NoPlayerConnectionsInRange()), state_Nothing3.AddTickTransition(deactivate, new Trans_NoPlayerConnectionsInRange()).AddTickTransition(flee, new Trans_RecentlyHurt() | new Trans_LoudNoiseNearby()).AddChildren(randomIdle.AddEndTransition(roamWater), roamWater.AddEndTransition(randomIdle)), flee.AddEndTransition(randomIdle));
			RegisterDebugMoveTo(state_Nothing2);
			SetState(state_Nothing4);
			SetFsmActive(newActive: true);
		}
	}

	public override bool OnDied(HitInfo hitInfo)
	{
		DeathTrans.Trigger(hitInfo);
		return false;
	}
}

