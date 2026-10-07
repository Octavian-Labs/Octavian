using System;
using ConVar;
using Facepunch;
using Prefabs.Misc;
using Rust.Ai.Gen2.Nav;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_IsNavmeshReady : FSMTransitionBase
{
	private readonly int humanoid = BaseNavigator.GetNavMeshAgentID("Humanoid");

	private readonly int animal = BaseNavigator.GetNavMeshAgentID("Animal");

	private MonumentNavMesh cachedMonumentNavMesh;

	public override void OnStateEnter()
	{
		base.OnStateEnter();
		if (!AI.useUnityNavmesh || base.Agent.agentTypeID != humanoid)
		{
			return;
		}
		if (DeepSeaManager.IsInsideDeepSea(Owner.transform.position))
		{
			if (!Owner.GetParentEntity().Is<GhostShip>(out var _))
			{
				using (PooledList<DeepSeaIsland> pooledList = Facepunch.Pool.Get<PooledList<DeepSeaIsland>>())
				{
					Vis.Entities(Owner.transform.position, 10f, pooledList, 8454145);
					DeepSeaIsland deepSeaIsland = ((pooledList.Count > 0) ? pooledList[0] : null);
					cachedMonumentNavMesh = ((deepSeaIsland != null) ? deepSeaIsland.monumentNavMesh : null);
				}
			}
		}
		else
		{
			if (TerrainMeta.TopologyMap == null || !TerrainMeta.TopologyMap.GetTopology(Owner.transform.position, 1024) || TerrainMeta.Path == null)
			{
				return;
			}
			foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
			{
				if (monument.HasNavmesh && monument.GetMonumentNavMesh().Is<MonumentNavMesh>(out var castedUnityObject2) && monument.IsInBounds(Owner.transform.position))
				{
					cachedMonumentNavMesh = castedUnityObject2;
					break;
				}
			}
		}
	}

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_IsNavmeshReady"))
		{
			if (!AI.move)
			{
				return false;
			}
			if (AI.useUnityNavmesh)
			{
				if (base.Agent.agentTypeID == animal)
				{
					if (SingletonComponent<DynamicNavMesh>.Instance == null || SingletonComponent<DynamicNavMesh>.Instance.IsBuilding)
					{
						return false;
					}
				}
				else if (base.Agent.agentTypeID == humanoid)
				{
					if (cachedMonumentNavMesh != null && cachedMonumentNavMesh.IsBuilding)
					{
						return false;
					}
					if (Owner.GetParentEntity().Is<GhostShip>(out var _))
					{
						return true;
					}
					if (!DungeonNavmesh.NavReady())
					{
						return false;
					}
				}
				NavVector3 positionNS = base.Agent.WorldToNavSpace(Owner.transform.position);
				NavHit hitNS;
				return base.Agent.SamplePosition(positionNS, out hitNS, 2f);
			}
			if (!base.Agent.IsNavMeshBuilt)
			{
				return false;
			}
			return base.Agent.isOnNavMesh;
		}
	}
}

