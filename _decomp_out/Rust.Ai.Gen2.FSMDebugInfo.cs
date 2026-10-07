namespace Rust.Ai.Gen2;

public static class FSMDebugInfo
{
	public const string RoseMarker = "+ ";

	public const string FellMarker = "- ";

	public static string Mark(string line, int direction)
	{
		if (direction > 0)
		{
			return "+ " + line;
		}
		if (direction < 0)
		{
			return "- " + line;
		}
		return line;
	}

	public static string Unmark(string line, out int direction)
	{
		if (line.StartsWith("+ "))
		{
			direction = 1;
			return line.Substring("+ ".Length);
		}
		if (line.StartsWith("- "))
		{
			direction = -1;
			return line.Substring("- ".Length);
		}
		direction = 0;
		return line;
	}
}

