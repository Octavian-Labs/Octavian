using System;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_FleeToHide : State_Flee
{
	public const string HitDuringChargeKey = "HitDuringCharge";

	private bool clockWise;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		base.Blackboard.Remove("HitDuringCharge");
		if (!base.Senses.FindTargetPosition(out var targetPosition))
		{
			return EFSMStateStatus.Success;
		}
		Vector3 rhs = (targetPosition - Owner.transform.position).NormalizeXZ();
		clockWise = Vector3.Dot(Owner.transform.right, rhs) > 0f;
		return base.OnStateEnter(payload);
	}

	protected override EFSMStateStatus MoveAwayFromTarget()
	{
		if (!base.Senses.FindTargetPosition(out var targetPosition))
		{
			return EFSMStateStatus.Success;
		}
		float magnitude = (Owner.transform.position - targetPosition).magnitude;
		Vector3 vector = Owner.transform.forward;
		float num = 15f;
		if (magnitude > 6f)
		{
			vector = (Owner.transform.position - targetPosition).NormalizeXZ();
			num = 55f;
		}
		vector = Quaternion.AngleAxis(num * (clockWise ? 1f : (-1f)), Vector3.up) * vector;
		NavVector3 nextPosition = base.Agent.nextPosition;
		NavVector3 aNS = base.Agent.WorldToNavDirection(vector);
		using PooledList<NavVector3> pooledList = Pool.Get<PooledList<NavVector3>>();
		bool flag = Eqs.SampleNavigablePositions(base.Agent, nextPosition, pooledList, distance, distance, 8);
		using Eqs.PooledScoreList pooledScoreList = Pool.Get<Eqs.PooledScoreList>();
		foreach (NavVector3 item3 in pooledList)
		{
			float item = NavVector3.Dot(aNS, (item3 - nextPosition).NormalizeXZ());
			pooledScoreList.Add((item3, item));
		}
		pooledScoreList.SortByScoreDesc(Owner);
		foreach (var item4 in pooledScoreList)
		{
			NavVector3 item2 = item4.pos;
			NavVector3 navVector = item2;
			if (!flag)
			{
				if (!base.Agent.SamplePosition(item2, out var hitNS, 10f))
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

