#define UNITY_ASSERTIONS
using System;
using System.Collections.Generic;
using System.Linq;
using ConVar;
using Facepunch;
using Facepunch.Rust;
using Network;
using ProtoBuf;
using Rust;
using Rust.Ai.Gen2;
using UnityEngine;
using UnityEngine.Assertions;

public class BaseCombatEntity : BaseEntity
{
	[Serializable]
	public struct Pickup
	{
		public bool enabled;

		[ItemSelector]
		public ItemDefinition itemTarget;

		public int itemCount;

		[Tooltip("Should we set the condition of the item based on the health of the picked up entity")]
		public bool setConditionFromHealth;

		[Tooltip("How much to reduce the item condition when picking up")]
		public float subtractCondition;

		[Tooltip("Must have building access to pick up")]
		public bool requireBuildingPrivilege;

		[Tooltip("Must have hammer equipped to pick up")]
		public bool requireHammer;

		[Tooltip("Inventory Must be empty (if applicable) to be picked up")]
		public bool requireEmptyInv;

		[Tooltip("If set, pickup will take this long in seconds")]
		public float overridePickupTime;
	}

	public static class PickupErrors
	{
		public static readonly Translate.Phrase ItemMustBeEmpty = new Translate.Phrase("pickuperror_itemmustbeempty", "{0} must be empty");

		public static readonly Translate.Phrase ItemInventoryMustBeEmpty = new Translate.Phrase("pickuperror_iteminventorymustbeempty", "{0} inventory must be empty");

		public static readonly Translate.Phrase ItemIsBeingUsed = new Translate.Phrase("pickuperrors_itemisbeingused", "{0} is being used");

		public static readonly Translate.Phrase ItemHasCloser = new Translate.Phrase("pickuperrors_itemhascloser", "{0} has closer");

		public static readonly Translate.Phrase ItemHasLock = new Translate.Phrase("pickuperrors_itemhaslock", "{0} has lock");

		public static readonly Translate.Phrase ItemHasStorageAdaptor = new Translate.Phrase("pickuperrors_itemhasstorageadaptor", "{0} has storage adaptor");

		public static readonly Translate.Phrase ItemHasStorageMonitor = new Translate.Phrase("pickuperrors_itemhasstoragemonitor", "{0} has storage monitor");

		public static readonly Translate.Phrase ItemHasDecoration = new Translate.Phrase("pickuperrors_itemhasdecoration", "{0} has decoration");

		public static readonly Translate.Phrase ItemHasAttachment = new Translate.Phrase("pickuperrors_itemhasattachment", "{0} has attachment");

		public static readonly Translate.Phrase ItemIsOnline = new Translate.Phrase("pickuperrors_itemisonline", "{0} is online");

		public static readonly Translate.Phrase ItemIsArmed = new Translate.Phrase("pickuperrors_itemisarmed", "{0} is armed");

		public static readonly Translate.Phrase ItemIsOccupied = new Translate.Phrase("pickuperror_itemisoccupied", "{0} is occupied");
	}

	[Serializable]
	public struct Repair
	{
		public bool enabled;

		[ItemSelector]
		public ItemDefinition itemTarget;

		[ItemSelector]
		public ItemDefinition ignoreForRepair;

		public GameObjectRef repairEffect;

		public GameObjectRef repairFullEffect;

		public GameObjectRef repairFailedEffect;
	}

	public struct EntityBuildCost
	{
		public List<ItemAmount> Items;

		public int CraftAmount;

		public EntityBuildCost(List<ItemAmount> items, int craftedAmount = 1)
		{
			Items = items;
			CraftAmount = craftedAmount;
		}
	}

	public enum ActionVolume
	{
		Quiet,
		Normal,
		Loud
	}

	public struct BaseCombatEntityPreserveInfo
	{
		public float health;

		public float lastAttackedTime;

		public ItemOwnershipShare ownership;
	}

	public enum LifeState
	{
		Alive,
		Dead
	}

	public enum ShowHealthThreshold
	{
		MinorDamage = 5,
		ThreeQuarters = 25,
		Half = 50,
		Quarter = 75,
		TenPercent = 90
	}

	[Serializable]
	public enum Faction
	{
		Default,
		Player,
		Bandit,
		Scientist,
		Horror
	}

	[Header("Deployable Corpse")]
	[InspectorName("Spawn Corpse")]
	public bool spawnDeployableCorpseOnDeath;

	[InspectorName("Corpse Prefab")]
	public GameObjectRef deployableCorpsePrefab;

	protected (Translate.Phrase format, Translate.Phrase arg0) pickupErrorToFormat;

	[NonSerialized]
	public ItemOwnershipShare ItemOwnership;

	private const float MAX_HEALTH_REPAIR = 50f;

	public static readonly Translate.Phrase RecentlyDamagedError = new Translate.Phrase("error_recentlydamaged", "Recently damaged, repairable in {0} seconds");

	public static readonly Translate.Phrase NotDamagedError = new Translate.Phrase("error_notdamaged", "Not damaged");

	[NonSerialized]
	public DamageType lastDamage;

	[NonSerialized]
	public BaseEntity lastAttacker;

	[NonSerialized]
	public BaseEntity lastDealtDamageTo;

	[NonSerialized]
	public bool ResetLifeStateOnSpawn = true;

