using System.Collections.Generic;
using ConVar;
using Facepunch;
using ProtoBuf;
using UnityEngine;

namespace Rust.Ai.Gen2.Nav;

public class IndependantNavmesh : MonoBehaviour, IServerComponent
{
	public Vector3 size = Vector3.one * 50f;

	public bool canMove;

	public bool buildOnEnable;

	[Tooltip("Bake this navmesh with the hi res build params. Needed for detailed structures like ghost ships, the tile builder only picks hi res by itself for building blocks and monument prevent building volumes")]
	public bool forceHiRes;

	private static SparseGridWithBounds<IndependantNavmesh> navmeshLookup = new SparseGridWithBounds<IndependantNavmesh>();

	private Matrix4x4 buildTimeTransform;

	private Bounds lastBounds;

	public RustNavmesh Navmesh { get; private set; }

	public static bool AnyRegistered => navmeshLookup.Count > 0;

	public Matrix4x4 WorldToNavMatrix
	{
		get
		{
			if (!RustNavigation.EnsureNewNavmesh())
			{
				return Matrix4x4.identity;
			}
			if (!canMove)
			{
				return Matrix4x4.identity;
			}
			return buildTimeTransform * base.transform.worldToLocalMatrix;
		}
	}

	public Matrix4x4 NavToWorldMatrix
	{
		get
		{
			if (!RustNavigation.EnsureNewNavmesh())
			{
				return Matrix4x4.identity;
			}
			if (!canMove)
			{
				return Matrix4x4.identity;
			}
			return WorldToNavMatrix.inverse;
		}
	}

	public bool IsBuilt()
	{
		if (Navmesh != null)
		{
			return Navmesh.IsBuilt();
		}
		return false;
	}

	private void OnEnable()
	{
		if (!AI.useUnityNavmesh && buildOnEnable && RustNavigation.Instance != null)
		{
			RustNavigation.Instance.AddNavmesh(this);
		}
	}

	private void OnDisable()
	{
		if (!AI.useUnityNavmesh)
		{
			navmeshLookup.Remove(this);
			if (RustNavigation.Instance != null)
			{
				RustNavigation.Instance.RemoveNavmesh(this);
			}
			if (Navmesh != null)
			{
				Navmesh.Dispose();
				Navmesh = null;
			}
		}
	}

	private void LateUpdate()
	{
		if (!AI.useUnityNavmesh && canMove && Navmesh != null)
		{
			Bounds bounds = GetBounds();
			if (bounds != lastBounds)
			{
				navmeshLookup.Remove(this);
				lastBounds = bounds;
				navmeshLookup.Add(lastBounds, this);
			}
		}
	}

	public void Rebuild(BackgroundTileBuilder tileBuilder, bool synchronous = false)
	{
		if (!RustNavigation.EnsureNewNavmesh())
		{
			return;
		}
		buildTimeTransform = base.transform.localToWorldMatrix;
		RustNavmesh rustNavmesh = new RustNavmesh(tileBuilder, null, null, GetBounds(), shouldBuild: true, canMove || synchronous, forceHiRes);
		if (rustNavmesh == null || !rustNavmesh.IsValid())
		{
			RustNavigation.LogError("Failed to build independent navmesh for object " + base.name);
			return;
		}
		rustNavmesh.debugName = base.transform.root.name;
		if (Navmesh != null)
		{
			Navmesh.Dispose();
		}
		Navmesh = rustNavmesh;
		navmeshLookup.Remove(this);
		lastBounds = GetBounds();
		navmeshLookup.Add(lastBounds, this);
		RustNavDoorGates.ReassertInBounds(lastBounds);
	}

	public void RebuildTilesInBounds(Bounds bounds, bool synchronous = false)
	{
		if (!RustNavigation.EnsureNewNavmesh() || Navmesh == null || !Navmesh.IsValid())
		{
			return;
		}
		if (canMove)
		{
			if (AI.logIssues)
			{
				RustNavigation.LogError("Rebuilding single tiles of moving navmesh is not supported.");
			}
			return;
		}
		Navmesh.RebuildTilesInBounds(bounds, canMove || synchronous);
		if (synchronous)
		{
			RustNavDoorGates.FlushTileReasserts();
		}
	}

