using System.Collections.Generic;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class BlackboardComponent : EntityComponent<BaseEntity>, IServerComponent
{
	private const float factDuration = 30f;

	private Dictionary<string, int> addedFacts = new Dictionary<string, int>();

	private Dictionary<string, float> factExpirationTimes = new Dictionary<string, float>();

	public override void InitShared()
	{
		base.InitShared();
		SingletonComponent<InvokeHandler>.Instance.InvokeRepeating(CleanExpiredFacts, Random.value, 1f);
	}

	public void Add(string value, float duration = 30f)
	{
		if (addedFacts.TryAdd(value, 1))
		{
			factExpirationTimes[value] = Time.time + duration;
		}
	}

	public void Increment(string value, float duration = 30f)
	{
		if (!addedFacts.TryGetValue(value, out var value2))
		{
			value2 = 0;
		}
		value2++;
		addedFacts[value] = value2;
		factExpirationTimes[value] = Time.time + duration;
	}

	public void Remove(string value)
	{
		if (addedFacts.Remove(value))
		{
			factExpirationTimes.Remove(value);
		}
	}

	public void Clear()
	{
		addedFacts.Clear();
		factExpirationTimes.Clear();
	}

	public bool Has(string value)
	{
		return addedFacts.ContainsKey(value);
	}

	public bool Count(string value, out int count)
	{
		return addedFacts.TryGetValue(value, out count);
	}

	public void CleanExpiredFacts()
	{
		using (TimeWarning.New("BlackboardComponent.CleanExpiredFacts"))
		{
			float time = Time.time;
			using PooledList<string> pooledList = Pool.Get<PooledList<string>>();
			foreach (var (text2, _) in addedFacts)
			{
				if (factExpirationTimes[text2] < time)
				{
					pooledList.Add(text2);
				}
			}
			foreach (string item in pooledList)
			{
				Remove(item);
			}
		}
	}
}