	protected DirectionProperties[] propDirection;

	protected float unHostileTime;

	private float lastNoiseTime;

	[Header("BaseCombatEntity")]
	public SkeletonProperties skeletonProperties;

	public ProtectionProperties baseProtection;

	public float startHealth;

	public Pickup pickup;

	public Repair repair;

	[Tooltip("[DEPRECATED] Should health info be shown on this entity, this is clientside only, and has been kept for backwards compatibility")]
	public bool ShowHealthInfo = true;

	[Tooltip("The health threshold at which health info will be shown")]
	public ShowHealthThreshold showHealthInfoThreshold = ShowHealthThreshold.ThreeQuarters;

	[ReadOnly]
	public LifeState lifestate;

	public bool sendsHitNotification;

	public bool sendsMeleeHitNotification = true;

	public bool markAttackerHostile = true;

	[NonSerialized]
	public float maxHealthOverride;

	protected float _health;

	protected float _maxHealth = 100f;

	public Faction faction;

	private float clientLastAttackedTime = float.NegativeInfinity;

	[NonSerialized]
	public float lastAttackedTime = float.NegativeInfinity;

	[NonSerialized]
	public float lastDealtDamageTime = float.NegativeInfinity;

	private int lastNotifyFrame;

	public float TimeSinceLastNoise => UnityEngine.Time.time - lastNoiseTime;

	public ActionVolume LastNoiseVolume { get; private set; }

	public Vector3 LastNoisePosition { get; private set; }

	public float SecondsSinceAttacked
	{
		get
		{
			if (base.isServer)
			{
				return UnityEngine.Time.time - lastAttackedTime;
			}
			return UnityEngine.Time.time - clientLastAttackedTime;
		}
	}

	public Vector3 LastAttackedDir { get; set; }

	public float SecondsSinceDealtDamage => UnityEngine.Time.time - lastDealtDamageTime;

	public float healthFraction => Health() / MaxHealth();

	public float health
	{
		get
		{
			return _health;
		}
		set
		{
			float num = _health;
			_health = Mathf.Clamp(value, 0f, MaxHealth());
			if (base.isServer && _health != num)
			{
				OnHealthChanged(num, _health);
			}
		}
	}

	public virtual bool ValidateMeleeColliderAntihack => true;

	public override bool OnRpcMessage(BasePlayer player, uint rpc, Message msg)
	{
		using (TimeWarning.New("BaseCombatEntity.OnRpcMessage"))
		{
			if (rpc == 1191093595 && player != null)
			{
				Assert.IsTrue(player.isServer, "SV_RPC Message is using a clientside player!");
				if (ConVar.Global.developer > 2)
				{
					Debug.Log("SV_RPCMessage: " + player?.ToString() + " - RPC_PickupStart");
				}
				using (TimeWarning.New("RPC_PickupStart"))
				{
					using (TimeWarning.New("Conditions"))
					{
						if (!RPC_Server.MaxDistance.Test(1191093595u, "RPC_PickupStart", this, player, 3f))
						{
							return true;
						}
					}
					try
					{
						using (TimeWarning.New("Call"))
						{
							RPCMessage rPCMessage = default(RPCMessage);
							rPCMessage.connection = msg.connection;
							rPCMessage.player = player;
							rPCMessage.read = msg.read;
							RPCMessage rpc2 = rPCMessage;
							RPC_PickupStart(rpc2);
						}
					}
					catch (Exception exception)
					{
						Debug.LogException(exception);
						player.Kick("RPC Error in RPC_PickupStart");
					}
				}
				return true;
			}
		}
		return base.OnRpcMessage(player, rpc, msg);
	}

	public virtual void TryDropCorpse(HitInfo info)
	{
		if (ShouldDropDeployableCorpse(info))
		{
			DropDeployableCorpse(base.OwnerID);
		}
	}

	public virtual bool ShouldDropDeployableCorpse(HitInfo info)
	{
		if (info == null)
		{
			return ShouldDropDeployableCorpse(null, DamageType.Generic);
		}
		return ShouldDropDeployableCorpse(info.InitiatorPlayer, info.damageTypes.GetMajorityDamageType());
	}

	public virtual bool ShouldDropDeployableCorpse(BaseEntity lastAttackerEnt, DamageType damageType)
	{
		if (!StorageContainer.dropCorpseOnDeath)
		{
			return false;
		}
		if (!spawnDeployableCorpseOnDeath)
		{
			return false;
		}
		if (damageType == DamageType.Decay)
		{
			return false;
		}
		if (HasParent() && parentEntity.Get(serverside: true) is PlayerBoat)
		{
			return false;
		}
		using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
		Vis.Entities(base.transform.position, 1f, pooledList, 134217728);
		foreach (BaseEntity item in pooledList)
		{
			if (item is BoatBuildingBlock)
			{
				return false;
			}
		}
		if (lastAttackerEnt != null && lastAttackerEnt is BasePlayer basePlayer && ((ulong)basePlayer.userID == base.OwnerID || basePlayer.IsBuildingAuthed(base.transform.position, base.transform.rotation, bounds)))
		{
			return false;
		}
		foreach (EntityComponentBase component in base.Components)
		{
			if (component is GroundWatch groundWatch)
			{
				if (!groundWatch.cachedGround.IsRealNull())
				{
					if (groundWatch.cachedGround.lastDamage == DamageType.Decay)
					{
						return false;
					}
					BasePlayer basePlayer2 = groundWatch.cachedGround.lastAttacker?.ToPlayer();
					if (basePlayer2 != null && ((ulong)basePlayer2.userID == base.OwnerID || basePlayer2.IsBuildingAuthed(base.transform.position, base.transform.rotation, bounds)))
					{
						return false;
					}
					break;
				}
				break;
			}
		}
		return true;
	}

