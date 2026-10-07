namespace Rust.Ai.Gen2;

public class Trans_And : Trans_Composite
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_And"))
		{
			foreach (FSMTransitionBase transition in transitions)
			{
				if (!transition.Evaluate(ref payload))
				{
					return false;
				}
			}
			return true;
		}
	}

	protected override string GetNameSeparator()
	{
		return "&&";
	}
}