	public NavVector3 TransformPointFromWorldSpaceToNavSpace(Vector3 worldPoint)
	{
		if (!RustNavigation.EnsureNewNavmesh() || !canMove)
		{
			return new NavVector3(worldPoint);
		}
		Vector3 point = base.transform.worldToLocalMatrix.MultiplyPoint3x4(worldPoint);
		return new NavVector3(buildTimeTransform.MultiplyPoint3x4(point));
	}

	public Vector3 TransformPointFromNavSpaceToWorldSpace(NavVector3 navSpacePoint)
	{
		if (!RustNavigation.EnsureNewNavmesh() || !canMove)
		{
			return navSpacePoint.Value;
		}
		Vector3 point = buildTimeTransform.inverse.MultiplyPoint3x4(navSpacePoint.Value);
		return base.transform.localToWorldMatrix.MultiplyPoint3x4(point);
	}

	public Vector3 TransformDirectionFromNavSpaceToWorldSpace(NavVector3 navSpaceDirection)
	{
		if (!RustNavigation.EnsureNewNavmesh() || !canMove)
		{
			return navSpaceDirection.Value;
		}
		Vector3 vector = buildTimeTransform.inverse.MultiplyVector(navSpaceDirection.Value);
		return base.transform.localToWorldMatrix.MultiplyVector(vector);
	}

	public NavVector3 TransformDirectionFromWorldSpaceToNavSpace(Vector3 worldSpaceDirection)
	{
		if (!RustNavigation.EnsureNewNavmesh() || !canMove)
		{
			return new NavVector3(worldSpaceDirection);
		}
		Vector3 vector = base.transform.worldToLocalMatrix.MultiplyVector(worldSpaceDirection);
		return new NavVector3(buildTimeTransform.MultiplyVector(vector));
	}

	public bool FillDebugDrawProto(NavMeshData navMeshData, Bounds bounds)
	{
		if (!RustNavigation.EnsureNewNavmesh() || Navmesh == null || !Navmesh.IsValid())
		{
			return false;
		}
		OBB oBB = new OBB(bounds);
		if (!canMove)
		{
			Navmesh.FillDebugDrawProto(navMeshData, oBB.ToBounds(), null, null);
			return true;
		}
		Matrix4x4 worldToNavMatrix = WorldToNavMatrix;
		oBB.Transform(worldToNavMatrix.GetPosition(), worldToNavMatrix.lossyScale, worldToNavMatrix.rotation);
		Navmesh.FillDebugDrawProto(navMeshData, oBB.ToBounds(), worldToNavMatrix.inverse, null);
		return true;
	}

	public static IndependantNavmesh FindNavmeshAtPosition(Vector3 worldPosition)
	{
		if (!RustNavigation.EnsureNewNavmesh())
		{
			return null;
		}
		using PooledHashSet<IndependantNavmesh> pooledHashSet = Facepunch.Pool.Get<PooledHashSet<IndependantNavmesh>>();
		navmeshLookup.FindAll(new Bounds(worldPosition, Vector3.one), pooledHashSet);
		foreach (IndependantNavmesh item in pooledHashSet)
		{
			if (item.Navmesh != null && item.Navmesh.IsValid() && item.GetBounds().Contains(worldPosition))
			{
				return item;
			}
		}
		return null;
	}

	public static void FindNavmeshesInBounds(Bounds bounds, List<IndependantNavmesh> results)
	{
		if (!RustNavigation.EnsureNewNavmesh())
		{
			return;
		}
		using PooledHashSet<IndependantNavmesh> pooledHashSet = Facepunch.Pool.Get<PooledHashSet<IndependantNavmesh>>();
		navmeshLookup.FindAll(bounds, pooledHashSet);
		foreach (IndependantNavmesh item in pooledHashSet)
		{
			results.Add(item);
		}
	}

	private Bounds GetBounds()
	{
		return new OBB(base.transform.position, size, base.transform.rotation).ToBounds();
	}
}

