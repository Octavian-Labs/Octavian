using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_IsInWater_Slow : FSMSlowTransitionBase
{
	[Tooltip("Depth in metres past which this counts as being in water, or zero for ai.minDepthToBeConsideredInWater. That default is 0.3m, which is ankle deep on a cow and shallower than the half metre drinking already stands her in, so anything that should let an animal wade has to give its own depth here.")]
	[SerializeField]
	public float minDepth;

	protected override bool EvaluateAtInterval(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_IsInWater_Slow"))
		{
			if (base.Agent.canSwim && minDepth <= 0f)
			{
				return base.Agent.IsSwimming;
			}
			return WaterLevel.GetWaterDepth(Owner.transform.position, waves: false, volumes: false) >= ((minDepth > 0f) ? minDepth : 0.3f);
		}
	}

	public override string GetName()
	{
		string text = ((minDepth > 0f) ? $" >{minDepth}m" : "");
		return base.GetName() + text;
	}
}

