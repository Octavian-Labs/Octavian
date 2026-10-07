using System.Collections.Generic;
using Rust.Ai.Gen2.Nav;
using UnityEngine;
using UnityEngine.AI;

namespace Rust.Ai.Gen2;

public static class NPCOverwatchSpot
{
	public static (NavVector3 loc, Vector3 dir)? Find(List<NavVector3> corners)
	{
		if (corners.Count < 3)
		{
			return null;
		}
		for (int num = corners.Count - 1; num >= 2; num--)
		{
			NavVector3 navVector = corners[num];
			NavVector3 navVector2 = corners[num - 1];
			NavVector3 navVector3 = corners[num - 2];
			NavVector3 navVector4 = (navVector - navVector2).NormalizeXZ();
			NavVector3 navVector5 = (navVector3 - navVector2).NormalizeXZ();
			NavVector3 navVector6 = (navVector4 + navVector5).NormalizeXZ() * -1f * 0.01f;
			navVector += navVector6;
			navVector2 += navVector6;
			NavVector3 navVector7 = (navVector2 - navVector).NormalizeXZ() * 100f;
			if (NavMesh.Raycast(navVector.Value, (navVector + navVector7).Value, out var hit, -1))
			{
				NavVector3 navVector8 = new NavVector3(hit.position);
				if (hit.distance >= 7f)
				{
					Vector3 value = corners[corners.Count - 1].Value;
					Vector3 position = hit.position;
					if (Physics.Linecast(value + 1.7f * Vector3.up, position + 1.7f * Vector3.up, out var _, 1218652417) && Physics.Linecast(value + 0.2f * Vector3.up, position + 0.2f * Vector3.up, out var _, 1218652417))
					{
						return (navVector8, NavVector3.LookDirection(navVector8, navVector));
					}
				}
			}
		}
		return null;
	}
}

