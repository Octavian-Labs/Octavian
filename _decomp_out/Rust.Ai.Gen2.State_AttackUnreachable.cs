using System;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_AttackUnreachable : FSMStateBase
{
	private enum Phase
	{
		PreJump,
		Jump,
		Attack,
		JumpBack,
		PostJumpBack
	}

	public float preJumpEnd = 0.29f;

	public float jumpEnd = 0.395f;

	public float attackEnd = 0.67f;

	public float jumpBackEnd = 0.765f;

	public float postJumpBackEnd = 0.95f;

	private const float groundCheckDistance = 2f;

	private const float damage = 35f;

	private const float meleeAttackRange = 1.7f;

	private const DamageType damageType = DamageType.Bite;

	public RootMotionData animClip;

	private Vector3 startLocation;

	private Quaternion startRotation;

	private Vector3 destination;

	private float elapsedTime;

	private LockState.LockHandle targetLock;

	private Phase phase;

	private float previousOffsetZ;

	private RootMotionPlayer.PlayServerState animState;

	public static bool SampleGroundPositionUnderTarget(RustNavMeshAgent agent, BasePlayer targetAsPlayer, out Vector3 projectedLocation)
	{
		float radius = BasePlayer.GetRadius();
		NavGroundHit hitInfoNS;
		bool result = agent.SampleGroundPositionWithPhysics(targetAsPlayer.transform.position, out hitInfoNS, 2f, radius);
		projectedLocation = agent.NavToWorldSpace(hitInfoNS.point);
		return result;
	}

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindTarget(out var target) || !(target is BasePlayer basePlayer))
		{
			return EFSMStateStatus.Failure;
		}
		destination = target.transform.position;
		if (!basePlayer.IsOnGround() && !SampleGroundPositionUnderTarget(base.Agent, basePlayer, out destination))
		{
			return EFSMStateStatus.Failure;
		}
		if (!State_MoveToLastReachablePointNearTarget.CanJumpFromPosToPos(Owner, Owner.transform.position, destination))
		{
			return EFSMStateStatus.Failure;
		}
		base.Agent.Pause(this);
		elapsedTime = 0f;
		targetLock = base.Senses.LockCurrentTarget();
		animState = base.AnimPlayer.PlayServerAndTakeFromPool(animClip.inPlaceAnimation);
		SetPhase(Phase.PreJump);
		return base.OnStateEnter(payload);
	}

	private void SetPhase(Phase newPhase)
	{
		phase = newPhase;
		previousOffsetZ = animClip.zMotionCurve.Evaluate(elapsedTime);
		if (phase == Phase.Jump)
		{
			if (base.Senses.FindTarget(out var target) && target is BasePlayer targetAsPlayer)
			{
				SampleGroundPositionUnderTarget(base.Agent, targetAsPlayer, out destination);
			}
			startLocation = Owner.transform.position;
			Owner.transform.rotation = Quaternion.LookRotation((destination - Owner.transform.position).WithY(0f));
			base.Agent.IsJumping = true;
		}
		else if (phase == Phase.Attack)
		{
			startRotation = Owner.transform.rotation;
			if (base.Senses.FindTarget(out var target2))
			{
				if (target2 is BaseCombatEntity baseCombatEntity && Vector3.Distance(Owner.transform.position, baseCombatEntity.transform.position) <= 1.7f)
				{
					baseCombatEntity.OnAttacked(35f, DamageType.Bite, Owner, ignoreShield: false);
				}
				if (target2 is BasePlayer basePlayer && Vector3.Distance(Owner.transform.position, basePlayer.transform.position) <= 1f)
				{
					basePlayer.DoPush(Owner.transform.forward * 10f + Vector3.up * 3f);
				}
			}
		}
		else if (phase == Phase.PostJumpBack)
		{
			base.Agent.IsJumping = false;
		}
	}

	private Vector3 ThreePointLerp(Vector3 a, Vector3 b, Vector3 c, float t)
	{
		return Vector3.Lerp(Vector3.Lerp(a, b, t), Vector3.Lerp(b, c, t), t);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		elapsedTime += deltaTime;
		float num = elapsedTime / Mathf.Max(animClip.inPlaceAnimation.length, 0.001f);
		if (phase == Phase.PreJump)
		{
			Quaternion b = Quaternion.LookRotation((destination - Owner.transform.position).WithY(0f));
			Owner.transform.rotation = Quaternion.Slerp(Owner.transform.rotation, b, 2f * deltaTime);
			float num2 = animClip.zMotionCurve.Evaluate(elapsedTime);
			Vector3 vector = Owner.transform.forward * (num2 - previousOffsetZ);
			previousOffsetZ = num2;
			Owner.transform.position += vector;
			if (num >= preJumpEnd)
			{
				SetPhase(Phase.Jump);
			}
		}
		if (phase == Phase.Jump)
		{
			Vector3 b2 = (startLocation + destination) * 0.5f;
			b2.y = Mathf.Max(startLocation.y, destination.y);
			float t = Mathx.RemapValClamped(num, preJumpEnd, jumpEnd, 0f, 1f);
			Vector3 position = ThreePointLerp(startLocation, b2, destination, t);
			Owner.transform.position = position;
			if (num >= jumpEnd)
			{
				SetPhase(Phase.Attack);
			}
		}
		if (phase == Phase.Attack)
		{
			Owner.transform.rotation = startRotation * Quaternion.AngleAxis(animClip.yRotationCurve.Evaluate(elapsedTime), Vector3.up);
			if (num > attackEnd)
			{
				SetPhase(Phase.JumpBack);
			}
		}
		if (phase == Phase.JumpBack)
		{
			Vector3 b3 = (startLocation + destination) * 0.5f;
			b3.y = Mathf.Max(startLocation.y, destination.y);
			float t2 = Mathx.RemapValClamped(num, attackEnd, jumpBackEnd, 0f, 1f);
			Vector3 position2 = ThreePointLerp(destination, b3, startLocation, t2);
			Owner.transform.position = position2;
			Owner.transform.rotation = Quaternion.LookRotation((startLocation - destination).WithY(0f));
			if (num >= jumpBackEnd)
			{
				SetPhase(Phase.PostJumpBack);
			}
		}
		if (phase == Phase.PostJumpBack)
		{
			float num3 = animClip.zMotionCurve.Evaluate(elapsedTime);
			Vector3 vector2 = Owner.transform.forward * (num3 - previousOffsetZ);
			previousOffsetZ = num3;
			Owner.transform.position -= vector2;
		}
		if (num >= postJumpBackEnd)
		{
			return EFSMStateStatus.Success;
		}
		return base.OnStateUpdate(deltaTime);
	}

	public override void OnStateExit()
	{
		base.AnimPlayer.StopServerAndReturnToPool(ref animState);
		base.Senses.UnlockTarget(ref targetLock);
		base.Agent.Unpause(this);
		if (phase != Phase.PostJumpBack)
		{
			base.Agent.IsJumping = false;
		}
		base.OnStateExit();
	}
}

