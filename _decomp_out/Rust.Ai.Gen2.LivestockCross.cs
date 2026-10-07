using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class LivestockCross
{
	[Tooltip("One parent, by entry name. Matched either way round, so which side is the dam does not matter.")]
	public string ParentA;

	public string ParentB;

	[Tooltip("What their calf is called.")]
	public string Name;
}

