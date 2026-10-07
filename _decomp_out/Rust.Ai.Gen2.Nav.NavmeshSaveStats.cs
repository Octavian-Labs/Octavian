using Cysharp.Text;

namespace Rust.Ai.Gen2.Nav;

public struct NavmeshSaveStats
{
	public bool valid;

	public bool succeeded;

	public string failure;

	public double mainMs;

	public double threadMs;

	public double replaceMs;

	public long bytes;

	public int pendingTiles;

	public int dirtyTiles;

	public int dataVersionDelta;

	public NavmeshSaveReplaceMode replaceMode;

	public bool wroteDelta;

	public long deltaAppendedBytes;

	public long deltaBaseBytes;

	public float deltaBudget;

	public int threadId;

	public bool phasesValid;

	public NavMeshSavePhases phases;

	public string Describe()
	{
		Utf16ValueStringBuilder sb = ZString.CreateStringBuilder();
		try
		{
			AppendDescription(ref sb);
			return sb.ToString();
		}
		finally
		{
			sb.Dispose();
		}
	}

	public void AppendDescription(ref Utf16ValueStringBuilder sb)
	{
		if (!valid)
		{
			sb.Append("none for this navmesh instance");
			return;
		}
		if (!succeeded)
		{
			sb.Append("FAILED, main ");
			sb.Append(mainMs, "F1");
			sb.Append(" ms, thread ");
			sb.Append(threadMs, "F1");
			sb.Append(" ms (id ");
			sb.Append(threadId);
			sb.Append("), ");
			sb.Append(dirtyTiles);
			sb.Append(" tiles were dirty and the next save rewrites the whole file: ");
			sb.Append(failure);
			return;
		}
		sb.Append(wroteDelta ? "delta" : "full");
		sb.Append(", main ");
		sb.Append(mainMs, "F1");
		sb.Append(" ms, thread ");
		sb.Append(threadMs, "F1");
		sb.Append(" ms (id ");
		sb.Append(threadId);
		sb.Append("), ");
		if (wroteDelta)
		{
			sb.Append("appended in place");
		}
		else
		{
			sb.Append("replace ");
			sb.Append(replaceMs, "F1");
			sb.Append(" ms (");
			sb.Append(replaceMode);
			sb.Append(")");
		}
		if (phasesValid)
		{
			sb.Append(", phases gather ");
			sb.Append(phases.gatherMs, "F1");
			sb.Append(" / compress ");
			sb.Append(phases.compressWaitMs, "F1");
			sb.Append(" / write ");
			sb.Append(phases.writeMs, "F1");
			sb.Append(" / flush ");
			sb.Append(phases.flushMs, "F1");
			sb.Append(" ms");
		}
		sb.Append(", ");
		sb.Append(bytes);
		sb.Append(" bytes on disk, appended ");
		sb.Append(deltaAppendedBytes);
		sb.Append(" of a ");
		sb.Append(deltaBaseBytes);
		sb.Append(" byte base (budget ");
		sb.Append(deltaBudget, "0.####");
		sb.Append("), ");
		sb.Append(pendingTiles);
		sb.Append(" pending tiles, ");
		sb.Append(dirtyTiles);
		sb.Append(" tiles dirty since the last save (version delta ");
		sb.Append(dataVersionDelta);
		sb.Append(")");
	}
}

