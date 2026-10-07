using System.Collections.Generic;
using Facepunch;
using Rust.Ai.Gen2.Nav;
using Spatial;
using UnityEngine;
using UnityEngine.AI;

namespace Rust.Ai.Gen2;

public class NpcCoverManager : SingletonComponent<NpcCoverManager>, IServerComponent
{
	private const float worldSize = 8096f;

	private const int cellSize = 32;

	private Grid<CoverComponent> coverGrid = new Grid<CoverComponent>();

	private Dictionary<Cover, BaseEntity> coverToEntity = new Dictionary<Cover, BaseEntity>();

	private Dictionary<BaseEntity, Cover> entityToCover = new Dictionary<BaseEntity, Cover>();

	public void Add(CoverComponent cover)
	{
		coverGrid.Add(cover, cover.transform.position.x, cover.transform.position.z);
	}

	public void Remove(CoverComponent cover)
	{
		coverGrid.Remove(cover);
	}

	public void GetCoversAround(BaseEntity entity, Vector3 origin, Vector3 threatPosition, float range, List<Cover> covers)
	{
		using (TimeWarning.New("NpcCoverManager.GetCoverAround"))
		{
			if (coverGrid == null)
			{
				return;
			}
			using PooledList<CoverComponent> pooledList = Pool.Get<PooledList<CoverComponent>>();
			coverGrid.Query(origin.x, origin.z, range, pooledList);
			foreach (CoverComponent item in pooledList)
			{
				item.GetCovers(covers, threatPosition);
			}
			using PooledList<BaseEntity> pooledList2 = Pool.Get<PooledList<BaseEntity>>();
			BaseEntity.Query.Server.GetInSphere(origin, range, pooledList2);
			foreach (BaseEntity item2 in pooledList2)
			{
				if (!(item2 is TreeEntity) && !(item2 is OreResourceEntity) && !(item2 is LootContainer))
				{
					continue;
				}
				Bounds bounds = item2.GetComponentInChildren<Collider>().bounds;
				float num = Mathf.Max(bounds.extents.x, bounds.extents.z);
				if (!(num > 1.5f) && !(num < 0.5f))
				{
					using PillarCoverGroup pillarCoverGroup = Pool.Get<PillarCoverGroup>();
					pillarCoverGroup.GenerateCovers(item2.gameObject);
					pillarCoverGroup.GetCovers(item2.transform, covers, threatPosition);
				}
			}
			for (int num2 = covers.Count - 1; num2 >= 0; num2--)
			{
				if (coverToEntity.TryGetValue(covers[num2], out var value) && value != entity)
				{
					covers.RemoveAt(num2);
				}
				else if (Vector3.Distance(covers[num2].position, origin) > range)
				{
					covers.RemoveAt(num2);
				}
			}
		}
	}

	public Cover? FindBestCover(RustNavMeshAgent agent, Vector3 threatPosition, float radius, float preferedEngagementDistance, ref RustNavMeshPath path, bool requireLoS, float? targetRadius = null)
	{
		using (TimeWarning.New("FindBestCover"))
		{
			using PooledList<Cover> pooledList = Pool.Get<PooledList<Cover>>();
			BaseEntity baseEntity = agent.GetBaseEntity();
			GetCoversAround(baseEntity, agent.transform.position, threatPosition, radius, pooledList);
			using PooledList<(Cover, float)> pooledList2 = Pool.Get<PooledList<(Cover, float)>>();
			foreach (Cover item2 in pooledList)
			{
				if (item2.ProtectsFrom(threatPosition))
				{
					float num = 0f - Mathf.Abs(Vector3.Distance(item2.position, threatPosition) - preferedEngagementDistance);
					float item = (0f - Vector3.Distance(agent.transform.position, item2.position)) * 4f + num;
					pooledList2.Add((item2, item));
				}
			}
			pooledList2.Sort(((Cover cover, float score) a, (Cover cover, float score) b) => b.score.CompareTo(a.score));
			pooledList.Clear();
			foreach (var item3 in pooledList2)
			{
				pooledList.Add(item3.Item1);
			}
			return GetFirstUsableCover(agent, threatPosition, pooledList, radius * 4f, ref path, requireLoS, targetRadius);
		}
	}

