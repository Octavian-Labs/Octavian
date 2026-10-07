using System;
using ConVar;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_WolfHurt : State_Hurt
{
	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		EFSMStateStatus result = base.OnStateEnter(payload);
		if (!base.Senses.FindTarget(out var target))
		{
			if (AI.logIssues)
			{
				Debug.LogWarning("Got attacked but couldn't find a target");
			}
			return result;
		}
		using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
		base.Senses.GetInitialAllies(pooledList);
		foreach (BaseEntity item in pooledList)
		{
			item.GetComponent<Wolf2FSM>().Intimidate(target);
		}
		return result;
	}
}

