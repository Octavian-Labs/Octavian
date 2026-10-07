using System;
using System.Runtime.CompilerServices;
using System.Threading;
using ConVar;
using Facepunch;
using Facepunch.Rust;
using Network;
using UnityEngine;

namespace Rust.Ai.Gen2;

[SoftRequireComponent(typeof(CowFSM))]
public class Sheep : LivestockAnimal
{
	[ServerVar(Help = "Population active on the server, per square km", ShowInAdminUI = true)]
	public static float Population = 2f;

	[Tooltip("What shearing the whole sheep yields, handed over a quarter at a time so the four quadrants add up to it. An entry too small to round to one gives nothing")]
	public ItemAmount[] ShearItems = Array.Empty<ItemAmount>();

	[Tooltip("How long (in seconds) after being sheared before the wool grows back. Every quadrant regrows together")]
	public float ShearCooldown = 300f;

	[Tooltip("Optional effect played on the sheep each time a quarter of its fleece is sheared")]
	public GameObjectRef ShearEffect;

	[Tooltip("Optional wool thrown off the sheep each time a quarter of its fleece is sheared, tinted to its fleece by a LivestockWoolTint")]
	public GameObjectRef ShearWoolEffect;

	public const Flags ShearReady = Flags.Reserved9;

	public const int ShearTimerId = 6;

	private const float WoolGrace = 0.12f;

	private LivestockFleece fleece;

	private PersistentTimer shearTimer;

	private int __sync_ShornQuadrants;

	[Sync(Autosave = true)]
	public int ShornQuadrants
	{
		[CompilerGenerated]
		get
		{
			return __sync_ShornQuadrants;
		}
		[CompilerGenerated]
		set
		{
			if (!IsSyncVarEqual(__sync_ShornQuadrants, value))
			{
				__sync_ShornQuadrants = value;
				byte nameID = __GetWeaverID("ShornQuadrants");
				QueueSyncVar(nameID);
			}
		}
	}

	public LivestockFleece Fleece
	{
		get
		{
			if (!(fleece != null))
			{
				return fleece = GetComponent<LivestockFleece>();
			}
			return fleece;
		}
	}

	public override int ShornFleece => ShornQuadrants;

	private int ShornCount
	{
		get
		{
			int num = 0;
			for (int i = 0; i < 4; i++)
			{
				if (!HasWool((LivestockFleece.Quadrant)i))
				{
					num++;
				}
			}
			return num;
		}
	}

	public override string Categorize()
	{
		return "Sheep";
	}

	public bool HasWool(LivestockFleece.Quadrant quadrant)
	{
		return (ShornQuadrants & (1 << (int)quadrant)) == 0;
	}

	public bool CanBeSheared(BasePlayer player)
	{
		if (player == null)
		{
			return false;
		}
		if (IsDead())
		{
			return false;
		}
		if (IsInfant())
		{
			return false;
		}
		if (ShornQuadrants == 15)
		{
			return false;
		}
		return true;
	}

	public bool IsShearTool(AttackEntity weapon)
	{
		BaseMelee baseMelee = weapon as BaseMelee;
		if (baseMelee == null || baseMelee.gathering == null)
		{
			return false;
		}
		ResourceDispenser.GatherPropertyEntry gatherInfoFromIndex = baseMelee.GetGatherInfoFromIndex(ResourceDispenser.GatherType.Flesh);
		if (gatherInfoFromIndex == null)
		{
			return false;
		}
		if (gatherInfoFromIndex.gatherDamage > 0f)
		{
			return gatherInfoFromIndex.destroyFraction <= 0f;
		}
		return false;
	}

	public bool WouldShear(HitInfo info, out LivestockFleece.Quadrant quadrant)
	{
		quadrant = LivestockFleece.Quadrant.FrontLeft;
		if (info == null)
		{
			return false;
		}
		if (info.InitiatorPlayer == null)
		{
			return false;
		}
		if (info.IsProjectile())
		{
			return false;
		}
		if (!IsShearTool(info.Weapon))
		{
			return false;
		}
		if (!CanBeSheared(info.InitiatorPlayer))
		{
			return false;
		}
		if (Fleece == null)
		{
			return false;
		}
		return TryPickShearQuadrant(info.HitPositionWorld, out quadrant);
	}

	public bool TryPickShearQuadrant(Vector3 worldPoint, out LivestockFleece.Quadrant quadrant)
	{
		quadrant = LivestockFleece.Quadrant.FrontLeft;
		if (Fleece == null)
		{
			return false;
		}
		float num = 0f;
		bool result = false;
		for (int i = 0; i < 4; i++)
		{
			LivestockFleece.Quadrant quadrant2 = (LivestockFleece.Quadrant)i;
			if (HasWool(quadrant2))
			{
				float num2 = Fleece.WeightAt(worldPoint, quadrant2);
				if (!(num2 < 0.12f) && !(num2 <= num))
				{
					num = num2;
					quadrant = quadrant2;
					result = true;
				}
			}
		}
		return result;
	}

