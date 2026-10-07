using System;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_AttackUnreachableWarped : FSMStateBase
{
	private enum Phase
	{
		PreJump,
		Jump,
		Attack,
		JumpBack,
		PostJumpBack
	}

	public float jumpOnStart = 1.03f;

	public float jumpOnEnd = 1.63f;

	public float jumpOffStart = 2.9f;

	public float jumpOffEnd = 3.47f;

	public float totalDuration = 3.6f;

	private const float groundCheckDistance = 2f;

	private const float damage = 35f;

	private const float meleeAttackRange = 1.7f;

	private const DamageType damageType = DamageType.Bite;

	public RootMotionData animClip;

	private float elapsedTime;

	private LockState.LockHandle targetLock;

	private Phase phase;

	private RootMotionPlayer.Warp[] warps = new RootMotionPlayer.Warp[2];

	private RootMotionPlayer.PlayServerState animState;

	private Vector3 destination;

	private bool didHit;

	public static bool SampleGroundPositionUnderTarget(RustNavMeshAgent agent, BasePlayer targetAsPlayer, out Vector3 projectedLocation)
	{
		if (targetAsPlayer.IsOnGround() && !targetAsPlayer.OnLadder())
		{
			projectedLocation = targetAsPlayer.transform.position;
			return true;
		}
		float radius = BasePlayer.GetRadius();
		NavGroundHit hitInfoNS;
		bool result = agent.SampleGroundPositionWithPhysics(targetAsPlayer.transform.position, out hitInfoNS, 2f, radius * 0.5f);
		projectedLocation = agent.NavToWorldSpace(hitInfoNS.point);
		return result;
	}

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindTarget(out var target) || !(target is BasePlayer targetAsPlayer))
		{
			return EFSMStateStatus.Failure;
		}
		destination = target.transform.position;
		if (!SampleGroundPositionUnderTarget(base.Agent, targetAsPlayer, out destination))
		{
			return EFSMStateStatus.Failure;
		}
		if (!State_MoveToLastReachablePointNearTarget.CanJumpFromPosToPos(Owner, Owner.transform.position, destination))
		{
			return EFSMStateStatus.Failure;
		}
		base.Agent.Pause(this);
		didHit = false;
		elapsedTime = 0f;
		targetLock = base.Senses.LockCurrentTarget();
		animState = RootMotionPlayer.PlayServerState.TakeFromPool(animClip, Owner.transform);
		animState.warps = warps;
		animState.constrainToNavmesh = false;
		base.AnimPlayer.PlayServer(animState);
		SetPhase(Phase.PreJump);
		return base.OnStateEnter(payload);
	}

	private EFSMStateStatus SetPhase(Phase newPhase)
	{
		phase = newPhase;
		if (phase == Phase.Jump)
		{
			if (!base.Senses.FindTarget(out var target) || !(target is BasePlayer targetAsPlayer))
			{
				return EFSMStateStatus.Failure;
			}
			if (SampleGroundPositionUnderTarget(base.Agent, targetAsPlayer, out var projectedLocation))
			{
				destination = projectedLocation;
			}
			Vector3 position = Owner.transform.position;
			animState.initialRotation = Quaternion.LookRotation((destination - Owner.transform.position).WithY(0f));
			Owner.transform.rotation = animState.initialRotation;
			float num = animClip.zMotionCurve.Evaluate(jumpOnEnd) - animClip.zMotionCurve.Evaluate(jumpOnStart);
			float num2 = animClip.yMotionCurve.Evaluate(jumpOnEnd) - animClip.yMotionCurve.Evaluate(jumpOnStart);
			RootMotionPlayer.Warp warp = new RootMotionPlayer.Warp(jumpOnStart, jumpOnEnd, Vector3.one);
			warp.translationScale.z = Vector3.Distance(destination.WithY(0f), position.WithY(0f)) / num;
			warp.translationScale.y = (destination.y - position.y) / num2;
			warps[0] = warp;
			float num3 = animClip.zMotionCurve.Evaluate(jumpOffStart) - animClip.zMotionCurve.Evaluate(jumpOffEnd);
			float num4 = animClip.yMotionCurve.Evaluate(jumpOffStart) - animClip.yMotionCurve.Evaluate(jumpOffEnd);
			RootMotionPlayer.Warp warp2 = new RootMotionPlayer.Warp(jumpOffStart, jumpOffEnd, Vector3.one);
			warp2.translationScale.z = Vector3.Distance(destination.WithY(0f), position.WithY(0f)) / num3;
			warp2.translationScale.y = (destination.y - position.y) / num4;
			warps[1] = warp2;
			base.Agent.IsJumping = true;
		}
		else if (phase == Phase.Attack)
		{
			if (base.Senses.FindTarget(out var target2))
			{
				if (target2 is BaseCombatEntity baseCombatEntity && Vector3.Distance(Owner.transform.position, baseCombatEntity.transform.position) <= 1.7f)
				{
					didHit = true;
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
		return EFSMStateStatus.None;
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		elapsedTime += deltaTime;
		if (!base.Senses.FindTargetPosition(out var targetPosition))
		{
			return EFSMStateStatus.Failure;
		}
		if (phase == Phase.PreJump)
		{
			Quaternion to = Quaternion.LookRotation((targetPosition - Owner.transform.position).WithY(0f));
			animState.initialRotation = Quaternion.RotateTowards(animState.initialRotation, to, Time.deltaTime * 60f);
			Owner.transform.rotation = animState.initialRotation;
			if (elapsedTime >= jumpOnStart && SetPhase(Phase.Jump) == EFSMStateStatus.Failure)
			{
				return EFSMStateStatus.Failure;
			}
		}
		if (phase == Phase.Jump && elapsedTime >= jumpOnEnd)
		{
			SetPhase(Phase.Attack);
		}
		if (phase == Phase.Attack && elapsedTime > jumpOffStart)
		{
			SetPhase(Phase.JumpBack);
		}
		if (phase == Phase.JumpBack && elapsedTime >= jumpOffEnd)
		{
			SetPhase(Phase.PostJumpBack);
		}
		if (elapsedTime >= animClip.inPlaceAnimation.length - 0.25f)
		{
			if (!didHit)
			{
				return EFSMStateStatus.Failure;
			}
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

