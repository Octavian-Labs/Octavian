using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_TurnToTarget : State_PlayAnimationRM
{
	[SerializeField]
	public RootMotionData TurnLeft;

	[SerializeField]
	public RootMotionData TurnRight;

	[SerializeField]
	public RootMotionData TurnAbout;

	[Tooltip("Past this many degrees off our facing, use the about turn rather than a side turn.")]
	[SerializeField]
	private float aboutFaceAngle = 130f;

	public bool HasTurnAnimation
	{
		get
		{
			if (!(TurnLeft != null) && !(TurnRight != null))
			{
				return TurnAbout != null;
			}
			return true;
		}
	}

	protected virtual bool FindTurnTarget(out Vector3 position)
	{
		return base.Senses.FindTargetPosition(out position);
	}

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!HasTurnAnimation || !FindTurnTarget(out var position))
		{
			return EFSMStateStatus.Failure;
		}
		Vector3 to = (position - Owner.transform.position).NormalizeXZ();
		float num = Vector3.SignedAngle(Owner.transform.forward, to, Vector3.up);
		if (Mathf.Abs(num) > aboutFaceAngle)
		{
			Animation = TurnAbout ?? ((num > 0f) ? TurnRight : TurnLeft);
		}
		else if (num > 0f)
		{
			Animation = TurnRight ?? TurnAbout ?? TurnLeft;
		}
		else
		{
			Animation = TurnLeft ?? TurnAbout ?? TurnRight;
		}
		return base.OnStateEnter(payload);
	}
}

