using ConVar;
using UnityEngine;

namespace Rust.Ai.Gen2;

public abstract class LivestockGeneRange : ScriptableObject
{
	public abstract bool CanPaint(Material material);

	public virtual string ShadeNameOf(int genome)
	{
		return null;
	}

	public static float TraitFor(int genome, LivestockGene gene)
	{
		float num = (float)LivestockGenome.Expressed(genome, gene) / 2f;
		if (!LivestockGenome.IsInbred(genome))
		{
			return num;
		}
		return num * Mathf.Clamp01(Livestock.inbredScale);
	}

	public static float InbredFor(int genome)
	{
		if (!LivestockGenome.IsInbred(genome))
		{
			return 0f;
		}
		return 1f;
	}
}

