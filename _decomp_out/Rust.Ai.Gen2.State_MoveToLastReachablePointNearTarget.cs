using System.Collections.Generic;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class State_MoveToLastReachablePointNearTarget : State_MoveToTarget
{
	private const float maxHorizontalDist = 7f;

	private const float projectSampleRadius = 2f;

	private const float maxVerticalDist = 2.7f;

	private const float traceVerticalOffset = 1f;

	private Vector3 reachableDestination;

	private LockState.LockHandle targetLock;

	public static bool CanJumpFromPosToPos(BaseEntity owner, Vector3 ownerLocation, Vector3 targetPos)
	{
		if (Mathf.Abs(targetPos.y - ownerLocation.y) > 2.7f)
		{
			return false;
		}
		if (Vector3.Distance(ownerLocation, targetPos) > 7f)
		{
			return false;
		}
		if (!owner.CanSee(ownerLocation + 1f * Vector3.up, targetPos + 1f * Vector3.up))
		{
			return false;
		}
		return true;
	}

	private bool IsJumpSpot(Vector3 spot, Vector3 targetPos)
	{
		if (CanJumpFromPosToPos(Owner, spot, targetPos))
		{
			return base.Agent.CanReach(spot);
		}
		return false;
	}

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!FindReachableLocation(out reachableDestination))
		{
			return EFSMStateStatus.Failure;
		}
		targetLock = base.Senses.LockCurrentTarget();
		base.Agent.deceleration.Value = 6f;
		return base.OnStateEnter(payload);
	}

	private bool FindReachableLocation(out Vector3 location)
	{
		location = default(Vector3);
		if (!base.Senses.FindTarget(out var target) || !(target is BasePlayer basePlayer))
		{
			return false;
		}
		if (basePlayer.isMounted)
		{
			return false;
		}
		Vector3 position = target.transform.position;
		if (Vector3.Distance(Owner.transform.position, position) > 50f)
		{
			return false;
		}
		Vector3? vector = null;
		if (base.Agent.lastValidPath.Count > 0)
		{
			RustNavMeshAgent agent = base.Agent;
			List<NavVector3> lastValidPath = base.Agent.lastValidPath;
			Vector3 vector2 = agent.NavToWorldSpace(lastValidPath[lastValidPath.Count - 1]);
			if (Vector3.Distance(vector2, position) <= 7f && base.Agent.SamplePosition(vector2, out var hitWS, 2f) && IsJumpSpot(hitWS.position, position))
			{
				vector = hitWS.position;
			}
		}
		if (!vector.HasValue && base.Agent.SamplePosition(position, out var hitWS2, 2f) && IsJumpSpot(hitWS2.position, position))
		{
			vector = hitWS2.position;
		}
		if (!vector.HasValue && base.Agent.lastValidPath.Count > 0)
		{
			List<NavVector3> lastValidPath2 = base.Agent.lastValidPath;
			NavVector3 positionNS = lastValidPath2[lastValidPath2.Count - 1];
			float num = 3f;
			int num2 = base.Agent.lastValidPath.Count - 1;
			while (num2 > 0 && num > 0f)
			{
				float num3 = NavVector3.Distance(base.Agent.lastValidPath[num2], base.Agent.lastValidPath[num2 - 1]);
				if (num3 >= num)
				{
					positionNS = NavVector3.MoveTowards(base.Agent.lastValidPath[num2], base.Agent.lastValidPath[num2 - 1], num);
					num = 0f;
				}
				else
				{
					positionNS = base.Agent.lastValidPath[num2 - 1];
					num -= num3;
				}
				num2--;
			}
			if (base.Agent.SamplePosition(base.Agent.NavToWorldSpace(positionNS), out var hitWS3, 2f) && IsJumpSpot(hitWS3.position, position))
			{
				vector = hitWS3.position;
			}
		}
		if (!vector.HasValue)
		{
			Vector3 vector3 = (Owner.transform.position - position).WithY(0f).normalized;
			if (vector3.sqrMagnitude < 0.01f)
			{
				vector3 = -Owner.transform.forward;
			}
			if (base.Agent.SamplePosition(position + vector3 * 4.5f, out var hitWS4, 2f) && IsJumpSpot(hitWS4.position, position))
			{
				vector = hitWS4.position;
			}
		}
		if (!vector.HasValue)
		{
			return false;
		}
		location = vector.Value;
		return true;
	}

	protected override bool GetMoveDestination(out NavVector3 destination)
	{
		destination = base.Agent.WorldToNavSpace(reachableDestination);
		return true;
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (Trans_TargetIsNearFire.Test(Owner, base.Senses))
		{
			float ratio = Mathx.RemapValClamped(Vector3.Distance(Owner.transform.position, reachableDestination), 4f, 16f, 0f, 1f);
			base.Agent.SetSpeedRatio(ratio, RustNavMeshAgent.Speeds.Sneak, RustNavMeshAgent.Speeds.Jog);
		}
		else
		{
			base.Agent.SetGait(speed);
		}
		return base.OnStateUpdate(deltaTime);
	}

	public override void OnStateExit()
	{
		base.OnStateExit();
		base.Senses.UnlockTarget(ref targetLock);
		base.Agent.deceleration.Reset();
	}
}

