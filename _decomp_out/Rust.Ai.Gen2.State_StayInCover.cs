using System;
using System.Collections.Generic;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_StayInCover : FSMStateBase
{
	private NPCHumanoidAnimController _clientAnim;

	private RustNavMeshPath _pathToLkp;

	private NPCHumanoidAnimController ClientAnim => _clientAnim ?? (_clientAnim = Owner.GetComponentInChildren<NPCHumanoidAnimController>());

	private RustNavMeshPath PathToLkp => _pathToLkp ?? (_pathToLkp = new RustNavMeshPath());

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindTarget(out var target))
		{
			return EFSMStateStatus.Failure;
		}
		Vector3 position = Owner.transform.position;
		if (base.Senses.FindLKP(target, out var lkp, applyHeightOffset: true) && (base.Senses.CanBeSeenAtFrom(position + 1.1f * Vector3.up, lkp, "navigation") || base.Senses.CanBeSeenAtFrom(position + 0.1f * Vector3.up, lkp, "navigation")))
		{
			return EFSMStateStatus.Failure;
		}
		ClientAnim.IsCrouching = true;
		if (base.Senses.FindLKP(target, out var lkp2) && Vector3.Distance(lkp2, position) < 40f && Mathf.Abs(lkp2.y - position.y) < 10f)
		{
			NavVector3 positionNS = base.Agent.WorldToNavSpace(lkp2);
			if (base.Agent.SamplePosition(positionNS, out var hitNS, 3.5f) && base.Agent.CalculatePath(hitNS.position, PathToLkp) && PathToLkp.corners.Count >= 2)
			{
				Vector3 vector = base.Agent.NavToWorldSpace(PathToLkp.corners[1]);
				base.Agent.overrideDirectionWS = (vector - position).normalized;
			}
		}
		if (!base.Agent.overrideDirectionWS.HasValue && base.Agent.lastValidPath.Count >= 2)
		{
			List<NavVector3> lastValidPath = base.Agent.lastValidPath;
			NavVector3 navVector = lastValidPath[lastValidPath.Count - 1];
			List<NavVector3> lastValidPath2 = base.Agent.lastValidPath;
			NavVector3 normalized = (navVector - lastValidPath2[lastValidPath2.Count - 2]).normalized;
			Vector3 value = base.Agent.NavToWorldDirection(normalized);
			base.Agent.overrideDirectionWS = value;
			Vector3 normalized2 = (lkp - base.Senses.EyePosition).normalized;
			if (Vector3.Dot(base.Agent.overrideDirectionWS.Value, normalized2) < 0f)
			{
				base.Agent.overrideDirectionWS = normalized2;
			}
		}
		return base.OnStateEnter(payload);
	}

	public override void OnStateExit()
	{
		ClientAnim.IsCrouching = false;
		base.Agent.overrideDirectionWS = null;
		base.OnStateExit();
	}
}

