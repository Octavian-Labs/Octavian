using System;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_GoBackToWater : State_MoveToTarget
{
	private NavVector3 nearestWaterPoint;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (base.Agent.IsSwimming)
		{
			return EFSMStateStatus.Success;
		}
		using (TimeWarning.New("State_GoBackToWater GetCoarseVectorToShore and GetHeight"))
		{
			(Vector3 shoreDir, float shoreDist) coarseVectorToShore = TerrainTexturing.Instance.GetCoarseVectorToShore(Owner.transform.position);
			Vector3 item = coarseVectorToShore.shoreDir;
			float item2 = coarseVectorToShore.shoreDist;
			Vector3 vector = item * item2;
			Vector3 vector2 = Owner.transform.position + vector.normalized * (vector.magnitude + 10f);
			vector2.y = TerrainMeta.HeightMap.GetHeight(vector2);
			using PooledList<NavVector3> pooledList = Pool.Get<PooledList<NavVector3>>();
			bool flag = Eqs.SampleNavigablePositions(base.Agent, base.Agent.WorldToNavSpace(vector2), pooledList, 10f, 10f, 8);
			pooledList.Shuffle((uint)Environment.TickCount);
			nearestWaterPoint = base.Agent.WorldToNavSpace(vector2);
			foreach (NavVector3 item3 in pooledList)
			{
				NavVector3 positionNS = item3;
				if (!flag)
				{
					if (!base.Agent.SamplePosition(item3, out var hitNS, 10f))
					{
						continue;
					}
					positionNS = hitNS.position;
				}
				if (base.Agent.IsInWater(positionNS))
				{
					nearestWaterPoint = positionNS;
					break;
				}
			}
		}
		return base.OnStateEnter(payload);
	}

	protected override bool GetMoveDestination(out NavVector3 destination)
	{
		destination = nearestWaterPoint;
		return true;
	}
}

