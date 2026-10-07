using System;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_AnimalChase : State_MoveToTarget
{
	[Tooltip("How far from an off-navmesh target to look for somewhere the animal can actually stand.")]
	[SerializeField]
	public float offNavmeshSampleRadius = 6f;

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (!base.Agent.hasPath && base.Senses.FindTargetPosition(out var targetPosition))
		{
			Vector3 forward = (targetPosition - Owner.transform.position).NormalizeXZ();
			if (forward.sqrMagnitude > 0.001f)
			{
				Owner.transform.rotation = Quaternion.RotateTowards(Owner.transform.rotation, Quaternion.LookRotation(forward), base.Agent.angularSpeed * deltaTime);
			}
		}
		return base.OnStateUpdate(deltaTime);
	}

	protected override bool GetMoveDestination(out NavVector3 destination)
	{
		if (!base.Senses.FindTargetPosition(out var targetPosition))
		{
			destination = NavVector3.zero;
			return false;
		}
		NavVector3 navVector = base.Agent.WorldToNavSpace(targetPosition);
		if (base.Agent.SamplePosition(navVector, out var hitNS, 0.5f))
		{
			destination = hitNS.position;
			return true;
		}
		if (base.Agent.SamplePosition(navVector, out var hitNS2, offNavmeshSampleRadius))
		{
			destination = hitNS2.position;
			return true;
		}
		destination = navVector;
		return true;
	}
}

