using System;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_ScientistSurprised : FSMStateBase
{
	public float angularSpeedOverride;

	public float timeBeforeTurning;

	public float minTimeBeforeShooting = 0.6f;

	public float shootingDuration = 0.4f;

	public float maxDuration = 1f;

	public ENPCVoicelineCategory voicelineCategory = ENPCVoicelineCategory.Surprise;

	private NpcShootingComponent _shooting;

	private NpcBarkComponent _barkComponent;

	private float elapsedTime;

	private float elapsedTimeShooting;

	private Quaternion startRotation;

	private bool wasSurprisedFromBehind;

	private float previousAngularSpeed;

	private NpcShootingComponent Shooting => _shooting ?? (_shooting = Owner.GetComponent<NpcShootingComponent>());

	private NpcBarkComponent BarkComponent => _barkComponent ?? (_barkComponent = Owner.GetComponent<NpcBarkComponent>());

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindTargetLKP(out var lkp))
		{
			return EFSMStateStatus.Failure;
		}
		startRotation = Owner.transform.rotation;
		base.Agent.overrideDirectionWS = (startRotation * Vector3.forward).NormalizeXZ();
		base.Agent.Pause(this);
		Shooting.AllowShooting = false;
		Shooting.AllowBeingAccurate = false;
		BarkComponent.PlayVoicelineFromCategory(voicelineCategory);
		elapsedTime = 0f;
		elapsedTimeShooting = 0f;
		if (angularSpeedOverride > 0f)
		{
			previousAngularSpeed = base.Agent.angularSpeed;
			base.Agent.angularSpeed = angularSpeedOverride;
		}
		wasSurprisedFromBehind = Vector3.Angle(Owner.transform.forward, (lkp - Owner.transform.position).WithY(0f)) > 60f;
		return base.OnStateEnter(payload);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (!base.Senses.FindTargetLKP(out var lkp, applyHeightOffset: true, predict: false, ignoreCrouch: false))
		{
			return EFSMStateStatus.Failure;
		}
		EFSMStateStatus result = base.OnStateUpdate(deltaTime);
		elapsedTime += deltaTime;
		if (!wasSurprisedFromBehind || !(elapsedTime < timeBeforeTurning))
		{
			Vector3 vector = lkp - base.Senses.EyePosition;
			base.Agent.overrideDirectionWS = vector;
			NavVector3 navVector = base.Agent.WorldToNavDirection(vector);
			base.Agent.Move(navVector.NormalizeXZ() * ((0f - deltaTime) * 1.7f));
		}
		if (elapsedTime >= minTimeBeforeShooting && Vector3.Angle(Owner.transform.forward, (lkp - Owner.transform.position).WithY(0f)) <= 5f)
		{
			Shooting.AllowShooting = true;
		}
		if (Shooting.AllowShooting)
		{
			elapsedTimeShooting += deltaTime;
			if (elapsedTimeShooting >= shootingDuration)
			{
				return EFSMStateStatus.Success;
			}
		}
		if (elapsedTime > maxDuration)
		{
			return EFSMStateStatus.Success;
		}
		return result;
	}

	public override void OnStateExit()
	{
		if (angularSpeedOverride > 0f)
		{
			base.Agent.angularSpeed = previousAngularSpeed;
		}
		base.Agent.overrideDirectionWS = null;
		base.Agent.Unpause(this);
		Shooting.AllowShooting = true;
		Shooting.AllowBeingAccurate = true;
		base.OnStateExit();
	}
}

