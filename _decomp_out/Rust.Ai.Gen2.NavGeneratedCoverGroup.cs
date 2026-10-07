using System.Collections.Generic;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class NavGeneratedCoverGroup : CoverGroup
{
	[SerializeField]
	public List<Cover> cachedCovers = new List<Cover>();

	public override bool IsSlow => true;

	public override bool GetCovers(Transform transform, List<Cover> covers, Vector3 from)
	{
		if (cachedCovers.Count == 0)
		{
			return false;
		}
		foreach (Cover cachedCover in cachedCovers)
		{
			Cover item = cachedCover;
			item.position = transform.TransformPoint(cachedCover.position);
			item.yaw = cachedCover.yaw + transform.rotation.eulerAngles.y;
			covers.Add(item);
		}
		return true;
	}
}

