using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public struct MountMontageWindow
{
	[Range(0f, 1f)]
	public float start;

	[Range(0f, 1f)]
	public float end;

	public static MountMontageWindow Whole
	{
		get
		{
			MountMontageWindow result = default(MountMontageWindow);
			result.start = 0f;
			result.end = 1f;
			return result;
		}
	}

	public bool IsAuthored => end > start;
}

