using UnityEngine;

namespace Rust.Ai.Gen2;

[ExecuteAlways]
public class LivestockPatternTester : MonoBehaviour, IEditorComponent
{
	private const string ShaderName = "Rust/Standard Livestock";

	[Header("Traits")]
	[Tooltip("Drives Threshold. Low yield cuts high so patches are small, high yield cuts low so patches are large.")]
	[Range(0f, 1f)]
	public float MilkYield = 0.25f;

	[Tooltip("Drives patch colour along the range's Dung Colour gradient, low at the left, high at the right.")]
	[Range(0f, 1f)]
	public float PoopYield = 0.25f;

	[Tooltip("Drives Edge Warp. Larger litters give more crinkled patch edges.")]
	[Range(0f, 1f)]
	public float LitterSize = 0.25f;

	[Tooltip("Drives Hair Length. Robust animals get longer hair tips on the patch edges.")]
	[Range(0f, 1f)]
	public float Robustness = 0.25f;

	[Tooltip("Drives Blotch Size. Long lived animals carry larger patches.")]
	[Range(0f, 1f)]
	public float Longevity = 0.25f;

	[Tooltip("Drives Bias Strength. 0 is a healthy animal held to the bias map and 1 is an inbred one that ignores it, the only two a shipped animal lands on. In game an inbred animal's other sliders are also scaled by livestock.inbredScale, so set them to what that leaves. Needs Bias Source on and a bias map on the material.")]
	[Range(0f, 1f)]
	public float Inbred = 1f;

	[Header("Identity")]
	[Tooltip("Scrambles where the blotch and warp textures sit on the animal. Same seed, same coat layout.")]
	public int Seed;

	[Header("Coat")]
	[Tooltip("The ranges to tune. This is the asset the species' prefab paints from, so what is set here is what the game ships.")]
	public LivestockPatternRange Range;

	private static readonly int TilingOffset1 = Shader.PropertyToID("_PatternTilingOffset1");

	private static readonly int TilingOffset2 = Shader.PropertyToID("_PatternTilingOffset2");

	private static readonly int Breakup = Shader.PropertyToID("_PatternBreakup");

	private static readonly int HairLength = Shader.PropertyToID("_PatternHairLength");

	private static readonly int BiasStrength = Shader.PropertyToID("_PatternBiasStrength");

	private static readonly int Threshold = Shader.PropertyToID("_PatternThreshold");

	private static readonly int PatternColor = Shader.PropertyToID("_PatternColor");

	private MaterialPropertyBlock block;
}

