using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public abstract class State_AnimalMontageLoop : State_AnimalMontage
{
	[SerializeField]
	public float MinDuration = 7f;

	[SerializeField]
	public float MaxDuration = 14f;

	private float remaining;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		remaining = UnityEngine.Random.Range(MinDuration, MaxDuration);
		return base.OnStateEnter(payload);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		remaining -= deltaTime;
		if (remaining <= 0f)
		{
			return EFSMStateStatus.Success;
		}
		if (animState != null && !animState.isPlaying)
		{
			PlayMontage(GetAnimation());
		}
		return EFSMStateStatus.None;
	}
}

