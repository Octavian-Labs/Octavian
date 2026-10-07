using UnityEngine;

namespace Rust.Ai.Gen2;

[CreateAssetMenu(fileName = "WoolRange", menuName = "Rust/AI/Livestock Wool Range")]
public class LivestockWoolRange : LivestockGeneRange
{
	public struct Traits
	{
		public float Yield;

		public float Dung;
	}

	public struct Values
	{
		public Color Tint;

		public float Density;

		public float Length;

		public float TipThinning;

		public float StrandSoftness;

		public float Specular;

		public float FuzzIntensity;

		public float FuzzScatter;

		public float RootShadow;
	}

	private interface IPaintTarget
	{
		void SetColor(int id, Color value);

		void SetFloat(int id, float value);
	}

	private readonly struct BlockTarget : IPaintTarget
	{
		private readonly MaterialPropertyBlock block;

		public BlockTarget(MaterialPropertyBlock block)
		{
			this.block = block;
		}

		public void SetColor(int id, Color value)
		{
			block.SetColor(id, value);
		}

		public void SetFloat(int id, float value)
		{
			block.SetFloat(id, value);
		}
	}

	[Tooltip("One shade per coat group on the prefab, matched by the group's name. A group with no shade of its own falls back to the first.")]
	public LivestockWoolShade[] Shades = DefaultShades();

	[Tooltip("Specular at Dung Bad (x) and Good (y), on the fleece shell. Art carries a black fleece at a negative, which switches its highlight off.")]
	public Vector2 Specular = new Vector2(-0.32f, 0.6f);

	[Tooltip("Fuzz intensity at Dung Bad (x) and Good (y). The soft sheen that makes wool read as wool, dialled right down on a dark coat.")]
	public Vector2 FuzzIntensity = new Vector2(0.096f, 0.353f);

	[Tooltip("Fuzz scatter at Dung Bad (x) and Good (y). Widens the sheen toward the silhouette, off entirely on a dark coat.")]
	public Vector2 FuzzScatter = new Vector2(0f, 0.405f);

	[Tooltip("Root shadow at Dung Bad (x) and Good (y). Higher darkens the roots, which is what keeps a dark fleece from flattening into a silhouette.")]
	public Vector2 RootShadow = new Vector2(0.726f, 0.358f);

	[Tooltip("Density at Yield Bad (x) and Good (y). Fraction of the strand noise that counts as wool at the root.")]
	public Vector2 Density = new Vector2(0.39f, 0.6f);

	[Tooltip("Length at Yield Bad (x) and Good (y), in shell heights. Above 1 the wool runs past the last shell.")]
	public Vector2 Length = new Vector2(0.72f, 1.16f);

	[Tooltip("Tip thinning at Yield Bad (x) and Good (y). Higher thins the strands earlier toward the tip.")]
	public Vector2 TipThinning = new Vector2(0.61f, 0.89f);

	[Tooltip("Strand softness at Yield Bad (x) and Good (y). Width of the dithered edge on each strand.")]
	public Vector2 StrandSoftness = new Vector2(0.11f, 0.15f);

	private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

	private static readonly int FuzzColorId = Shader.PropertyToID("_FuzzColor");

	private static readonly int DensityId = Shader.PropertyToID("_Density");

	private static readonly int LengthId = Shader.PropertyToID("_Length");

	private static readonly int TipThinningId = Shader.PropertyToID("_TipThinning");

	private static readonly int StrandSoftnessId = Shader.PropertyToID("_StrandSoftness");

	private static readonly int SpecularId = Shader.PropertyToID("_Specular");

	private static readonly int FuzzIntensityId = Shader.PropertyToID("_FuzzIntensity");

	private static readonly int FuzzScatterId = Shader.PropertyToID("_FuzzScatter");

	private static readonly int RootShadowId = Shader.PropertyToID("_RootShadow");

	private static readonly int ColorId = Shader.PropertyToID("_Color");

	private const string ColorizeKeyword = "_COLORIZELAYER_ON";

	private static readonly int ColorizeColorRId = Shader.PropertyToID("_ColorizeColorR");

	private static readonly Color Brown = new Color(0.412f, 0.366f, 0.318f);

	public Values Evaluate(int genome, LivestockWoolShade shade = null)
	{
		return Evaluate(TraitsOf(genome), shade);
	}

	public Values Evaluate(Traits traits, LivestockWoolShade shade = null)
	{
		Values result = default(Values);
		result.Tint = TintOf(shade, traits.Dung);
		result.Density = Mathf.Lerp(Density.x, Density.y, traits.Yield);
		result.Length = Mathf.Lerp(Length.x, Length.y, traits.Yield);
		result.TipThinning = Mathf.Lerp(TipThinning.x, TipThinning.y, traits.Yield);
		result.StrandSoftness = Mathf.Lerp(StrandSoftness.x, StrandSoftness.y, traits.Yield);
		result.Specular = Mathf.Lerp(Specular.x, Specular.y, traits.Dung);
		result.FuzzIntensity = Mathf.Lerp(FuzzIntensity.x, FuzzIntensity.y, traits.Dung);
		result.FuzzScatter = Mathf.Lerp(FuzzScatter.x, FuzzScatter.y, traits.Dung);
		result.RootShadow = Mathf.Lerp(RootShadow.x, RootShadow.y, traits.Dung);
		return result;
	}

