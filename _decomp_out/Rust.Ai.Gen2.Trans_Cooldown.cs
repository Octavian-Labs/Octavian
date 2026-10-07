using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_Cooldown : FSMTransitionBase
{
	[SerializeField]
	public float cooldown = 20f;

	[Tooltip("Spend the cooldown only once the state it leads to has started, rather than on every attempt. Off by default because a cooldown is usually what rations a state that refuses on entry.")]
	[SerializeField]
	public bool onlyWhenStarted;

	private double? lastTakenTime;

	public override void OnTransitionTaken(FSMStateBase from, FSMStateBase to)
	{
		base.OnTransitionTaken(from, to);
		if (!onlyWhenStarted)
		{
			lastTakenTime = Time.timeAsDouble;
		}
	}

	public override void OnTransitionConfirmed(FSMStateBase entered)
	{
		base.OnTransitionConfirmed(entered);
		if (onlyWhenStarted)
		{
			lastTakenTime = Time.timeAsDouble;
		}
	}

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_Cooldown"))
		{
			return !lastTakenTime.HasValue || Time.timeAsDouble - lastTakenTime.Value >= (double)cooldown;
		}
	}

	public override string GetName()
	{
		return $"{base.GetName()} {cooldown}s";
	}
}

