using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
[SoftRequireComponent(typeof(RootMotionPlayer))]
public abstract class State_AnimalMontage : FSMStateBase
{
	[Tooltip("How long to hold the state when there is no clip to play.")]
	[SerializeField]
	public float FallbackDuration = 1f;

	[Tooltip("Rate the clip plays at. Must match the m_Speed on the animator state of the same name, because the client reads that one and the server reads this one.")]
	[SerializeField]
	public float PlaybackSpeed = 1f;

	protected RootMotionPlayer.PlayServerState animState;

	private float remainingFallback;

	protected abstract AnimationClip GetAnimation();

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		PlayMontage(GetAnimation());
		return EFSMStateStatus.None;
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (animState != null)
		{
			if (!animState.isPlaying)
			{
				return EFSMStateStatus.Success;
			}
			return EFSMStateStatus.None;
		}
		remainingFallback -= deltaTime;
		if (!(remainingFallback > 0f))
		{
			return EFSMStateStatus.Success;
		}
		return EFSMStateStatus.None;
	}

	public override void OnStateExit()
	{
		base.AnimPlayer.StopServerAndReturnToPool(ref animState);
		base.OnStateExit();
	}

	protected void PlayMontage(AnimationClip clip)
	{
		base.AnimPlayer.StopServerAndReturnToPool(ref animState, interrupt: false);
		if (clip != null)
		{
			animState = base.AnimPlayer.PlayServerAndTakeFromPool(clip, PlaybackSpeed);
			remainingFallback = 0f;
		}
		else
		{
			animState = null;
			remainingFallback = FallbackDuration;
		}
	}
}

