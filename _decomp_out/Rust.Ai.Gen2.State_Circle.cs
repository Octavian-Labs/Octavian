using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_Circle : FSMStateBase
{
	[SerializeField]
	public float radius = 16f;

	[SerializeField]
	public RustNavMeshAgent.Speeds speed = RustNavMeshAgent.Speeds.Sprint;

	private bool clockWise = true;

	private float radiusOffset;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (payload.entity != null)
		{
			base.Senses.TrySetTarget(payload.entity);
		}
		radiusOffset = UnityEngine.Random.Range(-1f, 1f);
		clockWise = UnityEngine.Random.value > 0.5f;
		return base.OnStateEnter(payload);
	}

	public override void OnStateExit()
	{
		base.Agent.ResetPath();
		base.OnStateExit();
	}

	protected virtual bool GetCircleOrigin(out Vector3 origin)
	{
		return base.Senses.FindTargetPosition(out origin);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (!GetCircleOrigin(out var origin))
		{
			return EFSMStateStatus.Failure;
		}
		float num = radius + radiusOffset;
		float f = (Quaternion.LookRotation(Owner.transform.position - origin).eulerAngles.y + 5f * (float)(clockWise ? 1 : (-1))) * (MathF.PI / 180f);
		Vector3 vector = origin + new Vector3(Mathf.Sin(f), 0f, Mathf.Cos(f)) * num;
		vector.y = Mathf.Lerp(origin.y, Owner.transform.position.y, Mathf.InverseLerp(0f, Vector3.Distance(origin, Owner.transform.position), num));
		if (base.Agent.Raycast(vector, out var _))
		{
			return EFSMStateStatus.Failure;
		}
		if (!base.Agent.SetDestinationWithParams(base.Agent.WorldToNavSpace(vector), autoBraking: false, speed, null, null, null, null, null))
		{
			return EFSMStateStatus.Failure;
		}
		return base.OnStateUpdate(deltaTime);
	}
}

