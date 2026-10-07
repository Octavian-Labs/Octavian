namespace Rust.Ai.Gen2;

[SoftRequireComponent(typeof(TigerFSM))]
public class Tiger : BaseNPC2
{
	[ServerVar(Help = "Population active on the server, per square km", ShowInAdminUI = true)]
	public static float Population = 0.5f;

	public override string Categorize()
	{
		return "Tiger";
	}
}

