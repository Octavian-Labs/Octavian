using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_HeardNoise : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_HeardNoise"))
		{
			if (base.Senses.FindMostRelevantNoise(out var mostRelevantNoise))
			{
				payload.entity = mostRelevantNoise.Initiator;
				payload.position = mostRelevantNoise.GuessedInitiatorPosition;
				return true;
			}
			return false;
		}
	}
}