	public void DropDeployableCorpse(ulong owner)
	{
		if (!deployableCorpsePrefab.isValid)
		{
			return;
		}
		BaseEntity baseEntity = GameManager.server.CreateEntity(deployableCorpsePrefab.resourcePath, base.transform.position, base.transform.rotation);
		if (baseEntity == null)
		{
			return;
		}
		if (HasParent())
		{
			baseEntity.SetParent(GetParentEntity(), worldPositionStays: true);
		}
		baseEntity.OwnerID = owner;
		baseEntity.skinID = skinID;
		baseEntity?.Spawn();
		if (baseEntity is ContainerCorpse containerCorpse)
		{
			BaseLock baseLock = GetSlot(Slot.Lock) as BaseLock;
			if (baseLock != null)
			{
				containerCorpse.SaveLock(baseLock);
			}
			containerCorpse.timePlaced = GetNetworkTime();
		}
		OnDeployableCorpseSpawned(baseEntity);
	}

	public virtual void OnDeployableCorpseSpawned(BaseEntity corpse)
	{
	}

	public override void AdminKill()
	{
		TryDropCorpse(null);
		base.AdminKill();
	}

	public override void OnDeployed(BaseEntity parent, BasePlayer deployedBy, Item fromItem)
	{
		base.OnDeployed(parent, deployedBy, fromItem);
		if (fromItem != null && fromItem.HasItemOwnership())
		{
			ItemOwnership = fromItem.TakeOwnershipShare();
		}
		else if (deployedBy != null)
		{
			ItemOwnership = new ItemOwnershipShare
			{
				username = deployedBy.displayName,
				amount = 1
			};
		}
	}

	protected virtual int GetPickupCount()
	{
		return pickup.itemCount;
	}

	protected virtual bool ShouldDisplayPickupOption(BasePlayer player)
	{
		bool parentIsBoat;
		bool flag = PlayerBoat.HasPermissionToPickup(player, this, out parentIsBoat);
		bool flag2 = pickup.enabled && (!pickup.requireBuildingPrivilege || player.CanBuild() || flag) && (!pickup.requireHammer || player.IsHoldingEntity<Hammer>()) && player != null && !player.IsInTutorial;
		if (flag2 && !flag && parentIsBoat)
		{
			flag2 = false;
		}
		if (flag2)
		{
			PickupVolume[] volumes = PrefabAttribute.server.FindAll<PickupVolume>(prefabID);
			if (PickupVolume.Check(base.transform.position, base.transform.rotation, volumes, this))
			{
				flag2 = false;
			}
		}
		return flag2;
	}

	protected virtual bool CanCompletePickup(BasePlayer player)
	{
		return true;
	}

	public virtual void OnPickedUp(Item createdItem, BasePlayer player)
	{
	}

	public virtual void OnPickedUpPreItemMove(Item createdItem, BasePlayer player)
	{
	}

	[RPC_Server]
	[RPC_Server.MaxDistance(3f)]
	private void RPC_PickupStart(RPCMessage rpc)
	{
		pickupErrorToFormat = (format: null, arg0: null);
		if (!rpc.player.CanInteract() || !ShouldDisplayPickupOption(rpc.player))
		{
			return;
		}
		if (!CanCompletePickup(rpc.player))
		{
			if (pickupErrorToFormat.format == null || pickupErrorToFormat.arg0 == null)
			{
				Debug.LogWarning(rpc.player.ToString() + " failed to pickup " + base.name + " but no valid error reason was given", this);
				return;
			}
			string token = pickupErrorToFormat.format.token;
			string token2 = pickupErrorToFormat.arg0.token;
			ClientRPC(RpcTarget.Player("Client_ShowFormattedErrorToast", rpc.player), token, token2);
			return;
		}
		Item item = ItemManager.Create(pickup.itemTarget, GetPickupCount(), skinID, isServerSide: true, 0uL);
		if (ItemOwnership.IsValid())
		{
			item.SetItemOwnership(ItemOwnership);
		}
		else
		{
			item.SetItemOwnership(rpc.player, ItemOwnershipPhrases.PickedUp);
		}
		if (pickup.setConditionFromHealth && item.hasCondition)
		{
			item.conditionNormalized = Mathf.Clamp01(healthFraction - pickup.subtractCondition);
		}
		OnPickedUpPreItemMove(item, rpc.player);
		rpc.player.GiveItem(item, GiveItemReason.PickedUp);
		OnPickedUp(item, rpc.player);
		Analytics.Azure.OnEntityPickedUp(rpc.player, this);
		Kill();
	}

