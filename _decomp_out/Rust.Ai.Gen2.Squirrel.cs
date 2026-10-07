namespace Rust.Ai.Gen2;

public class Squirrel : CritterAnimal
{
	[ServerVar(Help = "Population active on the server, per square km", ShowInAdminUI = true)]
	public static float Population = 2f;

	private RustNavMeshAgent _agent;

	protected RustNavMeshAgent Agent => _agent ?? (_agent = GetComponent<RustNavMeshAgent>());
}

