using ConVar;
using UnityEngine;

namespace Rust.Ai.Gen2;

public abstract class State_PlayAnimationBase : FSMStateBase
{
	[SerializeField]
	public bool FaceTarget;

	protected RootMotionPlayer.PlayServerState animState;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (FaceTarget && base.Senses.FindTargetPosition(out var targetPosition))
		{
			Vector3 forward = targetPosition - Owner.transform.position;
			forward.y = 0f;
			if (forward.magnitude > 0.001f)
			{
				Owner.transform.rotation = Quaternion.LookRotation(forward);
			}
		}
		return base.OnStateEnter(payload);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (animState == null)
		{
			if (AI.logIssues)
			{
				Debug.LogError($"[FSM] Animation state is null in state update {base.Name} from {Owner}");
			}
			return EFSMStateStatus.Failure;
		}
		if (!animState.isPlaying)
		{
			return EFSMStateStatus.Success;
		}
		return EFSMStateStatus.None;
	}

	public override void OnStateExit()
	{
		base.AnimPlayer.StopServerAndReturnToPool(ref animState);
	}
}

