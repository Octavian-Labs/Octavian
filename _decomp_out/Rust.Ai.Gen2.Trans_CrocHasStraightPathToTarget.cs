using System;
using UnityEngine;
using UnityEngine.AI;

namespace Rust.Ai.Gen2;

[Serializable]
internal class Trans_CrocHasStraightPathToTarget : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_CrocHasStraightPathToTarget"))
		{
			if (!base.Senses.FindTarget(out var target))
			{
				return false;
			}
			Vector3 targetPositionWS = target.transform.position;
			if (target.IsNonNpcPlayer() && base.Agent.canSwim && base.Senses.GetVisibilityStatus(target, out var status) && status.isInWaterCached)
			{
				targetPositionWS = target.transform.position.WithY(status.lastWaterInfo.Value.terrainHeight);
			}
			NavMeshHit hitWS;
			return !base.Agent.Raycast(targetPositionWS, out hitWS);
		}
	}
}

