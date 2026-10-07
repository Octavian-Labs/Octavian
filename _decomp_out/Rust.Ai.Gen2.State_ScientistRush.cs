using System;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_ScientistRush : State_MoveToTarget
{
	public bool useSuppressiveFire;

	private NpcZoneComponent _npcZoneComponent;

	private NpcShootingComponent _shooting;

	private NpcZoneComponent NpcZoneComponent => _npcZoneComponent ?? (_npcZoneComponent = Owner.GetComponent<NpcZoneComponent>());

	private NpcShootingComponent Shooting => _shooting ?? (_shooting = Owner.GetComponent<NpcShootingComponent>());

	protected override bool GetMoveDestination(out NavVector3 destination)
	{
		destination = default(NavVector3);
		if (!base.Senses.FindTargetLKP(out var lkp, applyHeightOffset: false, predict: true))
		{
			return false;
		}
		NavVector3 positionNS = base.Agent.WorldToNavSpace(lkp);
		if (!base.Agent.SamplePosition(positionNS, out var hitNS, 3.5f) && !base.Agent.SamplePosition(positionNS, out hitNS, 20f))
		{
			return false;
		}
		Vector3 positionWS = base.Agent.NavToWorldSpace(hitNS.position);
		if (base.Agent.IsInWater(positionWS))
		{
			return false;
		}
		destination = hitNS.position;
		return true;
	}

	public override EFSMStateStatus OnStateEnter(FSMPayload assistRequest)
	{
		Shooting.OnlyShootIfTargetIsVisible = !useSuppressiveFire;
		if (assistRequest.entity != null && assistRequest.position.HasValue)
		{
			base.Senses.SimulateSighting(assistRequest.entity, assistRequest.position.Value);
			base.Senses.TrySetTarget(assistRequest.entity);
		}
		else
		{
			NpcPushHelper.CoordinatePush(Owner);
		}
		return base.OnStateEnter(assistRequest);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (useSuppressiveFire && base.Senses.FindTargetLKP(out var lkp, applyHeightOffset: true, predict: true))
		{
			if (!base.Senses.IsLineOccluded(base.Senses.EyePosition, lkp, 1218519041))
			{
				base.Agent.overrideDirectionWS = (lkp - base.Senses.EyePosition).normalized;
				Shooting.OnlyShootIfTargetIsVisible = true;
			}
			else
			{
				base.Agent.overrideDirectionWS = null;
				Shooting.OnlyShootIfTargetIsVisible = false;
			}
		}
		return base.OnStateUpdate(deltaTime);
	}

	public override void OnStateExit()
	{
		if (!NpcZoneComponent.IsPointInsideZone(Owner.transform.position))
		{
			NpcZoneComponent.AbandonZone();
		}
		Shooting.OnlyShootIfTargetIsVisible = true;
		base.Agent.overrideDirectionWS = null;
		base.OnStateExit();
	}
}

