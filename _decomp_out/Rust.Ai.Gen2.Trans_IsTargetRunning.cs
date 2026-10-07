namespace Rust.Ai.Gen2;

public class Trans_IsTargetRunning : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_IsTargetRunning"))
		{
			if (!base.Senses.FindTarget(out var target))
			{
				return false;
			}
			if (!target.ToNonNpcPlayer(out var player))
			{
				return false;
			}
			return player.IsRunning();
		}
	}
}