	private static Cover? GetFirstUsableCover(RustNavMeshAgent agent, Vector3 threatPosition, List<Cover> covers, float maxPathLength, ref RustNavMeshPath navPath, bool requireLoS, float? targetRadius)
	{
		using (TimeWarning.New("GetFirstUsableCover"))
		{
			BaseEntity baseEntity = agent.GetBaseEntity();
			foreach (Cover cover in covers)
			{
				if ((!requireLoS || cover.GetFirstUnoccludedPeek(threatPosition, baseEntity) != 0) && agent.CalculatePath(cover.position, navPath) && navPath.status == NavMeshPathStatus.PathComplete && !(navPath.GetPathLength() > maxPathLength) && (!targetRadius.HasValue || !DoesPathIntersectTarget(navPath, agent.WorldToNavSpace(threatPosition), targetRadius.Value)))
				{
					return cover;
				}
			}
			return null;
		}
	}

	private static bool DoesPathIntersectTarget(RustNavMeshPath path, NavVector3 targetPosition, float targetRadius)
	{
		for (int i = 0; i < path.corners.Count - 1; i++)
		{
			NavVector3 navVector = path.corners[i];
			NavVector3 navVector2 = path.corners[i + 1];
			if (SegmentSphereIntersection(navVector.Value, navVector2.Value, targetPosition.Value, targetRadius))
			{
				return true;
			}
		}
		return false;
	}

	public static bool SegmentSphereIntersection(Vector3 start, Vector3 end, Vector3 center, float radius)
	{
		Vector3 vector = end - start;
		if (vector.sqrMagnitude == 0f)
		{
			return false;
		}
		float num = radius * radius;
		bool flag = (start - center).sqrMagnitude <= num;
		bool flag2 = (end - center).sqrMagnitude <= num;
		if (flag && flag2)
		{
			return true;
		}
		if (flag ^ flag2)
		{
			return true;
		}
		Vector3 vector2 = start - center;
		float num2 = Vector3.Dot(vector, vector);
		float num3 = 2f * Vector3.Dot(vector2, vector);
		float num4 = Vector3.Dot(vector2, vector2) - num;
		float num5 = num3 * num3 - 4f * num2 * num4;
		if (Mathf.Abs(num5) < Mathf.Epsilon)
		{
			num5 = 0f;
		}
		if (num5 < 0f)
		{
			return false;
		}
		float num6 = Mathf.Sqrt(num5);
		float num7 = (0f - num3 - num6) / (2f * num2);
		float num8 = (0f - num3 + num6) / (2f * num2);
		if (!(num7 >= 0f) || !(num7 <= 1f))
		{
			if (num8 >= 0f)
			{
				return num8 <= 1f;
			}
			return false;
		}
		return true;
	}

	public void Reserve(Cover cover, BaseEntity entity)
	{
		if (TryGetCover(entity, out var cover2))
		{
			Release(cover2);
		}
		coverToEntity[cover] = entity;
		entityToCover[entity] = cover;
	}

	public void Release(Cover cover)
	{
		entityToCover.Remove(coverToEntity[cover]);
		coverToEntity.Remove(cover);
	}

	public bool TryGetCover(BaseEntity entity, out Cover cover)
	{
		if (entityToCover.TryGetValue(entity, out cover))
		{
			return true;
		}
		return false;
	}

	public void Tick()
	{
		using (TimeWarning.New("NpcCoverManager.Tick"))
		{
			using PooledList<BaseEntity> pooledList = Pool.Get<PooledList<BaseEntity>>();
			foreach (KeyValuePair<BaseEntity, Cover> item in entityToCover)
			{
				BaseEntity key = item.Key;
				if (!key.IsValid() || (key is BaseCombatEntity baseCombatEntity && baseCombatEntity.IsDead()))
				{
					pooledList.Add(item.Key);
				}
			}
			foreach (BaseEntity item2 in pooledList)
			{
				Release(entityToCover[item2]);
			}
		}
	}
}