	public virtual EntityBuildCost BuildCost()
	{
		if (repair.itemTarget == null)
		{
			return default(EntityBuildCost);
		}
		ItemBlueprint itemBlueprint = ItemManager.FindBlueprint(repair.itemTarget);
		if (itemBlueprint == null)
		{
			return default(EntityBuildCost);
		}
		return new EntityBuildCost(itemBlueprint.GetIngredients(), itemBlueprint.amountToCreate);
	}

	public virtual bool ShouldRepairViaParent()
	{
		return false;
	}

	public virtual BaseCombatEntity GetRepairableParent()
	{
		return null;
	}

	public virtual float RepairCostFraction()
	{
		return 0.5f;
	}

	public List<ItemAmount> RepairCost(float healthMissingFraction)
	{
		EntityBuildCost entityBuildCost = BuildCost();
		if (entityBuildCost.Items == null)
		{
			return null;
		}
		List<ItemAmount> list = new List<ItemAmount>();
		foreach (ItemAmount item in entityBuildCost.Items)
		{
			if (!(repair.ignoreForRepair != null) || item.itemDef.itemid != repair.ignoreForRepair.itemid)
			{
				list.Add(new ItemAmount(item.itemDef, Mathf.Max(Mathf.RoundToInt(item.amount * RepairCostFraction() * healthMissingFraction), 1)));
			}
		}
		RepairBench.StripComponentRepairCost(list, RepairCostFraction() * healthMissingFraction);
		return list;
	}

	public virtual void OnRepair()
	{
		Effect.server.Run(repair.repairEffect.isValid ? repair.repairEffect.resourcePath : "assets/bundled/prefabs/fx/build/repair.prefab", this, 0u, Vector3.zero, Vector3.zero);
	}

	public virtual void OnRepairFinished(BasePlayer player)
	{
		Effect.server.Run(repair.repairFullEffect.isValid ? repair.repairFullEffect.resourcePath : "assets/bundled/prefabs/fx/build/repair_full.prefab", this, 0u, Vector3.zero, Vector3.zero);
	}

	public virtual void OnRepairFailed(BasePlayer player, Translate.Phrase reason, params string[] args)
	{
		Effect.server.Run(repair.repairFailedEffect.isValid ? repair.repairFailedEffect.resourcePath : "assets/bundled/prefabs/fx/build/repair_failed.prefab", this, 0u, Vector3.zero, Vector3.zero);
		if (player != null && !string.IsNullOrEmpty(reason.token))
		{
			player.ShowToast(GameTip.Styles.Error, reason, overlay: false, args);
		}
	}

	public virtual void OnRepairFailedResources(BasePlayer player, List<ItemAmount> requirements)
	{
		Effect.server.Run(repair.repairFailedEffect.isValid ? repair.repairFailedEffect.resourcePath : "assets/bundled/prefabs/fx/build/repair_failed.prefab", this, 0u, Vector3.zero, Vector3.zero);
		if (player != null)
		{
			using (ItemAmountList arg = ItemAmount.SerialiseList(requirements))
			{
				player.ClientRPC(RpcTarget.Player("Client_OnRepairFailedResources", player), arg);
			}
		}
	}

	public virtual void DoRepair(BasePlayer player)
	{
		if (!repair.enabled)
		{
			return;
		}
		float num = GetDamageRepairCooldown();
		if (player.IsInCreativeMode && Creative.freeRepair)
		{
			num = 0f;
		}
		if (SecondsSinceAttacked <= num)
		{
			OnRepairFailed(player, RecentlyDamagedError, (num - SecondsSinceAttacked).ToString("N0"));
			return;
		}
		float num2 = MaxHealth() - Health();
		float num3 = num2 / MaxHealth();
		if (num2 <= 0f || num3 <= 0f)
		{
			OnRepairFailed(player, NotDamagedError);
			return;
		}
		List<ItemAmount> list = RepairCost(num3);
		if (list == null)
		{
			return;
		}
		float num4 = list.Sum((ItemAmount x) => x.amount);
		float healthBefore = health;
		if (player.IsInCreativeMode && Creative.freeRepair)
		{
			num4 = 0f;
		}
		if (num4 > 0f)
		{
			float num5 = list.Min((ItemAmount x) => Mathf.Clamp01((float)player.inventory.GetAmount(x.itemid) / x.amount));
			if (float.IsNaN(num5))
			{
				num5 = 0f;
			}
			num5 = Mathf.Min(num5, 50f / num2);
			if (num5 <= 0f)
			{
				OnRepairFailedResources(player, list);
				return;
			}
			int num6 = 0;
			foreach (ItemAmount item in list)
			{
				int amount = Mathf.CeilToInt(num5 * item.amount);
				int num7 = player.inventory.Take(null, item.itemid, amount);
				Analytics.Azure.LogResource(Analytics.Azure.ResourceMode.Consumed, "repair_entity", item.itemDef.shortname, num7, this, null, safezone: false, null, player.userID, null, null, null, 0uL);
				if (num7 > 0)
				{
					num6 += num7;
					player.Command("note.inv", item.itemid, num7 * -1);
				}
			}
			float num8 = (float)num6 / num4;
			health += num2 * num8;
			SendNetworkUpdate();
		}
		else
		{
			health += num2;
			SendNetworkUpdate();
		}
		Analytics.Azure.OnEntityRepaired(player, this, healthBefore, health);
		if (Health() >= MaxHealth())
		{
			OnRepairFinished(player);
		}
		else
		{
			OnRepair();
		}
	}

