using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_CanThrowGrenade : FSMSlowTransitionBase
{
	protected override bool EvaluateAtInterval(ref FSMPayload payload)
	{
		if (!State_ThrowGrenade.FindPotentialLandingPoint(base.Senses, out var landingPoint, out var throwVelocity))
		{
			return false;
		}
		if (!State_ThrowGrenade.ValidateLandingPoint(Owner, base.Senses.EyePosition, landingPoint, throwVelocity, out var _))
		{
			return false;
		}
		payload.velocity = throwVelocity;
		return true;
	}
}

