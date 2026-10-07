using System.Collections.Generic;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

public static class LivestockCensus
{
	public enum DeathCause
	{
		OldAge,
		Neglect,
		Killed,
		Other,
		Count
	}

	private class AreaTally
	{
		public Vector3 Centre;

		public float Radius;

		public int Births;

		public int DungDropped;

		public int TroughItemsEaten;

		public readonly int[] Deaths = new int[4];

		public void Clear()
		{
			Births = 0;
			DungDropped = 0;
			TroughItemsEaten = 0;
			for (int i = 0; i < Deaths.Length; i++)
			{
				Deaths[i] = 0;
			}
		}
	}

	public struct Snapshot
	{
		public int Count;

		public int Cattle;

		public int Sheep;

		public int OtherSpecies;

		public int Adults;

		public int Infants;

		public int Males;

		public int Females;

		public int Pregnant;

		public int Leading;

		public float MeanFullness;

		public float MinFullness;

		public float MeanHydration;

		public float MinHydration;

		public float MeanPersonalSpace;

		public float MinPersonalSpace;

		public float MeanHerdLoadTarget;

		public float MeanPackingTarget;

		public float MeanContentment;

		public float MinContentment;

		public float MeanCondition;

		public float MinCondition;

		public int BreedingPaused;

		public int Sleeping;

		public float MeanGrazeShare;

		public float MeanRestShare;

		public float HerdRadius;

		public Vector3 Centroid;
	}

	private static readonly int[] deaths = new int[4];

	private static readonly List<AreaTally> areas = new List<AreaTally>();

	public static int Births { get; private set; }

	public static int DungDropped { get; private set; }

	public static int TroughItemsEaten { get; private set; }

	public static int AreaCount => areas.Count;

	public static int Deaths
	{
		get
		{
			int num = 0;
			for (int i = 0; i < deaths.Length; i++)
			{
				num += deaths[i];
			}
			return num;
		}
	}

	public static int AddArea(Vector3 centre, float radius)
	{
		areas.Add(new AreaTally
		{
			Centre = centre,
			Radius = radius
		});
		return areas.Count - 1;
	}

	public static void ClearAreas()
	{
		areas.Clear();
	}

	public static float ClosestAreaGap()
	{
		float num = float.MaxValue;
		for (int i = 0; i < areas.Count; i++)
		{
			for (int j = i + 1; j < areas.Count; j++)
			{
				float num2 = Vector3.Distance(areas[i].Centre, areas[j].Centre) - areas[i].Radius - areas[j].Radius;
				if (num2 < num)
				{
					num = num2;
				}
			}
		}
		if (areas.Count >= 2)
		{
			return num;
		}
		return float.MaxValue;
	}

	public static int BirthsIn(int area)
	{
		return areas[area].Births;
	}

	public static int DungDroppedIn(int area)
	{
		return areas[area].DungDropped;
	}

	public static int TroughItemsEatenIn(int area)
	{
		return areas[area].TroughItemsEaten;
	}

	public static int DeathsIn(int area, DeathCause cause)
	{
		return areas[area].Deaths[(int)cause];
	}

	private static AreaTally TallyAt(Vector3 position)
	{
		for (int i = 0; i < areas.Count; i++)
		{
			AreaTally areaTally = areas[i];
			if (Vector3.Distance(position, areaTally.Centre) <= areaTally.Radius)
			{
				return areaTally;
			}
		}
		return null;
	}

	public static int DeathsBy(DeathCause cause)
	{
		return deaths[(int)cause];
	}

	public static void Reset()
	{
		Births = 0;
		DungDropped = 0;
		TroughItemsEaten = 0;
		for (int i = 0; i < deaths.Length; i++)
		{
			deaths[i] = 0;
		}
		for (int j = 0; j < areas.Count; j++)
		{
			areas[j].Clear();
		}
	}

	public static void RecordBirth(Vector3 at)
	{
		Births++;
		AreaTally areaTally = TallyAt(at);
		if (areaTally != null)
		{
			areaTally.Births++;
		}
	}

	public static void RecordDung(Vector3 at)
	{
		DungDropped++;
		AreaTally areaTally = TallyAt(at);
		if (areaTally != null)
		{
			areaTally.DungDropped++;
		}
	}

	public static void RecordTroughItemEaten(Vector3 at)
	{
		TroughItemsEaten++;
		AreaTally areaTally = TallyAt(at);
		if (areaTally != null)
		{
			areaTally.TroughItemsEaten++;
		}
	}

	public static void RecordDeath(HitInfo info, Vector3 at)
	{
		DeathCause deathCause = CauseOf(info);
		deaths[(int)deathCause]++;
		AreaTally areaTally = TallyAt(at);
		if (areaTally != null)
		{
			areaTally.Deaths[(int)deathCause]++;
		}
	}

	public static DeathCause CauseOf(HitInfo info)
	{
		if (info == null)
		{
			return DeathCause.OldAge;
		}
		if (info.damageTypes != null && info.damageTypes.Has(DamageType.Hunger) && info.Initiator == null)
		{
			return DeathCause.Neglect;
		}
		if (!(info.InitiatorPlayer != null))
		{
			return DeathCause.Other;
		}
		return DeathCause.Killed;
	}

