using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Rust.Ai.Gen2.Nav;

[Serializable]
public struct NavMeshBuildParams
{
	public enum EPartitionType : uint
	{
		Watershed,
		Monotone,
		Layers
	}

	[Tooltip("The xz-plane cell size to use for fields. [Limit: > 0] [Units: wu]")]
	[Min(0f)]
	public float cellSize;

	[Tooltip("The y-axis cell size to use for fields. [Limit: > 0] [Units: wu]")]
	[Min(0f)]
	public float cellHeight;

	[Tooltip("Agent height. Needs to be a multiple of cellHeight")]
	[Min(0f)]
	public float agentHeight;

	[Tooltip("Agent radius. Needs to be a multiple of walkableRadius")]
	[Min(0f)]
	public float agentRadius;

	[Tooltip("Maximum climb height for agent. Needs to be a multiple of cellHeight")]
	[Min(0f)]
	public float agentMaxClimb;

	[Tooltip("The maximum slope that is considered walkable. [Limits: 0 <= value < 90] [Units: Degrees]")]
	[Range(0f, 90f)]
	public float agentMaxSlope;

	[Tooltip("The width/height size of tile's on the xz-plane. [Limit: >= 0] [Units: vx]")]
	[Range(16f, 1024f)]
	public float tileSize;

	[Tooltip("The type of partitioning used for NavMesh generation")]
	public EPartitionType partitionType;

	[Range(0f, 65535f)]
	public int maxNodes;

	[Min(0f)]
	public float minRegionSizeMeters;

	[MarshalAs(UnmanagedType.U1)]
	[Tooltip("Removes small obstacles and rasterization artifacts that the agent would be able to walk over")]
	public bool filterLowHangingObstacles;

	[MarshalAs(UnmanagedType.U1)]
	[Tooltip("Remove regions hanging in the air over ledges")]
	public bool filterLedgeSpans;

	[MarshalAs(UnmanagedType.U1)]
	[Tooltip("Marks walkable spans as not walkable if the clearance above the span is less than the specified walkableHeight")]
	public bool filterWalkableLowHeightSpans;

	[MarshalAs(UnmanagedType.U1)]
	public bool buildDetailMesh;

	[Tooltip("Initializes NavMeshBuildParams with default values")]
	public NavMeshBuildParams(bool dummy = true)
	{
		agentHeight = 1.7f;
		agentRadius = 0.25f;
		cellSize = agentRadius / 3f;
		cellHeight = cellSize;
		agentMaxClimb = 0.4f;
		agentMaxSlope = 45f;
		minRegionSizeMeters = 5.76f;
		partitionType = EPartitionType.Watershed;
		tileSize = 512f;
		filterLowHangingObstacles = true;
		filterLedgeSpans = true;
		filterWalkableLowHeightSpans = true;
		maxNodes = 2048;
		buildDetailMesh = true;
	}
}

