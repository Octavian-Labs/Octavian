using UnityEngine;

namespace Rust.Ai.Gen2;

public abstract class FSMSlowTransitionBase : FSMTransitionBase
{
	private bool cachedEvalResult;

	private double? lastEvalTime;

	private double cacheLifeTime = 1.0;

	protected sealed override bool EvaluateInternal(ref FSMPayload payload)
	{
		double timeAsDouble = Time.timeAsDouble;
		if (!lastEvalTime.HasValue || timeAsDouble - lastEvalTime.Value > cacheLifeTime)
		{
			cachedEvalResult = EvaluateAtInterval(ref payload);
			lastEvalTime = timeAsDouble;
		}
		return cachedEvalResult;
	}

	protected abstract bool EvaluateAtInterval(ref FSMPayload payload);
}

