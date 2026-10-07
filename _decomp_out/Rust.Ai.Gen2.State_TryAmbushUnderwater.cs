using System;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_TryAmbushUnderwater : FSMStateBase
{
	[SerializeField]
	private Vector2 distanceRange = new Vector2(10f, 20f);

	[SerializeField]
	private float maxDistFromDivingPoint = 50f;

	private const float desiredDepth = 3f;

	private Vector3 divePosition;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		divePosition = Owner.transform.position;
		return FindNewUnderwaterWaitingPosition();
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (!base.Agent.hasPath)
		{
			return FindNewUnderwaterWaitingPosition();
		}
		return base.OnStateUpdate(deltaTime);
	}

	public override void OnStateExit()
	{
		base.Agent.ResetPath();
		base.Agent.desiredSwimDepth.Reset();
		base.OnStateExit();
	}

	private EFSMStateStatus FindNewUnderwaterWaitingPosition()
	{
		NavVector3 nextPosition = base.Agent.nextPosition;
		using PooledList<NavVector3> pooledList = Pool.Get<PooledList<NavVector3>>();
		float num = UnityEngine.Random.Range(distanceRange.x, distanceRange.y);
		bool flag = Eqs.SampleNavigablePositions(base.Agent, nextPosition, pooledList, num, num, 8);
		if (Vector3.Distance(divePosition, Owner.transform.position) > maxDistFromDivingPoint)
		{
			using Eqs.PooledScoreList pooledScoreList = Pool.Get<Eqs.PooledScoreList>();
			NavVector3 normalized = (base.Agent.WorldToNavSpace(divePosition) - nextPosition).normalized;
			foreach (NavVector3 item2 in pooledList)
			{
				float item = NavVector3.Dot(normalized, (item2 - nextPosition).NormalizeXZ());
				pooledScoreList.Add((item2, item));
			}
			pooledScoreList.SortByScoreDesc(Owner);
			pooledScoreList.Reorder(pooledList);
		}
		else
		{
			pooledList.Shuffle((uint)Environment.TickCount);
		}
		foreach (NavVector3 item3 in pooledList)
		{
			NavVector3 navVector = item3;
			if (!flag)
			{
				if (!base.Agent.SamplePosition(item3, out var hitNS, 10f))
				{
					continue;
				}
				navVector = hitNS.position;
			}
			if (base.Agent.IsInWater(navVector))
			{
				RustNavMeshAgent agent = base.Agent;
				NavVector3 targetPositionNS = navVector;
				RustNavMeshAgent.Speeds? gait = ((!base.Agent.IsSwimming) ? RustNavMeshAgent.Speeds.Run : RustNavMeshAgent.Speeds.Sneak);
				float? swimDepth = 3f;
				if (agent.SetDestinationWithParams(targetPositionNS, autoBraking: true, gait, null, null, null, swimDepth, null))
				{
					return EFSMStateStatus.None;
				}
			}
		}
		return EFSMStateStatus.Failure;
	}
}

