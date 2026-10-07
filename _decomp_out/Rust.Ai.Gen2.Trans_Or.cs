namespace Rust.Ai.Gen2;

public class Trans_Or : Trans_Composite
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_Or"))
		{
			foreach (FSMTransitionBase transition in transitions)
			{
				if (transition.Evaluate(ref payload))
				{
					return true;
				}
			}
			return false;
		}
	}

	protected override string GetNameSeparator()
	{
		return "||";
	}
}

