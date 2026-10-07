using System;
using ConVar;
using UnityEngine;

namespace Rust.Ai.Gen2;

[SoftRequireComponent(typeof(SenseComponent))]
public class NpcShootingComponent : EntityComponent<BaseEntity>
{
	[Header("Weapon Stats")]
	[SerializeField]
	public ItemDefinition weaponItemDefinition;

	[SerializeField]
	private Vector3 offset = new Vector3(0.25f, 1.4f, 0.71f);

	[SerializeField]
	private float damageModifier = 1f;

	private const float spreadWhenAccurate = 0.1f;

	private AttackEntity weapon;

	private SenseComponent _senses;

	private RustNavMeshAgent _agent;

	private double burstEndTime;

	private double nextBurstBeginTime;

	private bool toggleAttachmentLightAtNight;

	private SenseComponent Senses => _senses ?? (_senses = GetComponent<SenseComponent>());

	private RustNavMeshAgent Agent => _agent ?? (_agent = GetComponent<RustNavMeshAgent>());

	private Vector3 EyePosition => Senses.EyePosition;

	public bool AllowShooting { get; set; } = true;

	public bool AllowBeingAccurate { get; set; } = true;

	public bool OnlyShootIfTargetIsVisible { get; set; } = true;

	public override void ServerInitPostNetworkGroupAssign()
	{
		base.ServerInitPostNetworkGroupAssign();
		Item item = ItemManager.Create(weaponItemDefinition, 1, 0uL, isServerSide: true, 0uL);
		HeldEntity component = item.GetHeldEntity().GetComponent<HeldEntity>();
		weapon = component as AttackEntity;
		weapon.limitNetworking = false;
		weapon.SetHeld(bHeld: true);
		weapon.SetParent(base.baseEntity, StringPool.Get(weapon.handBone));
		weapon.TopUpAmmo();
		if (weapon.Is<BaseProjectile>(out var _) && item.contents != null)
		{
			Item item2 = ItemManager.CreateByName((toggleAttachmentLightAtNight = UnityEngine.Random.Range(0, 3) == 0) ? "weapon.mod.flashlight" : "weapon.mod.lasersight", 1, 0uL);
			if (!item2.MoveToContainer(item.contents))
			{
				item2.Remove();
			}
			else if (!toggleAttachmentLightAtNight)
			{
				weapon.SetLightsOn(isOn: true);
			}
		}
		weapon.EnableSaving(base.baseEntity.enableSaving);
		foreach (BaseEntity child in weapon.children)
		{
			child.EnableSaving(base.baseEntity.enableSaving);
		}
	}

	public override void DestroyShared()
	{
		base.DestroyShared();
		if (weapon != null && !weapon.IsDestroyed)
		{
			if (AI.logIssues && weapon.GetParentEntity() != base.baseEntity)
			{
				Debug.LogError($"Weapon {weapon} of {base.baseEntity} was not parented to the entity.", weapon);
			}
			weapon.Kill();
			weapon = null;
		}
	}

	private void Reset()
	{
		burstEndTime = 0.0;
		nextBurstBeginTime = 0.0;
	}

