using System;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_ScriptedNade : FSMStateBase
{
	public GameObjectRef deployedGrenadePrefab;

	public const string thrownGrenadeKey = "ThrownScriptedNadeRecently";

	private const float explosionRadius = 6f;

	private const float cooldown = 60f;

	private static RustNavMeshPath _path;

	private NpcBarkComponent _barkComponent;

	private NpcGrenadePositionHint currentHint;

	private static RustNavMeshPath Path => _path ?? (_path = new RustNavMeshPath());

	private NpcBarkComponent BarkComponent => _barkComponent ?? (_barkComponent = Owner.GetComponent<NpcBarkComponent>());

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		currentHint = null;
		if (!base.Senses.FindTargetLKP(out var lkp, applyHeightOffset: false, predict: true))
		{
			return EFSMStateStatus.Failure;
		}
		NpcZone npcZone = NpcZone.GetForPoint(Owner, lkp);
		if (Owner.TryGetComponent<NpcZoneComponent>(out var component) && component.zone != null)
		{
			if (npcZone == null)
			{
				npcZone = component.zone;
			}
			else if (component.zone != npcZone)
			{
				return EFSMStateStatus.Failure;
			}
		}
		if (npcZone == null)
		{
			return EFSMStateStatus.Failure;
		}
		using PooledList<NpcLevelScript> pooledList = Pool.Get<PooledList<NpcLevelScript>>();
		npcZone.GetComponentsInChildren(pooledList);
		if (pooledList.Count == 0)
		{
			return EFSMStateStatus.Failure;
		}
		using PooledList<NpcGrenadePositionHint> pooledList2 = Pool.Get<PooledList<NpcGrenadePositionHint>>();
		foreach (NpcLevelScript item in pooledList)
		{
			bool flag = false;
			foreach (NpcLevelTrigger linkedTrigger in item.linkedTriggers)
			{
				if (linkedTrigger.isActiveAndEnabled && linkedTrigger.TryGetComponent<BoxCollider>(out var component2) && !(Vector3.Distance(component2.ClosestPoint(lkp), lkp) > 2f))
				{
					flag = true;
					break;
				}
			}
			if (!flag)
			{
				continue;
			}
			foreach (NpcPositionHint positionHint in item.positionHints)
			{
				if (positionHint.isActiveAndEnabled && positionHint is NpcGrenadePositionHint npcGrenadePositionHint && !(Vector3.Distance(npcGrenadePositionHint.landingPoint.transform.position, lkp) > 6f))
				{
					pooledList2.Add(npcGrenadePositionHint);
				}
			}
		}
		if (pooledList2.Count == 0)
		{
			return EFSMStateStatus.Failure;
		}
		pooledList2.Sort((NpcGrenadePositionHint a, NpcGrenadePositionHint b) => (a.transform.position - lkp).sqrMagnitude.CompareTo((b.transform.position - lkp).sqrMagnitude));
		float num = float.PositiveInfinity;
		NpcGrenadePositionHint npcGrenadePositionHint2 = null;
		foreach (NpcGrenadePositionHint item2 in pooledList2)
		{
			if (base.Agent.CalculatePath(item2.transform.position, Path))
			{
				float pathLength = Path.GetPathLength();
				if (pathLength < num)
				{
					num = pathLength;
					npcGrenadePositionHint2 = item2;
				}
			}
		}
		if (npcGrenadePositionHint2 == null)
		{
			return EFSMStateStatus.Failure;
		}
		if (!base.Agent.SetDestinationWithParams(npcGrenadePositionHint2.transform.position, autoBraking: true, RustNavMeshAgent.Speeds.Run, null, null, null, null, null))
		{
			return EFSMStateStatus.Failure;
		}
		currentHint = npcGrenadePositionHint2;
		return EFSMStateStatus.None;
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (base.Agent.hasPath)
		{
			return EFSMStateStatus.None;
		}
		NpcGrenade npcGrenade = GameManager.server.CreateEntity(deployedGrenadePrefab.resourcePath, base.Senses.EyePosition, Owner.transform.rotation) as NpcGrenade;
		if (npcGrenade == null)
		{
			return EFSMStateStatus.Failure;
		}
		npcGrenade.SetCreatorEntity(Owner);
		npcGrenade.grenadeHint = currentHint;
		npcGrenade.Spawn();
		base.Blackboard.Add("ThrownScriptedNadeRecently", 60f);
		using (PooledList<BaseEntity> pooledList = Pool.Get<PooledList<BaseEntity>>())
		{
			base.Senses.GetPerceivedAllies(pooledList);
			foreach (BaseEntity item in pooledList)
			{
				item.GetComponent<BlackboardComponent>().Add("ThrownScriptedNadeRecently", 60f);
			}
		}
		NpcPushHelper.CoordinatePush(Owner);
		return EFSMStateStatus.Success;
	}
}

