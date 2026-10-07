using System;

namespace Rust.Ai.Gen2;

[Serializable]
internal class Trans_IsTargetTooFarFromWater : FSMTransitionBase
{
	public float maxDistance = 30f;

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_IsTargetTooFarFromWater"))
		{
			if (!base.Senses.FindTargetPosition(out var targetPosition))
			{
				return false;
			}
			return TerrainTexturing.Instance.GetCoarseDistanceToShore(targetPosition) < 0f - maxDistance;
		}
	}
}

