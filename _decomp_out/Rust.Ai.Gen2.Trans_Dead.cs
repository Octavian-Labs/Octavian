using UnityEngine;

namespace Rust.Ai.Gen2;

public class Trans_Dead : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		return true;
	}

	public override void OnTransitionTaken(FSMStateBase from, FSMStateBase to)
	{
		Vector3 position = Owner.transform.position;
		Debug.LogWarning($"Transitioning to dead state from {from.Name}: {Owner.ShortPrefabName} suicided on AI failure at {position} in {MapHelper.PositionToString(position)}", Owner);
	}
}

