using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_IsInSafeZone : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_IsInSafeZone"))
		{
			return Owner.InSafeZone();
		}
	}
}

