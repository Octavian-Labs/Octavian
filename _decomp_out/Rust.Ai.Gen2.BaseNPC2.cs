using ConVar;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class BaseNPC2 : BaseCombatEntity
{
	public const Flags Mounted = Flags.Busy;

	private EntityRef<BaseEntity> mountHost;

	private int mountAnchorIndex = -1;

	private RustNavMeshAgent _mountAgent;

	private FSMComponent _mountFsm;

	private const float DismountNavmeshSampleRadius = 10f;

	[SerializeField]
	private float mass = 45f;

	private int safeZoneCheckedFrame = -1;

	private bool inSafeZone;

	public bool IsMounted => HasFlag(Flags.Busy);

	private RustNavMeshAgent MountAgent => _mountAgent ?? (_mountAgent = GetComponent<RustNavMeshAgent>());

	private FSMComponent MountFsm => _mountFsm ?? (_mountFsm = GetComponent<FSMComponent>());

	private bool HasMountReservation => mountHost.IsSet;

	public bool IsDismounting
	{
		get
		{
			if (MountFsm != null)
			{
				return MountFsm.IsDismounting;
			}
			return false;
		}
	}

	public int MountAnchorIndex => mountAnchorIndex;

	public NPCMountable Mount
	{
		get
		{
			BaseEntity baseEntity = mountHost.Get(serverside: true);
			if (!(baseEntity != null) || !baseEntity.TryGetComponent<NPCMountable>(out var component))
			{
				return null;
			}
			return component;
		}
	}

	public override bool IsNpc => true;

	public virtual bool IsAnimal => true;

	public virtual bool MovesOnNavmesh => true;

	public override float RealisticMass => mass;

	public string displayName
	{
		get
		{
			PrefabInformation prefabInformation = null;
			if (base.isServer)
			{
				prefabInformation = PrefabAttribute.server.Find<PrefabInformation>(prefabID);
			}
			if (prefabInformation == null)
			{
				if (AI.logIssues)
				{
					Debug.LogError("PrefabInformation not found for " + Categorize() + ")");
				}
				return "NPC";
			}
			return prefabInformation.title.english;
		}
	}

	public bool StartMountTo(NPCMountable mountable, int index)
	{
		if (mountable == null || IsDead() || IsMounted)
		{
			return false;
		}
		if (MountFsm == null || !MountFsm.SupportsMounting)
		{
			return false;
		}
		mountHost.Set(mountable.GetBaseEntity());
		mountAnchorIndex = index;
		if (MountFsm.StartMount())
		{
			return true;
		}
		mountHost = default(EntityRef<BaseEntity>);
		mountAnchorIndex = -1;
		return false;
	}

	public void ResumeMountedState()
	{
		MountFsm?.ResumeMounted();
	}

	public bool StartDismount()
	{
		if (IsMounted && MountFsm != null)
		{
			return MountFsm.StartDismount();
		}
		return false;
	}

	public void CancelMount()
	{
		if (!IsMounted)
		{
			Mount?.ReleaseAnchor(this);
			mountHost = default(EntityRef<BaseEntity>);
			mountAnchorIndex = -1;
		}
	}

	public void OnMountFinalized(BaseEntity host, int index)
	{
		if (!(host == null) && host.TryGetComponent<NPCMountable>(out var component))
		{
			mountHost.Set(host);
			mountAnchorIndex = index;
			SetParent(host, worldPositionStays: true);
			Transform anchorTransform = component.GetAnchorTransform(index);
			if (anchorTransform != null)
			{
				base.transform.localPosition = host.transform.InverseTransformPoint(anchorTransform.position);
				base.transform.localRotation = Quaternion.Inverse(host.transform.rotation) * anchorTransform.rotation;
			}
			MountAgent?.Pause(this);
			SetMountedFlag(mounted: true);
		}
	}

	private void SetMountedFlag(bool mounted)
	{
		using FlagsUpdateScope flagsUpdateScope = StartSetFlags(FlagsUpdateMode.SendNetworkUpdate);
		flagsUpdateScope.Set(Flags.Busy, mounted);
	}

	public void DetachFromHost()
	{
		if (HasParent())
		{
			SetParent(null, worldPositionStays: true);
		}
	}

	public bool TrySampleNavmesh(Vector3 position, float radius, out Vector3 onNavmesh)
	{
		onNavmesh = default(Vector3);
		if (MountAgent == null || !MountAgent.SamplePosition(position, out var hitWS, radius, debugDraw: false))
		{
			return false;
		}
		onNavmesh = hitWS.position;
		return true;
	}

	public void OnDismountFinalized(Vector3 position, Quaternion rotation)
	{
		NPCMountable mount = Mount;
		Vector3 onNavmesh;
		bool flag = TrySampleNavmesh(position, 10f, out onNavmesh);
		if (flag)
		{
			position = onNavmesh;
		}
		DetachFromHost();
		base.transform.SetPositionAndRotation(position, rotation);
		mountHost = default(EntityRef<BaseEntity>);
		mountAnchorIndex = -1;
		MountAgent?.Unpause(this);
		mount?.ReleaseAnchor(this);
		SetMountedFlag(mounted: false);
		if (!flag)
		{
			DieOffNavmesh();
		}
	}

	public void DieOffNavmesh()
	{
		HitInfo hitInfo = new HitInfo();
		hitInfo.damageTypes.Set(DamageType.Generic, MaxHealth());
		Die(hitInfo);
	}

	public void HandOverMountTo(BaseNPC2 next)
	{
		NPCMountable mount = Mount;
		if (!(mount == null) && !(next == null))
		{
			int index = mountAnchorIndex;
			if (mount.HandOverAnchor(this, next))
			{
				mountHost = default(EntityRef<BaseEntity>);
				mountAnchorIndex = -1;
				SetMountedFlag(mounted: false);
				MountAgent?.Unpause(this);
				next.OnMountFinalized(mount.GetBaseEntity(), index);
				next.ResumeMountedState();
			}
		}
	}

	public override void PostServerLoad()
	{
		base.PostServerLoad();
		if (!HasMountReservation)
		{
			BaseEntity baseEntity = GetParentEntity();
			if (baseEntity == null || !baseEntity.TryGetComponent<NPCMountable>(out var component) || !component.TryReclaimAnchor(this, out var index))
			{
				SetMountedFlag(mounted: false);
				return;
			}
			mountHost.Set(baseEntity);
			mountAnchorIndex = index;
			SetMountedFlag(mounted: true);
			MountAgent?.Pause(this);
			ResumeMountedState();
		}
	}

	private void ReleaseMountOnDestroy()
	{
		Mount?.ReleaseAnchor(this);
		mountHost = default(EntityRef<BaseEntity>);
		mountAnchorIndex = -1;
	}

	public override float AntiHackVelocity()
	{
		return 10f;
	}

	public override float AntiHackPadding()
	{
		return 2f;
	}

	public override void InitShared()
	{
		base.InitShared();
		if (base.isServer)
		{
			startHealth *= AI.npcHealthMultiplier;
			startHealth = Mathf.Max(1f, startHealth);
			HasBrain = true;
			Query.Server.AddBrain(this);
		}
	}

	public override void Hurt(HitInfo info)
	{
		if (TryGetComponent<SenseComponent>(out var component))
		{
			component.NoteAttacker(info.Initiator);
		}
		base.Hurt(info);
	}

	public override void OnAttacked(HitInfo info)
	{
		base.OnAttacked(info);
		if (base.isServer && (bool)info.InitiatorPlayer && !info.damageTypes.IsMeleeType())
		{
			info.InitiatorPlayer.LifeStoryShotHit(info.Weapon);
		}
	}

	public override bool InSafeZone()
	{
		if (safeZoneCheckedFrame != UnityEngine.Time.frameCount)
		{
			safeZoneCheckedFrame = UnityEngine.Time.frameCount;
			inSafeZone = TriggerSafeZone.IsBoundsInsideSafeZone(WorldSpaceBounds());
		}
		return inSafeZone;
	}

	public override void ApplyInheritedVelocity(Vector3 velocity)
	{
		if (!(velocity.sqrMagnitude < 0.0001f) && !IsDead() && !HasMountReservation && !IsMounted && !IsDismounting)
		{
			RustNavMeshAgent mountAgent = MountAgent;
			if (!(mountAgent == null) && !mountAgent.IsPaused)
			{
				Vector3 position = base.transform.position;
				mountAgent.Move(mountAgent.WorldToNavSpace(position + velocity * UnityEngine.Time.fixedDeltaTime) - mountAgent.WorldToNavSpace(position));
			}
		}
	}

	public override void DestroyShared()
	{
		base.DestroyShared();
		if (base.isServer && !Application.isQuitting)
		{
			ReleaseMountOnDestroy();
			Query.Server.RemoveBrain(this);
		}
	}
}

