#define UNITY_ASSERTIONS
using System;
using ConVar;
using UnityEngine;
using UnityEngine.Assertions;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_Attack : State_PlayAnimationRM
{
	[SerializeField]
	public float Damage = 30f;

	[SerializeField]
	public float Delay = 0.5f;

	[SerializeField]
	public DamageType DamageType = DamageType.Bite;

	[Tooltip("Line the attack up on the target as it starts. Turn off for an attack that deliberately does not face what it hits, such as a backwards kick.")]
	[SerializeField]
	public bool AlignToTarget = true;

	private Action _doDamageAction;

	protected Action DoDamageAction => _doDamageAction ?? (_doDamageAction = DoDamage);

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.HasAnimation)
		{
			return EFSMStateStatus.Failure;
		}
		Assert.IsTrue(Delay < Animation.inPlaceAnimation.length);
		if (!base.Senses.FindTargetPosition(out var targetPosition))
		{
			return EFSMStateStatus.Failure;
		}
		if (!FaceTarget && AlignToTarget)
		{
			Vector3 rhs = (Owner.transform.position - targetPosition).NormalizeXZ();
			Vector3 vector = Vector3.Cross(Vector3.up, rhs);
			targetPosition += ((UnityEngine.Random.value > 0.5f) ? 1f : (-1f)) * vector;
			Vector3 forward = (targetPosition - Owner.transform.position).NormalizeXZ();
			Owner.transform.rotation = Quaternion.LookRotation(forward);
		}
		Owner.Invoke(DoDamageAction, Delay + AI.defaultInterpolationDelay);
		return base.OnStateEnter(payload);
	}

	public override void OnStateExit()
	{
		Owner.CancelInvoke(DoDamageAction);
		base.OnStateExit();
	}

	protected virtual void DoDamage()
	{
		if (base.Senses.FindTarget(out var target) && target is BaseCombatEntity baseCombatEntity)
		{
			baseCombatEntity.OnAttacked(Damage, DamageType, Owner, ignoreShield: false);
		}
	}
}

