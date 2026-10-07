using System;
using ConVar;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_AnimalBite : State_AnimalChase
{
	[SerializeField]
	public AnimationClip Animation;

	[SerializeField]
	public float Damage = 20f;

	[SerializeField]
	public DamageType DamageType = DamageType.Bite;

	[Tooltip("How long to hold the state when there is no clip to play.")]
	[SerializeField]
	public float FallbackDuration = 1f;

	[Tooltip("Rate the clip plays at. Must match the m_Speed on the animator state of the same name, because the client reads that one and the server reads this one.")]
	[SerializeField]
	public float PlaybackSpeed = 1f;

	[Tooltip("Where in the bite the damage lands, as a fraction of the clip. A fraction rather than seconds so it stays on the same frame of the animation whatever length the clip is and whatever rate PlaybackSpeed runs it at.")]
	[SerializeField]
	[Range(0f, 0.9f)]
	public float DamageAtClipFraction = 0.35f;

	[NonSerialized]
	public float Range;

	[NonSerialized]
	public Vector3 AttackOffset;

	private Action _doDamageAction;

	private RootMotionPlayer.PlayServerState animState;

	private float remainingFallback;

	private Action DoDamageAction => DoDamage;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindTarget(out var _))
		{
			return EFSMStateStatus.Failure;
		}
		ScheduleDamage();
		PlayMontage();
		return base.OnStateEnter(payload);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (base.OnStateUpdate(deltaTime) == EFSMStateStatus.Failure)
		{
			return EFSMStateStatus.Failure;
		}
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
		Owner.CancelInvoke(DoDamageAction);
		base.AnimPlayer.StopServerAndReturnToPool(ref animState);
		base.OnStateExit();
	}

	private void PlayMontage()
	{
		if (Animation == null)
		{
			animState = null;
			remainingFallback = FallbackDuration;
		}
		else
		{
			animState = base.AnimPlayer.PlayServerAndTakeFromPool(Animation, PlaybackSpeed, pauseAgent: false);
			remainingFallback = 0f;
		}
	}

	private void ScheduleDamage()
	{
		if (Animation == null || DamageAtClipFraction <= 0f)
		{
			DoDamage();
			return;
		}
		float num = Animation.length / ((PlaybackSpeed > 0f) ? PlaybackSpeed : 1f);
		float a = num * DamageAtClipFraction + AI.defaultInterpolationDelay;
		Owner.Invoke(DoDamageAction, Mathf.Min(a, Mathf.Max(0f, num - 0.25f)));
	}

	private void DoDamage()
	{
		if (base.Senses.FindTarget(out var target) && target is BaseCombatEntity baseCombatEntity)
		{
			Vector3 b = Owner.ServerPosition + Owner.transform.TransformDirection(AttackOffset);
			if (!(Range > 0f) || !(Vector3.Distance(target.transform.position, b) > Range))
			{
				baseCombatEntity.OnAttacked(Damage, DamageType, Owner, ignoreShield: false);
			}
		}
	}
}

