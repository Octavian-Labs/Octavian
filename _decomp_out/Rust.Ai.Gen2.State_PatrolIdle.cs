using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_PatrolIdle : FSMStateBase
{
	private NPCHumanoidAnimController _clientAnim;

	private NpcShootingComponent _shooting;

	private NPCHumanoidAnimController ClientAnim => _clientAnim ?? (_clientAnim = Owner.GetComponentInChildren<NPCHumanoidAnimController>());

	private NpcShootingComponent Shooting => _shooting ?? (_shooting = Owner.GetComponent<NpcShootingComponent>());

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		ClientAnim.IsRelaxed = true;
		ClientAnim.IsAiming = false;
		Shooting.AllowShooting = false;
		base.Agent.overrideDirectionWS = Owner.transform.forward;
		return base.OnStateEnter(payload);
	}

	public override void OnStateExit()
	{
		ClientAnim.IsRelaxed = false;
		ClientAnim.IsAiming = true;
		Shooting.AllowShooting = true;
		base.Agent.overrideDirectionWS = null;
		base.OnStateExit();
	}
}

