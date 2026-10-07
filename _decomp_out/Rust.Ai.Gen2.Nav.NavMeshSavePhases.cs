namespace Rust.Ai.Gen2.Nav;

public struct NavMeshSavePhases
{
	public double gatherMs;

	public double compressWaitMs;

	public double writeMs;

	public double flushMs;

	public long fileBytes;
}

