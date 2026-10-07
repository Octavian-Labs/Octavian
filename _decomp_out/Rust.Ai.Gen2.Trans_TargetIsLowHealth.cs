namespace Rust.Ai.Gen2;

public class Trans_TargetIsLowHealth : FSMTransitionBase
{
	public float maxHealthFraction = 0.5f;

	public bool requireDamagedByUs = true;

	public float maxTimeSinceDamagedByUs = 5f;

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_TargetIsLowHealth"))
		{
			if (!base.Senses.FindTarget(out var target))
			{
				return false;
			}
			if (!target.Is<BaseCombatEntity>(out var castedUnityObject))
			{
				return false;
			}
			if (requireDamagedByUs)
			{
				if (!Owner.Is<BaseCombatEntity>(out var castedUnityObject2))
				{
					return false;
				}
				return castedUnityObject.healthFraction <= maxHealthFraction && castedUnityObject2.lastDealtDamageTo == castedUnityObject && castedUnityObject2.SecondsSinceDealtDamage < maxTimeSinceDamagedByUs;
			}
			return castedUnityObject.healthFraction <= maxHealthFraction;
		}
	}
}

