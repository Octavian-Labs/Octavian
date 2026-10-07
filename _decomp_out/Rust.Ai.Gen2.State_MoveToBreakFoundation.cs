using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_MoveToBreakFoundation : FSMStateBase
{
	private const float maxHorizontalDist = 10f;

	private bool FindReachableLocation(out Vector3 location)
	{
		location = default(Vector3);
		if (!base.Senses.FindTarget(out var target) || !target.ToNonNpcPlayer(out var player))
		{
			return false;
		}
		Vector3 position = target.transform.position;
		if (Vector3.Distance(Owner.transform.position, position) > 50f)
		{
			return false;
		}
		if (State_CrocBreakFoundation.FindNearestTwigFoundationOnTargetBuilding(base.Agent, player, null).Is<BuildingBlock>(out var castedUnityObject) && base.Agent.SamplePosition(castedUnityObject.ClosestPoint(Owner.transform.position), out var hitWS, 10f))
		{
			location = hitWS.position;
			return true;
		}
		if (base.Agent.SamplePosition(position, out var hitWS2, 3f))
		{
			location = hitWS2.position;
			return true;
		}
		return false;
	}

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindTarget(out var target) || !target.ToNonNpcPlayer(out var _))
		{
			return EFSMStateStatus.Failure;
		}
		if (!FindReachableLocation(out var location))
		{
			return EFSMStateStatus.Failure;
		}
		Vector3 vector = location + Owner.bounds.extents.y * Vector3.up;
		Vector3 direction = target.CenterPoint() - vector;
		if (GamePhysics.Trace(new Ray(vector, direction), 0f, out var _, direction.magnitude, 1503731969))
		{
			return EFSMStateStatus.Failure;
		}
		if (!base.Agent.SetDestinationWithParams(location, autoBraking: true, RustNavMeshAgent.Speeds.Run, null, null, null, null, null))
		{
			return EFSMStateStatus.Failure;
		}
		return base.OnStateEnter(payload);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (!base.Agent.hasPath)
		{
			return EFSMStateStatus.Success;
		}
		return base.OnStateUpdate(deltaTime);
	}
}

