using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_CrocTurn : State_PlayAnimationRM
{
	[SerializeField]
	public RootMotionData turn90L;

	[SerializeField]
	public RootMotionData turn90R;

	[SerializeField]
	public RootMotionData turn180;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindTargetPosition(out var targetPosition))
		{
			return EFSMStateStatus.Failure;
		}
		Vector3 normalized = (targetPosition - Owner.transform.position).normalized;
		float num = Vector3.SignedAngle(Owner.transform.forward, normalized, Vector3.up);
		if (Mathf.Abs(num) > 130f)
		{
			Animation = turn180;
		}
		else if (num > 0f)
		{
			Animation = turn90R;
		}
		else
		{
			Animation = turn90L;
		}
		return base.OnStateEnter(payload);
	}
}

