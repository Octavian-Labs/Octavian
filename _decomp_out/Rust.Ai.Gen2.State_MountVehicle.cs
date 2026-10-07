using System;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_MountVehicle : State_MoveToTarget
{
	[Tooltip("Optional montage for climbing aboard. Without one the animal is placed on the anchor when it arrives.")]
	[SerializeField]
	public RootMotionData Animation;

	[Tooltip("The slice of the montage that carries the animal up onto the anchor. Leave zeroed to warp the whole clip.")]
	[SerializeField]
	public MountMontageWindow travelWindow = MountMontageWindow.Whole;

	private const float navmeshSampleRadius = 5f;

	private RootMotionPlayer.PlayServerState animState;

	private readonly RootMotionPlayer.Warp[] warps = new RootMotionPlayer.Warp[1];

	private bool isClimbing;

	public bool HasAnimation
	{
		get
		{
			if (Animation != null)
			{
				return Animation.inPlaceAnimation != null;
			}
			return false;
		}
	}

	private BaseNPC2 Npc => Owner as BaseNPC2;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		isClimbing = false;
		animState = null;
		if (Npc == null || Npc.Mount == null)
		{
			return EFSMStateStatus.Failure;
		}
		return base.OnStateEnter(payload);
	}

	protected override bool GetMoveDestination(out NavVector3 destination)
	{
		destination = default(NavVector3);
		NPCMountable nPCMountable = ((Npc != null) ? Npc.Mount : null);
		if (nPCMountable == null)
		{
			return false;
		}
		Vector3 approachPosition = nPCMountable.GetApproachPosition(Npc.MountAnchorIndex);
		if (!base.Agent.SamplePosition(approachPosition, out var hitWS, 5f))
		{
			return false;
		}
		destination = base.Agent.WorldToNavSpace(hitWS.position);
		return true;
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (Npc.Mount == null || !Npc.Mount.IsHostRideable)
		{
			return EFSMStateStatus.Failure;
		}
		if (!isClimbing)
		{
			return base.OnStateUpdate(deltaTime) switch
			{
				EFSMStateStatus.Failure => EFSMStateStatus.Failure, 
				EFSMStateStatus.Success => BeginClimb(), 
				_ => EFSMStateStatus.None, 
			};
		}
		if (animState == null || !animState.isPlaying)
		{
			return Finish();
		}
		return EFSMStateStatus.None;
	}

	private EFSMStateStatus BeginClimb()
	{
		isClimbing = true;
		NPCMountable nPCMountable = ((Npc != null) ? Npc.Mount : null);
		Transform transform = ((nPCMountable != null) ? nPCMountable.GetAnchorTransform(Npc.MountAnchorIndex) : null);
		if (transform == null)
		{
			return EFSMStateStatus.Failure;
		}
		if (!HasAnimation)
		{
			return Finish();
		}
		base.Agent.ResetPath();
		base.Agent.Pause(this);
		base.Agent.IsJumping = true;
		Vector3 position = Owner.transform.position;
		Vector3 forward = (transform.position - position).NormalizeXZ();
		if (forward.sqrMagnitude > 0.001f)
		{
			Owner.transform.rotation = Quaternion.LookRotation(forward);
		}
		animState = RootMotionPlayer.PlayServerState.TakeFromPool(Animation, Owner.transform);
		animState.constrainToNavmesh = false;
		warps[0] = NPCMountMontage.BuildWarp(Animation, travelWindow, animState.initialRotation, position, transform.position, transform.rotation, animState.playbackSpeed);
		animState.warps = warps;
		base.AnimPlayer.PlayServer(animState);
		return EFSMStateStatus.None;
	}

	private EFSMStateStatus Finish()
	{
		NPCMountable nPCMountable = ((Npc != null) ? Npc.Mount : null);
		if (nPCMountable == null)
		{
			return EFSMStateStatus.Failure;
		}
		Npc.OnMountFinalized(nPCMountable.GetBaseEntity(), Npc.MountAnchorIndex);
		return EFSMStateStatus.Success;
	}

	public override void OnStateExit()
	{
		bool num = animState != null;
		base.AnimPlayer.StopServerAndReturnToPool(ref animState);
		if (num)
		{
			base.Agent.IsJumping = false;
			base.Agent.Unpause(this);
		}
		if (Npc != null && !Npc.IsMounted)
		{
			Npc.CancelMount();
		}
		base.OnStateExit();
	}
}

