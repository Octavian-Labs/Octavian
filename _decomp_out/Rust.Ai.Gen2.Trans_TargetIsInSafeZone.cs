using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_TargetIsInSafeZone : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_TargetIsInSafeZone"))
		{
			BaseEntity target;
			return base.Senses.FindTarget(out target) && target.InSafeZone();
		}
	}
}