	public virtual float GetDamageRepairCooldown()
	{
		return 30f;
	}

	public virtual void InitializeHealth(float newhealth, float newmax)
	{
		_maxHealth = newmax;
		_health = newhealth;
		lifestate = LifeState.Alive;
	}

	public override void ServerInit()
	{
		propDirection = PrefabAttribute.server.FindAll<DirectionProperties>(prefabID);
		if (ResetLifeStateOnSpawn)
		{
			InitializeHealth(StartHealth(), StartMaxHealth());
			lifestate = LifeState.Alive;
		}
		base.ServerInit();
	}

	public virtual void OnHealthChanged(float oldvalue, float newvalue)
	{
	}

	public void Hurt(float amount)
	{
		Hurt(Mathf.Abs(amount), DamageType.Generic);
	}

	public void Hurt(float amount, DamageType type, BaseEntity attacker = null, bool useProtection = true)
	{
		using (TimeWarning.New("Hurt"))
		{
			HitInfo obj = Facepunch.Pool.Get<HitInfo>();
			obj.Init(attacker, this, type, amount, base.transform.position);
			obj.UseProtection = useProtection;
			Hurt(obj);
			Facepunch.Pool.Free(ref obj);
		}
	}

	public virtual void Hurt(HitInfo info)
	{
		Assert.IsTrue(base.isServer, "This should be called serverside only");
		if (IsDead() || IsTransferProtected() || RaidWindow.BlocksDamage(this, info))
		{
			return;
		}
		using (TimeWarning.New("Hurt( HitInfo )", 50))
		{
			float num = health;
			ScaleDamage(info);
			if (info.PointStart != Vector3.zero)
			{
				for (int i = 0; i < propDirection.Length; i++)
				{
					if (!(propDirection[i].extraProtection == null) && !propDirection[i].IsWeakspot(base.transform, info))
					{
						propDirection[i].extraProtection.Scale(info.damageTypes);
					}
				}
			}
			info.damageTypes.Scale(DamageType.Arrow, ConVar.Server.arrowdamage);
			info.damageTypes.Scale(DamageType.Bullet, ConVar.Server.bulletdamage);
			info.damageTypes.Scale(DamageType.Slash, ConVar.Server.meleedamage);
			info.damageTypes.Scale(DamageType.Blunt, ConVar.Server.meleedamage);
			info.damageTypes.Scale(DamageType.Stab, ConVar.Server.meleedamage);
			info.damageTypes.Scale(DamageType.Bleeding, ConVar.Server.bleedingdamage);
			if (base.Components != null)
			{
				for (int j = 0; j < base.Components.Count; j++)
				{
					if (!(base.Components[j] == null))
					{
						base.Components[j].Hurt(info);
					}
				}
			}
			if (!(this is BasePlayer))
			{
				info.damageTypes.Scale(DamageType.Fun_Water, 0f);
			}
			if (info.damageTypes.Has(DamageType.Paintball))
			{
				float num2 = 1f;
				if (PaintballColorLookup.instance != null && PaintballColorLookup.instance.overallsItemDefinition != null)
				{
					bool num3 = this is BasePlayer basePlayer && basePlayer.inventory != null && basePlayer.inventory.containerWear != null && basePlayer.inventory.containerWear.HasItem(PaintballColorLookup.instance.overallsItemDefinition);
					bool flag = info.InitiatorPlayer != null && info.InitiatorPlayer.inventory != null && info.InitiatorPlayer.inventory.containerWear != null && info.InitiatorPlayer.inventory.containerWear.HasItem(PaintballColorLookup.instance.overallsItemDefinition);
					bool flag2 = info.Initiator is AutoTurret;
					num2 = ((!num3 || !(flag || flag2)) ? ConVar.Server.paintballstandarddamage : (30f * ConVar.Server.paintballoverallsdamage));
				}
				else
				{
					num2 = 0f;
				}
				info.damageTypes.Scale(DamageType.Paintball, num2);
			}
			DebugHurt(info);
			float num4 = info.damageTypes.Total();
			health = num - num4;
			SendNetworkUpdate();
			LogEntry(RustLog.EntryType.Combat, 2, "hurt {0}/{1} - {2} health left", info.damageTypes.GetMajorityDamageType(), num4, health);
			lastDamage = info.damageTypes.GetMajorityDamageType();
			lastAttacker = info.Initiator;
			if (lastAttacker != null)
			{
				BaseCombatEntity baseCombatEntity = lastAttacker as BaseCombatEntity;
				if (baseCombatEntity != null)
				{
					baseCombatEntity.lastDealtDamageTime = UnityEngine.Time.time;
					baseCombatEntity.lastDealtDamageTo = this;
				}
				if (this.IsValid() && lastAttacker is BasePlayer basePlayer2)
				{
					basePlayer2.ProcessMissionEvent(BaseMission.MissionEventType.HURT_ENTITY, net.ID, num4);
				}
			}
			BaseCombatEntity baseCombatEntity2 = lastAttacker as BaseCombatEntity;
			if (markAttackerHostile && baseCombatEntity2 != null && baseCombatEntity2 != this)
			{
				baseCombatEntity2.MarkHostileFor();
			}
			if (lastDamage.IsConsideredAnAttack())
			{
				SetJustAttacked();
				if (lastAttacker != null)
				{
					LastAttackedDir = (lastAttacker.transform.position - base.transform.position).normalized;
				}
			}
			bool flag3 = Health() <= 0f;
			Analytics.Azure.OnEntityTakeDamage(info, flag3);
			if (flag3)
			{
				Die(info);
			}
			BasePlayer initiatorPlayer = info.InitiatorPlayer;
			if ((bool)initiatorPlayer)
			{
				if (IsDead())
				{
					initiatorPlayer.stats.combat.LogAttack(info, "killed", num);
				}
				else
				{
					initiatorPlayer.stats.combat.LogAttack(info, "", num);
				}
			}
		}
	}

