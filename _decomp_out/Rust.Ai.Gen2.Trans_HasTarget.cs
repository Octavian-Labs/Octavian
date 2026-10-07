using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_HasTarget : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_HasTarget"))
		{
			BaseEntity target;
			return base.Senses.FindTarget(out target);
		}
	}
}

