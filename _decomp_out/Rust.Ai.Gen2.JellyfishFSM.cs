using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class JellyfishFSM : SwimmingNPCFSM
{
	[Serializable]
	public class State_Drift : FSMStateBase
	{
		[Tooltip("How long the swarm hangs in the current before setting off on its next leg, in seconds.")]
		[SerializeField]
		private Vector2 driftDurationRange = new Vector2(4f, 12f);

		private TimeUntil driftEndTime;

		public override EFSMStateStatus OnStateEnter(FSMPayload payload)
		{
			if (!Owner.TryGetComponent<SwimmingNPC>(out var component))
			{
				return EFSMStateStatus.Failure;
			}
			component.destination = component.transform.position;
			driftEndTime = UnityEngine.Random.Range(driftDurationRange.x, driftDurationRange.y);
			return base.OnStateEnter(payload);
		}

		public override EFSMStateStatus OnStateUpdate(float deltaTime)
		{
			if ((float)driftEndTime <= 0f)
			{
				return EFSMStateStatus.Success;
			}
			return EFSMStateStatus.None;
		}
	}

	public State_Drift drift = new State_Drift();

	public State_RoamWater roamWater = new State_RoamWater();

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
				Name = "Can drift"
			};
			State_Nothing state_Nothing4 = new State_Nothing
			{
				Name = "Spawning"
			};
			state_Nothing.AddChildren(state_Nothing2.AddTickTransition(dead, DeathTrans), dead);
			state_Nothing2.AddChildren(state_Nothing4.AddTickTransition(drift, new Trans_AlwaysValid()), deactivate.AddTickTransition(drift, ~new Trans_NoPlayerConnectionsInRange()), state_Nothing3.AddTickTransition(deactivate, new Trans_NoPlayerConnectionsInRange()).AddChildren(drift.AddEndTransition(roamWater), roamWater.AddEndTransition(drift)));
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

