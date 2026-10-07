using System;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_CrocBreakFoundation : State_AttackWithTracking
{
	public const float attackRange = 3f;

	private static bool FindBuildingBlockNearby(RustNavMeshAgent agent, Vector3 position, out BuildingBlock buildingBlock)
	{
		if (agent.SampleGroundPositionWithPhysics(position, out var hitInfoNS, 2f, BasePlayer.GetRadius(), 2097152) && hitInfoNS.rawHitWS.GetEntity() is BuildingBlock buildingBlock2)
		{
			buildingBlock = buildingBlock2;
			return true;
		}
		using PooledList<BuildingBlock> pooledList = Pool.Get<PooledList<BuildingBlock>>();
		Vis.Entities(position, 4f, pooledList, 2097152);
		if (pooledList.Count > 0)
		{
			buildingBlock = pooledList[0];
			return true;
		}
		buildingBlock = null;
		return false;
	}

	public static BuildingBlock FindNearestTwigFoundationOnTargetBuilding(RustNavMeshAgent agent, BasePlayer targetPlayer, float? maxDistance = null)
	{
		if (!FindBuildingBlockNearby(agent, targetPlayer.transform.position, out var buildingBlock))
		{
			return null;
		}
		BuildingManager.Building building = BuildingManager.server.GetBuilding(buildingBlock.buildingID);
		BuildingBlock result = null;
		float num = float.MaxValue;
		foreach (BuildingBlock buildingBlock2 in building.buildingBlocks)
		{
			if (buildingBlock2.grade == BuildingGrade.Enum.Twigs && buildingBlock2.parentEntity.Get(serverside: true) == null && (buildingBlock2.ShortPrefabName == "foundation" || buildingBlock2.ShortPrefabName == "foundation.triangle"))
			{
				float num2 = buildingBlock2.Distance(agent.transform.position);
				if ((!maxDistance.HasValue || !(num2 > maxDistance.Value)) && num2 < num)
				{
					result = buildingBlock2;
					num = num2;
				}
			}
		}
		return result;
	}

	protected override void DoDamage()
	{
		if (base.Senses.FindTarget(out var target) && target.ToNonNpcPlayer(out var player))
		{
			BuildingBlock buildingBlock = FindNearestTwigFoundationOnTargetBuilding(base.Agent, player, null);
			if (buildingBlock == null)
			{
				base.DoDamage();
			}
			else
			{
				buildingBlock.Kill(BaseNetworkable.DestroyMode.Gib);
			}
		}
	}
}

