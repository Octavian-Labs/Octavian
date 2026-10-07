using System;
using ConVar;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_ThrowGrenade : FSMStateBase
{
	public GameObjectRef deployedGrenadePrefab;

	public float cooldown = 60f;

	public const float grenadeRadius = 0.2f;

	public const float explosionRadius = 6f;

	public const string thrownGrenadeKey = "ThrownGrenadeRecently";

	private const float duration = 1f;

	private const float overshoot = 0.01f;

	private NpcShootingComponent _shooting;

	private NpcBarkComponent _barkComponent;

	private float remainingDuration;

	private NpcShootingComponent Shooting => _shooting ?? (_shooting = Owner.GetComponent<NpcShootingComponent>());

	private NpcBarkComponent BarkComponent => _barkComponent ?? (_barkComponent = Owner.GetComponent<NpcBarkComponent>());

	public static bool FindPotentialLandingPoint(SenseComponent Senses, out Vector3 landingPoint, out Vector3 throwVelocity)
	{
		landingPoint = Vector3.zero;
		throwVelocity = Vector3.zero;
		if (!Senses.FindTargetLKP(out var lkp))
		{
			return false;
		}
		Vector3 eyePosition = Senses.EyePosition;
		Vector3 vector = (lkp - eyePosition).NormalizeXZ();
		landingPoint = lkp + vector * 1f;
		Vector3 normalized = Vector3.Cross(Vector3.up, vector).normalized;
		Vector3 vector2 = vector + Quaternion.AngleAxis(10f, normalized) * Vector3.up;
		float throwVelocity2 = ThrownWeapon.GetThrowVelocity(eyePosition, landingPoint, vector2);
		if (float.IsNaN(throwVelocity2))
		{
			vector2 = vector + Quaternion.AngleAxis(20f, normalized) * Vector3.up;
			throwVelocity2 = ThrownWeapon.GetThrowVelocity(eyePosition, landingPoint, vector2);
			if (float.IsNaN(throwVelocity2))
			{
				return false;
			}
		}
		throwVelocity = vector2 * throwVelocity2;
		return true;
	}

	public static bool ValidateLandingPoint(BaseEntity querier, Vector3 origin, Vector3 destination, Vector3 initialVelocity, out RaycastHit hitInfo, int maxSegments = 5)
	{
		hitInfo = default(RaycastHit);
		float magnitude = initialVelocity.WithY(0f).magnitude;
		if (magnitude < 0.001f)
		{
			return false;
		}
		float num = (destination - origin).WithY(0f).magnitude / magnitude + 0.01f;
		Vector3 vector = origin;
		Vector3 vector2 = initialVelocity;
		float num2 = Mathf.Abs(UnityEngine.Physics.gravity.y);
		float num3 = num / (float)maxSegments;
		int num4 = Mathf.CeilToInt(num / num3);
		num3 = num / (float)num4;
		float num5 = 0f;
		for (int i = 0; i < num4; i++)
		{
			if (!(num5 < num))
			{
				break;
			}
			float num6 = Mathf.Min(num3, num - num5);
			Vector3 vector3 = new Vector3(0f, 0f - num2, 0f);
			Vector3 vector4 = vector + vector2 * num6 + 0.5f * vector3 * num6 * num6;
			Vector3 vector5 = vector2 + vector3 * num6;
			Vector3 vector6 = vector4 - vector;
			float magnitude2 = vector6.magnitude;
			if (magnitude2 > 0.001f && GamePhysics.Trace(new Ray(vector, vector6.normalized), 0.2f, out hitInfo, magnitude2, 1218519297, QueryTriggerInteraction.UseGlobal, querier))
			{
				if (Vector3.Distance(hitInfo.point, origin) <= 6f)
				{
					return false;
				}
				if (Vector3.Distance(hitInfo.point, destination) > 6f)
				{
					return false;
				}
				return true;
			}
			vector = vector4;
			vector2 = vector5;
			num5 += num6;
		}
		return false;
	}

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!payload.velocity.HasValue)
		{
			if (AI.logIssues)
			{
				Debug.LogError($"State_ThrowGrenade entered without valid velocity payload for {Owner}");
			}
			return EFSMStateStatus.Failure;
		}
		BaseEntity baseEntity = GameManager.server.CreateEntity(deployedGrenadePrefab.resourcePath, base.Senses.EyePosition, Quaternion.LookRotation(payload.velocity.Value));
		if (baseEntity == null)
		{
			return EFSMStateStatus.Failure;
		}
		baseEntity.SetCreatorEntity(Owner);
		baseEntity.SetVelocity(payload.velocity.Value);
		baseEntity.Spawn();
		remainingDuration = 1f;
		Shooting.AllowShooting = false;
		base.Blackboard.Add("ThrownGrenadeRecently", cooldown);
		using (PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>())
		{
			base.Senses.GetPerceivedAllies(pooledList);
			foreach (BaseEntity item in pooledList)
			{
				item.GetComponent<BlackboardComponent>().Add("ThrownGrenadeRecently", cooldown);
			}
		}
		return base.OnStateEnter(payload);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		remainingDuration -= deltaTime;
		if (remainingDuration <= 0f)
		{
			return EFSMStateStatus.Success;
		}
		return base.OnStateUpdate(deltaTime);
	}

	public override void OnStateExit()
	{
		Shooting.AllowShooting = true;
		base.OnStateExit();
	}
}

