using System;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_Roam : FSMStateBase
{
	[SerializeField]
	private Vector2 distanceRange = new Vector2(10f, 20f);

	[SerializeField]
	private float homeRadius = 50f;

	[NonSerialized]
	private float authoredHomeRadius = -1f;

	[SerializeField]
	private RustNavMeshAgent.Speeds minSpeed;

	[SerializeField]
	private RustNavMeshAgent.Speeds maxSpeed = RustNavMeshAgent.Speeds.Sprint;

	[SerializeField]
	[Tooltip("When determining the speed to roam at, does the interpolation start from the minimum distance range (true), or from 0 (false)")]
	private bool useDistanceRangeMinforMinSpeed;

	[SerializeField]
	protected WaterAvoidance waterAvoidance;

	private Vector3? spawnPosition;

	protected virtual Vector3 HomePosition => spawnPosition ?? Owner.transform.position;

	protected virtual bool HealsWhenUndisturbed => true;

	public void SetHomeRadiusScale(float scale)
	{
		if (authoredHomeRadius < 0f)
		{
			authoredHomeRadius = homeRadius;
		}
		homeRadius = authoredHomeRadius * Mathf.Clamp01(scale);
	}

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		Reset();
		if (!spawnPosition.HasValue)
		{
			spawnPosition = Owner.transform.position;
		}
		if (!TrySetRoamDestination())
		{
			return EFSMStateStatus.Failure;
		}
		return base.OnStateEnter(payload);
	}

	private bool TrySetRoamDestination()
	{
		NavVector3 nextPosition = base.Agent.nextPosition;
		using PooledList<NavVector3> pooledList = Pool.Get<PooledList<NavVector3>>();
		float num = UnityEngine.Random.Range(distanceRange.x, distanceRange.y);
		bool flag = Eqs.SampleNavigablePositions(base.Agent, nextPosition, pooledList, num, num, 8);
		Vector3 homePosition = HomePosition;
		bool flag2 = Vector3.Distance(homePosition, Owner.transform.position) > homeRadius;
		using Eqs.PooledScoreList pooledScoreList = Pool.Get<Eqs.PooledScoreList>();
		NavVector3 normalized = (base.Agent.WorldToNavSpace(homePosition) - nextPosition).normalized;
		foreach (NavVector3 item2 in pooledList)
		{
			float num2 = 0f;
			if (flag2)
			{
				num2 += Mathx.RemapValClamped(NavVector3.Dot(normalized, (item2 - nextPosition).NormalizeXZ()), -1f, 1f, 0f, 1f);
				if (base.Agent.IsPositionOnFavoredTerrain(item2))
				{
					num2 += 0.25f;
				}
			}
			else
			{
				num2 += UnityEngine.Random.value;
				if (base.Agent.IsPositionOnFavoredTerrain(item2))
				{
					num2 += 10f;
				}
			}
			if (waterAvoidance != 0 && WaterLevel.GetOverallWaterDepth(base.Agent.NavToWorldSpace(item2), waves: false, volumes: false, Owner) > 0.01f)
			{
				switch (waterAvoidance)
				{
				case WaterAvoidance.Prefer:
					num2 += 0.5f;
					break;
				case WaterAvoidance.Avoid:
					num2 -= 1f;
					break;
				case WaterAvoidance.Refuse:
					continue;
				}
			}
			pooledScoreList.Add((item2, num2));
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
			if ((base.Agent.canSwim || !base.Agent.IsInWater(navVector)) && base.Agent.SetDestinationWithParams(navVector, autoBraking: true, null, null, null, null, null, null))
			{
				float ratio = Mathf.InverseLerp(useDistanceRangeMinforMinSpeed ? distanceRange.x : 0f, distanceRange.y, num);
				base.Agent.SetSpeedRatio(ratio, minSpeed, maxSpeed);
				return true;
			}
		}
		return false;
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (!base.Agent.hasPath)
		{
			return EFSMStateStatus.Success;
		}
		return base.OnStateUpdate(deltaTime);
	}

	public override void OnStateExit()
	{
		base.Agent.ResetPath();
		base.OnStateExit();
	}

	private void Reset()
	{
		base.Senses.ClearTarget();
		base.Blackboard.Clear();
		if (HealsWhenUndisturbed && Owner is BaseCombatEntity { healthFraction: <1f, SecondsSinceAttacked: >120f } baseCombatEntity)
		{
			baseCombatEntity.SetHealth(Owner.MaxHealth());
		}
	}
}

