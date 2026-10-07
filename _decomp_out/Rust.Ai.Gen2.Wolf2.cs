namespace Rust.Ai.Gen2;

[SoftRequireComponent(typeof(Wolf2FSM))]
public class Wolf2 : BaseNPC2
{
	[ServerVar(Help = "Population active on the server, per square km", ShowInAdminUI = true)]
	public static float Population = 2f;

	public override string Categorize()
	{
		return "Wolf";
	}
}

