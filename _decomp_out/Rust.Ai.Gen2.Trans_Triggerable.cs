namespace Rust.Ai.Gen2;

public class Trans_Triggerable : FSMTransitionBase
{
	private FSMPayload? Parameter;

	protected bool Triggered { get; private set; }

	public void Trigger()
	{
		Triggered = true;
	}

	public void Trigger(FSMPayload parameter)
	{
		Parameter = parameter;
		Trigger();
	}

	public override void OnStateEnter()
	{
		Triggered = false;
	}

	public override void OnStateExit()
	{
		Triggered = false;
	}

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_Triggerable"))
		{
			if (Triggered && Parameter.HasValue)
			{
				payload.CopyFrom(Parameter.Value);
				Parameter = null;
			}
			return Triggered;
		}
	}
}

