using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_DismountVehicle : FSMStateBase
{
	[Tooltip("Optional montage for climbing down. Without one the animal is placed on the dismount point, which is what the CH47 has always done.")]
	[SerializeField]
	public RootMotionData Animation;

	[Tooltip("The slice of the montage that carries the animal down to the ground. Leave zeroed to warp the whole clip.")]
	[SerializeField]
	public MountMontageWindow travelWindow = MountMontageWindow.Whole;

	private RootMotionPlayer.PlayServerState animState;

	private readonly RootMotionPlayer.Warp[] warps = new RootMotionPlayer.Warp[1];

	private Vector3 destination;

	private Quaternion destinationRotation;

	private bool hasDestination;

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
		animState = null;
		hasDestination = false;
		NPCMountable nPCMountable = ((Npc != null) ? Npc.Mount : null);
		if (nPCMountable == null)
		{
			return EFSMStateStatus.Failure;
		}
		if (!nPCMountable.TryGetDismountPosition(Npc, out destination, out destinationRotation))
		{
			return EFSMStateStatus.Failure;
		}
		hasDestination = true;
		if (!HasAnimation)
		{
			Npc.OnDismountFinalized(destination, destinationRotation);
			return EFSMStateStatus.Success;
		}
		Vector3 position = Owner.transform.position;
		Npc.DetachFromHost();
		animState = RootMotionPlayer.PlayServerState.TakeFromPool(Animation, Owner.transform);
		animState.constrainToNavmesh = false;
		warps[0] = NPCMountMontage.BuildWarp(Animation, travelWindow, animState.initialRotation, position, destination, destinationRotation, animState.playbackSpeed);
		animState.warps = warps;
		base.AnimPlayer.PlayServer(animState);
		return base.OnStateEnter(payload);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (animState != null && animState.isPlaying)
		{
			return EFSMStateStatus.None;
		}
		PutDown();
		return EFSMStateStatus.Success;
	}

	public override void OnStateExit()
	{
		base.AnimPlayer.StopServerAndReturnToPool(ref animState);
		PutDown();
		base.OnStateExit();
	}

	private void PutDown()
	{
		if (hasDestination && Npc != null && Npc.IsMounted)
		{
			Npc.OnDismountFinalized(destination, destinationRotation);
		}
	}
}

