using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_PlayAnimLoop : State_PlayAnimationBase
{
	[SerializeField]
	public AnimationClip Start;

	[SerializeField]
	public AnimationClip Loop;

	[SerializeField]
	public AnimationClip Stop;

	[SerializeField]
	public float MinDuration = 7f;

	[SerializeField]
	public float MaxDuration = 14f;

	[Tooltip("How far through a clip to hand over to the next one, as a fraction of its length. Has to beat the animator's own exit time back to the default state, or the pose visibly blends to standing between loops.")]
	[Range(0.5f, 1f)]
	[SerializeField]
	private float handoverFraction = 0.8f;

	private float duration;

	public bool HasAnimation
	{
		get
		{
			if (Start != null && Loop != null)
			{
				return Stop != null;
			}
			return false;
		}
	}

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		EFSMStateStatus result = base.OnStateEnter(payload);
		duration = UnityEngine.Random.Range(MinDuration, MaxDuration);
		animState = base.AnimPlayer.PlayServerAndTakeFromPool(Start);
		return result;
	}

	private bool ReadyToHandOver()
	{
		if (animState == null || !animState.isPlaying)
		{
			return true;
		}
		float animLength = animState.GetAnimLength();
		if (!(animLength <= 0f))
		{
			return animState.elapsedTime >= animLength * handoverFraction;
		}
		return true;
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (duration > 0f)
		{
			duration -= deltaTime;
			if (duration <= 0f)
			{
				base.AnimPlayer.StopServerAndReturnToPool(ref animState, interrupt: false);
				animState = base.AnimPlayer.PlayServerAndTakeFromPool(Stop);
			}
			else if (ReadyToHandOver())
			{
				base.AnimPlayer.StopServerAndReturnToPool(ref animState, interrupt: false);
				animState = base.AnimPlayer.PlayServerAndTakeFromPool(Loop);
			}
		}
		return base.OnStateUpdate(deltaTime);
	}
}

