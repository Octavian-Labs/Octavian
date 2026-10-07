using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_ScientistDead : State_Dead
{
	public GameObjectRef DeathEffect;

	private NpcBarkComponent _barkComponent;

	private NpcBarkComponent BarkComponent => _barkComponent ?? (_barkComponent = Owner.GetComponent<NpcBarkComponent>());

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (DeathEffect != null)
		{
			Effect.server.Run(DeathEffect.resourcePath, Owner.transform.position, Vector3.up);
		}
		return base.OnStateEnter(payload);
	}
}

