using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_IsAfraidOfTarget : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		if (!base.Senses.FindTarget(out var target))
		{
			return false;
		}
		return base.Senses.StanceToward(target) == NPCStance.Fear;
	}
}

