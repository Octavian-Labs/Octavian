using System;
using Rust.Ai.Gen2.Nav;

namespace Rust.Ai.Gen2;

[Serializable]
[SoftRequireComponent(typeof(RustNavMeshAgent), typeof(SenseComponent), typeof(BlackboardComponent))]
public class State_ApproachFood : State_MoveToTarget
{
	public const string TriedToApproachUnreachableFood = "TriedToApproachUnreachableFood";

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindFood(out var food))
		{
			return EFSMStateStatus.Failure;
		}
		if (food.WaterFactor() > 0f || !base.Agent.CanReach(food.transform.position))
		{
			base.Blackboard.Add("TriedToApproachUnreachableFood");
			base.Senses.IgnoreFood(food);
			return EFSMStateStatus.Failure;
		}
		return base.OnStateEnter(payload);
	}

	protected override bool GetMoveDestination(out NavVector3 destination)
	{
		if (!base.Senses.FindFood(out var food))
		{
			destination = NavVector3.zero;
			return false;
		}
		destination = base.Agent.WorldToNavSpace(food.transform.position);
		return true;
	}
}

