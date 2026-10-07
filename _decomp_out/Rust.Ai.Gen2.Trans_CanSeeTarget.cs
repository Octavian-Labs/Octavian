namespace Rust.Ai.Gen2;

public class Trans_CanSeeTarget : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_CanSeeTarget"))
		{
			if (!base.Senses.FindTarget(out var target))
			{
				return false;
			}
			if (!base.Senses.GetVisibilityStatus(target, out var status))
			{
				return false;
			}
			return status.IsVisible && status.IsAware;
		}
	}
}

