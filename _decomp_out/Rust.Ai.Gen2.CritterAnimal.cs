using System;
using System.Runtime.CompilerServices;
using System.Threading;
using ConVar;
using Facepunch;
using Network;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class CritterAnimal : BaseNPC2, ISimpleHearingReceiver
{
	public enum CritterType
	{
		Frog,
		Rabbit,
		Squirrel,
		Crabs
	}

	public const Flags Interested = Flags.Reserved8;

	public const Flags Deactivated = Flags.Reserved12;

	public CritterType CritterSpecies;

	public bool resetAnimatorOnEnable;

	private TimeSince lastLoudNoise;

	public const int MaxAnimalNameLength = 24;

	private string __sync_AnimalName;

	[Sync(Autosave = true)]
	public string AnimalName
	{
		[CompilerGenerated]
		get
		{
			return __sync_AnimalName;
		}
		[CompilerGenerated]
		set
		{
			if (!IsSyncVarEqual(__sync_AnimalName, value))
			{
				__sync_AnimalName = value;
				byte nameID = __GetWeaverID("AnimalName");
				QueueSyncVar(nameID);
			}
		}
	}

	public bool RecentlyHeardLoudNoise => (float)lastLoudNoise < 10f;

	public bool IsInterested()
	{
		return HasFlag(Flags.Reserved8);
	}

	public bool IsDeactivated()
	{
		return HasFlag(Flags.Reserved12);
	}

	public override void ServerInit()
	{
		base.ServerInit();
		lastLoudNoise = 100f;
	}

	public void SetInterested(bool interested)
	{
		SetNetworkedFlag(Flags.Reserved8, interested);
	}

	public void SetDeactivatedFlag(bool toggle)
	{
		SetNetworkedFlag(Flags.Reserved12, toggle);
	}

	protected void SetNetworkedFlag(Flags flag, bool value)
	{
		using FlagsUpdateScope flagsUpdateScope = StartSetFlags(FlagsUpdateMode.SendNetworkUpdate);
		flagsUpdateScope.Set(flag, value);
	}

	public void OnHeardNoise(NpcNoiseEvent noise)
	{
		if (noise.Intensity >= NpcNoiseIntensity.Medium)
		{
			lastLoudNoise = 0f;
		}
	}

	protected override bool WriteSyncVar(byte id, NetWrite writer)
	{
		if (id == 0)
		{
			if (ConVar.Global.developer > 2)
			{
				NetworkableId iD = net.ID;
				Debug.Log("SyncVar Writing: AnimalName for " + iD.ToString());
			}
			SyncVarNetWrite(writer, __sync_AnimalName);
			return true;
		}
		return base.WriteSyncVar(id, writer);
	}

	protected override bool OnSyncVar(byte id, NetRead reader, bool fromAutoSave = false)
	{
		if (id == 0)
		{
			try
			{
				_ = __sync_AnimalName;
				string _sync_AnimalName = reader.String();
				__sync_AnimalName = _sync_AnimalName;
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
		if (propertyName == "AnimalName")
		{
			return 0;
		}
		return byte.MaxValue;
	}

	protected override void WriteAutoSaveSyncVars(NetWrite writer)
	{
		base.WriteAutoSaveSyncVars(writer);
		WriteSyncVar(0, writer);
	}

	protected override void ReadAutoSaveSyncVars(NetRead reader)
	{
		base.ReadAutoSaveSyncVars(reader);
		OnSyncVar(0, reader, fromAutoSave: true);
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
		__sync_AnimalName = null;
	}

	protected override bool ShouldInvalidateCache(byte id)
	{
		if (id == 0)
		{
			return true;
		}
		return base.ShouldInvalidateCache(id);
	}
}

