using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_AnimalIdle : State_AnimalMontageLoop
{
	[SerializeField]
	public AnimationClip[] Animations;

	protected override AnimationClip GetAnimation()
	{
		if (Animations == null || Animations.Length == 0)
		{
			return null;
		}
		return Animations.GetRandom();
	}
}

