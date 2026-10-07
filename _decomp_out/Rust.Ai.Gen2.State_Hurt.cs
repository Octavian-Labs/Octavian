using System;
using ConVar;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_Hurt : State_PlayAnimationRM
{
	[SerializeField]
	private RootMotionData StrongHitL;

	[SerializeField]
	private RootMotionData StrongHitR;

	[SerializeField]
	private RootMotionData WeakHit;

	[SerializeField]
	private float StaggerRatio = 0.5f;

	public virtual bool HasStaggerAnimation
	{
		get
		{
			if (StrongHitL != null)
			{
				return StrongHitR != null;
			}
			return false;
		}
	}

	protected virtual RootMotionData PickStrongHit(HitInfo hitInfo)
	{
		if (!(Vector3.Dot(hitInfo.attackNormal, Owner.transform.right) > 0f))
		{
			return StrongHitR;
		}
		return StrongHitL;
	}

	public bool ShouldStagger(BaseEntity owner, HitInfo hitInfo)
	{
		float num = owner.Health() - hitInfo.damageTypes.Total();
		float num2 = owner.MaxHealth() * StaggerRatio;
		if (owner.Health() > num2)
		{
			return num < num2;
		}
		return false;
	}

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (payload.hitInfo == null && AI.logIssues && AI.logIssues)
		{
			Debug.LogError($"Entering {base.Name} without HitInfo payload, this should not happen and may cause issues with stats tracking. Owner: {Owner}", Owner);
		}
		if (payload.hitInfo.damageTypes.Has(DamageType.Heat))
		{
			base.Blackboard.Add("HitByFire");
		}
		if (WeakHit == null || ShouldStagger(Owner, payload.hitInfo))
		{
			Animation = PickStrongHit(payload.hitInfo);
		}
		else
		{
			Animation = WeakHit;
		}
		if (payload.hitInfo.Initiator is BaseCombatEntity baseCombatEntity)
		{
			bool flag = true;
			if (base.Senses.FindTarget(out var target))
			{
				bool num = Owner.Distance(baseCombatEntity) < 16f;
				bool flag2 = !target.IsNonNpcPlayer() && baseCombatEntity.IsNonNpcPlayer();
				flag = num || flag2;
			}
			if (flag)
			{
				base.Senses.TrySetTarget(baseCombatEntity);
			}
		}
		return base.OnStateEnter(payload);
	}
}

