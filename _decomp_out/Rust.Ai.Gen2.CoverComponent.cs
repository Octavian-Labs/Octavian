using System.Collections.Generic;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class CoverComponent : MonoBehaviour, IServerComponent
{
	[SerializeReference]
	[Polymorphic]
	public CoverGroup coverGroup;

	private void OnEnable()
	{
		if (coverGroup != null)
		{
			coverGroup.GenerateCovers(base.gameObject);
		}
		SingletonComponent<NpcCoverManager>.Instance.Add(this);
	}

	private void OnDisable()
	{
		if (SingletonComponent<NpcCoverManager>.Instance != null)
		{
			SingletonComponent<NpcCoverManager>.Instance.Remove(this);
		}
	}

	public bool GetCovers(List<Cover> covers, Vector3 from)
	{
		return coverGroup.GetCovers(base.transform, covers, from);
	}

	[Button("Bake")]
	public void Bake()
	{
		if (coverGroup != null)
		{
			coverGroup.GenerateCovers(base.gameObject);
		}
	}
}