	public virtual bool IsHostile()
	{
		return unHostileTime > UnityEngine.Time.realtimeSinceStartup;
	}

	public virtual void MarkHostileFor(float duration = 60f)
	{
		float b = UnityEngine.Time.realtimeSinceStartup + duration;
		unHostileTime = Mathf.Max(unHostileTime, b);
	}

	private void DebugHurt(HitInfo info)
	{
		if (!ConVar.Vis.damage)
		{
			return;
		}
		if (info.PointStart != info.PointEnd)
		{
			ConsoleNetwork.BroadcastToAllClients("ddraw.arrow", 60, Color.cyan, info.PointStart, info.PointEnd, 0.1f);
			ConsoleNetwork.BroadcastToAllClients("ddraw.sphere", 60, Color.cyan, info.HitPositionWorld, 0.01f);
		}
		string text = "";
		for (int i = 0; i < info.damageTypes.types.Length; i++)
		{
			float num = info.damageTypes.types[i];
			if (num != 0f)
			{
				string[] obj = new string[5] { text, " ", null, null, null };
				DamageType damageType = (DamageType)i;
				obj[2] = damageType.ToString().PadRight(10);
				obj[3] = num.ToString("0.00");
				obj[4] = "\n";
				text = string.Concat(obj);
			}
		}
		string text2 = "<color=lightblue>Damage:</color>".PadRight(10) + info.damageTypes.Total().ToString("0.00") + "\n<color=lightblue>Health:</color>".PadRight(10) + health.ToString("0.00") + " / " + ((health - info.damageTypes.Total() <= 0f) ? "<color=red>" : "<color=green>") + (health - info.damageTypes.Total()).ToString("0.00") + "</color>" + "\n<color=lightblue>HitEnt:</color>".PadRight(10) + this?.ToString() + "\n<color=lightblue>HitBone:</color>".PadRight(10) + info.boneName + "\n<color=lightblue>Attacker:</color>".PadRight(10) + info.Initiator?.ToString() + "\n<color=lightblue>WeaponPrefab:</color>".PadRight(10) + info.WeaponPrefab?.ToString() + "\n<color=lightblue>Damages:</color>\n" + text;
		ConsoleNetwork.BroadcastToAllClients("ddraw.text", 60, Color.white, info.HitPositionWorld, text2);
	}

	public void SetHealth(float hp)
	{
		if (health != hp)
		{
			health = hp;
			SendNetworkUpdate();
		}
	}

	public virtual void Heal(float amount)
	{
		LogEntry(RustLog.EntryType.Combat, 2, "healed {0}", amount);
		health = _health + amount;
		SendNetworkUpdate();
	}

	public virtual void OnDied(HitInfo info)
	{
		bool flag = true;
		if (base.Components != null)
		{
			for (int i = 0; i < base.Components.Count; i++)
			{
				if (!(base.Components[i] == null) && !base.Components[i].OnDied(info) && flag)
				{
					flag = false;
				}
			}
		}
		if (flag)
		{
			Kill(DestroyMode.Gib);
		}
	}

	public virtual void Die(HitInfo info = null)
	{
		if (IsDead())
		{
			return;
		}
		LogEntry(RustLog.EntryType.Combat, 2, "died");
		health = 0f;
		lifestate = LifeState.Dead;
		if (info != null && info.InitiatorPlayer != null && !info.InitiatorPlayer.IsNpc && info.InitiatorPlayer != this)
		{
			BaseMission.MissionEventPayload missionEventPayload = default(BaseMission.MissionEventPayload);
			missionEventPayload.NetworkIdentifier = info.InitiatorPlayer.net.ID;
			missionEventPayload.UintIdentifier = prefabID;
			missionEventPayload.IntIdentifier = 1;
			missionEventPayload.WorldPosition = base.transform.position;
			BaseMission.MissionEventPayload payload = missionEventPayload;
			info.InitiatorPlayer.ProcessMissionEvent(BaseMission.MissionEventType.KILL_ENTITY, payload, 0f);
			if (info.InitiatorPlayer.Team != null)
			{
				for (int i = 0; i < info.InitiatorPlayer.Team.members.Count; i++)
				{
					BasePlayer basePlayer = RelationshipManager.FindByID(info.InitiatorPlayer.Team.members[i]);
					if (!(basePlayer == null) && !(basePlayer == info.InitiatorPlayer))
					{
						basePlayer.ProcessMissionEvent(BaseMission.MissionEventType.KILL_ENTITY, payload, 0f);
					}
				}
			}
		}
		using (TimeWarning.New("OnDied"))
		{
			TryDropCorpse(info);
			OnDied(info);
		}
	}

