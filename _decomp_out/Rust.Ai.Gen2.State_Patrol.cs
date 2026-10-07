using System;
using System.Collections.Generic;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_Patrol : FSMStateBase
{
	[SerializeField]
	private Vector2 distanceRange = new Vector2(4f, 6f);

	[SerializeField]
	private float homeRadius = 10f;

	[SerializeField]
	private RustNavMeshAgent.Speeds speed;

	private NPCHumanoidAnimController _clientAnim;

	private NpcShootingComponent _shooting;

	private NpcZoneComponent _npcZoneComponent;

	private NavVector3? spawnPositionNS;

	private Vector3? desiredEndDirection;

	private static RustNavMeshPath _path;

	private NPCHumanoidAnimController ClientAnim => _clientAnim ?? (_clientAnim = Owner.GetComponentInChildren<NPCHumanoidAnimController>());

	private NpcShootingComponent Shooting => _shooting ?? (_shooting = Owner.GetComponent<NpcShootingComponent>());

	private NpcZoneComponent NpcZoneComponent => _npcZoneComponent ?? (_npcZoneComponent = Owner.GetComponent<NpcZoneComponent>());

	private static RustNavMeshPath Path => _path ?? (_path = new RustNavMeshPath());

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		Reset();
		if (!spawnPositionNS.HasValue)
		{
			spawnPositionNS = base.Agent.nextPosition;
		}
		if (!TrySetPatrolDestination())
		{
			return EFSMStateStatus.Failure;
		}
		ClientAnim.IsRelaxed = true;
		ClientAnim.IsAiming = false;
		Shooting.AllowShooting = false;
		return base.OnStateEnter(payload);
	}

	private bool TrySetPatrolDestination()
	{
		NavVector3 nextPosition = base.Agent.nextPosition;
		bool flag = NavVector3.Distance(spawnPositionNS.Value, nextPosition) > homeRadius;
		using PooledList<NavVector3> pooledList = Pool.Get<PooledList<NavVector3>>();
		float num = UnityEngine.Random.Range(distanceRange.x, distanceRange.y);
		bool flag2 = Eqs.SampleNavigablePositions(base.Agent, nextPosition, pooledList, num, num, 8);
		using Eqs.PooledScoreList pooledScoreList = Pool.Get<Eqs.PooledScoreList>();
		NavVector3 normalized = (spawnPositionNS.Value - nextPosition).normalized;
		foreach (NavVector3 item2 in pooledList)
		{
			float num2 = 0f;
			num2 = ((!flag) ? (num2 + UnityEngine.Random.value) : (num2 + Mathx.RemapValClamped(NavVector3.Dot(normalized, (item2 - nextPosition).NormalizeXZ()), -1f, 1f, 0f, 1f)));
			pooledScoreList.Add((item2, num2));
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
			if (!NpcZoneComponent.IsPointInsideZone(vector) || base.Agent.IsInWater(vector) || !base.Agent.CalculatePath(navVector, Path))
			{
				continue;
			}
			if (Path.status != 0)
			{
				Vector3 vector2 = base.Agent.NavToWorldSpace(Path.GetDestinationNS());
				if (!NpcZoneComponent.IsPointInsideZone(vector2) || base.Agent.IsInWater(vector2))
				{
					continue;
				}
			}
			base.Agent.SetPath(Path);
			base.Agent.speed = base.Agent.GetSpeedForGait(speed);
			if (base.Agent.lastValidPath.Count >= 2)
			{
				List<NavVector3> lastValidPath = base.Agent.lastValidPath;
				NavVector3 navVector2 = lastValidPath[lastValidPath.Count - 1];
				List<NavVector3> lastValidPath2 = base.Agent.lastValidPath;
				NavVector3 directionNS = (navVector2 - lastValidPath2[lastValidPath2.Count - 2]).NormalizeXZ() * 3f;
				Vector3 direction = base.Agent.NavToWorldDirection(directionNS);
				RustNavMeshAgent agent = base.Agent;
				List<NavVector3> lastValidPath3 = base.Agent.lastValidPath;
				Vector3 vector3 = agent.NavToWorldSpace(lastValidPath3[lastValidPath3.Count - 1]);
				if (base.Senses.Trace(vector3 + base.Senses.EyeOffset, direction, out var hitInfo, 1218519041, "patrol"))
				{
					desiredEndDirection = hitInfo.normal.WithY(0f);
				}
			}
			return true;
		}
		return false;
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (!base.Agent.hasPath)
		{
			return EFSMStateStatus.Success;
		}
		if (desiredEndDirection.HasValue && !base.Agent.overrideDirectionWS.HasValue && base.Agent.remainingDistance < 2.5f)
		{
			base.Agent.overrideDirectionWS = desiredEndDirection.Value;
		}
		return base.OnStateUpdate(deltaTime);
	}

	public override void OnStateExit()
	{
		ClientAnim.IsRelaxed = false;
		ClientAnim.IsAiming = true;
		Shooting.AllowShooting = true;
		desiredEndDirection = null;
		base.Agent.overrideDirectionWS = null;
		base.Agent.ResetPath();
		base.OnStateExit();
	}

	private void Reset()
	{
		base.Senses.ClearTarget();
		base.Blackboard.Clear();
		if (Owner is BaseCombatEntity { healthFraction: <1f, SecondsSinceAttacked: >120f } baseCombatEntity)
		{
			baseCombatEntity.SetHealth(Owner.MaxHealth());
		}
	}
}

