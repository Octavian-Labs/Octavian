using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_FastSneak : State_CircleDynamic
{
	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		base.Agent.deceleration.Value = 10f;
		return base.OnStateEnter(payload);
	}

	protected override void SetSpeed(BaseEntity target, float distToTarget, float normalizedDist)
	{
		if (!target.ToNonNpcPlayer(out var player))
		{
			base.SetSpeed(target, distToTarget, normalizedDist);
		}
		else if (distToTarget > 50f)
		{
			base.Agent.speed = 8.25f;
		}
		else if (player.modelState.sprinting && distToTarget < 20f)
		{
			base.Agent.speed = 6.875f;
		}
		else if (player.modelState.sprinting)
		{
			base.Agent.speed = 8.25f;
		}
		else if (player.modelState.ducked || player.estimatedSpeed < 0.1f)
		{
			base.Agent.speed = 1.7f;
		}
		else
		{
			base.Agent.speed = 3.5f;
		}
	}
}

