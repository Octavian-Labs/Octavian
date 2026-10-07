using UnityEngine;

namespace Rust.Ai.Gen2;

public class LivestockPattern : MonoBehaviour
{
	[Tooltip("The renderers the pattern paints, grouped by the material they share. Every LOD of a group has to be listed or the far ones keep the authored hide.")]
	public LivestockCoatGroup[] Groups;

	[Tooltip("What each gene maps to at Bad and at Good, for the shader this species is drawn with. Keeps the coat believable across every genome.")]
	public LivestockGeneRange Range;

	public static int SeedFor(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return 0;
		}
		uint num = 2166136261u;
		for (int i = 0; i < name.Length; i++)
		{
			num ^= name[i];
			num *= 16777619;
		}
		return (int)num;
	}
}

