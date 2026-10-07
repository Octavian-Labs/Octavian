using System;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_Flee : FSMStateBase
{
	[SerializeField]
	public float desiredDistance = 50f;

	[SerializeField]
	public float distance = 20f;

	[SerializeField]
	protected RustNavMeshAgent.Speeds speed = RustNavMeshAgent.Speeds.Sprint;

	[SerializeField]
	private int maxAttempts = 3;

	[SerializeField]
	public WaterAvoidance waterAvoidance;

	[SerializeField]
	public bool clearSensesTargetOnExit;

	private int attempts;

	protected float startDistance;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		base.Blackboard.Remove("HitByFire");
		if (!base.Senses.FindTargetPosition(out var targetPosition))
		{
			return EFSMStateStatus.Success;
		}
		attempts = 0;
		startDistance = Vector3.Distance(Owner.transform.position, targetPosition);
		return MoveAwayFromTarget();
	}

	public override void OnStateExit()
	{
		base.Agent.ResetPath();
		if (clearSensesTargetOnExit)
		{
			base.Senses.ClearTarget();
		}
		base.OnStateExit();
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		Vector3 targetPosition;
		bool flag = base.Senses.FindTargetPosition(out targetPosition);
		if (flag && Vector3.Distance(targetPosition, Owner.transform.position) > desiredDistance + startDistance)
		{
			return EFSMStateStatus.Success;
		}
		if (base.Agent.hasPath)
		{
			return base.OnStateUpdate(deltaTime);
		}
		if (!flag)
		{
			return EFSMStateStatus.Success;
		}
		attempts++;
		if (attempts >= maxAttempts)
		{
			return EFSMStateStatus.Success;
		}
		return MoveAwayFromTarget();
	}

	protected virtual NavVector3 GetFleeDirection(NavVector3 posNS, Vector3 targetPosition)
	{
		return (posNS - base.Agent.WorldToNavSpace(targetPosition)).NormalizeXZ();
	}

	protected virtual EFSMStateStatus MoveAwayFromTarget()
	{
		if (!base.Senses.FindTargetPosition(out var targetPosition))
		{
			return EFSMStateStatus.Success;
		}
		NavVector3 nextPosition = base.Agent.nextPosition;
		using PooledList<NavVector3> pooledList = Pool.Get<PooledList<NavVector3>>();
		bool flag = Eqs.SampleNavigablePositions(base.Agent, nextPosition, pooledList, distance, distance, 8);
		using Eqs.PooledScoreList pooledScoreList = Pool.Get<Eqs.PooledScoreList>();
		NavVector3 fleeDirection = GetFleeDirection(nextPosition, targetPosition);
		foreach (NavVector3 item2 in pooledList)
		{
			float num = NavVector3.Dot(fleeDirection, (item2 - nextPosition).NormalizeXZ());
			if (waterAvoidance != 0 && WaterLevel.GetOverallWaterDepth(base.Agent.NavToWorldSpace(item2), waves: false, volumes: false, Owner) > 0.01f)
			{
				switch (waterAvoidance)
				{
				case WaterAvoidance.Prefer:
					num += 0.5f;
					break;
				case WaterAvoidance.Avoid:
					num -= 1f;
					break;
				case WaterAvoidance.Refuse:
					continue;
				}
			}
			pooledScoreList.Add((item2, num));
		}
		pooledScoreList.SortByScoreDesc(Owner);
		foreach (var item3 in pooledScoreList)
		{
			NavVector3 item = item3.pos;
			NavVector3 navVector = item;
			if (!flag)
			{
				if (!base.Agent.SamplePosition(item, out var hitNS, 10f))
				{
					continue;
				}
				navVector = hitNS.position;
			}
			if ((base.Agent.canSwim || !base.Agent.IsInWater(navVector)) && base.Agent.SetDestinationWithParams(navVector, autoBraking: false, speed, null, null, null, null, null))
			{
				return EFSMStateStatus.None;
			}
		}
		return EFSMStateStatus.Failure;
	}
}

