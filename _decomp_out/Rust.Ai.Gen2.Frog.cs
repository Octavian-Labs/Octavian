using System;
using System.Runtime.CompilerServices;
using ConVar;
using Network;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class Frog : CritterAnimal
{
	[ServerVar(Help = "Population active on the server, per square km", ShowInAdminUI = true)]
	public static float Population = 2f;

	private Vector3 __sync_NavTarget;

	[Sync]
	public Vector3 NavTarget
	{
		[CompilerGenerated]
		get
		{
			return __sync_NavTarget;
		}
		[CompilerGenerated]
		set
		{
			if (!IsSyncVarEqual(__sync_NavTarget, value))
			{
				__sync_NavTarget = value;
				byte nameID = __GetWeaverID("NavTarget");
				QueueSyncVar(nameID);
			}
		}
	}

	public void SetNavTarget(Vector3 nextPositionWS)
	{
		NavTarget = nextPositionWS;
	}

	protected override bool WriteSyncVar(byte id, NetWrite writer)
	{
		if (id == 1)
		{
			if (ConVar.Global.developer > 2)
			{
				NetworkableId iD = net.ID;
				Debug.Log("SyncVar Writing: NavTarget for " + iD.ToString());
			}
			SyncVarNetWrite(writer, __sync_NavTarget);
			return true;
		}
		return base.WriteSyncVar(id, writer);
	}

	protected override bool OnSyncVar(byte id, NetRead reader, bool fromAutoSave = false)
	{
		if (id == 1)
		{
			try
			{
				_ = __sync_NavTarget;
				Vector3 _sync_NavTarget = reader.Vector3();
				__sync_NavTarget = _sync_NavTarget;
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
		if (propertyName == "NavTarget")
		{
			return 1;
		}
		return byte.MaxValue;
	}

	protected override void WriteAutoSaveSyncVars(NetWrite writer)
	{
		base.WriteAutoSaveSyncVars(writer);
	}

	protected override void ReadAutoSaveSyncVars(NetRead reader)
	{
		base.ReadAutoSaveSyncVars(reader);
	}

	protected override bool AutoSaveSyncVars(SaveInfo save)
	{
		return true;
	}

	protected override bool AutoLoadSyncVars(LoadInfo load)
	{
		return true;
	}

	protected override void ResetSyncVars()
	{
		base.ResetSyncVars();
		__sync_NavTarget = default(Vector3);
	}

	protected override bool ShouldInvalidateCache(byte id)
	{
		if (id == 1)
		{
			return true;
		}
		return base.ShouldInvalidateCache(id);
	}
}

