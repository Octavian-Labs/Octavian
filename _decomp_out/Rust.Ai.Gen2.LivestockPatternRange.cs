using UnityEngine;

namespace Rust.Ai.Gen2;

[CreateAssetMenu(fileName = "PatternRange", menuName = "Rust/AI/Livestock Pattern Range")]
public class LivestockPatternRange : LivestockGeneRange
{
	public struct Traits
	{
		public float Yield;

		public float Dung;

		public float Fertility;

		public float Hardiness;

		public float Longevity;

		public float Inbred;
	}

	public struct Values
	{
		public Vector4 BlotchTilingOffset;

		public Vector4 WarpTilingOffset;

		public float Threshold;

		public Color Color;

		public float Breakup;

		public float HairLength;

		public float BiasStrength;
	}

	[Tooltip("Threshold at Yield Bad (x) and Good (y). Higher threshold means smaller patches.")]
	public Vector2 Threshold = new Vector2(0.6f, 0.2f);

	[Tooltip("Patch colour from Dung Bad on the left to Good on the right. Multiplied over the base fur.")]
	public Gradient DungColor = DefaultDungColor();

	[Tooltip("Edge warp at Fertility Bad (x) and Good (y), in blotch repeats.")]
	public Vector2 EdgeWarp = new Vector2(0f, 0.06f);

	[Tooltip("Hair length at Hardiness Bad (x) and Good (y), in UV units.")]
	public Vector2 HairLength = new Vector2(0f, 0.002f);

	[Tooltip("Blotch size at Longevity Bad (x) and Good (y), relative to the texture. 1 is one repeat over the UV range.")]
	public Vector2 BlotchSize = new Vector2(0.7f, 1.6f);

	[Tooltip("Bias strength at Inbred 0 (x) and Inbred 1 (y), the only two a shipped animal lands on. 1 holds the patches to the bias map and 0 ignores the map.")]
	public Vector2 BiasStrength = new Vector2(1f, 0f);

	[Tooltip("Size of the warp features relative to the texture. Smaller crinkles the edges, larger bends whole blobs.")]
	[Min(0.01f)]
	public float EdgeWarpSize = 0.33f;

	private const int WarpSeedMix = 1540483477;

	private static readonly int TilingOffset1Id = Shader.PropertyToID("_PatternTilingOffset1");

	private static readonly int TilingOffset2Id = Shader.PropertyToID("_PatternTilingOffset2");

	private static readonly int BreakupId = Shader.PropertyToID("_PatternBreakup");

	private static readonly int HairLengthId = Shader.PropertyToID("_PatternHairLength");

	private static readonly int BiasStrengthId = Shader.PropertyToID("_PatternBiasStrength");

	private static readonly int ThresholdId = Shader.PropertyToID("_PatternThreshold");

	private static readonly int PatternColorId = Shader.PropertyToID("_PatternColor");

	public override bool CanPaint(Material material)
	{
		if (material != null)
		{
			return material.HasProperty(ThresholdId);
		}
		return false;
	}

	public Values Evaluate(int genome, int seed)
	{
		return Evaluate(TraitsOf(genome), seed);
	}

	public Values Evaluate(Traits traits, int seed)
	{
		float tiling = 1f / Mathf.Max(0.01f, Mathf.Lerp(BlotchSize.x, BlotchSize.y, traits.Longevity));
		float tiling2 = 1f / Mathf.Max(0.01f, EdgeWarpSize);
		Values result = default(Values);
		result.BlotchTilingOffset = TilingOffsetFor(tiling, seed);
		result.WarpTilingOffset = TilingOffsetFor(tiling2, seed ^ 0x5BD1E995);
		result.Threshold = Mathf.Lerp(Threshold.x, Threshold.y, traits.Yield);
		result.Color = DungColor.Evaluate(traits.Dung);
		result.Breakup = Mathf.Lerp(EdgeWarp.x, EdgeWarp.y, traits.Fertility);
		result.HairLength = Mathf.Lerp(HairLength.x, HairLength.y, traits.Hardiness);
		result.BiasStrength = Mathf.Lerp(BiasStrength.x, BiasStrength.y, traits.Inbred);
		return result;
	}

	public static Traits TraitsOf(int genome)
	{
		Traits result = default(Traits);
		result.Yield = LivestockGeneRange.TraitFor(genome, LivestockGene.Yield);
		result.Dung = LivestockGeneRange.TraitFor(genome, LivestockGene.Dung);
		result.Fertility = LivestockGeneRange.TraitFor(genome, LivestockGene.Fertility);
		result.Hardiness = LivestockGeneRange.TraitFor(genome, LivestockGene.Hardiness);
		result.Longevity = LivestockGeneRange.TraitFor(genome, LivestockGene.Longevity);
		result.Inbred = LivestockGeneRange.InbredFor(genome);
		return result;
	}

	private static Vector4 TilingOffsetFor(float tiling, int seed)
	{
		Vector2 vector = SeedToOffset(seed);
		return new Vector4(tiling, tiling, vector.x, vector.y);
	}

	private static Gradient DefaultDungColor()
	{
		Gradient gradient = new Gradient();
		gradient.SetKeys(new GradientColorKey[3]
		{
			new GradientColorKey(new Color(0.05f, 0.04f, 0.035f), 0f),
			new GradientColorKey(new Color(0.22f, 0.13f, 0.07f), 0.5f),
			new GradientColorKey(new Color(0.45f, 0.26f, 0.12f), 1f)
		}, new GradientAlphaKey[2]
		{
			new GradientAlphaKey(1f, 0f),
			new GradientAlphaKey(1f, 1f)
		});
		return gradient;
	}

	private static Vector2 SeedToOffset(int seed)
	{
		int num = seed * -1640531527;
		int num2 = (num ^ (num >>> 16)) * -2048144789;
		int num3 = num2 ^ (num2 >>> 13);
		uint num4 = (uint)(num3 & 0xFFFF);
		uint num5 = (uint)num3 >> 16;
		return new Vector2((float)num4 / 65536f, (float)num5 / 65536f);
	}
}

