using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_CrocIntimidate : FSMStateBase
{
	private float remainingDuration;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		EFSMStateStatus result = base.OnStateEnter(payload);
		if (Owner is BaseCombatEntity baseCombatEntity)
		{
			remainingDuration = Mathx.RemapValClamped(baseCombatEntity.healthFraction, 1f, 0.3f, 5f, 0f);
			if (remainingDuration == 0f)
			{
				return EFSMStateStatus.Success;
			}
			return result;
		}
		return EFSMStateStatus.Failure;
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (remainingDuration > 0f)
		{
			remainingDuration -= deltaTime;
			if (remainingDuration <= 0f)
			{
				return EFSMStateStatus.Success;
			}
		}
		return base.OnStateUpdate(deltaTime);
	}
}