	private void Update()
	{
		if (!base.baseEntity.isServer)
		{
			return;
		}
		if (weapon.GetParentEntity() != base.baseEntity && AI.logIssues)
		{
			Debug.LogError($"Weapon {weapon} of {base.baseEntity} was not parented to the entity.", weapon);
		}
		if ((base.baseEntity.Is<BaseCombatEntity>(out var castedUnityObject) && castedUnityObject.IsDead()) || IsReloading())
		{
			return;
		}
		if (toggleAttachmentLightAtNight)
		{
			if (TOD_Sky.Instance.IsNight && !weapon.LightsOn())
			{
				weapon.SetLightsOn(isOn: true);
			}
			else if (TOD_Sky.Instance.IsDay && weapon.LightsOn())
			{
				weapon.SetLightsOn(isOn: false);
			}
		}
		if (!Senses.FindTarget(out var target))
		{
			Reset();
			if (ShouldReload(weapon, 0.5f))
			{
				Reload();
			}
		}
		else
		{
			if (!Senses.GetVisibilityStatus(target, out var status))
			{
				return;
			}
			if (ShouldReload(weapon, 0.5f) && !status.IsAware && status.timeNotVisible > 6f)
			{
				Reload();
			}
			else
			{
				if (!AllowShooting || Agent.IsSprinting || weapon.HasAttackCooldown())
				{
					return;
				}
				double timeAsDouble = UnityEngine.Time.timeAsDouble;
				if ((timeAsDouble > burstEndTime && timeAsDouble < nextBurstBeginTime) || !Senses.FindLKP(target, out var lkp))
				{
					return;
				}
				bool flag = status.IsVisible && status.IsAware;
				bool flag2 = !OnlyShootIfTargetIsVisible && status.timeNotVisible <= 5f;
				if ((!flag && !flag2) || Vector3.Angle(base.baseEntity.transform.forward, (lkp - base.baseEntity.transform.position).WithY(0f)) > 5f)
				{
					return;
				}
				Vector3 entityPointToShootAt = GetEntityPointToShootAt(target, lkp);
				Vector3 vector = entityPointToShootAt;
				float num = Mathx.RemapValClamped(Vector3.Distance(base.baseEntity.transform.position, lkp), 0f, weapon.effectiveRange, 0f, 1f);
				bool flag3 = false;
				if (flag)
				{
					float num2 = 0.1f;
					float num3 = 0.1f;
					flag3 = CheckIfShouldMiss(target, num, lkp);
					if (flag3)
					{
						Vector3 extents = target.bounds.extents;
						num2 += extents.x;
						num3 += extents.y;
					}
					vector += CalculateSpreadOffset(entityPointToShootAt, num2, num3);
				}
				else
				{
					vector += CalculateSpreadOffset(entityPointToShootAt);
				}
				Vector3 muzzleEstimatedPositionOnServer = GetMuzzleEstimatedPositionOnServer(lkp);
				if (!flag3 && !CanShootFromAt(muzzleEstimatedPositionOnServer, vector))
				{
					if (!Senses.FindLKP(target, out var lkp2, applyHeightOffset: true, predict: false, ignoreCrouch: false))
					{
						return;
					}
					vector = lkp2;
				}
				Matrix4x4 value = Matrix4x4.TRS(muzzleEstimatedPositionOnServer, Quaternion.LookRotation(vector - muzzleEstimatedPositionOnServer), Vector3.one);
				weapon.ServerUse(new HeldEntityServerUseParams(damageModifier, 1f, value, useBulletThickness: false, useProtectionForNPCs: true));
				base.baseEntity.ClientRPC(RpcTarget.NetworkGroup("CL_Attack"));
				if (status.IsAware && status.IsVisible)
				{
					SingletonComponent<NpcNoiseManager>.Instance.OnNpcWeaponShot(base.baseEntity, target, vector);
				}
				if (ShouldReload(weapon))
				{
					Reset();
					Reload();
				}
				if (timeAsDouble >= nextBurstBeginTime)
				{
					if (num < 0.5f)
					{
						burstEndTime = timeAsDouble + (double)((float)UnityEngine.Random.Range(3, 10) * weapon.repeatDelay);
						nextBurstBeginTime = burstEndTime + (double)UnityEngine.Random.Range(0.3f, 0.4f);
					}
					else if (num < 1f)
					{
						burstEndTime = timeAsDouble + (double)((float)UnityEngine.Random.Range(3, 6) * weapon.repeatDelay);
						nextBurstBeginTime = burstEndTime + (double)UnityEngine.Random.Range(0.5f, 1.2f);
					}
					else
					{
						burstEndTime = timeAsDouble + (double)weapon.repeatDelay;
						nextBurstBeginTime = burstEndTime + (double)UnityEngine.Random.Range(0.5f, 1.5f);
					}
				}
			}
		}
	}

