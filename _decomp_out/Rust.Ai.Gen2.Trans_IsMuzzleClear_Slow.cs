using UnityEngine;

namespace Rust.Ai.Gen2;

public class Trans_IsMuzzleClear_Slow : FSMSlowTransitionBase
{
	private NpcShootingComponent _shootingComponent;

	private NpcShootingComponent ShootingComponent => _shootingComponent ?? (_shootingComponent = Owner.GetComponent<NpcShootingComponent>());

	protected override bool EvaluateAtInterval(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_IsMuzzleClear_Slow"))
		{
			if (!base.Senses.FindTarget(out var target))
			{
				return false;
			}
			if (!base.Senses.FindLKP(target, out var lkp))
			{
				return false;
			}
			Vector3 muzzleEstimatedPositionOnServer = ShootingComponent.GetMuzzleEstimatedPositionOnServer(lkp);
			Vector3 entityPointToShootAt = NpcShootingComponent.GetEntityPointToShootAt(target, lkp);
			return ShootingComponent.CanShootFromAt(muzzleEstimatedPositionOnServer, entityPointToShootAt, "muzzle clear");
		}
	}
}

