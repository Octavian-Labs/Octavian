namespace Rust.Ai.Gen2;

public class Trans_TargetCamping : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_TargetCamping"))
		{
			if (!base.Senses.FindTarget(out var target))
			{
				return true;
			}
			if (!base.Senses.GetVisibilityStatus(target, out var status))
			{
				return true;
			}
			return status.IsCamping;
		}
	}
}

