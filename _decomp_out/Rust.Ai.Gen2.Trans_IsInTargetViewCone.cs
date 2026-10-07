using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_IsInTargetViewCone : FSMTransitionBase
{
	public float angle = 90f;

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		return IsInTargetViewCone(base.Senses, angle);
	}

	public static bool IsInTargetViewCone(SenseComponent senses, float testAngle)
	{
		if (!senses.FindTarget(out var target))
		{
			return false;
		}
		Vector3 normalized = (senses.GetBaseEntity().transform.position - target.transform.position).normalized;
		BasePlayer player;
		return Vector3.Dot((target.ToNonNpcPlayer(out player) ? player.eyes.BodyForward() : target.transform.forward).normalized, normalized) > Mathf.Cos(testAngle * (MathF.PI / 180f));
	}

	public override string ToString()
	{
		if (!Inverted)
		{
			return $"We are in target view cone of {angle} degrees";
		}
		return $"We are not in target view cone of {angle} degrees";
	}
}