	private bool CheckIfShouldMiss(BaseEntity target, float distanceRatio, Vector3 groundLkp)
	{
		if (!AllowBeingAccurate)
		{
			return false;
		}
		target.ToNonNpcPlayer(out var player);
		float num = ((player != null && ((player.IsRunning() && Vector3.Angle(player.estimatedVelocity, base.baseEntity.transform.position - groundLkp) < 30f) || player.estimatedSpeed < 1f)) ? 1f : ((player != null && player.IsRunning()) ? 0.5f : ((distanceRatio < 0.5f) ? 1f : ((!(distanceRatio < 1f)) ? 0.5f : 0.75f))));
		return UnityEngine.Random.value > num;
	}

	public bool CanShootFromAt(Vector3 potentialLocation, Vector3 targetLocation, string debugCategory = "shoot trace")
	{
		return !Senses.IsLineOccluded(potentialLocation, targetLocation, 1218519297, debugCategory);
	}

	public bool IsReloading()
	{
		if (weapon == null)
		{
			return false;
		}
		return weapon.ServerIsReloading();
	}

	private static bool ShouldReload(AttackEntity weapon, float ammoThresholdModifier = 0f)
	{
		if (weapon == null)
		{
			return false;
		}
		int ammoCount = GetAmmoCount(weapon);
		if (Mathf.Approximately(ammoThresholdModifier, 0f))
		{
			return ammoCount <= 0;
		}
		int num = Mathf.FloorToInt((float)GetMagazineSize(weapon) * ammoThresholdModifier);
		return ammoCount <= num;
	}

	private static int GetAmmoCount(AttackEntity weapon)
	{
		if (weapon == null)
		{
			return 0;
		}
		if (weapon.Is<BaseProjectile>(out var castedUnityObject))
		{
			return castedUnityObject.primaryMagazine.contents;
		}
		if (weapon.Is<FlameThrower>(out var castedUnityObject2))
		{
			return castedUnityObject2.ammo;
		}
		return 0;
	}

	private static int GetMagazineSize(AttackEntity weapon)
	{
		if (weapon == null)
		{
			return 0;
		}
		if (weapon.Is<BaseProjectile>(out var castedUnityObject))
		{
			return castedUnityObject.primaryMagazine.definition.builtInSize;
		}
		if (weapon.Is<FlameThrower>(out var castedUnityObject2))
		{
			return castedUnityObject2.maxAmmo;
		}
		return 0;
	}

	private void Reload()
	{
		weapon.ServerReload();
		base.baseEntity.ClientRPC(RpcTarget.NetworkGroup("CL_Reload"));
	}

	private Vector3 CalculateSpreadOffset(Vector3 targetPos, float spreadX = 0.1f, float spreadY = 0.1f)
	{
		Vector3 normalized = (targetPos - EyePosition).normalized;
		Vector3 rhs = Vector3.up;
		if (Mathf.Abs(Vector3.Dot(normalized, Vector3.up)) > 0.99f)
		{
			rhs = Vector3.right;
		}
		Vector3 normalized2 = Vector3.Cross(normalized, rhs).normalized;
		Vector3 normalized3 = Vector3.Cross(normalized2, normalized).normalized;
		float f = UnityEngine.Random.Range(0f, MathF.PI * 2f);
		return normalized2 * Mathf.Cos(f) * spreadX + normalized3 * Mathf.Sin(f) * spreadY;
	}

	public static Vector3 GetEntityPointToShootAt(BaseEntity entity, Vector3 entityGroundPos)
	{
		return entityGroundPos + entity.bounds.extents.y * Vector3.up;
	}

	public Vector3 GetMuzzleEstimatedPositionOnServer(Vector3 targetGroundPos, bool noZ = false)
	{
		Quaternion quaternion = Quaternion.LookRotation(targetGroundPos - base.baseEntity.transform.position);
		if (noZ)
		{
			return base.baseEntity.transform.TransformPoint(offset.WithZ(0f));
		}
		Quaternion quaternion2 = Quaternion.Inverse(base.baseEntity.transform.rotation) * quaternion;
		return base.baseEntity.transform.TransformPoint(quaternion2 * offset.WithXY(0f, 0f) + offset.WithZ(0f));
	}
}

