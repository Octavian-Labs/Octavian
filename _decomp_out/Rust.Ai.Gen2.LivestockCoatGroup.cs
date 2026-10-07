using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class LivestockCoatGroup
{
	[Tooltip("Names the group in the inspector, and picks the shade a range paints it with where the range has one per surface. Renaming it changes what this group is painted.")]
	public string Name;

	[Tooltip("Every renderer that wears this group's material, LODs included.")]
	public Renderer[] Renderers;

	[Tooltip("Which material slot on those renderers a coat paints.")]
	public int MaterialIndex;

	public Material MaterialOn(Renderer renderer)
	{
		if (renderer == null)
		{
			return null;
		}
		Material[] sharedMaterials = renderer.sharedMaterials;
		if (MaterialIndex < 0 || MaterialIndex >= sharedMaterials.Length)
		{
			return null;
		}
		return sharedMaterials[MaterialIndex];
	}

	public Material SharedMaterial()
	{
		if (Renderers == null)
		{
			return null;
		}
		Renderer[] renderers = Renderers;
		foreach (Renderer renderer in renderers)
		{
			Material material = MaterialOn(renderer);
			if (material != null)
			{
				return material;
			}
		}
		return null;
	}
}

