using System;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_DebugMoveToPosition : State_MoveToTarget
{
	public const float navmeshSampleRadius = 5f;

	[NonSerialized]
	public Vector3 destinationWS;

	private NpcShootingComponent _shooting;

	private NpcShootingComponent Shooting => _shooting ?? (_shooting = Owner.GetComponent<NpcShootingComponent>());

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (Shooting != null)
		{
			Shooting.AllowShooting = false;
		}
		return base.OnStateEnter(payload);
	}

	public override void OnStateExit()
	{
		if (Shooting != null)
		{
			Shooting.AllowShooting = true;
		}
		base.OnStateExit();
	}

	protected override bool GetMoveDestination(out NavVector3 destination)
	{
		destination = default(NavVector3);
		Vector3 positionWS = Owner.WorldToNavMeshSpace.MultiplyPoint(destinationWS);
		if (!base.Agent.SamplePosition(positionWS, out var hitWS, 5f))
		{
			return false;
		}
		destination = base.Agent.WorldToNavSpace(hitWS.position);
		return true;
	}
}

