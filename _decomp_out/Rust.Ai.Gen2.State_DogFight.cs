using System;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_DogFight : FSMStateBase
{
	private bool shouldGoRightNext;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (CalculatePathDestination() == EFSMStateStatus.Failure)
		{
			return EFSMStateStatus.Failure;
		}
		shouldGoRightNext = UnityEngine.Random.value > 0.5f;
		return base.OnStateEnter(payload);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (base.Agent.hasPath)
		{
			return base.OnStateUpdate(deltaTime);
		}
		if (CalculatePathDestination() == EFSMStateStatus.Failure)
		{
			return EFSMStateStatus.Failure;
		}
		return EFSMStateStatus.None;
	}

	public override void OnStateExit()
	{
		base.Agent.ResetPath();
		base.OnStateExit();
	}

	private EFSMStateStatus CalculatePathDestination()
	{
		if (!base.Senses.FindTargetPosition(out var targetPosition))
		{
			return EFSMStateStatus.Failure;
		}
		Vector3 position = Owner.transform.position;
		Vector3 normalized = (targetPosition - position).normalized;
		Vector3 vector = (shouldGoRightNext ? Vector3.Cross(normalized, Vector3.up).normalized : Vector3.Cross(Vector3.up, normalized).normalized);
		shouldGoRightNext = !shouldGoRightNext;
		Vector3 directionWS = Quaternion.AngleAxis(UnityEngine.Random.Range(-50f, 50f), Vector3.up) * vector;
		float num;
		RustNavMeshAgent.Speeds value;
		if (UnityEngine.Random.value > 0.95f && Vector3.Distance(targetPosition, position) > 8f)
		{
			num = UnityEngine.Random.Range(3f, 4f);
			value = RustNavMeshAgent.Speeds.Sprint;
		}
		else
		{
			num = UnityEngine.Random.Range(1f, 2f);
			value = RustNavMeshAgent.Speeds.Walk;
		}
		NavVector3 nextPosition = base.Agent.nextPosition;
		NavVector3 aNS = base.Agent.WorldToNavDirection(directionWS);
		using PooledList<NavVector3> pooledList = Pool.Get<PooledList<NavVector3>>();
		bool flag = Eqs.SampleNavigablePositions(base.Agent, nextPosition, pooledList, num, num, 8);
		using Eqs.PooledScoreList pooledScoreList = Pool.Get<Eqs.PooledScoreList>();
		foreach (NavVector3 item3 in pooledList)
		{
			float item = Mathx.RemapValClamped(NavVector3.Dot(aNS, (item3 - nextPosition).NormalizeXZ()), -1f, 1f, 0f, 1f);
			pooledScoreList.Add((item3, item));
		}
		pooledScoreList.SortByScoreDesc(Owner);
		foreach (var item4 in pooledScoreList)
		{
			NavVector3 item2 = item4.pos;
			NavVector3 navVector = item2;
			if (!flag)
			{
				if (!base.Agent.SamplePosition(item2, out var hitNS, 3.5f))
				{
					continue;
				}
				navVector = hitNS.position;
			}
			Vector3 positionWS = base.Agent.NavToWorldSpace(navVector);
			if (!base.Agent.IsInWater(positionWS) && base.Agent.SetDestinationWithParams(navVector, autoBraking: false, value, null, null, null, null, null))
			{
				return EFSMStateStatus.None;
			}
		}
		return EFSMStateStatus.Failure;
	}
}

