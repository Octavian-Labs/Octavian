using System.Collections.Generic;
using Rust.Ai.Gen2.Nav;
using UnityEngine;

namespace Rust.Ai.Gen2;

public static class NPCFlankSpot
{
	private static readonly List<Quaternion> sampleRotations = new List<Quaternion>
	{
		Quaternion.Euler(0f, 90f, 0f),
		Quaternion.Euler(0f, -90f, 0f),
		Quaternion.Euler(0f, 45f, 0f),
		Quaternion.Euler(0f, -45f, 0f)
	};

	public static bool Find(RustNavMeshAgent agent, NavVector3 enemyPositionNs, RustNavMeshPath directPath, RustNavMeshPath pathToFlank, RustNavMeshPath pathFromFlankToEnemy, float flankWidth = 15f, float sampleRadius = 3.5f, float minAngle = 30f, float minSimilarity = 0.25f)
	{
		if (UnityEngine.Application.isPlaying || !UnityEngine.Application.isEditor)
		{
			agent.GetBaseEntity();
		}
		if (directPath.corners.Count < 2)
		{
			return false;
		}
		NavVector3 navVector = (enemyPositionNs - agent.nextPosition).NormalizeXZ() * flankWidth;
		for (int i = 0; i < sampleRotations.Count; i++)
		{
			NavVector3 positionNS = enemyPositionNs + sampleRotations[i] * navVector;
			if (!agent.SamplePosition(positionNS, out var hitNS, sampleRadius))
			{
				continue;
			}
			NavVector3 position = hitNS.position;
			if (!agent.CalculatePath(position, enemyPositionNs, pathFromFlankToEnemy) || pathFromFlankToEnemy.status != 0 || pathFromFlankToEnemy.corners.Count < 2)
			{
				continue;
			}
			List<NavVector3> corners = directPath.corners;
			NavVector3 navVector2 = corners[corners.Count - 1];
			List<NavVector3> corners2 = directPath.corners;
			Vector3 value = (navVector2 - corners2[corners2.Count - 2]).NormalizeXZ().Value;
			List<NavVector3> corners3 = pathFromFlankToEnemy.corners;
			NavVector3 navVector3 = corners3[corners3.Count - 1];
			List<NavVector3> corners4 = pathFromFlankToEnemy.corners;
			if (Vector3.Angle(value, (navVector3 - corners4[corners4.Count - 2]).NormalizeXZ().Value) < minAngle || !agent.CalculatePath(position, pathToFlank) || pathToFlank.status != 0 || State_Flank.ComputePathsInitialSimilarity(directPath, pathToFlank) > minSimilarity)
			{
				continue;
			}
			NavVector3? navVector4 = null;
			if (pathToFlank.corners.Count < 2)
			{
				continue;
			}
			for (int j = 0; j < pathToFlank.corners.Count - 1; j++)
			{
				for (int num = pathFromFlankToEnemy.corners.Count - 1; num >= 1; num--)
				{
					if (NavVector3.Distance(pathToFlank.corners[j], pathFromFlankToEnemy.corners[num]) < 2f && !agent.Raycast(pathToFlank.corners[j], pathFromFlankToEnemy.corners[num], out var _))
					{
						navVector4 = pathToFlank.corners[j];
						break;
					}
				}
				if (navVector4.HasValue)
				{
					break;
				}
			}
			if (navVector4.HasValue)
			{
				position = navVector4.Value;
				if (!agent.CalculatePath(position, enemyPositionNs, pathFromFlankToEnemy) || pathFromFlankToEnemy.status != 0)
				{
					return false;
				}
				if (pathFromFlankToEnemy.corners.Count < 2)
				{
					return false;
				}
				List<NavVector3> corners5 = directPath.corners;
				NavVector3 navVector5 = corners5[corners5.Count - 1];
				List<NavVector3> corners6 = directPath.corners;
				Vector3 value2 = (navVector5 - corners6[corners6.Count - 2]).NormalizeXZ().Value;
				List<NavVector3> corners7 = pathFromFlankToEnemy.corners;
				NavVector3 navVector6 = corners7[corners7.Count - 1];
				List<NavVector3> corners8 = pathFromFlankToEnemy.corners;
				if (Vector3.Angle(value2, (navVector6 - corners8[corners8.Count - 2]).NormalizeXZ().Value) < minAngle)
				{
					return false;
				}
				if (!agent.CalculatePath(position, pathToFlank) || pathToFlank.status != 0)
				{
					return false;
				}
			}
			return true;
		}
		return false;
	}
}

