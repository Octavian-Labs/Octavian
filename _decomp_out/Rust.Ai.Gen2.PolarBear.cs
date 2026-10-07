namespace Rust.Ai.Gen2;

[SoftRequireComponent(typeof(BasicAnimalFsm))]
public class PolarBear : BaseNPC2
{
	[ServerVar(Help = "Population active on the server, per square km", ShowInAdminUI = true)]
	public static float Population = 1f;

	public override TraitFlag Traits => TraitFlag.Alive | TraitFlag.Animal | TraitFlag.Food | TraitFlag.Meat;

	public override string Categorize()
	{
		return "Polarbear";
	}
}

