using ConVar;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

public static class NpcPushHelper
{
	public static void CoordinatePush(BaseEntity coordinator, float maxDistance = 50f)
	{
		if (coordinator.TryGetComponent<SenseComponent>(out var component) && coordinator.TryGetComponent<NpcZoneComponent>(out var component2) && component.FindTarget(out var target) && component.FindLKP(target, out var lkp) && FindBestPartner(coordinator.transform.position, component, component2, out var bestPartner, maxDistance) && bestPartner.TryGetComponent<Scientist2FSM>(out var component3))
		{
			component3.RushPositionTrans.Trigger(new FSMPayload
			{
				entity = target,
				position = lkp
			});
			if (AI.npcBarksEnabled && coordinator.TryGetComponent<NpcBarkComponent>(out var component4))
			{
				component4.PlayVoicelineFromCategory(ENPCVoicelineCategory.Push, bestPartner);
			}
		}
	}

	public static bool FindBestPartner(Vector3 worldPosition, SenseComponent SenseComponent, NpcZoneComponent NpcZoneComponent, out BaseEntity bestPartner, float maxDistance)
	{
		using (TimeWarning.New("NpcPushHelper.FindBestPartner"))
		{
			bestPartner = null;
			using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
			SenseComponent.GetPerceivedAllies(pooledList);
			float num = float.MaxValue;
			BaseEntity baseEntity = null;
			float num2 = float.MaxValue;
			BaseEntity baseEntity2 = null;
			foreach (BaseEntity item in pooledList)
			{
				float num3 = DistanceWithExaggeratedY(worldPosition, item.transform.position);
				if (!(num3 > maxDistance))
				{
					if (num3 < num2)
					{
						num2 = num3;
						baseEntity2 = item;
					}
					if (NpcZoneComponent.IsInSameZone(item) && num3 < num)
					{
						num = num3;
						baseEntity = item;
					}
				}
			}
			if (baseEntity2 == null && baseEntity == null)
			{
				return false;
			}
			bestPartner = baseEntity ?? baseEntity2;
			return true;
		}
	}

	private static float DistanceWithExaggeratedY(Vector3 a, Vector3 b, float yDistMultiplier = 6f)
	{
		float num = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
		float num2 = Mathf.Abs(a.y - b.y);
		return num + num2 * yDistMultiplier;
	}
}

