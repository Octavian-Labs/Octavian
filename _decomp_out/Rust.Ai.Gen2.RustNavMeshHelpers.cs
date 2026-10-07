using ConVar;
using Rust.Ai.Gen2.Nav;
using UnityEngine;
using UnityEngine.AI;

namespace Rust.Ai.Gen2;

public static class RustNavMeshHelpers
{
	private static readonly Vector3[] cornerBuffer = new Vector3[256];

	public const int AllAreas = -1;

	public static int GetAreaFromName(string areaName)
	{
		return NavMesh.GetAreaFromName(areaName);
	}

	public static bool SamplePosition(Vector3 sourcePositionWS, out NavMeshHit hitWS, float maxDistance, int areaMask, bool allowNpcDoors = false)
	{
		hitWS = default(NavMeshHit);
		if (AI.useUnityNavmesh)
		{
			return NavMesh.SamplePosition(sourcePositionWS, out hitWS, maxDistance, areaMask);
		}
		IndependantNavmesh independantNavmesh = IndependantNavmesh.FindNavmeshAtPosition(sourcePositionWS);
		NavHit hit;
		if (independantNavmesh != null && independantNavmesh.Navmesh != null && independantNavmesh.Navmesh.IsBuilt())
		{
			NavVector3 position = independantNavmesh.TransformPointFromWorldSpaceToNavSpace(sourcePositionWS);
			if (!independantNavmesh.Navmesh.SamplePosition(position, out hit, Vector3.one * maxDistance, allowNpcDoors))
			{
				return false;
			}
			hitWS = hit.ToUnity();
			hitWS.position = independantNavmesh.TransformPointFromNavSpaceToWorldSpace(hit.position);
			hitWS.normal = independantNavmesh.TransformDirectionFromNavSpaceToWorldSpace(hit.normal);
			return true;
		}
		if (!RustNavigation.Instance.IsDefaultNavmeshBuilt())
		{
			if (AI.logIssues)
			{
				RustNavigation.LogError("Trying to sample position on the navmesh before it's built. This will always fail. Make sure to check IsDefaultNavmeshBuilt() before sampling positions.");
			}
			hitWS = default(NavMeshHit);
			return false;
		}
		if (!RustNavigation.Instance.DefaultNavmesh.SamplePosition(new NavVector3(sourcePositionWS), out hit, Vector3.one * maxDistance, allowNpcDoors))
		{
			return false;
		}
		hitWS = hit.ToUnity();
		return true;
	}

	public static bool Raycast(Vector3 sourcePositionWS, Vector3 targetPositionWS, out NavMeshHit hitWS, int areaMask, bool allowNpcDoors = false)
	{
		hitWS = default(NavMeshHit);
		if (AI.useUnityNavmesh)
		{
			return NavMesh.Raycast(sourcePositionWS, targetPositionWS, out hitWS, areaMask);
		}
		IndependantNavmesh independantNavmesh = IndependantNavmesh.FindNavmeshAtPosition(sourcePositionWS);
		if (independantNavmesh != null && independantNavmesh.Navmesh != null && independantNavmesh.Navmesh.IsBuilt())
		{
			NavVector3 startPos = independantNavmesh.TransformPointFromWorldSpaceToNavSpace(sourcePositionWS);
			NavVector3 endPos = independantNavmesh.TransformPointFromWorldSpaceToNavSpace(targetPositionWS);
			NavHit hit;
			bool result = independantNavmesh.Navmesh.Raycast(startPos, endPos, out hit, allowNpcDoors);
			hitWS = hit.ToUnity();
			hitWS.position = independantNavmesh.TransformPointFromNavSpaceToWorldSpace(hit.position);
			hitWS.normal = independantNavmesh.TransformDirectionFromNavSpaceToWorldSpace(hit.normal);
			return result;
		}
		if (!RustNavigation.Instance.IsDefaultNavmeshBuilt())
		{
			if (AI.logIssues)
			{
				RustNavigation.LogError("Trying to raycast on the navmesh before it's built. This will always fail. Make sure to check IsDefaultNavmeshBuilt() before raycasting.");
			}
			hitWS = default(NavMeshHit);
			hitWS.position = Vector3.negativeInfinity;
			return false;
		}
		NavHit hit2;
		bool result2 = RustNavigation.Instance.DefaultNavmesh.Raycast(new NavVector3(sourcePositionWS), new NavVector3(targetPositionWS), out hit2, allowNpcDoors);
		hitWS = hit2.ToUnity();
		return result2;
	}

	public static bool CalculatePath(Vector3 sourcePositionWS, Vector3 targetPositionWS, NavMeshQueryFilter filter, RustNavMeshPath pathNS, bool allowNpcDoors = false)
	{
		return CalculatePath(sourcePositionWS, targetPositionWS, filter.areaMask, pathNS, allowNpcDoors);
	}

	public static bool CalculatePath(Vector3 sourcePositionWS, Vector3 targetPositionWS, int areaMask, RustNavMeshPath pathNS, bool allowNpcDoors = false)
	{
		pathNS.corners.Clear();
		if (AI.useUnityNavmesh)
		{
			if (pathNS.unityPath == null)
			{
				pathNS.unityPath = new NavMeshPath();
			}
			bool result = NavMesh.CalculatePath(sourcePositionWS, targetPositionWS, areaMask, pathNS.unityPath);
			pathNS.status = pathNS.unityPath.status;
			int cornersNonAlloc = pathNS.unityPath.GetCornersNonAlloc(cornerBuffer);
			for (int i = 0; i < cornersNonAlloc; i++)
			{
				pathNS.corners.Add(new NavVector3(cornerBuffer[i]));
			}
			return result;
		}
		IndependantNavmesh independantNavmesh = IndependantNavmesh.FindNavmeshAtPosition(sourcePositionWS);
		if (independantNavmesh != null && independantNavmesh.Navmesh != null && independantNavmesh.Navmesh.IsBuilt())
		{
			NavVector3 start = independantNavmesh.TransformPointFromWorldSpaceToNavSpace(sourcePositionWS);
			NavVector3 end = independantNavmesh.TransformPointFromWorldSpaceToNavSpace(targetPositionWS);
			if (!independantNavmesh.Navmesh.CalculatePath(start, end, pathNS, allowNpcDoors))
			{
				return false;
			}
			return true;
		}
		if (!RustNavigation.Instance.IsDefaultNavmeshBuilt())
		{
			if (AI.logIssues)
			{
				RustNavigation.LogError("Trying to calculate a path on the navmesh before it's built. This will always fail. Make sure to check IsDefaultNavmeshBuilt() before calculating paths.");
			}
			pathNS.Reset();
			return false;
		}
		return RustNavigation.Instance.DefaultNavmesh.CalculatePath(new NavVector3(sourcePositionWS), new NavVector3(targetPositionWS), pathNS, allowNpcDoors);
	}
}

