using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[CreateAssetMenu(menuName = "Rust/AI/Livestock Specials")]
public class LivestockSpecialTable : ScriptableObject
{
	public const string AssetPath = "assets/rust.ai/agents/cow/livestock.specials.asset";

	[Tooltip("The specials that can turn up in the world.")]
	public LivestockSpecial[] Entries;

	[Tooltip("What two specials bred together produce. Only a name, never a genome.")]
	public LivestockCross[] Crosses;

	private static LivestockSpecialTable instance;

	public static LivestockSpecialTable Instance
	{
		get
		{
			if (instance == null)
			{
				instance = FileSystem.Load<LivestockSpecialTable>("assets/rust.ai/agents/cow/livestock.specials.asset", complain: false);
			}
			return instance;
		}
	}

	public LivestockSpecial Find(string name)
	{
		if (Entries == null || string.IsNullOrEmpty(name))
		{
			return null;
		}
		LivestockSpecial[] entries = Entries;
		foreach (LivestockSpecial livestockSpecial in entries)
		{
			if (livestockSpecial != null && string.Equals(livestockSpecial.Name, name, StringComparison.Ordinal))
			{
				return livestockSpecial;
			}
		}
		return null;
	}

	public LivestockCross CrossFor(string first, string second)
	{
		if (Crosses == null || string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second))
		{
			return null;
		}
		LivestockCross[] crosses = Crosses;
		foreach (LivestockCross livestockCross in crosses)
		{
			if (livestockCross != null && !string.IsNullOrEmpty(livestockCross.Name) && (Pairs(livestockCross, first, second) || Pairs(livestockCross, second, first)))
			{
				return livestockCross;
			}
		}
		return null;
	}

	private static bool Pairs(LivestockCross cross, string a, string b)
	{
		if (string.Equals(cross.ParentA, a, StringComparison.Ordinal))
		{
			return string.Equals(cross.ParentB, b, StringComparison.Ordinal);
		}
		return false;
	}
}