	public override void OnAttacked(HitInfo info)
	{
		if (!WouldShear(info, out var quadrant))
		{
			base.OnAttacked(info);
			return;
		}
		if (base.isServer)
		{
			if (!Shear(info.InitiatorPlayer, quadrant))
			{
				base.OnAttacked(info);
				return;
			}
			WearShearTool(info);
		}
		info.damageTypes.Clear();
		info.DoHitEffects = false;
		if (!IsDead())
		{
			DoHitNotify(info);
		}
	}

	public override void ServerInit()
	{
		base.ServerInit();
		shearTimer = new PersistentTimer(timers, 6)
		{
			onElapsed = EnableShearing,
			onStarted = SyncShearReady
		};
		SyncShearReady();
	}

	private void WearShearTool(HitInfo info)
	{
		if (info.Weapon is BaseMelee baseMelee)
		{
			baseMelee.LoseCondition(baseMelee.GetConditionLoss());
		}
		info.DidGather = true;
	}

	public bool Shear(BasePlayer player, LivestockFleece.Quadrant quadrant)
	{
		if (!CanBeSheared(player))
		{
			return false;
		}
		if (!HasWool(quadrant))
		{
			return false;
		}
		ShornQuadrants |= 1 << (int)quadrant;
		if (ShearEffect != null && ShearEffect.isValid)
		{
			Effect.server.Run(ShearEffect.resourcePath, this, 0u, Vector3.zero, Vector3.zero);
		}
		if (ShearWoolEffect != null && ShearWoolEffect.isValid)
		{
			Effect.server.Run(ShearWoolEffect.resourcePath, this, 0u, Vector3.zero, Vector3.zero);
		}
		GiveShearItems(player);
		if (!TrustsToHandle(player))
		{
			OnGrabbedBy(player);
		}
		Analytics.Azure.OnLivestockSheepSheared(player, this);
		shearTimer.Start(ShearCooldown / GeneScale(LivestockGene.Yield));
		return true;
	}

	public bool TryShear(BasePlayer player)
	{
		for (int i = 0; i < 4; i++)
		{
			if (HasWool((LivestockFleece.Quadrant)i))
			{
				return Shear(player, (LivestockFleece.Quadrant)i);
			}
		}
		return false;
	}

	private void GiveShearItems(BasePlayer player)
	{
		ItemAmount[] shearItems = ShearItems;
		foreach (ItemAmount itemAmount in shearItems)
		{
			if (itemAmount != null && !(itemAmount.itemDef == null))
			{
				int num = ShornAmount(itemAmount);
				if (num > 0)
				{
					Item item = ItemManager.Create(itemAmount.itemDef, num, 0uL, isServerSide: true, 0uL);
					item.SetItemOwnership(player, ItemOwnershipPhrases.GatheredPhrase);
					player.GiveItem(item, GiveItemReason.ResourceHarvested, GiveItemOptions.BackpackOverflow);
				}
			}
		}
	}

	public int FleeceAmount(ItemAmount shearItem)
	{
		if (shearItem.amount <= 0f)
		{
			return 0;
		}
		return Mathf.RoundToInt(shearItem.amount * GeneScale(LivestockGene.Yield));
	}

	public int ShornAmount(ItemAmount shearItem)
	{
		int total = FleeceAmount(shearItem);
		int shornCount = ShornCount;
		if (shornCount <= 0)
		{
			return 0;
		}
		return QuarterSplit(total, shornCount) - QuarterSplit(total, shornCount - 1);
	}

	public int UnshornAmount(ItemAmount shearItem)
	{
		int num = FleeceAmount(shearItem);
		return num - QuarterSplit(num, ShornCount);
	}

	private static int QuarterSplit(int total, int quarters)
	{
		return total * quarters / 4;
	}

	public override void AddCorpseYield(ResourceDispenser dispenser)
	{
		if (dispenser == null || IsInfant())
		{
			return;
		}
		ItemAmount[] shearItems = ShearItems;
		foreach (ItemAmount itemAmount in shearItems)
		{
			if (itemAmount != null && !(itemAmount.itemDef == null))
			{
				int num = UnshornAmount(itemAmount);
				if (num > 0)
				{
					dispenser.containedItems.Add(new ItemAmount(itemAmount.itemDef, num));
				}
			}
		}
	}

