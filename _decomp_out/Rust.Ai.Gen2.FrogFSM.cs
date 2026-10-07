namespace Rust.Ai.Gen2;

public class FrogFSM : CritterAnimalFSM
{
	public State_Idle stopIdle = new State_Idle();

	private RustNavMeshAgent _agent;

	public override FSMStateBase idle => stopIdle;

	private RustNavMeshAgent Agent => _agent ?? (_agent = base.baseEntity.GetComponent<RustNavMeshAgent>());

	protected override void OnTicked(float deltaTime)
	{
		base.OnTicked(deltaTime);
		if (base.baseEntity is Frog frog)
		{
			frog.SetNavTarget(Agent.destinationWS);
		}
	}
}

