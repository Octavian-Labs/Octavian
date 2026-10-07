using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_IsFlankedByTarget : FSMTransitionBase
{
	private Vector3? previousLkp;

	public override void OnStateEnter()
	{
		base.OnStateEnter();
		previousLkp = null;
		if (base.Senses.FindTargetLKP(out var lkp, applyHeightOffset: true))
		{
			previousLkp = lkp;
		}
	}

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_IsFlankedByTarget"))
		{
			if (!base.Senses.FindTargetLKP(out var lkp, applyHeightOffset: true))
			{
				return false;
			}
			if (previousLkp.HasValue && Vector3.Distance(previousLkp.Value, lkp) > 2f && Vector3.Angle(lkp - Owner.transform.position, previousLkp.Value - Owner.transform.position) > 75f)
			{
				return true;
			}
			previousLkp = lkp;
			return false;
		}
	}

	public override void OnStateExit()
	{
		base.OnStateExit();
		previousLkp = null;
	}
}

