using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_Observe : State_MoveToTarget
{
	private double startTime;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		EFSMStateStatus eFSMStateStatus = base.OnStateEnter(payload);
		if (eFSMStateStatus == EFSMStateStatus.Failure)
		{
			return eFSMStateStatus;
		}
		succeedWhenDestinationIsReached = false;
		stopAtDestination = false;
		base.Agent.deceleration.Value = 0.1f;
		base.Agent.SetGait(RustNavMeshAgent.Speeds.Sneak);
		startTime = Time.timeAsDouble;
		base.Agent.currentDeviation = 2f;
		return eFSMStateStatus;
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (Time.timeAsDouble - startTime > 6.0)
		{
			base.Agent.speed = 0.2f;
		}
		return base.OnStateUpdate(deltaTime);
	}
}

