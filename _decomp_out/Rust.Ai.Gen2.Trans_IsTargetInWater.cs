using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_IsTargetInWater : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_IsTargetInWater"))
		{
			if (!base.Senses.FindTarget(out var target))
			{
				return false;
			}
			if (!target.ToNonNpcPlayer(out var _))
			{
				return false;
			}
			if (!base.Senses.GetVisibilityStatus(target, out var status))
			{
				return false;
			}
			return status.isInWaterCached;
		}
	}
}

