namespace Rust.Ai.Gen2;

public class Trans_IsTargetLkpInOurZone : FSMTransitionBase
{
	private NpcZoneComponent _npcZoneComponent;

	private NpcBarkComponent _barkComponent;

	private NpcZoneComponent NpcZoneComponent => _npcZoneComponent ?? (_npcZoneComponent = Owner.GetComponent<NpcZoneComponent>());

	private NpcBarkComponent BarkComponent => _barkComponent ?? (_barkComponent = Owner.GetComponent<NpcBarkComponent>());

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_IsTargetLkpInOurZone"))
		{
			if (!base.Senses.FindTargetLKP(out var lkp))
			{
				return false;
			}
			if (NpcZoneComponent.IsPointInsideZone(lkp))
			{
				return true;
			}
			BarkComponent.PlayVoicelineFromCategory(ENPCVoicelineCategory.Hold);
			return false;
		}
	}
}

