using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_AnimalSleep : State_AnimalMontageLoop
{
	[SerializeField]
	public AnimationClip Animation;

	protected override AnimationClip GetAnimation()
	{
		return Animation;
	}
}

