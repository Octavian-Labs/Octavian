namespace Rust.Ai.Gen2;

public class Trans_IsReloading : FSMTransitionBase
{
	private NpcShootingComponent _shooting;

	private NpcShootingComponent Shooting => _shooting ?? (_shooting = Owner.GetComponent<NpcShootingComponent>());

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_IsReloading"))
		{
			return Shooting.IsReloading();
		}
	}
}

