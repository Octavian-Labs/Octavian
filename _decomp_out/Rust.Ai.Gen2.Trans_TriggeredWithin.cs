using UnityEngine;

namespace Rust.Ai.Gen2;

public class Trans_TriggeredWithin : FSMTransitionBase
{
	[SerializeField]
	public float Window = 1f;

	private double? lastTriggerTime;

	public void Trigger()
	{
		lastTriggerTime = Time.timeAsDouble;
	}

	public override void OnStateEnter()
	{
		lastTriggerTime = null;
	}

	public override void OnStateExit()
	{
		lastTriggerTime = null;
	}

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		if (lastTriggerTime.HasValue)
		{
			return Time.timeAsDouble - lastTriggerTime.Value <= (double)Window;
		}
		return false;
	}

	public override string GetName()
	{
		return $"{base.GetName()} {Window}s";
	}
}

