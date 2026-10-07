using System.Collections.Generic;
using ConVar;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class ClawMarkSpawner : EntityComponent<BaseEntity>, IServerComponent
{
	public GameObjectRef clawDecal;

	public float radius = 130f;

	public float height = 1.8f;

	public float minTreeRadius = 1.4f;

	[Range(0f, 1f)]
	public float ratioOfTreesMarked = 0.5f;

	private static bool showClawMarks;

	private List<ClawMark> clawMarks = new List<ClawMark>();

	[ServerVar]
	public static void ShowClawMarks(ConsoleSystem.Arg arg)
	{
		bool @bool = arg.GetBool(0, !showClawMarks);
		if (@bool == showClawMarks)
		{
			arg.ReplyWith("Claw marks are already " + (showClawMarks ? "visible" : "hidden") + ".");
			return;
		}
		showClawMarks = @bool;
		BaseNPC2[] array;
		if (showClawMarks)
		{
			array = BaseEntity.Util.FindAll<BaseNPC2>();
			for (int i = 0; i < array.Length; i++)
			{
				ClawMarkSpawner component = array[i].GetComponent<ClawMarkSpawner>();
				if (component != null)
				{
					component.SpawnClawMarks();
				}
			}
			arg.ReplyWith("Claw marks are now visible.");
			return;
		}
		array = BaseEntity.Util.FindAll<BaseNPC2>();
		for (int i = 0; i < array.Length; i++)
		{
			ClawMarkSpawner component2 = array[i].GetComponent<ClawMarkSpawner>();
			if (component2 != null)
			{
				component2.ClearClawMarks();
			}
		}
		arg.ReplyWith("Claw marks are now hidden.");
	}

	public override void InitShared()
	{
		UpdateBaseEntity();
		if (showClawMarks)
		{
			SpawnClawMarks();
		}
	}

	public override void DestroyShared()
	{
		ClearClawMarks();
	}

	private void SpawnClawMarks()
	{
		if (!base.baseEntity.isServer)
		{
			return;
		}
		if (AI.logIssues && clawMarks.Count > 0)
		{
			Debug.LogWarning($"Claw marks already spawned for {base.baseEntity}.");
			return;
		}
		using PooledList<TreeEntity> pooledList = Facepunch.Pool.Get<PooledList<TreeEntity>>();
		BaseEntity.Query.Server.GetInSphere(base.baseEntity.transform.position, radius, pooledList);
		clawMarks.Capacity = pooledList.Count;
		foreach (TreeEntity item in pooledList)
		{
			if (Random.value > ratioOfTreesMarked || item.serverCollider == null)
			{
				continue;
			}
			float num = Mathf.Min(item.bounds.extents.x, item.bounds.extents.z);
			if (num < minTreeRadius)
			{
				continue;
			}
			Vector3 vector = item.transform.position + Vector3.up * height;
			Vector3 forward = item.transform.forward;
			if (!item.serverCollider.Raycast(new Ray(vector - forward * num, forward), out var hitInfo, num))
			{
				continue;
			}
			ClawMark clawMark = GameManager.server.CreateEntity(clawDecal.resourcePath, hitInfo.point, Quaternion.LookRotation(-hitInfo.normal)) as ClawMark;
			if (clawMark == null)
			{
				if (AI.logIssues)
				{
					Debug.LogWarning($"Failed to create claw mark for {base.baseEntity}.");
				}
			}
			else
			{
				clawMarks.Add(clawMark);
				clawMark.SetParent(item, worldPositionStays: true);
				clawMark.Spawn();
			}
		}
	}

	private void ClearClawMarks()
	{
		if (!base.baseEntity.isServer)
		{
			return;
		}
		foreach (ClawMark clawMark in clawMarks)
		{
			clawMark.Kill();
		}
		clawMarks.Clear();
	}
}

