using System.Collections.Generic;
using UnityEngine;

namespace Rust.Ai.Gen2;

[ExecuteAlways]
public class SheepTraitTester : MonoBehaviour, IEditorComponent
{
	[Header("Traits")]
	[Tooltip("Drives the range's Dung Tint, which colours the wool, its fuzz, the body and the far fleece together. Black at 0, brown in the middle, white at 1.")]
	[Range(0f, 1f)]
	public float PoopYield = 0.5f;

	[Tooltip("Drives Density, Length, Tip Thinning and Strand Softness. Low yield is a thin, patchy fleece, high yield a full one.")]
	[Range(0f, 1f)]
	public float WoolYield = 0.5f;

	private MaterialPropertyBlock block;

	private readonly List<Renderer> paintedRenderers = new List<Renderer>();

	private readonly List<int> paintedSlots = new List<int>();
}

