using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_SeesFood : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_SeesFood"))
		{
			BaseEntity food;
			return base.Senses.FindFood(out food);
		}
	}
}

