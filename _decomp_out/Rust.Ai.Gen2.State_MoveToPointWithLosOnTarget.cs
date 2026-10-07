using System;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using UnityEngine;
using UnityEngine.AI;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_MoveToPointWithLosOnTarget : FSMStateBase
{
	public float searchRadius = 8f;

	private static RustNavMeshPath _path;

	private NpcZoneComponent _npcZoneComponent;

	private NpcShootingComponent _shooting;

	private NavVector3? lastChosenPeekNS;

	private static RustNavMeshPath Path => _path ?? (_path = new RustNavMeshPath());

	private NpcZoneComponent NpcZoneComponent => _npcZoneComponent ?? (_npcZoneComponent = Owner.GetComponent<NpcZoneComponent>());

	private NpcShootingComponent Shooting => _shooting ?? (_shooting = Owner.GetComponent<NpcShootingComponent>());

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindTarget(out var target))
		{
			return EFSMStateStatus.Failure;
		}
		if (!base.Senses.FindLKP(target, out var lkp, applyHeightOffset: true))
		{
			return EFSMStateStatus.Failure;
		}
		SenseComponent.VisibilityStatus status;
		bool flag = NpcZoneComponent.IsPointInsideZone(lkp) || (base.Senses.GetVisibilityStatus(target, out status) && status.timeNotVisible < 30f);
		using PooledList<NavVector3> pooledList = Pool.Get<PooledList<NavVector3>>();
		NavVector3 nextPosition = base.Agent.nextPosition;
		bool flag2 = ((!flag) ? Eqs.SampleNavigablePositions(base.Agent, nextPosition, pooledList, searchRadius, searchRadius, 4) : Eqs.SampleNavigablePositions(base.Agent, nextPosition, pooledList, searchRadius, searchRadius * 0.5f, 16));
		using Eqs.PooledScoreList pooledScoreList = Pool.Get<Eqs.PooledScoreList>();
		foreach (NavVector3 item2 in pooledList)
		{
			float num = 0f;
			if (lastChosenPeekNS.HasValue)
			{
				num += Mathx.RemapValClamped(NavVector3.Distance(item2, lastChosenPeekNS.Value), 0f, searchRadius, 0f, 1f);
			}
			pooledScoreList.Add((item2, num));
		}
		pooledScoreList.SortByScoreDesc(Owner);
		foreach (var item3 in pooledScoreList)
		{
			NavVector3 item = item3.pos;
			NavVector3 navVector = item;
			if (!flag2)
			{
				if (!base.Agent.SamplePosition(item, out var hitNS, 3.5f))
				{
					continue;
				}
				navVector = hitNS.position;
			}
			Vector3 vector = base.Agent.NavToWorldSpace(navVector);
			if (NpcZoneComponent.IsPointInsideZone(vector) && (!lastChosenPeekNS.HasValue || !(NavVector3.Distance(navVector, lastChosenPeekNS.Value) < 2f)) && !base.Agent.IsInWater(vector) && Shooting.CanShootFromAt(vector + base.Senses.EyeOffset, lkp, "navigation") && base.Agent.CalculatePath(navVector, Path) && Path.status == NavMeshPathStatus.PathComplete && !(Path.GetPathLength() > searchRadius * 3f) && base.Agent.SetDestinationWithParams(navVector, autoBraking: true, RustNavMeshAgent.Speeds.Walk, null, null, null, null, null))
			{
				lastChosenPeekNS = navVector;
				return base.OnStateEnter(payload);
			}
		}
		return EFSMStateStatus.Failure;
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (!base.Agent.hasPath)
		{
			return EFSMStateStatus.Success;
		}
		return base.OnStateUpdate(deltaTime);
	}

	public override void OnStateExit()
	{
		base.Agent.ResetPath();
		base.OnStateExit();
	}
}