	public void DieInstantly()
	{
		if (!IsDead())
		{
			LogEntry(RustLog.EntryType.Combat, 2, "died");
			health = 0f;
			lifestate = LifeState.Dead;
			TryDropCorpse(null);
			OnDied(null);
		}
	}

	public void UpdateSurroundingsOnDestroy()
	{
		BaseEntity baseEntity = GetParentEntity();
		if (baseEntity != null && baseEntity.GetWorldVelocity().sqrMagnitude > 5f)
		{
			StabilityEntity.UpdateSurroundingsQueue.NotifyNeighbours(WorldSpaceBounds().ToBounds());
		}
		else
		{
			StabilityEntity.updateSurroundingsQueue.Add(WorldSpaceBounds().ToBounds());
		}
	}

	public void MakeNoise(Vector3 position, ActionVolume loudness)
	{
		LastNoisePosition = position;
		LastNoiseVolume = loudness;
		lastNoiseTime = UnityEngine.Time.time;
	}

	public bool CanLastNoiseBeHeard(Vector3 listenPosition, float listenRange)
	{
		if (listenRange <= 0f)
		{
			return false;
		}
		return Vector3.Distance(listenPosition, LastNoisePosition) <= listenRange;
	}

	public override bool CanBeReskinned(BasePlayer player)
	{
		if (SecondsSinceAttacked < 30f)
		{
			SprayCan.LastReskinError = SprayCan.RecentlyDamaged;
			SprayCan.LastReskinErrorArgString = (30f - SecondsSinceAttacked).ToString("N0");
			return false;
		}
		foreach (BaseEntity child in children)
		{
			if (child is TimedExplosive)
			{
				SprayCan.LastReskinError = SprayCan.ExplosivesActive;
				return false;
			}
		}
		return base.CanBeReskinned(player);
	}

	public override void Reskin_Preserve(ref SprayCan.ReskinPreserveInfo preserveInfo)
	{
		base.Reskin_Preserve(ref preserveInfo);
		ref BaseCombatEntityPreserveInfo baseCombatEntityPreserve = ref preserveInfo.baseCombatEntityPreserve;
		baseCombatEntityPreserve.health = Health();
		baseCombatEntityPreserve.lastAttackedTime = lastAttackedTime;
		baseCombatEntityPreserve.ownership = ItemOwnership;
	}

	public override void Reskin_Restore(ref SprayCan.ReskinPreserveInfo preserveInfo)
	{
		base.Reskin_Restore(ref preserveInfo);
		ref BaseCombatEntityPreserveInfo baseCombatEntityPreserve = ref preserveInfo.baseCombatEntityPreserve;
		SetHealth(baseCombatEntityPreserve.health);
		lastAttackedTime = baseCombatEntityPreserve.lastAttackedTime;
		ItemOwnership = baseCombatEntityPreserve.ownership;
	}

	public virtual bool IsDead()
	{
		return lifestate == LifeState.Dead;
	}

	public virtual bool IsAlive()
	{
		return lifestate == LifeState.Alive;
	}

	public Faction GetFaction()
	{
		return faction;
	}

	public virtual bool IsFriendly(BaseCombatEntity other)
	{
		return false;
	}

	public override void ResetState()
	{
		base.ResetState();
		BaseCombatEntity baseCombatEntity = LookupPrefab<BaseCombatEntity>();
		if (baseCombatEntity != null)
		{
			baseProtection = baseCombatEntity.baseProtection;
		}
		health = MaxHealth();
		maxHealthOverride = 0f;
		if (base.isServer)
		{
			if (baseCombatEntity != null)
			{
				_maxHealth = baseCombatEntity._maxHealth;
			}
			ResetLifeStateOnSpawn = true;
			lastAttackedTime = float.NegativeInfinity;
			lastDealtDamageTime = float.NegativeInfinity;
			lastAttacker = null;
			lastDealtDamageTo = null;
			LastAttackedDir = default(Vector3);
		}
	}

	public override void DestroyShared()
	{
		base.DestroyShared();
		if (base.isServer && !(this is PlayerBoat { KilledForEditMode: not false }))
		{
			UpdateSurroundingsOnDestroy();
		}
	}

	public virtual float GetThreatLevel()
	{
		return 0f;
	}

	public override float PenetrationResistance(HitInfo info)
	{
		if (!baseProtection)
		{
			return 100f;
		}
		return baseProtection.density;
	}

	public virtual void ScaleDamage(HitInfo info)
	{
		if (info.UseProtection && baseProtection != null)
		{
			baseProtection.Scale(info.damageTypes);
		}
	}

	public HitArea SkeletonLookup(uint boneID)
	{
		if (skeletonProperties == null)
		{
			return (HitArea)(-1);
		}
		return skeletonProperties.FindBone(boneID)?.area ?? ((HitArea)(-1));
	}

	public override void Save(SaveInfo info)
	{
		base.Save(info);
		info.msg.baseCombat = Facepunch.Pool.Get<BaseCombat>();
		info.msg.baseCombat.state = (int)lifestate;
		info.msg.baseCombat.health = Health();
		info.msg.baseCombat.maxHealthOverride = maxHealthOverride;
		if (ItemOwnership.IsValid())
		{
			info.msg.ownership = Facepunch.Pool.Get<ItemOwnershipAmount>();
			info.msg.ownership.username = ItemOwnership.username;
			info.msg.ownership.reason = ItemOwnership.reason;
			info.msg.ownership.amount = ItemOwnership.amount;
		}
	}

