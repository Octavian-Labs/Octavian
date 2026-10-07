namespace Rust.Ai.Gen2;

public class Trans_Bark : FSMTransitionBase
{
	public ENPCVoicelineCategory category;

	private NpcBarkComponent _barkComponent;

	private NpcBarkComponent BarkComponent => _barkComponent ?? (_barkComponent = Owner.GetComponent<NpcBarkComponent>());

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		return true;
	}

	public override void OnTransitionTaken(FSMStateBase from, FSMStateBase to)
	{
		BarkComponent.PlayVoicelineFromCategory(category);
	}
}

