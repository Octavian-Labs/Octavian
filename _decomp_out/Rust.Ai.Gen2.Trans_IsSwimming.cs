using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_IsSwimming : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_IsSwimming"))
		{
			return base.Agent.canSwim && base.Agent.IsSwimming;
		}
	}
}

