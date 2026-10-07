namespace Rust.Ai.Gen2;

[SoftRequireComponent(typeof(BasicAnimalFsm))]
public class Boar : BaseNPC2
{
	[ServerVar(Help = "Population active on the server, per square km", ShowInAdminUI = true)]
	public static float Population = 5f;

	public override TraitFlag Traits => TraitFlag.Alive | TraitFlag.Animal | TraitFlag.Food | TraitFlag.Meat;

	public override string Categorize()
	{
		return "Boar";
	}
}

