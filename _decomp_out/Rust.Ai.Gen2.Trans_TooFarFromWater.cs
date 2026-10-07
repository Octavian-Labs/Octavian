using System;

namespace Rust.Ai.Gen2;

[Serializable]
internal class Trans_TooFarFromWater : FSMTransitionBase
{
	public float maxDistance = 20f;

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_TooFarFromWater"))
		{
			return TerrainTexturing.Instance.GetCoarseDistanceToShore(Owner.transform.position) < 0f - maxDistance;
		}
	}
}

