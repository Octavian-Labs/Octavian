using System;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_CircleDynamic : FSMStateBase
{
	[SerializeField]
	private RustNavMeshAgent.Speeds minSpeed;

	[SerializeField]
	private RustNavMeshAgent.Speeds maxSpeed = RustNavMeshAgent.Speeds.Sprint;

	[SerializeField]
	protected Vector2 distanceSpeedRange = new Vector2(10f, 50f);

	[SerializeField]
	private Vector2 angleRange = new Vector3(20f, 80f);

	[SerializeField]
	private Vector2 angleDurationRange = new Vector2(1f, 3f);

	[SerializeField]
	private Vector2 burstDurationRange = new Vector2(1f, 3f);

	[SerializeField]
	private Vector2 burstCooldownRange = new Vector2(1f, 10f);

	private Action _updateBurstAction;

	private Action _endBurstAction;

	private Action _updateAngleAction;

	private bool clockWise = true;

	private int burstSpeedIndexOffset;

	private float randomAngle;

	private Action UpdateBurstAction => UpdateBurst;

	private Action EndBurstAction => EndBurst;

	private Action UpdateAngleAction => UpdateAngle;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (payload.entity != null)
		{
			base.Senses.TrySetTarget(payload.entity);
		}
		clockWise = UnityEngine.Random.value > 0.5f;
		EndBurst();
		UpdateAngle();
		return base.OnStateEnter(payload);
	}

	public override void OnStateExit()
	{
		base.Agent.ResetPath();
		Owner.CancelInvoke(UpdateBurstAction);
		Owner.CancelInvoke(EndBurstAction);
		Owner.CancelInvoke(UpdateAngleAction);
		base.OnStateExit();
	}

	private void UpdateAngle()
	{
		randomAngle = UnityEngine.Random.Range(angleRange.x, angleRange.y) * (float)(clockWise ? 1 : (-1));
		Owner.Invoke(UpdateAngleAction, UnityEngine.Random.Range(angleDurationRange.x, angleDurationRange.y));
	}

	private void UpdateBurst()
	{
		burstSpeedIndexOffset = 2;
		clockWise = UnityEngine.Random.value > 0.5f;
		float time = UnityEngine.Random.Range(burstDurationRange.x, burstDurationRange.y);
		Owner.Invoke(EndBurstAction, time);
	}

	private void EndBurst()
	{
		burstSpeedIndexOffset = 0;
		float time = UnityEngine.Random.Range(burstCooldownRange.x, burstCooldownRange.y);
		Owner.Invoke(UpdateBurstAction, time);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (!base.Senses.FindTarget(out var target))
		{
			return EFSMStateStatus.Failure;
		}
		Vector3 position = target.transform.position;
		float num = Vector3.Distance(Owner.transform.position, position);
		float normalizedDist = Mathf.InverseLerp(distanceSpeedRange.x, distanceSpeedRange.y, num);
		SetSpeed(target, num, normalizedDist);
		float value = Mathx.RemapValClamped(num, distanceSpeedRange.x, distanceSpeedRange.y, randomAngle, 0f);
		Vector3 positionWS = position;
		RustNavMeshAgent agent = base.Agent;
		NavVector3 targetPositionNS = base.Agent.WorldToNavSpace(positionWS);
		float? deviation = value;
		if (!agent.SetDestinationWithParams(targetPositionNS, autoBraking: true, null, null, null, deviation, null, null))
		{
			return EFSMStateStatus.Failure;
		}
		return base.OnStateUpdate(deltaTime);
	}

	protected virtual void SetSpeed(BaseEntity target, float distToTarget, float normalizedDist)
	{
		base.Agent.SetSpeedRatio(normalizedDist, minSpeed, maxSpeed, burstSpeedIndexOffset);
	}
}

