using System;
using System.Collections.Generic;
using Rust.Ai.Gen2.Nav;
using UnityEngine;
using UnityEngine.AI;

namespace Rust.Ai.Gen2;

public class RustNavMeshPath
{
	public readonly List<NavVector3> corners = new List<NavVector3>();

	public NavMeshPathStatus status = NavMeshPathStatus.PathInvalid;

	public NavMeshPath unityPath;

	public readonly ulong[] polyRefs = new ulong[256];

	public int polyRefCount;

	public float GetPathLength()
	{
		using (TimeWarning.New("RustNavMeshPath.GetPathLength"))
		{
			float num = 0f;
			if (corners.Count < 2)
			{
				return num;
			}
			for (int i = 0; i < corners.Count - 1; i++)
			{
				num += Vector3.Distance(corners[i].Value, corners[i + 1].Value);
			}
			return num;
		}
	}

	public NavVector3 GetDestinationNS()
	{
		if (corners.Count < 1)
		{
			return NavVector3.zero;
		}
		List<NavVector3> list = corners;
		return list[list.Count - 1];
	}

	public void Reset()
	{
		corners.Clear();
		status = NavMeshPathStatus.PathInvalid;
		unityPath = null;
		polyRefCount = 0;
	}

	public void CopyFrom(RustNavMeshPath other)
	{
		corners.Clear();
		corners.AddRange(other.corners);
		status = other.status;
		unityPath = other.unityPath;
		Array.Copy(other.polyRefs, polyRefs, other.polyRefCount);
		polyRefCount = other.polyRefCount;
	}
}

