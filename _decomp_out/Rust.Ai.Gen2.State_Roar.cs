using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_Roar : State_PlayAnimation
{
	public const string AlreadyRoared = "AlreadyRoared";

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindTarget(out var _))
		{
			return EFSMStateStatus.Failure;
		}
		base.Blackboard.Add("AlreadyRoared");
		return base.OnStateEnter(payload);
	}
}

