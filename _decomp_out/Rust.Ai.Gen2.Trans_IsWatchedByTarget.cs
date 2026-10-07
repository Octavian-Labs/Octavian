using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_IsWatchedByTarget : FSMTransitionBase
{
	public float minTime = 1.5f;

	public bool requireAiming = true;

	public bool wantsWatched = true;

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		if (!base.Senses.FindTarget(out var target))
		{
			return false;
		}
		if (!base.Senses.GetVisibilityStatus(target, out var status))
		{
			return false;
		}
		return ((!wantsWatched) ? (requireAiming ? status.timeNotAimedAt : status.timeNotWatched) : (requireAiming ? status.timeAimedAt : status.timeWatched)) > minTime;
	}
}

