using UnityEngine;

namespace Rust.Ai.Gen2;

public class Trans_TargetLkpInRange : FSMTransitionBase
{
	public float Range = 10f;

	public bool Predict;

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_TargetLkpInRange"))
		{
			if (!base.Senses.FindTargetLKP(out var lkp, applyHeightOffset: false, Predict))
			{
				return false;
			}
			return Vector3.Distance(Owner.transform.position, lkp) <= Range;
		}
	}
}

