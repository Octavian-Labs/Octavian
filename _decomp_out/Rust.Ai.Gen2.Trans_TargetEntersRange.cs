using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_TargetEntersRange : Trans_TargetInRange
{
	private bool wasInRange;

	public override void OnStateEnter()
	{
		FSMPayload payload = default(FSMPayload);
		wasInRange = base.EvaluateInternal(ref payload);
	}

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		bool flag = base.EvaluateInternal(ref payload);
		bool result = flag && !wasInRange;
		wasInRange = flag;
		return result;
	}
}

