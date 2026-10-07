using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_IsTargetProtectedByMount : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_IsTargetProtectedByMount"))
		{
			BaseEntity target;
			return base.Senses.FindTarget(out target) && IsProtected(target);
		}
	}

	public static bool IsProtected(BaseEntity target)
	{
		if (!target.ToNonNpcPlayer(out var player))
		{
			return false;
		}
		if (player.GetMounted().Is<BaseMountable>(out var castedUnityObject))
		{
			return castedUnityObject.ProtectsFromAnimals;
		}
		return false;
	}
}