	public static Snapshot Sample(Vector3 origin, float radius)
	{
		using PooledList<LivestockAnimal> pooledList = Pool.Get<PooledList<LivestockAnimal>>();
		LivestockAnimal[] array = BaseEntity.Util.FindAll<LivestockAnimal>();
		foreach (LivestockAnimal livestockAnimal in array)
		{
			if (!(livestockAnimal == null) && !livestockAnimal.IsDestroyed && !livestockAnimal.IsDead() && livestockAnimal.isServer && (!(radius > 0f) || !(Vector3.Distance(livestockAnimal.transform.position, origin) > radius)))
			{
				pooledList.Add(livestockAnimal);
			}
		}
		return SampleLiving(pooledList);
	}

	public static Snapshot Sample(List<LivestockAnimal> herd)
	{
		using PooledList<LivestockAnimal> pooledList = Pool.Get<PooledList<LivestockAnimal>>();
		for (int i = 0; i < herd.Count; i++)
		{
			LivestockAnimal livestockAnimal = herd[i];
			if (!(livestockAnimal == null) && !livestockAnimal.IsDestroyed && !livestockAnimal.IsDead() && livestockAnimal.isServer)
			{
				pooledList.Add(livestockAnimal);
			}
		}
		return SampleLiving(pooledList);
	}

	private static Snapshot SampleLiving(List<LivestockAnimal> sampled)
	{
		Snapshot snapshot = default(Snapshot);
		snapshot.MinFullness = 1f;
		snapshot.MinHydration = 1f;
		snapshot.MinPersonalSpace = 1f;
		snapshot.MinContentment = 1f;
		snapshot.MinCondition = 1f;
		Snapshot result = snapshot;
		if (sampled.Count == 0)
		{
			result.MinFullness = 0f;
			result.MinHydration = 0f;
			result.MinPersonalSpace = 0f;
			result.MinContentment = 0f;
			result.MinCondition = 0f;
			return result;
		}
		Vector3 zero = Vector3.zero;
		foreach (LivestockAnimal item in sampled)
		{
			result.Count++;
			zero += item.transform.position;
			if (item is Cow)
			{
				result.Cattle++;
			}
			else if (item is Sheep)
			{
				result.Sheep++;
			}
			else
			{
				result.OtherSpecies++;
			}
			if (item.IsAdult())
			{
				result.Adults++;
			}
			else
			{
				result.Infants++;
			}
			if (item.IsMale)
			{
				result.Males++;
			}
			else
			{
				result.Females++;
			}
			if (item.IsPregnant())
			{
				result.Pregnant++;
			}
			if (item.IsLeading())
			{
				result.Leading++;
			}
			float needValue = item.Fullness.NeedValue;
			float needValue2 = item.Hydration.NeedValue;
			float needValue3 = item.PersonalSpace.NeedValue;
			float contentment = item.Contentment;
			result.MeanFullness += needValue;
			result.MeanHydration += needValue2;
			result.MeanPersonalSpace += needValue3;
			result.MeanContentment += contentment;
			result.MeanHerdLoadTarget += item.LastHerdLoadTarget;
			result.MeanPackingTarget += item.LastPackingTarget;
			result.MinFullness = Mathf.Min(result.MinFullness, needValue);
			result.MinHydration = Mathf.Min(result.MinHydration, needValue2);
			result.MinPersonalSpace = Mathf.Min(result.MinPersonalSpace, needValue3);
			result.MinContentment = Mathf.Min(result.MinContentment, contentment);
			result.MeanCondition += item.Condition;
			result.MinCondition = Mathf.Min(result.MinCondition, item.Condition);
			if (item.BreedingPaused)
			{
				result.BreedingPaused++;
			}
			if (item.IsSleeping())
			{
				result.Sleeping++;
			}
			result.MeanGrazeShare += item.ShareOfDay(LivestockAnimal.DayActivity.Grazing);
			result.MeanRestShare += item.ShareOfDay(LivestockAnimal.DayActivity.Resting);
		}
		int count = result.Count;
		result.MeanFullness /= count;
		result.MeanHydration /= count;
		result.MeanPersonalSpace /= count;
		result.MeanContentment /= count;
		result.MeanCondition /= count;
		result.MeanHerdLoadTarget /= count;
		result.MeanPackingTarget /= count;
		result.MeanGrazeShare /= count;
		result.MeanRestShare /= count;
		result.Centroid = zero / count;
		foreach (LivestockAnimal item2 in sampled)
		{
			float num = Vector3.Distance(item2.transform.position, result.Centroid);
			if (num > result.HerdRadius)
			{
				result.HerdRadius = num;
			}
		}
		return result;
	}

	public static int CountDroppedItems(ItemDefinition definition, Vector3 origin, float radius)
	{
		if (definition == null)
		{
			return 0;
		}
		int num = 0;
		DroppedItem[] array = BaseEntity.Util.FindAll<DroppedItem>();
		foreach (DroppedItem droppedItem in array)
		{
			if (!(droppedItem == null) && !droppedItem.IsDestroyed && droppedItem.item != null && droppedItem.isServer && !(droppedItem.item.info != definition) && (!(radius > 0f) || !(Vector3.Distance(droppedItem.transform.position, origin) > radius)))
			{
				num++;
			}
		}
		return num;
	}
}

