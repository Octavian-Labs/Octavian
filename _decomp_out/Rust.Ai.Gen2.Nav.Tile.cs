namespace Rust.Ai.Gen2.Nav;

public class Tile
{
	public int tx;

	public int ty;

	public bool hasData;

	public bool wasBuiltOnce;

	public bool dirtySinceSave;

	public Tile(int tx, int ty)
	{
		this.tx = tx;
		this.ty = ty;
	}
}