	public LivestockWoolShade ShadeFor(LivestockCoatGroup group)
	{
		return ShadeNamed(group?.Name);
	}

	public LivestockWoolShade ShadeNamed(string group)
	{
		if (Shades == null || Shades.Length == 0)
		{
			return null;
		}
		if (group != null)
		{
			for (int i = 0; i < Shades.Length; i++)
			{
				if (Shades[i] != null && Shades[i].Group == group)
				{
					return Shades[i];
				}
			}
		}
		return Shades[0];
	}

	public bool HasShadeFor(LivestockCoatGroup group)
	{
		if (Shades == null || group == null)
		{
			return false;
		}
		for (int i = 0; i < Shades.Length; i++)
		{
			if (Shades[i] != null && Shades[i].Group == group.Name)
			{
				return true;
			}
		}
		return false;
	}

	private Color TintOf(LivestockWoolShade shade, float dung)
	{
		if (shade == null)
		{
			shade = ShadeFor(null);
		}
		if (shade?.DungTint == null)
		{
			return Color.white;
		}
		return shade.DungTint.Evaluate(dung);
	}

	public static Traits TraitsOf(int genome)
	{
		Traits result = default(Traits);
		result.Yield = LivestockGeneRange.TraitFor(genome, LivestockGene.Yield);
		result.Dung = LivestockGeneRange.TraitFor(genome, LivestockGene.Dung);
		return result;
	}

	public override bool CanPaint(Material material)
	{
		if (material != null)
		{
			if (!material.HasProperty(DensityId))
			{
				return material.HasProperty(ColorId);
			}
			return true;
		}
		return false;
	}

	public override string ShadeNameOf(int genome)
	{
		float num = LivestockGeneRange.TraitFor(genome, LivestockGene.Dung);
		if (num < 0.25f)
		{
			return "Black";
		}
		if (!(num < 0.75f))
		{
			return "White";
		}
		return "Brown";
	}

	public static bool PaintOnto(MaterialPropertyBlock block, Material authored, Values values)
	{
		return PaintOnto(new BlockTarget(block), authored, authored, values);
	}

	private static bool PaintOnto<T>(T target, Material worn, Material authored, Values values) where T : struct, IPaintTarget
	{
		if (worn.HasProperty(DensityId))
		{
			target.SetColor(BaseColorId, authored.GetColor(BaseColorId) * values.Tint);
			target.SetColor(FuzzColorId, authored.GetColor(FuzzColorId) * values.Tint);
			target.SetFloat(DensityId, values.Density);
			target.SetFloat(LengthId, values.Length);
			target.SetFloat(TipThinningId, values.TipThinning);
			target.SetFloat(StrandSoftnessId, values.StrandSoftness);
			target.SetFloat(SpecularId, values.Specular);
			target.SetFloat(FuzzIntensityId, values.FuzzIntensity);
			target.SetFloat(FuzzScatterId, values.FuzzScatter);
			target.SetFloat(RootShadowId, values.RootShadow);
			return true;
		}
		if (worn.IsKeywordEnabled("_COLORIZELAYER_ON"))
		{
			Color color = authored.GetColor(ColorizeColorRId);
			Color color2 = color * values.Tint;
			target.SetColor(ColorizeColorRId, new Color(color2.r, color2.g, color2.b, color.a));
			return true;
		}
		if (!worn.HasProperty(ColorId))
		{
			return false;
		}
		target.SetColor(ColorId, authored.GetColor(ColorId) * values.Tint);
		return true;
	}

	private static LivestockWoolShade[] DefaultShades()
	{
		return new LivestockWoolShade[3]
		{
			ShadeOf("Fleece", new Color(0.3333f, 0.3333f, 0.3333f)),
			ShadeOf("Body", new Color(0.2777f, 0.301f, 0.3418f)),
			ShadeOf("Fleece LOD", new Color(0.1887f, 0.1546f, 0.1433f))
		};
	}

	private static LivestockWoolShade ShadeOf(string group, Color black)
	{
		Gradient gradient = new Gradient();
		gradient.SetKeys(new GradientColorKey[3]
		{
			new GradientColorKey(black, 0f),
			new GradientColorKey(Brown, 0.5f),
			new GradientColorKey(Color.white, 1f)
		}, new GradientAlphaKey[2]
		{
			new GradientAlphaKey(1f, 0f),
			new GradientAlphaKey(1f, 1f)
		});
		return new LivestockWoolShade
		{
			Group = group,
			DungTint = gradient
		};
	}
}

