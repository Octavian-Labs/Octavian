using System;
using UnityEngine;
using UnityEngine.AI;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_HasStraightPathToTarget : FSMTransitionBase
{
	[Tooltip("Aim at the spot CanReach would path to rather than at the target itself, so a target standing against an obstacle does not read as behind it.")]
	[SerializeField]
	public bool ProjectTarget;

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_HasStraightPathToTarget"))
		{
			if (!base.Senses.FindTargetPosition(out var targetPosition))
			{
				return false;
			}
			if (ProjectTarget)
			{
				return base.Agent.HasStraightPathTo(targetPosition);
			}
			NavMeshHit hitWS;
			return !base.Agent.Raycast(targetPosition, out hitWS);
		}
	}
}