	public override bool TryGetYield(out ItemDefinition item, out int amount, out float interval, out float bestOfBreed)
	{
		item = null;
		amount = 0;
		interval = ShearCooldown / GeneScale(LivestockGene.Yield);
		bestOfBreed = 0f;
		float num = Mathf.Max(0.01f, Livestock.geneYieldGood);
		int num2 = 0;
		ItemAmount[] shearItems = ShearItems;
		foreach (ItemAmount itemAmount in shearItems)
		{
			if (itemAmount != null && !(itemAmount.itemDef == null))
			{
				int num3 = FleeceAmount(itemAmount);
				if (num3 > amount)
				{
					item = itemAmount.itemDef;
					amount = num3;
					num2 = Mathf.RoundToInt(itemAmount.amount * num);
				}
			}
		}
		float num4 = ShearCooldown / num;
		if (interval > 0f && num4 > 0f && num2 > 0)
		{
			bestOfBreed = Mathf.Clamp01((float)amount / interval / ((float)num2 / num4));
		}
		if (item != null)
		{
			return interval > 0f;
		}
		return false;
	}

	private void SyncShearReady()
	{
		using FlagsUpdateScope flagsUpdateScope = StartSetFlags(FlagsUpdateMode.SendNetworkUpdate);
		flagsUpdateScope.Set(Flags.Reserved9, ShornQuadrants != 15);
	}

	private void EnableShearing()
	{
		ShornQuadrants = 0;
		SyncShearReady();
	}

	public bool ForceShearReady()
	{
		if (IsDead() || IsInfant())
		{
			return false;
		}
		shearTimer.Stop();
		EnableShearing();
		return true;
	}

	private void OnSyncVar_ShornQuadrants(int? oldValue, int newValue)
	{
	}

	protected override bool WriteSyncVar(byte id, NetWrite writer)
	{
		if (id == 7)
		{
			if (ConVar.Global.developer > 2)
			{
				NetworkableId iD = net.ID;
				Debug.Log("SyncVar Writing: ShornQuadrants for " + iD.ToString());
			}
			SyncVarNetWrite(writer, __sync_ShornQuadrants);
			return true;
		}
		return base.WriteSyncVar(id, writer);
	}

	protected override bool OnSyncVar(byte id, NetRead reader, bool fromAutoSave = false)
	{
		if (id == 7)
		{
			try
			{
				int? oldValue = __sync_ShornQuadrants;
				int newValue = (__sync_ShornQuadrants = reader.Int32());
				if (fromAutoSave)
				{
					oldValue = null;
				}
				OnSyncVar_ShornQuadrants(oldValue, newValue);
			}
			catch (Exception exception)
			{
				Debug.LogException(exception);
			}
			return true;
		}
		return base.OnSyncVar(id, reader, fromAutoSave);
	}

	private byte __GetWeaverID(string propertyName)
	{
		if (propertyName == "ShornQuadrants")
		{
			return 7;
		}
		return byte.MaxValue;
	}

	protected override void WriteAutoSaveSyncVars(NetWrite writer)
	{
		base.WriteAutoSaveSyncVars(writer);
		WriteSyncVar(7, writer);
	}

	protected override void ReadAutoSaveSyncVars(NetRead reader)
	{
		base.ReadAutoSaveSyncVars(reader);
		OnSyncVar(7, reader, fromAutoSave: true);
	}

	protected override bool AutoSaveSyncVars(SaveInfo save)
	{
		NetWrite obj = Network.Net.sv.StartWrite();
		WriteAutoSaveSyncVars(obj);
		(byte[] Buffer, int Length) buffer = obj.GetBuffer();
		byte[] item = buffer.Buffer;
		int item2 = buffer.Length;
		byte[] array = _autosaveBuffer;
		if (array == null || array.Length < item2)
		{
			byte[] array2 = BaseEntity._autosaveBufferPool.Rent(item2);
			while (array == null || array.Length < item2)
			{
				byte[] array3 = Interlocked.CompareExchange(ref _autosaveBuffer, array2, array);
				if (array3 == array)
				{
					if (array3 != null)
					{
						BaseEntity._autosaveBufferPool.Return(array3);
					}
					array = array2;
					break;
				}
				array = array3;
			}
			if (array != array2)
			{
				BaseEntity._autosaveBufferPool.Return(array2);
			}
		}
		Buffer.BlockCopy(item, 0, array, 0, item2);
		save.msg.baseEntity.syncVars = array;
		Facepunch.Pool.Free(ref obj);
		return true;
	}

	protected override bool AutoLoadSyncVars(LoadInfo load)
	{
		if (load.msg.baseEntity != null && load.msg.baseEntity.syncVars != null)
		{
			NetRead obj = Facepunch.Pool.Get<NetRead>();
			obj.Init(load.msg.baseEntity.syncVars.AsSpan());
			ReadAutoSaveSyncVars(obj);
			Facepunch.Pool.Free(ref obj);
		}
		return true;
	}

	protected override void ResetSyncVars()
	{
		base.ResetSyncVars();
		__sync_ShornQuadrants = 0;
	}

	protected override bool ShouldInvalidateCache(byte id)
	{
		if (id == 7)
		{
			return true;
		}
		return base.ShouldInvalidateCache(id);
	}
}

