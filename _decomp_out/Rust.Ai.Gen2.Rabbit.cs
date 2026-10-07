namespace Rust.Ai.Gen2;

public class Rabbit : CritterAnimal
{
	[ServerVar(Help = "Population active on the server, per square km", ShowInAdminUI = true)]
	public static float Population = 2f;
}

