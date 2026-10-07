using System;
using System.Collections.Generic;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class CoverGroup : Pool.IPooled, IDisposable
{
	[SerializeField]
	protected bool isTall;

	protected List<Cover> covers = new List<Cover>();

	public bool HasCovers => covers.Count > 0;

	public virtual bool IsSlow => false;

	public virtual void GenerateCovers(GameObject gameObject)
	{
	}

	public virtual bool GetCovers(Transform transform, List<Cover> covers, Vector3 from)
	{
		return false;
	}

	public virtual void EnterPool()
	{
		covers.Clear();
	}

	public virtual void LeavePool()
	{
	}

	public void Dispose()
	{
		CoverGroup obj = this;
		Pool.Free(ref obj);
	}
}