	public override void PostServerLoad()
	{
		base.PostServerLoad();
		if (Health() > MaxHealth())
		{
			health = MaxHealth();
		}
		if (float.IsNaN(Health()))
		{
			health = MaxHealth();
		}
	}

	public void SetJustAttacked()
	{
		lastAttackedTime = UnityEngine.Time.time;
		ClientRPC(RpcTarget.NetworkGroup("Client_SetJustAttacked"));
	}

	public override void Load(LoadInfo info)
	{
		if (base.isServer)
		{
			lifestate = LifeState.Alive;
		}
		if (info.msg.baseCombat != null)
		{
			lifestate = (LifeState)info.msg.baseCombat.state;
			_health = info.msg.baseCombat.health;
			maxHealthOverride = info.msg.baseCombat.maxHealthOverride;
		}
		base.Load(info);
		if (info.msg.ownership != null)
		{
			ItemOwnership = new ItemOwnershipShare
			{
				username = info.msg.ownership.username,
				reason = info.msg.ownership.reason,
				amount = info.msg.ownership.amount
			};
		}
	}

	public override float Health()
	{
		return _health;
	}

	public override float MaxHealth()
	{
		if (maxHealthOverride > 0f)
		{
			return maxHealthOverride;
		}
		return _maxHealth;
	}

	public virtual float StartHealth()
	{
		return startHealth;
	}

	public virtual float StartMaxHealth()
	{
		return StartHealth();
	}

	public void SetMaxHealth(float newMax)
	{
		_maxHealth = newMax;
		_health = Mathf.Min(_health, newMax);
		SendNetworkUpdate();
	}

	public void OverrideMaxHealth(float value, bool sendNetworkUpdate = true, bool clampHealth = true)
	{
		maxHealthOverride = value;
		if (clampHealth && health > MaxHealth())
		{
			health = MaxHealth();
		}
		if (sendNetworkUpdate)
		{
			SendNetworkUpdate();
		}
	}

	public void DoHitNotify(HitInfo info)
	{
		using (TimeWarning.New("DoHitNotify"))
		{
			if (sendsHitNotification && !(info.Initiator == null) && info.Initiator is BasePlayer && !(this == info.Initiator) && (!info.isHeadshot || (!(info.HitEntity is BasePlayer) && !(info.HitEntity is ScientistNPC2 { canBeHeadshot: not false }))) && UnityEngine.Time.frameCount != lastNotifyFrame)
			{
				lastNotifyFrame = UnityEngine.Time.frameCount;
				bool flag = info.Weapon is BaseMelee;
				if (base.isServer && (!flag || sendsMeleeHitNotification))
				{
					bool arg = info.Initiator.net.connection == info.Predicted;
					ClientRPC(RpcTarget.PlayerAndSpectators("HitNotify", info.Initiator as BasePlayer), arg);
				}
			}
		}
	}

	public bool OnAttacked(float amount, DamageType type, BaseEntity attacker = null, bool ignoreShield = true)
	{
		if (!ignoreShield && ProcessAngleBasedShieldDetection(amount, type, attacker))
		{
			return false;
		}
		HitInfo obj = Facepunch.Pool.Get<HitInfo>();
		obj.Init(attacker, this, type, amount, base.transform.position);
		OnAttacked(obj);
		Facepunch.Pool.Free(ref obj);
		return true;
	}

	private bool ProcessAngleBasedShieldDetection(float amount, DamageType type, BaseEntity attacker)
	{
		if (attacker.IsValid() && this.ToNonNpcPlayer(out var player) && player.TryGetActiveShield(out var foundShield) && foundShield.IsBlocking())
		{
			Vector3 lhs = (attacker.transform.position - attacker.transform.forward * 1f - base.transform.position).NormalizeXZ();
			Vector3 rhs = player.eyes.BodyForward().NormalizeXZ();
			if (Vector3.Dot(lhs, rhs) > 0f)
			{
				HitInfo obj = Facepunch.Pool.Get<HitInfo>();
				obj.Init(attacker, foundShield, type, amount, base.transform.position);
				foundShield.OnAttacked(obj);
				Facepunch.Pool.Free(ref obj);
				return true;
			}
		}
		return false;
	}

	public bool OnAttacked(HitInfo info, bool ignoreShield)
	{
		if (!ignoreShield && ProcessAngleBasedShieldDetection(info.damageTypes.Total(), info.damageTypes.GetMajorityDamageType(), info.Initiator))
		{
			return false;
		}
		OnAttacked(info);
		return true;
	}

	public override void OnAttacked(HitInfo info)
	{
		using (TimeWarning.New("BaseCombatEntity.OnAttacked"))
		{
			if (base.Components != null)
			{
				for (int i = 0; i < base.Components.Count; i++)
				{
					if (!(base.Components[i] == null))
					{
						base.Components[i].OnAttacked(info);
					}
				}
			}
			if (!IsDead())
			{
				DoHitNotify(info);
			}
			if (base.isServer)
			{
				Hurt(info);
			}
		}
		base.OnAttacked(info);
	}
}

