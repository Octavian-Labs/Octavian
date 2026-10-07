using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class ForcedGene
{
	public LivestockGene Gene;

	[Tooltip("The first copy. Both Good means the animal breeds true for this gene.")]
	public LivestockAllele First = LivestockAllele.Good;

	[Tooltip("The second copy. One Good and one worse means it performs like Good but only passes it on half the time.")]
	public LivestockAllele Second = LivestockAllele.Good;
}

