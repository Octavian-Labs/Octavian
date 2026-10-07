using System;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using UnityEngine;
using UnityEngine.AI;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_MoveToCoverHiddenFromTarget : FSMStateBase
{
	public float searchRadius = 8f;

	public float walkDurationBeforeSprint = 1f;

	public RustNavMeshAgent.Speeds speed = RustNavMeshAgent.Speeds.Walk;

	private static RustNavMeshPath _path;

	private NpcShootingComponent _shooting;

	private NpcZoneComponent _npcZoneComponent;

	private double? remainingWalkBeforeSprintTime;

	private NavVector3? lastChosenHidingSpotNS;

	private AIInformationZone _infoZone;

	private AICoverPoint heldCover;

	private static RustNavMeshPath Path => _path ?? (_path = new RustNavMeshPath());

	private NpcShootingComponent Shooting => _shooting ?? (_shooting = Owner.GetComponent<NpcShootingComponent>());

	private NpcZoneComponent NpcZoneComponent => _npcZoneComponent ?? (_npcZoneComponent = Owner.GetComponent<NpcZoneComponent>());

	private AIInformationZone InfoZone => _infoZone ?? (_infoZone = AIInformationZone.GetForPoint(Owner.transform.position));

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (payload.entity != null)
		{
			base.Senses.TrySetTarget(payload.entity);
			base.Senses.ForgetAllNoises();
		}
		if (!base.Senses.FindTargetLKP(out var lkp, applyHeightOffset: true))
		{
			return EFSMStateStatus.Failure;
		}
		NavVector3 navVector = base.Agent.WorldToNavSpace(lkp);
		NavVector3 nextPosition = base.Agent.nextPosition;
		using PooledList<NavVector3> pooledList = Pool.Get<PooledList<NavVector3>>();
		bool flag = Eqs.SampleNavigablePositions(base.Agent, nextPosition, pooledList, searchRadius, searchRadius * 0.5f, 16);
		float num = NavVector3.Distance(navVector, nextPosition);
		using Eqs.PooledScoreList pooledScoreList = Pool.Get<Eqs.PooledScoreList>();
		foreach (NavVector3 item2 in pooledList)
		{
			float num2 = 0f;
			if (num < 20f)
			{
				num2 += Mathx.RemapValClamped(NavVector3.Distance(item2, navVector), 0f, searchRadius, 0f, 1f);
			}
			else if (num > 50f)
			{
				num2 += Mathx.RemapValClamped(NavVector3.Distance(item2, navVector), 0f, searchRadius, 0f, 1f);
			}
			else if (lastChosenHidingSpotNS.HasValue)
			{
				num2 += Mathx.RemapValClamped(NavVector3.Distance(item2, lastChosenHidingSpotNS.Value), 0f, searchRadius, 0f, 1f);
			}
			if (Owner.TryGetComponent<RustNavMeshAgent>(out var component) && component.FindClosestEdge(item2, out var hitNS) && NavVector3.Distance(hitNS.position, item2) < 1.5f)
			{
				num2 += 2f;
			}
			pooledScoreList.Add((item2, num2));
		}
		pooledScoreList.SortByScoreDesc(Owner);
		foreach (var item3 in pooledScoreList)
		{
			NavVector3 item = item3.pos;
			NavVector3 navVector2 = item;
			if (!flag)
			{
				if (!base.Agent.SamplePosition(item, out var hitNS2, 3.5f))
				{
					continue;
				}
				navVector2 = hitNS2.position;
			}
			Vector3 vector = base.Agent.NavToWorldSpace(navVector2);
			if (NpcZoneComponent.IsPointInsideZone(vector) && !base.Agent.IsInWater(vector) && !base.Senses.CanBeSeenAtFrom(vector + 1.1f * Vector3.up, lkp, "navigation") && base.Agent.CalculatePath(navVector2, Path) && Path.status == NavMeshPathStatus.PathComplete)
			{
				float pathLength = Path.GetPathLength();
				if (!(pathLength < 0.5f) && !(pathLength > searchRadius * 3f) && base.Agent.SetDestinationWithParams(navVector2, autoBraking: true, speed, null, null, null, null, null))
				{
					remainingWalkBeforeSprintTime = walkDurationBeforeSprint;
					lastChosenHidingSpotNS = navVector2;
					return base.OnStateEnter(payload);
				}
			}
		}
		if (InfoZone == null)
		{
			return EFSMStateStatus.Failure;
		}
		AICoverPoint bestCoverPoint = InfoZone.GetBestCoverPoint(Owner.transform.position, lkp, 0f, searchRadius, Owner);
		if (bestCoverPoint != null && NpcZoneComponent.IsPointInsideZone(bestCoverPoint.transform.position))
		{
			NavVector3 targetPositionNS = base.Agent.WorldToNavSpace(bestCoverPoint.transform.position);
			if (base.Agent.SetDestinationWithParams(targetPositionNS, autoBraking: true, speed, null, null, null, null, null))
			{
				heldCover = bestCoverPoint;
				heldCover.SetUsedBy(Owner);
				remainingWalkBeforeSprintTime = walkDurationBeforeSprint;
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
		if (remainingWalkBeforeSprintTime.HasValue)
		{
			remainingWalkBeforeSprintTime -= deltaTime;
			if (remainingWalkBeforeSprintTime <= 0.0)
			{
				base.Agent.SetGait(RustNavMeshAgent.Speeds.Sprint);
				remainingWalkBeforeSprintTime = null;
			}
		}
		return base.OnStateUpdate(deltaTime);
	}

	public override void OnStateExit()
	{
		if (heldCover != null)
		{
			heldCover.ClearIfUsedBy(Owner);
			heldCover = null;
		}
		remainingWalkBeforeSprintTime = null;
		base.Agent.ResetPath();
		base.OnStateExit();
	}
}

