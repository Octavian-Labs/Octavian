using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class LivestockWoolShade
{
	[Tooltip("The coat group on the prefab this shades, matched on the group's name.")]
	public string Group;

	[Tooltip("Colour from Dung Bad on the left to Good on the right: black, through brown, to white. Multiplied over what this group was authored with.")]
	public Gradient DungTint = new Gradient();
}

