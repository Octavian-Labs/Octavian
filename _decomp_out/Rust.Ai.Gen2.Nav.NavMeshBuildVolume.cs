namespace Rust.Ai.Gen2.Nav;

public struct NavMeshBuildVolume
{
	public unsafe fixed float verts[24];

	public int nverts;

	public float hmin;

	public float hmax;

	public int area;
}

