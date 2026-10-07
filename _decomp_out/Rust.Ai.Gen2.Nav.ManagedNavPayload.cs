using System.Runtime.InteropServices;
using UnityEngine;

namespace Rust.Ai.Gen2.Nav;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ManagedNavPayload
{
	public const int Version = 6;

	public int payloadVersion;

	public NavMeshBuildParams buildParams;

	public NavMeshBuildParams buildParamsHiRes;

	public Bounds currentNavmeshBounds;

	public Vector2Int tileNum;

	public int pendingTileCount;
}

