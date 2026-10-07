using System;
using System.Collections.Generic;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_Search : FSMStateBase
{
	[SerializeField]
	private float searchRadius = 10f;

	[SerializeField]
	private RustNavMeshAgent.Speeds speed;

	[SerializeField]
	private int numCloseSearchesBeforeExpanding = 2;

	public bool predict;

	public bool loop = true;

	private NPCHumanoidAnimController _clientAnim;

	private NpcBarkComponent _barkComponent;

	private NavVector3 searchOriginNS;

	private int numIterations;

	private NPCHumanoidAnimController ClientAnim => _clientAnim ?? (_clientAnim = Owner.GetComponentInChildren<NPCHumanoidAnimController>());

	private NpcBarkComponent BarkComponent => _barkComponent ?? (_barkComponent = Owner.GetComponent<NpcBarkComponent>());

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindTarget(out var target))
		{
			return EFSMStateStatus.Failure;
		}
		if (!base.Senses.FindLKP(target, out var lkp, applyHeightOffset: false, predict: true))
		{
			return EFSMStateStatus.Failure;
		}
		NavVector3 positionNS = base.Agent.WorldToNavSpace(lkp);
		if (!base.Agent.SamplePosition(positionNS, out var hitNS, 3.5f) && !base.Agent.SamplePosition(positionNS, out hitNS, 20f))
		{
			return EFSMStateStatus.Failure;
		}
		searchOriginNS = hitNS.position;
		if (!TrySetSearchDestination())
		{
			return EFSMStateStatus.Failure;
		}
		if (predict)
		{
			BarkComponent.PlayVoicelineFromCategory(ENPCVoicelineCategory.Lost);
		}
		if (!predict)
		{
			using PooledList<BaseEntity> pooledList = Pool.Get<PooledList<BaseEntity>>();
			base.Senses.GetPerceivedAllies(pooledList);
			foreach (BaseEntity item in pooledList)
			{
				if (item.TryGetComponent<Scientist2FSM>(out var component) && item.TryGetComponent<SenseComponent>(out var component2) && component2.FindTarget(out var target2) && !(target2 != target))
				{
					component.SearchTrans.Trigger();
				}
			}
		}
		return base.OnStateEnter(payload);
	}

	private bool TrySetSearchDestination()
	{
		using PooledList<NavVector3> pooledList = Pool.Get<PooledList<NavVector3>>();
		bool flag = ((numIterations >= numCloseSearchesBeforeExpanding) ? Eqs.SampleNavigablePositions(base.Agent, base.Agent.nextPosition, pooledList, searchRadius, searchRadius, 8) : Eqs.SampleNavigablePositions(base.Agent, searchOriginNS, pooledList, searchRadius, searchRadius * 0.5f, 16));
		if (!base.Senses.FindTargetLKP(out var lkp, applyHeightOffset: false, predict: true))
		{
			return false;
		}
		if (!base.Senses.FindTargetLKP(out var lkp2, applyHeightOffset: false, predict: true))
		{
			return false;
		}
		NavVector3 navVector = base.Agent.WorldToNavSpace(lkp);
		base.Agent.WorldToNavSpace(lkp2);
		NavVector3 aNS = base.Agent.WorldToNavDirection(Owner.transform.forward);
		using Eqs.PooledScoreList pooledScoreList = Pool.Get<Eqs.PooledScoreList>();
		using PooledList<ScientistNPC2> pooledList2 = Pool.Get<PooledList<ScientistNPC2>>();
		BaseEntity.Query.Server.GetBrainsInSphere(Owner.transform.position, searchRadius * 3f, pooledList2);
		foreach (NavVector3 item2 in pooledList)
		{
			float num = 0f;
			if (predict)
			{
				num += Mathx.RemapValClamped(NavVector3.Dot(aNS, (item2 - navVector).NormalizeXZ()), -1f, 1f, 0f, 1f) * 3f;
				num += Mathx.RemapValClamped(NavVector3.Distance(item2, navVector), 0f, searchRadius, 0f, 1f);
			}
			else
			{
				foreach (ScientistNPC2 item3 in pooledList2)
				{
					if (!(item3 == Owner))
					{
						Vector3 b = base.Agent.NavToWorldSpace(item2);
						if (item3.TryGetComponent<RustNavMeshAgent>(out var component) && component.hasPath && component.lastValidPath.Count > 0)
						{
							float num2 = num;
							RustNavMeshAgent rustNavMeshAgent = component;
							List<NavVector3> lastValidPath = component.lastValidPath;
							num = num2 + Vector3.Distance(rustNavMeshAgent.NavToWorldSpace(lastValidPath[lastValidPath.Count - 1]), b);
						}
						else
						{
							num += Vector3.Distance(item3.transform.position, b);
						}
					}
				}
				num += UnityEngine.Random.value * 0.01f;
			}
			pooledScoreList.Add((item2, num));
		}
		pooledScoreList.SortByScoreDesc(Owner);
		foreach (var item4 in pooledScoreList)
		{
			NavVector3 item = item4.pos;
			NavVector3 navVector2 = item;
			if (!flag)
			{
				if (!base.Agent.SamplePosition(item, out var hitNS, 3.5f))
				{
					continue;
				}
				navVector2 = hitNS.position;
			}
			Vector3 positionWS = base.Agent.NavToWorldSpace(navVector2);
			if (!base.Agent.IsInWater(positionWS) && !(NavVector3.Distance(navVector2, base.Agent.nextPosition) < 2f) && base.Agent.SetDestinationWithParams(navVector2, autoBraking: true, speed, null, null, null, null, null))
			{
				numIterations++;
				ClientAnim.IsCrouching = speed == RustNavMeshAgent.Speeds.Sneak;
				return true;
			}
		}
		return false;
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (base.Agent.lastValidPath.Count > 0)
		{
			List<NavVector3> lastValidPath = base.Agent.lastValidPath;
			NavVector3 normalized = (lastValidPath[lastValidPath.Count - 1] - base.Agent.nextPosition).normalized;
			base.Agent.overrideDirectionWS = base.Agent.NavToWorldDirection(normalized);
		}
		if (!base.Agent.hasPath)
		{
			if (!loop)
			{
				return EFSMStateStatus.Success;
			}
			if (!TrySetSearchDestination())
			{
				return EFSMStateStatus.Failure;
			}
		}
		return base.OnStateUpdate(deltaTime);
	}

	public override void OnStateExit()
	{
		numIterations = 0;
		base.Agent.ResetPath();
		base.Agent.overrideDirectionWS = null;
		ClientAnim.IsCrouching = false;
		base.OnStateExit();
	}
}

