using System;
using Rust.Ai.Gen2.Nav;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_CanReachTarget_Slow : FSMSlowTransitionBase
{
	protected override bool EvaluateAtInterval(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_CanReachTarget_Slow"))
		{
			if (!base.Senses.FindTargetPosition(out var targetPosition))
			{
				return false;
			}
			NavVector3 locationNS = base.Agent.WorldToNavSpace(targetPosition);
			return base.Agent.CanReach(locationNS);
		}
	}
}

