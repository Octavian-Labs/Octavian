using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_HasBlackboardBool : FSMTransitionBase
{
	[SerializeField]
	public string Key;

	private BlackboardComponent _blackboard;

	private BlackboardComponent Blackboard => _blackboard ?? (_blackboard = Owner.GetComponent<BlackboardComponent>());

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_HasBlackboardBool"))
		{
			return Blackboard.Has(Key);
		}
	}

	public override string GetName()
	{
		return base.GetName() + " " + Key;
	}
}

