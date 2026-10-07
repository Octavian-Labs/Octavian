using System;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_Flank : FSMStateBase
{
	public RustNavMeshAgent.Speeds speed;

	private NPCHumanoidAnimController _clientAnim;

	private NpcBarkComponent _barkComponent;

	private RustNavMeshPath _pathToLkp;

	private RustNavMeshPath _pathToFlank;

	private RustNavMeshPath _pathFromFlankToEnemy;

	private bool isOnSecondPath;

	private NPCHumanoidAnimController ClientAnim => _clientAnim ?? (_clientAnim = Owner.GetComponentInChildren<NPCHumanoidAnimController>());

	private NpcBarkComponent BarkComponent => _barkComponent ?? (_barkComponent = Owner.GetComponent<NpcBarkComponent>());

	private RustNavMeshPath PathToLkp => _pathToLkp ?? (_pathToLkp = new RustNavMeshPath());

	private RustNavMeshPath PathToFlank => _pathToFlank ?? (_pathToFlank = new RustNavMeshPath());

	private RustNavMeshPath PathFromFlankToEnemy => _pathFromFlankToEnemy ?? (_pathFromFlankToEnemy = new RustNavMeshPath());

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindTargetLKP(out var lkp, applyHeightOffset: false, predict: true))
		{
			return EFSMStateStatus.Failure;
		}
		NavVector3 positionNS = base.Agent.WorldToNavSpace(lkp);
		if (!base.Agent.SamplePosition(positionNS, out var hitNS, 3.5f))
		{
			return EFSMStateStatus.Failure;
		}
		if (!base.Agent.CalculatePath(hitNS.position, PathToLkp) || PathToLkp.status != 0)
		{
			return EFSMStateStatus.Failure;
		}
		if (!NPCFlankSpot.Find(base.Agent, hitNS.position, PathToLkp, PathToFlank, PathFromFlankToEnemy))
		{
			return EFSMStateStatus.Failure;
		}
		if (!base.Agent.SetPath(PathToFlank))
		{
			return EFSMStateStatus.Failure;
		}
		if (speed == RustNavMeshAgent.Speeds.Sneak)
		{
			ClientAnim.IsCrouching = true;
		}
		base.Agent.speed = base.Agent.GetSpeedForGait(speed);
		isOnSecondPath = false;
		BarkComponent.PlayVoicelineFromCategory(ENPCVoicelineCategory.Flank);
		return base.OnStateEnter(payload);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (!base.Agent.hasPath)
		{
			if (isOnSecondPath)
			{
				return EFSMStateStatus.Success;
			}
			isOnSecondPath = true;
			if (!base.Senses.FindTarget(out var _))
			{
				return EFSMStateStatus.Failure;
			}
			if (!base.Agent.SetPath(PathFromFlankToEnemy))
			{
				return EFSMStateStatus.Failure;
			}
		}
		return base.OnStateUpdate(deltaTime);
	}

	public override void OnStateExit()
	{
		ClientAnim.IsCrouching = false;
		base.Agent.ResetPath();
		base.OnStateExit();
	}

	public static float ComputePathsInitialSimilarity(RustNavMeshPath pathA, RustNavMeshPath pathB)
	{
		using (TimeWarning.New("ComputePathDistance"))
		{
			int num = Mathf.Min(pathA.corners.Count, pathB.corners.Count);
			float num2 = 0f;
			for (int i = 0; i + 1 < num && !(pathA.corners[i] != pathB.corners[i]) && !(pathA.corners[i + 1] != pathB.corners[i + 1]); i++)
			{
				num2 += NavVector3.Distance(pathA.corners[i], pathA.corners[i + 1]);
			}
			return num2 / Mathf.Min(pathA.GetPathLength(), pathB.GetPathLength());
		}
	}
}

