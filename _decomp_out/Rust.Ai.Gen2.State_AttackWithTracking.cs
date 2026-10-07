using System;
using ConVar;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_AttackWithTracking : State_PlayAnimationRM
{
	public DamageType DamageType = DamageType.Bite;

	public float damageDelay = 0.5f;

	public float damage = 30f;

	public float forceScale = 1f;

	public float trackingSpeed = 90f;

	public float trackingDuration = 9999f;

	public float radius = 4f;

	public bool doesStrafeDodge;

	private Action _doDamageAction;

	private static readonly Vector3 force = new Vector3(15f, 3f, 15f);

	private double startTime;

	private Action DoDamageAction => _doDamageAction ?? (_doDamageAction = DoDamage);

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		FaceTarget = false;
		startTime = UnityEngine.Time.timeAsDouble;
		Owner.Invoke(DoDamageAction, damageDelay + AI.defaultInterpolationDelay);
		return base.OnStateEnter(payload);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (trackingDuration > 0f && trackingSpeed > 0f && UnityEngine.Time.timeAsDouble - startTime < (double)(trackingDuration + AI.defaultInterpolationDelay) && base.Senses.FindTarget(out var target))
		{
			base.AnimPlayer.Track(target.transform.position, trackingSpeed, null);
		}
		return base.OnStateUpdate(deltaTime);
	}

	protected virtual void DoDamage()
	{
		if (base.Senses.FindTarget(out var target) && target is BaseCombatEntity baseCombatEntity && (!baseCombatEntity.ToNonNpcPlayer(out var player) || !doesStrafeDodge || !(Mathf.Abs(Vector3.Dot(player.estimatedVelocity.normalized, Owner.transform.right)) > 0.7f)) && base.Senses.GetVisibilityStatus(target, out var status) && status.timeNotVisible < 1f && Vector3.Distance(Owner.transform.position, baseCombatEntity.transform.position) < radius)
		{
			baseCombatEntity.OnAttacked(damage, DamageType, Owner, ignoreShield: false);
			if (forceScale > 0f && player != null)
			{
				Vector3 vector = ((Vector3.Dot(Owner.transform.right, (player.transform.position - Owner.transform.position).NormalizeXZ()) > 0f) ? Owner.transform.right : (-Owner.transform.right));
				player.DoPush((Owner.transform.forward * force.z + vector * force.x + Vector3.up * force.y) * forceScale);
			}
		}
	}

	public override void OnStateExit()
	{
		Owner.CancelInvoke(DoDamageAction);
		base.OnStateExit();
	}
}

