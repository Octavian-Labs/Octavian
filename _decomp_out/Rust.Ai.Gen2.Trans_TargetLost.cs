namespace Rust.Ai.Gen2;

public class Trans_TargetLost : FSMTransitionBase
{
	public float minLostDuration = 10f;

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_TargetLost"))
		{
			if (!base.Senses.FindTarget(out var target))
			{
				return false;
			}
			if (!base.Senses.GetVisibilityStatus(target, out var status) || status.IsVisible)
			{
				return false;
			}
			return status.timeNotVisible >= minLostDuration;
		}
	}
}

