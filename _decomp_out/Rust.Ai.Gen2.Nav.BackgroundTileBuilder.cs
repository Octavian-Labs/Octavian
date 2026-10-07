using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ConVar;
using Facepunch;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.AI;

namespace Rust.Ai.Gen2.Nav;

public class BackgroundTileBuilder : IDisposable
{
	private static class TileScratch
	{
		[ThreadStatic]
		private static RawBuffer<Vector3> _vertices;

		[ThreadStatic]
		private static RawBuffer<int> _triangles;

		[ThreadStatic]
		private static RawBuffer<int> _indices;

		[ThreadStatic]
		private static RawBuffer<byte> _triAreas;

		private static readonly List<IDisposable> _all = new List<IDisposable>();

		public static RawBuffer<Vector3> Vertices => _vertices ?? (_vertices = Track(new RawBuffer<Vector3>()));

		public static RawBuffer<int> Triangles => _triangles ?? (_triangles = Track(new RawBuffer<int>()));

		public static RawBuffer<int> Indices => _indices ?? (_indices = Track(new RawBuffer<int>()));

		public static RawBuffer<byte> TriAreas => _triAreas ?? (_triAreas = Track(new RawBuffer<byte>()));

		private static T Track<T>(T b) where T : IDisposable
		{
			lock (_all)
			{
				_all.Add(b);
				return b;
			}
		}

		public static void DisposeAll()
		{
			lock (_all)
			{
				foreach (IDisposable item in _all)
				{
					item.Dispose();
				}
				_all.Clear();
			}
			_vertices = null;
			_triangles = null;
			_indices = null;
			_triAreas = null;
		}
	}

	private sealed class TileCancellation
	{
		private volatile bool cancelled;

		public bool IsCancellationRequested => cancelled;

		public void Cancel()
		{
			cancelled = true;
		}
	}

	private struct TileCollectRequest
	{
		public readonly int tx;

		public readonly int ty;

		public RustNavmesh navmesh;

		public TileCancellation cancellation;

		public TileCollectRequest(int tx, int ty, RustNavmesh navmesh)
		{
			this.tx = tx;
			this.ty = ty;
			this.navmesh = navmesh;
			cancellation = new TileCancellation();
		}
	}

	private struct TileBuildRequest
	{
		public readonly int tx;

		public readonly int ty;

		public RustNavmesh navmesh;

		public NavMeshBuildParams buildParams;

		public List<ThreadSafeNavMeshBuildSource> sources;

		public NavMeshBuildVolume[] volumes;

		public int volumeCount;

		public TileCancellation cancellation;

		public TileBuildRequest(in TileCollectRequest collectRequest, List<ThreadSafeNavMeshBuildSource> sources, NavMeshBuildParams buildParams, NavMeshBuildVolume[] volumes, int volumeCount)
		{
			tx = collectRequest.tx;
			ty = collectRequest.ty;
			navmesh = collectRequest.navmesh;
			cancellation = collectRequest.cancellation;
			this.sources = sources;
			this.buildParams = buildParams;
			this.volumes = volumes;
			this.volumeCount = volumeCount;
		}
	}

	public enum TileBuildResultCode
	{
		Success,
		Cancelled,
		NoGeometry,
		UnknownError,
		ExtractGeometryError,
		SpanHeightError,
		CreateHeightFieldError,
		CreateCompactHeightFieldError,
		CreatePolymeshError,
		CreateDetailPolymeshError,
		CreateAndAddNavDataError,
		ValidationError
	}

	private struct TileBuildResult
	{
		public readonly int tx;

		public readonly int ty;

		public RustNavmesh navmesh;

		public IntPtr tileBytes;

		public readonly int dataSize;

		public TileBuildResultCode resultCode;

		public TileCancellation cancellation;

		public float debugSpanMinY;

		public float debugSpanMaxY;

		public TileBuildResult(in TileBuildRequest request, IntPtr tileBytes, int dataSize)
		{
			tx = request.tx;
			ty = request.ty;
			navmesh = request.navmesh;
			this.tileBytes = tileBytes;
			this.dataSize = dataSize;
			resultCode = TileBuildResultCode.Success;
			cancellation = request.cancellation;
			debugSpanMinY = 0f;
			debugSpanMaxY = 0f;
		}

		public TileBuildResult(in TileBuildRequest request, TileBuildResultCode resultCode)
		{
			tx = request.tx;
			ty = request.ty;
			navmesh = request.navmesh;
			tileBytes = IntPtr.Zero;
			dataSize = 0;
			this.resultCode = resultCode;
			cancellation = request.cancellation;
			debugSpanMinY = 0f;
			debugSpanMaxY = 0f;
		}
	}

	private struct ThreadSafeNavMeshBuildSource
	{
		public NavMeshBuildSourceShape shape;

		public int sourceObjectID;

		public Matrix4x4 transform;

		public Vector3 size;

		public int area;

		public bool forceUnwalkable;
	}

	private static readonly int[] boxTriangleIndices = new int[36]
	{
		7, 4, 3, 7, 6, 4, 4, 6, 5, 4,
		5, 0, 4, 5, 1, 4, 1, 0, 5, 6,
		2, 5, 2, 1, 6, 7, 3, 6, 3, 2,
		0, 1, 3, 0, 3, 7
	};

	public static (int tx, int ty, string path)? DumpGeometryRequest;

	public const int GeometryLayerMask = 1092714753;

	private Stopwatch stopwatch = new Stopwatch();

	private readonly Dictionary<(RustNavmesh navmesh, int tx, int ty), TileCancellation> tileCancellations = new Dictionary<(RustNavmesh, int, int), TileCancellation>();

	private readonly Queue<TileCollectRequest> collectMainThreadWorkQueue = new Queue<TileCollectRequest>();

	private readonly BlockingCollection<TileBuildRequest> backgroundWorkQueue = new BlockingCollection<TileBuildRequest>();

	private readonly ConcurrentBag<TileBuildResult> finalMainthreadWorkBag = new ConcurrentBag<TileBuildResult>();

	private Thread[] workers;

	private CancellationTokenSource globalInterrupt;

	private NavmeshSaveCatchUpStats gateCatchUp;

	private long gateLiftTimestamp;

	private readonly Queue<TileBuildResult> parkedResults = new Queue<TileBuildResult>();

	public NavmeshSaveCatchUpStats SaveGateCatchUp => gateCatchUp;

	public static void CreateBoxMesh(List<Vector3> vertices, List<int> triangles, Vector3 center, Vector3 size)
	{
		vertices.Clear();
		triangles.Clear();
		vertices.Add(center + new Vector3(0f - size.x, 0f - size.y, 0f - size.z) * 0.5f);
		vertices.Add(center + new Vector3(size.x, 0f - size.y, 0f - size.z) * 0.5f);
		vertices.Add(center + new Vector3(size.x, 0f - size.y, size.z) * 0.5f);
		vertices.Add(center + new Vector3(0f - size.x, 0f - size.y, size.z) * 0.5f);
		vertices.Add(center + new Vector3(0f - size.x, size.y, 0f - size.z) * 0.5f);
		vertices.Add(center + new Vector3(size.x, size.y, 0f - size.z) * 0.5f);
		vertices.Add(center + new Vector3(size.x, size.y, size.z) * 0.5f);
		vertices.Add(center + new Vector3(0f - size.x, size.y, size.z) * 0.5f);
		for (int i = 0; i < boxTriangleIndices.Length; i++)
		{
			triangles.Add(boxTriangleIndices[i]);
		}
	}

	public unsafe static void ExtractTerrainGeometry(Vector3 topLeftCorner, int tileSize, RawBuffer<Vector3> vertices, RawBuffer<int> triangles)
	{
		int num = tileSize + 1;
		int num2 = num * num;
		RawBuffer<int> indices = TileScratch.Indices;
		indices.Clear();
		int* ptr = indices.AppendUninitialized(num2);
		vertices.EnsureCapacity(vertices.Count + num2);
		triangles.EnsureCapacity(triangles.Count + tileSize * tileSize * 6);
		Vector3 vector = new Vector3(topLeftCorner.x, 0f, topLeftCorner.z);
		TerrainHeightMap heightMap = TerrainMeta.HeightMap;
		TerrainAlphaMap alphaMap = TerrainMeta.AlphaMap;
		float x = vector.x + (float)tileSize * 0.5f;
		float z = vector.z + (float)tileSize * 0.5f;
		bool deepSea = DeepSeaManager.IsInsideDeepSea(new Vector3(x, 0f, z));
		TerrainHeightMap.HeightSampler heightSampler = heightMap.CreateSampler(deepSea);
		TerrainAlphaMap.AlphaSampler alphaSampler = alphaMap.CreateSampler();
		int num3 = 0;
		for (int i = 0; i <= tileSize; i++)
		{
			float num4 = vector.z + (float)i;
			heightSampler.BeginRow(num4);
			alphaSampler.BeginRow(num4);
			int num5 = 0;
			while (num5 <= tileSize)
			{
				float num6 = vector.x + (float)num5;
				float num7 = heightSampler.SampleRow(num6);
				if (num7 < -1f)
				{
					ptr[num3] = -1;
				}
				else if (alphaSampler.SampleRow(num6) < 0.1f)
				{
					ptr[num3] = -1;
				}
				else
				{
					ptr[num3] = vertices.Count;
					vertices.Add(new Vector3(num6, num7, num4));
				}
				num5++;
				num3++;
			}
		}
		int num8 = 0;
		int num9 = 0;
		while (num9 < tileSize)
		{
			int num10 = 0;
			while (num10 < tileSize)
			{
				int num11 = ptr[num8];
				int num12 = ptr[num8 + tileSize + 1];
				int num13 = ptr[num8 + 1];
				int num14 = ptr[num8 + 1];
				int num15 = ptr[num8 + tileSize + 1];
				int num16 = ptr[num8 + tileSize + 2];
				if (num11 != -1 && num12 != -1 && num13 != -1)
				{
					triangles.Add(num11);
					triangles.Add(num12);
					triangles.Add(num13);
				}
				if (num14 != -1 && num15 != -1 && num16 != -1)
				{
					triangles.Add(num14);
					triangles.Add(num15);
					triangles.Add(num16);
				}
				num10++;
				num8++;
			}
			num9++;
			num8++;
		}
	}

	private unsafe static void DumpTileGeometry(string path, in NavMeshBuildParams buildParams, int tx, int ty, Vector3 hfMin, Vector3 hfMax, RawBuffer<Vector3> vertices, RawBuffer<int> triangles, RawBuffer<byte> triAreas)
	{
		using FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write);
		using BinaryWriter binaryWriter = new BinaryWriter(output);
		binaryWriter.Write(1380402511);
		binaryWriter.Write(3);
		NavMeshBuildParams navMeshBuildParams = buildParams;
		ReadOnlySpan<byte> buffer = new ReadOnlySpan<byte>(&navMeshBuildParams, sizeof(NavMeshBuildParams));
		binaryWriter.Write(buffer.Length);
		binaryWriter.Write(buffer);
		binaryWriter.Write(tx);
		binaryWriter.Write(ty);
		binaryWriter.Write(hfMin.x);
		binaryWriter.Write(hfMin.y);
		binaryWriter.Write(hfMin.z);
		binaryWriter.Write(hfMax.x);
		binaryWriter.Write(hfMax.y);
		binaryWriter.Write(hfMax.z);
		binaryWriter.Write(vertices.Count);
		binaryWriter.Write(new ReadOnlySpan<byte>((void*)vertices.Ptr, vertices.Count * 12));
		binaryWriter.Write(triangles.Count);
		binaryWriter.Write(new ReadOnlySpan<byte>((void*)triangles.Ptr, triangles.Count * 4));
		binaryWriter.Write(0);
		binaryWriter.Write(triAreas.Count);
		if (triAreas.Count > 0)
		{
			binaryWriter.Write(new ReadOnlySpan<byte>((void*)triAreas.Ptr, triAreas.Count));
		}
	}

	public BackgroundTileBuilder()
	{
		int num = Mathf.Clamp(RustNav.numThreads, 1, SystemInfo.processorCount - 1);
		globalInterrupt = new CancellationTokenSource();
		workers = new Thread[num];
		for (int i = 0; i < num; i++)
		{
			workers[i] = new Thread(WorkerLoopFromBackgroundThread)
			{
				IsBackground = true,
				Name = $"RustNavTileBuilder-{i}"
			};
			workers[i].Start();
		}
	}

	public void GetPendingTilesOnMainThread(List<(RustNavmesh navmesh, int tx, int ty)> pendingTiles)
	{
		foreach (KeyValuePair<(RustNavmesh, int, int), TileCancellation> tileCancellation in tileCancellations)
		{
			pendingTiles.Add(tileCancellation.Key);
		}
	}

	public void GetPendingTilesForNavmeshOnMainThread(RustNavmesh navmesh, List<(int tx, int ty)> pendingTiles)
	{
		foreach (KeyValuePair<(RustNavmesh, int, int), TileCancellation> tileCancellation in tileCancellations)
		{
			if (tileCancellation.Key.Item1 == navmesh)
			{
				pendingTiles.Add((tileCancellation.Key.Item2, tileCancellation.Key.Item3));
			}
		}
	}

	public void CancelPendingTilesForOnMainThread(RustNavmesh navmesh)
	{
		foreach (KeyValuePair<(RustNavmesh, int, int), TileCancellation> tileCancellation in tileCancellations)
		{
			if (tileCancellation.Key.Item1 == navmesh)
			{
				tileCancellation.Value.Cancel();
			}
		}
	}

	public void Dispose()
	{
		if (globalInterrupt == null)
		{
			return;
		}
		backgroundWorkQueue.CompleteAdding();
		try
		{
			globalInterrupt.Cancel();
		}
		catch (Exception exception)
		{
			UnityEngine.Debug.LogException(exception);
		}
		bool flag = true;
		Thread[] array = workers;
		foreach (Thread thread in array)
		{
			try
			{
				if (!thread.Join(1000))
				{
					flag = false;
				}
			}
			catch (Exception exception2)
			{
				UnityEngine.Debug.LogException(exception2);
				flag = false;
			}
		}
		if (flag)
		{
			TileScratch.DisposeAll();
		}
		else
		{
			RustNavigation.LogError("RustNav: workers did not join in time, leaking tile scratch buffers deliberately");
		}
		CleanupRemainingWorkItemsOnMainThread();
		globalInterrupt.Dispose();
		globalInterrupt = null;
	}

	private TileBuildRequest DoInitialWorkOnMainThread(in TileCollectRequest collectRequest)
	{
		using (TimeWarning.New("RustNavigation.DoInitialWorkOnMainThread"))
		{
			long num = BakeStats.Timestamp();
			long num2 = num;
			Bounds tileBounds = collectRequest.navmesh.rcCalcTileBounds(new Vector2Int(collectRequest.tx, collectRequest.ty));
			tileBounds = collectRequest.navmesh.rcExpandTileBounds(tileBounds);
			BakeStats.AddStage(BakeStats.Stage.CollectBounds, BakeStats.Timestamp() - num2);
			int layerMask = 1629585665;
			int areaFromName = NavMesh.GetAreaFromName("Walkable");
			List<ThreadSafeNavMeshBuildSource> list = Facepunch.Pool.Get<List<ThreadSafeNavMeshBuildSource>>();
			using PooledList<Collider> pooledList = Facepunch.Pool.Get<PooledList<Collider>>();
			num2 = BakeStats.Timestamp();
			GamePhysics.OverlapBounds(tileBounds, pooledList, layerMask, QueryTriggerInteraction.Collide);
			BakeStats.AddStage(BakeStats.Stage.CollectOverlap, BakeStats.Timestamp() - num2);
			bool flag = collectRequest.navmesh.ForceHiRes;
			PooledHashSet<Door> obj = null;
			bool hasAny = RustNavmeshModifierVolume.HasAny;
			if (!flag && RustNavigation.HasTunnelRegions && RustNavigation.Instance != null && RustNavigation.Instance.IsInTunnelRegion(tileBounds))
			{
				flag = true;
			}
			num2 = BakeStats.Timestamp();
			foreach (Collider item2 in pooledList)
			{
				try
				{
					BaseEntity baseEntity = item2.ToBaseEntity(allowDestroyed: true);
					if ((baseEntity != null && (baseEntity.isClient || baseEntity.IsDestroyed)) || PlayerBoat.IsPartOfPlayerBoat(baseEntity))
					{
						continue;
					}
					ThreadSafeNavMeshBuildSource threadSafeNavMeshBuildSource = default(ThreadSafeNavMeshBuildSource);
					threadSafeNavMeshBuildSource.shape = NavMeshBuildSourceShape.Mesh;
					threadSafeNavMeshBuildSource.sourceObjectID = 0;
					threadSafeNavMeshBuildSource.transform = item2.transform.localToWorldMatrix;
					threadSafeNavMeshBuildSource.size = Vector3.zero;
					threadSafeNavMeshBuildSource.area = areaFromName;
					ThreadSafeNavMeshBuildSource item = threadSafeNavMeshBuildSource;
					if (!flag)
					{
						if (baseEntity.Is<BuildingBlock>(out var _))
						{
							flag = true;
						}
						else if (ConstructionErrors.GetPreventBuildingMonumentTag(item2) != null)
						{
							flag = true;
						}
					}
					if (baseEntity.Is<TreeEntity>(out var castedUnityObject2) && !castedUnityObject2.IncludeInNavmesh)
					{
						continue;
					}
					if (!baseEntity.Is<Door>(out var castedUnityObject3))
					{
						goto IL_01e9;
					}
					if (castedUnityObject3.IsNavGate)
					{
						if (obj == null)
						{
							obj = Facepunch.Pool.Get<PooledHashSet<Door>>();
						}
						obj.Add(castedUnityObject3);
						if (!castedUnityObject3.IsNavGateMovingCollider(item2))
						{
							goto IL_01e9;
						}
					}
					else if (!castedUnityObject3.IsNpcOpenable)
					{
						goto IL_01e9;
					}
					goto end_IL_00e6;
					IL_01e9:
					if (item2.isTrigger || (0x20000000 & (1 << item2.gameObject.layer)) != 0)
					{
						continue;
					}
					if (item2.Is<MeshCollider>(out var castedUnityObject4))
					{
						if (castedUnityObject4.sharedMesh == null)
						{
							continue;
						}
						item.shape = NavMeshBuildSourceShape.Mesh;
						item.sourceObjectID = castedUnityObject4.sharedMesh.GetInstanceID();
						MeshCache.Get(castedUnityObject4.sharedMesh);
						goto IL_03d5;
					}
					if (item2.Is<BoxCollider>(out var castedUnityObject5))
					{
						item.shape = NavMeshBuildSourceShape.Box;
						item.size = castedUnityObject5.size;
						item.transform = item2.transform.localToWorldMatrix * Matrix4x4.Translate(castedUnityObject5.center);
						goto IL_03d5;
					}
					if (item2.Is<SphereCollider>(out var castedUnityObject6))
					{
						item.shape = NavMeshBuildSourceShape.Box;
						item.size = Vector3.one * castedUnityObject6.radius * 2f;
						item.transform = item2.transform.localToWorldMatrix * Matrix4x4.Translate(castedUnityObject6.center);
						goto IL_03d5;
					}
					if (!item2.Is<CapsuleCollider>(out var castedUnityObject7))
					{
						continue;
					}
					item.shape = NavMeshBuildSourceShape.Box;
					float num3 = castedUnityObject7.radius * 2f;
					if (castedUnityObject7.direction == 0)
					{
						item.size = new Vector3(castedUnityObject7.height, num3, num3);
					}
					else if (castedUnityObject7.direction == 1)
					{
						item.size = new Vector3(num3, castedUnityObject7.height, num3);
					}
					else if (castedUnityObject7.direction == 2)
					{
						item.size = new Vector3(num3, num3, castedUnityObject7.height);
					}
					else
					{
						item.size = new Vector3(num3, castedUnityObject7.height, num3);
					}
					item.transform = item2.transform.localToWorldMatrix * Matrix4x4.Translate(castedUnityObject7.center);
					goto IL_03d5;
					IL_03d5:
					item.forceUnwalkable = hasAny && item2.GetComponentInParent<RustNavmeshModifierVolume>() != null;
					list.Add(item);
					end_IL_00e6:;
				}
				finally
				{
				}
			}
			BakeStats.AddStage(BakeStats.Stage.CollectColliders, BakeStats.Timestamp() - num2);
			if (obj != null || RustNavDoorGates.HasGateDoors)
			{
				num2 = BakeStats.Timestamp();
				if (obj == null)
				{
					obj = Facepunch.Pool.Get<PooledHashSet<Door>>();
				}
				RustNavDoorGates.CollectGateDoorsReaching(tileBounds, obj);
				if (obj.Count == 0)
				{
					Facepunch.Pool.Free(ref obj);
				}
				BakeStats.AddStage(BakeStats.Stage.CollectDoors, BakeStats.Timestamp() - num2);
				BakeStats.AddStage(BakeStats.Stage.CollectDoorsQuery, BakeStats.Timestamp() - num2);
			}
			NavMeshBuildParams buildParams = (flag ? collectRequest.navmesh.BuildParamsHiRes : collectRequest.navmesh.BuildParams);
			NavMeshBuildVolume[] array = null;
			int num4 = 0;
			if (obj != null)
			{
				num2 = BakeStats.Timestamp();
				using PooledList<RustNavDoorGates.BakeVolumes> pooledList2 = Facepunch.Pool.Get<PooledList<RustNavDoorGates.BakeVolumes>>();
				int num5 = 0;
				foreach (Door item3 in obj)
				{
					RustNavDoorGates.BakeVolumes bakeVolumes = RustNavDoorGates.GetBakeVolumes(item3, flag, buildParams.agentRadius);
					pooledList2.Add(bakeVolumes);
					num5 += bakeVolumes.Count;
				}
				if (num5 > 0)
				{
					array = System.Buffers.ArrayPool<NavMeshBuildVolume>.Shared.Rent(num5);
					foreach (RustNavDoorGates.BakeVolumes item4 in pooledList2)
					{
						num4 += item4.CopyLeafVolumes(array, num4);
					}
					foreach (RustNavDoorGates.BakeVolumes item5 in pooledList2)
					{
						num4 += item5.CopyApertureVolume(array, num4);
					}
				}
				Facepunch.Pool.Free(ref obj);
				BakeStats.AddStage(BakeStats.Stage.CollectDoors, BakeStats.Timestamp() - num2);
				BakeStats.AddStage(BakeStats.Stage.CollectDoorsVolumes, BakeStats.Timestamp() - num2);
			}
			BakeStats.OnTileCollected(flag);
			BakeStats.AddStage(BakeStats.Stage.CollectTotal, BakeStats.Timestamp() - num);
			return new TileBuildRequest(in collectRequest, list, buildParams, array, num4);
		}
	}

	private unsafe static void FillTriAreasUpTo(RawBuffer<byte> triAreas, int triangleIndexCount, byte area)
	{
		int num = triangleIndexCount / 3 - triAreas.Count;
		if (num > 0)
		{
			UnsafeUtility.MemSet(triAreas.AppendUninitialized(num), area, num);
		}
	}

	private static void FreeTileVolumes(ref TileBuildRequest buildRequest)
	{
		if (buildRequest.volumes != null)
		{
			System.Buffers.ArrayPool<NavMeshBuildVolume>.Shared.Return(buildRequest.volumes);
			buildRequest.volumes = null;
			buildRequest.volumeCount = 0;
		}
	}

	private TileBuildResult DoWorkFromBackgroundThread(ref TileBuildRequest buildRequest, CancellationToken globalInterruptToken)
	{
		if (globalInterruptToken.IsCancellationRequested || buildRequest.cancellation.IsCancellationRequested)
		{
			FreeTileVolumes(ref buildRequest);
			return new TileBuildResult(in buildRequest, TileBuildResultCode.Cancelled);
		}
		RawBuffer<Vector3> vertices = TileScratch.Vertices;
		RawBuffer<int> triangles = TileScratch.Triangles;
		RawBuffer<byte> triAreas = TileScratch.TriAreas;
		vertices.Clear();
		triangles.Clear();
		triAreas.Clear();
		bool flag = false;
		foreach (ThreadSafeNavMeshBuildSource source in buildRequest.sources)
		{
			if (source.forceUnwalkable)
			{
				flag = true;
				break;
			}
		}
		IntPtr intPtr = IntPtr.Zero;
		IntPtr intPtr2 = IntPtr.Zero;
		IntPtr intPtr3 = IntPtr.Zero;
		IntPtr intPtr4 = IntPtr.Zero;
		long num = BakeStats.Timestamp();
		long num2 = num;
		BakeStats.TileTiming timing = default(BakeStats.TileTiming);
		timing.hiRes = buildRequest.buildParams.cellSize == buildRequest.navmesh.BuildParamsHiRes.cellSize && buildRequest.buildParams.tileSize == buildRequest.navmesh.BuildParamsHiRes.tileSize;
		try
		{
			if (TerrainMeta.HeightMap != null)
			{
				Bounds tileBounds = buildRequest.navmesh.rcCalcTileBounds(new Vector2Int(buildRequest.tx, buildRequest.ty));
				tileBounds = buildRequest.navmesh.rcExpandTileBounds(tileBounds);
				Vector3 topLeftCorner = (tileBounds.center - tileBounds.extents).WithY(0f);
				int tileSize = Mathf.CeilToInt(tileBounds.size.x);
				ExtractTerrainGeometry(topLeftCorner, tileSize, vertices, triangles);
			}
			timing.terrain = BakeStats.Timestamp() - num2;
			timing.terrainTris = triangles.Count / 3;
			if (globalInterruptToken.IsCancellationRequested || buildRequest.cancellation.IsCancellationRequested)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.Cancelled);
			}
			num2 = BakeStats.Timestamp();
			foreach (ThreadSafeNavMeshBuildSource source2 in buildRequest.sources)
			{
				if (globalInterruptToken.IsCancellationRequested || buildRequest.cancellation.IsCancellationRequested)
				{
					break;
				}
				if (source2.shape == NavMeshBuildSourceShape.Terrain)
				{
					continue;
				}
				if (flag)
				{
					FillTriAreasUpTo(triAreas, triangles.Count, 0);
				}
				if (source2.shape == NavMeshBuildSourceShape.Box)
				{
					using PooledList<Vector3> pooledList = Facepunch.Pool.Get<PooledList<Vector3>>();
					using PooledList<int> pooledList2 = Facepunch.Pool.Get<PooledList<int>>();
					CreateBoxMesh(pooledList, pooledList2, Vector3.zero, source2.size);
					Matrix4x4 transform = source2.transform;
					for (int i = 0; i < pooledList.Count; i++)
					{
						pooledList[i] = transform.MultiplyPoint3x4(pooledList[i]);
					}
					int count = vertices.Count;
					triangles.EnsureCapacity(triangles.Count + pooledList2.Count);
					foreach (int item2 in pooledList2)
					{
						triangles.Add(count + item2);
					}
					vertices.EnsureCapacity(vertices.Count + pooledList.Count);
					foreach (Vector3 item3 in pooledList)
					{
						vertices.Add(item3);
					}
				}
				else if (source2.sourceObjectID != 0)
				{
					if (!MeshCache.TryGet(source2.sourceObjectID, out var data))
					{
						continue;
					}
					using PooledList<Vector3> pooledList3 = Facepunch.Pool.Get<PooledList<Vector3>>();
					using PooledList<int> pooledList4 = Facepunch.Pool.Get<PooledList<int>>();
					pooledList3.AddRange(data.vertices);
					pooledList4.AddRange(data.triangles);
					Matrix4x4 transform2 = source2.transform;
					for (int j = 0; j < pooledList3.Count; j++)
					{
						pooledList3[j] = transform2.MultiplyPoint3x4(pooledList3[j]);
					}
					int count2 = vertices.Count;
					triangles.EnsureCapacity(triangles.Count + pooledList4.Count);
					foreach (int item4 in pooledList4)
					{
						triangles.Add(count2 + item4);
					}
					vertices.EnsureCapacity(vertices.Count + pooledList3.Count);
					foreach (Vector3 item5 in pooledList3)
					{
						vertices.Add(item5);
					}
				}
				if (flag)
				{
					FillTriAreasUpTo(triAreas, triangles.Count, (byte)(source2.forceUnwalkable ? 62 : 0));
				}
			}
			if (flag)
			{
				FillTriAreasUpTo(triAreas, triangles.Count, 0);
			}
			timing.sources = BakeStats.Timestamp() - num2;
			timing.totalTris = triangles.Count / 3;
			timing.sourceCount = buildRequest.sources.Count;
			if (vertices.Count == 0 || triangles.Count == 0)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.NoGeometry);
			}
			num2 = BakeStats.Timestamp();
			Bounds bounds = buildRequest.navmesh.rcExpandTileBounds(buildRequest.navmesh.rcCalcTileBounds(new Vector2Int(buildRequest.tx, buildRequest.ty)));
			float outMaxY;
			float outMinY;
			bool num3 = RecastWrapper.ComputeTriangleYExtent(vertices.Ptr, triangles.Ptr, triangles.Count / 3, bounds.min.x, bounds.max.x, bounds.min.z, bounds.max.z, out outMinY, out outMaxY);
			timing.yExtent = BakeStats.Timestamp() - num2;
			if (!num3)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.NoGeometry);
			}
			float cellHeight = buildRequest.buildParams.cellHeight;
			float num4 = cellHeight * 2f;
			outMinY -= num4;
			outMaxY += num4;
			Bounds currentNavmeshBounds = buildRequest.navmesh.CurrentNavmeshBounds;
			outMinY = Mathf.Max(outMinY, currentNavmeshBounds.min.y - num4);
			outMaxY = Mathf.Min(outMaxY, currentNavmeshBounds.max.y + num4);
			if (outMinY > outMaxY)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.NoGeometry);
			}
			float y = buildRequest.navmesh.CurrentNavmeshBounds.min.y;
			outMinY = y + Mathf.Floor((outMinY - y) / cellHeight) * cellHeight;
			if (Mathf.CeilToInt((outMaxY - outMinY) / cellHeight) > 8191)
			{
				TileBuildResult result = new TileBuildResult(in buildRequest, TileBuildResultCode.SpanHeightError);
				result.debugSpanMinY = outMinY;
				result.debugSpanMaxY = outMaxY;
				return result;
			}
			if (globalInterruptToken.IsCancellationRequested || buildRequest.cancellation.IsCancellationRequested)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.Cancelled);
			}
			num2 = BakeStats.Timestamp();
			Bounds bounds2 = buildRequest.navmesh.rcCalcTileBounds(new Vector2Int(buildRequest.tx, buildRequest.ty));
			Vector3 bmin = new Vector3(bounds2.min.x, outMinY, bounds2.min.z);
			Vector3 bmax = new Vector3(bounds2.max.x, outMaxY, bounds2.max.z);
			if (DumpGeometryRequest.HasValue && DumpGeometryRequest.Value.tx == buildRequest.tx && DumpGeometryRequest.Value.ty == buildRequest.ty)
			{
				string item = DumpGeometryRequest.Value.path;
				DumpGeometryRequest = null;
				DumpTileGeometry(item, in buildRequest.buildParams, buildRequest.tx, buildRequest.ty, bmin, bmax, vertices, triangles, triAreas);
			}
			RecastWrapper.SetLegacyBuild(RustNav.legacyBuild);
			intPtr = RecastWrapper.CreateHeightFieldRaw(in buildRequest.buildParams, vertices.Ptr, vertices.Count, triangles.Ptr, triangles.Count / 3, in bmin, in bmax, flag ? triAreas.Ptr : IntPtr.Zero);
			timing.heightField = BakeStats.Timestamp() - num2;
			if (intPtr == IntPtr.Zero)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.CreateHeightFieldError);
			}
			if (globalInterruptToken.IsCancellationRequested || buildRequest.cancellation.IsCancellationRequested)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.Cancelled);
			}
			num2 = BakeStats.Timestamp();
			intPtr2 = RecastWrapper.CreateCompactHeightField(in buildRequest.buildParams, intPtr, buildRequest.volumes, buildRequest.volumeCount);
			timing.compact = BakeStats.Timestamp() - num2;
			if (intPtr2 == IntPtr.Zero)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.CreateCompactHeightFieldError);
			}
			if (globalInterruptToken.IsCancellationRequested || buildRequest.cancellation.IsCancellationRequested)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.Cancelled);
			}
			num2 = BakeStats.Timestamp();
			intPtr3 = RecastWrapper.CreatePolymesh(in buildRequest.buildParams, intPtr2);
			timing.polymesh = BakeStats.Timestamp() - num2;
			if (intPtr3 == IntPtr.Zero)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.CreatePolymeshError);
			}
			if (globalInterruptToken.IsCancellationRequested || buildRequest.cancellation.IsCancellationRequested)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.Cancelled);
			}
			if (buildRequest.buildParams.buildDetailMesh)
			{
				num2 = BakeStats.Timestamp();
				intPtr4 = RecastWrapper.CreateDetailPolymesh(in buildRequest.buildParams, intPtr3, intPtr2, RustNav.detailSampleDistMult, RustNav.detailSampleMaxErrorMult);
				timing.detail = BakeStats.Timestamp() - num2;
				if (intPtr4 == IntPtr.Zero)
				{
					return new TileBuildResult(in buildRequest, TileBuildResultCode.CreateDetailPolymeshError);
				}
			}
			if (globalInterruptToken.IsCancellationRequested || buildRequest.cancellation.IsCancellationRequested)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.Cancelled);
			}
			num2 = BakeStats.Timestamp();
			int dataSize;
			IntPtr intPtr5 = RecastWrapper.CreateNavData(in buildRequest.buildParams, buildRequest.tx, buildRequest.ty, intPtr3, intPtr4, out dataSize);
			timing.navData = BakeStats.Timestamp() - num2;
			if (intPtr5 == IntPtr.Zero)
			{
				return new TileBuildResult(in buildRequest, TileBuildResultCode.CreateAndAddNavDataError);
			}
			if (AI.checkTileValid && !RecastWrapper.ValidateTileData(intPtr5, dataSize))
			{
				RecastWrapper.FreeTileData(intPtr5);
				return new TileBuildResult(in buildRequest, TileBuildResultCode.ValidationError);
			}
			return new TileBuildResult(in buildRequest, intPtr5, dataSize);
		}
		finally
		{
			if (intPtr != IntPtr.Zero)
			{
				RecastWrapper.FreeHeightField(intPtr);
			}
			if (intPtr2 != IntPtr.Zero)
			{
				RecastWrapper.FreeCompactHeightField(intPtr2);
			}
			if (intPtr3 != IntPtr.Zero)
			{
				RecastWrapper.FreePolymesh(intPtr3);
			}
			if (intPtr4 != IntPtr.Zero)
			{
				RecastWrapper.FreeDetailPolymesh(intPtr4);
			}
			Facepunch.Pool.FreeUnmanaged(ref buildRequest.sources);
			FreeTileVolumes(ref buildRequest);
			long num5 = BakeStats.Timestamp() - num;
			BakeStats.AddStage(BakeStats.Stage.WorkerTotal, num5);
			BakeStats.OnTileBuilt(buildRequest.tx, buildRequest.ty, in timing);
			if (RustNav.bakeStatsEnabled && buildRequest.navmesh != null)
			{
				Interlocked.Add(ref buildRequest.navmesh.workerBuildTicks, num5);
			}
		}
	}

	public void TickOnMainThread()
	{
		int num = 0;
		int resultBagDepth = finalMainthreadWorkBag.Count + parkedResults.Count;
		bool flag = IsDefaultNavmeshSaveGated();
		stopwatch.Restart();
		TileBuildResult buildResult;
		bool wasParked;
		while (!flag && TryTakeResult(out buildResult, out wasParked))
		{
			long num2 = BakeStats.Timestamp();
			AddSingleBuiltTileOnMainThread(ref buildResult, wasParked);
			BakeStats.AddStage(BakeStats.Stage.MainAddTile, BakeStats.Timestamp() - num2);
			num++;
			if (RustNav.addTileBudgetMs > 0f && stopwatch.Elapsed.TotalMilliseconds >= (double)RustNav.addTileBudgetMs)
			{
				break;
			}
		}
		if (!flag)
		{
			RustNavDoorGates.FlushTileReasserts(complete: false);
			RustNavDoorGates.FlushSaveDeferredDoors();
			ReportSaveGateCatchUpIfDrained();
		}
		stopwatch.Restart();
		int num3 = 0;
		TileCollectRequest result;
		while (collectMainThreadWorkQueue.TryDequeue(out result))
		{
			(RustNavmesh, int, int) key = (result.navmesh, result.tx, result.ty);
			TileCancellation value;
			bool flag2 = tileCancellations.TryGetValue(key, out value) && value == result.cancellation;
			bool isCancellationRequested = result.cancellation.IsCancellationRequested;
			if (!flag2 || isCancellationRequested)
			{
				if (flag2)
				{
					tileCancellations.Remove(key);
				}
				continue;
			}
			TileBuildRequest item = DoInitialWorkOnMainThread(in result);
			backgroundWorkQueue.Add(item);
			num3++;
			if (stopwatch.Elapsed.TotalMilliseconds >= (double)RustNav.collectBudgetMs)
			{
				break;
			}
		}
		bool budgetLimited = num3 > 0 && collectMainThreadWorkQueue.Count > 0;
		BakeStats.OnMainThreadTick(collectMainThreadWorkQueue.Count, backgroundWorkQueue.Count, resultBagDepth, num3 > 0, budgetLimited);
	}

	private static bool IsDefaultNavmeshSaveGated()
	{
		RustNavigation instance = RustNavigation.Instance;
		if (instance != null)
		{
			return instance.IsNavmeshSaveInFlight;
		}
		return false;
	}

	public void OnNavmeshSaveGateLifted()
	{
		TileBuildResult result;
		while (finalMainthreadWorkBag.TryTake(out result))
		{
			parkedResults.Enqueue(result);
		}
		int count = parkedResults.Count;
		gateCatchUp = new NavmeshSaveCatchUpStats
		{
			valid = true,
			parked = count,
			inProgress = (count > 0)
		};
		gateLiftTimestamp = Stopwatch.GetTimestamp();
	}

	private bool TryTakeResult(out TileBuildResult buildResult, out bool wasParked)
	{
		if (parkedResults.TryDequeue(out buildResult))
		{
			wasParked = true;
			return true;
		}
		wasParked = false;
		return finalMainthreadWorkBag.TryTake(out buildResult);
	}

	public void LandParkedResultsOnMainThread()
	{
		if (!IsDefaultNavmeshSaveGated())
		{
			TileBuildResult result;
			while (parkedResults.TryDequeue(out result))
			{
				AddSingleBuiltTileOnMainThread(ref result, countForCatchUp: true);
			}
			RustNavDoorGates.FlushSaveDeferredDoors();
			ReportSaveGateCatchUpIfDrained();
		}
	}

	private void ReportSaveGateCatchUpIfDrained()
	{
		if (gateCatchUp.inProgress && parkedResults.Count <= 0)
		{
			gateCatchUp.inProgress = false;
			gateCatchUp.drainMs = BakeStats.TicksToMs(Stopwatch.GetTimestamp() - gateLiftTimestamp);
			RustNavigation.Log(DescribeSaveGateCatchUp());
		}
	}

	public string DescribeSaveGateCatchUp()
	{
		if (!gateCatchUp.valid)
		{
			return "navmesh save catch up: nothing has been parked this session";
		}
		if (!gateCatchUp.inProgress)
		{
			return $"navmesh save catch up: {gateCatchUp.parked} parked, {gateCatchUp.applied} distinct tiles applied, " + $"{gateCatchUp.superseded} superseded, drain {gateCatchUp.drainMs:F0} ms";
		}
		return $"navmesh save catch up in progress: {gateCatchUp.parked} parked, {gateCatchUp.applied} applied, {gateCatchUp.superseded} superseded, {parkedResults.Count} still parked";
	}

	private bool AddSingleBuiltTileOnMainThread(ref TileBuildResult buildResult, bool countForCatchUp)
	{
		(RustNavmesh, int, int) key = (buildResult.navmesh, buildResult.tx, buildResult.ty);
		TileCancellation value;
		bool num = tileCancellations.TryGetValue(key, out value) && value == buildResult.cancellation;
		bool isCancellationRequested = buildResult.cancellation.IsCancellationRequested;
		buildResult.cancellation = null;
		if (num)
		{
			tileCancellations.Remove(key);
		}
		if (!num || isCancellationRequested)
		{
			if (buildResult.tileBytes != IntPtr.Zero)
			{
				RecastWrapper.FreeTileData(buildResult.tileBytes);
				buildResult.tileBytes = IntPtr.Zero;
			}
			BakeStats.OnResult((int)buildResult.resultCode, superseded: true);
			if (countForCatchUp && gateCatchUp.inProgress)
			{
				gateCatchUp.superseded++;
			}
			return false;
		}
		BakeStats.OnResult((int)buildResult.resultCode, superseded: false);
		if (countForCatchUp && gateCatchUp.inProgress)
		{
			gateCatchUp.applied++;
		}
		if (buildResult.resultCode != 0)
		{
			if (buildResult.tileBytes != IntPtr.Zero)
			{
				RecastWrapper.FreeTileData(buildResult.tileBytes);
				buildResult.tileBytes = IntPtr.Zero;
			}
			if (buildResult.resultCode != TileBuildResultCode.CreatePolymeshError && buildResult.resultCode != TileBuildResultCode.NoGeometry)
			{
				if (buildResult.resultCode == TileBuildResultCode.SpanHeightError)
				{
					Vector3 center = buildResult.navmesh.rcCalcTileBounds(new Vector2Int(buildResult.tx, buildResult.ty)).center;
					RustNavigation.LogError($"Failed to build navmesh tile {buildResult.tx},{buildResult.ty} at {center}, error code SpanHeightError: " + $"tile geometry spans y {buildResult.debugSpanMinY:F1} to {buildResult.debugSpanMaxY:F1} ({buildResult.debugSpanMaxY - buildResult.debugSpanMinY:F0}m), more than 8191 span cells");
				}
				else
				{
					RustNavigation.LogError($"Failed to build navmesh tile {buildResult.tx},{buildResult.ty}, error code {buildResult.resultCode}");
				}
			}
			buildResult.navmesh.FailTile(buildResult.tx, buildResult.ty);
			return false;
		}
		buildResult.navmesh.AddTile(buildResult.tx, buildResult.ty, buildResult.tileBytes, buildResult.dataSize);
		buildResult.tileBytes = IntPtr.Zero;
		return true;
	}

	private void CleanupRemainingWorkItemsOnMainThread()
	{
		TileBuildRequest item;
		while (backgroundWorkQueue.TryTake(out item))
		{
			Facepunch.Pool.FreeUnmanaged(ref item.sources);
			FreeTileVolumes(ref item);
		}
		TileBuildResult result;
		while (finalMainthreadWorkBag.TryTake(out result))
		{
			if (result.tileBytes != IntPtr.Zero)
			{
				RecastWrapper.FreeTileData(result.tileBytes);
				result.tileBytes = IntPtr.Zero;
			}
		}
		TileBuildResult result2;
		while (parkedResults.TryDequeue(out result2))
		{
			if (result2.tileBytes != IntPtr.Zero)
			{
				RecastWrapper.FreeTileData(result2.tileBytes);
			}
		}
	}

	private void WorkerLoopFromBackgroundThread()
	{
		CancellationToken token = globalInterrupt.Token;
		while (true)
		{
			TileBuildRequest buildRequest;
			try
			{
				long waitStartTs = BakeStats.Timestamp();
				buildRequest = backgroundWorkQueue.Take(token);
				BakeStats.AddWorkerIdle(waitStartTs, BakeStats.Timestamp());
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (InvalidOperationException)
			{
				break;
			}
			catch (Exception exception)
			{
				UnityEngine.Debug.LogException(exception);
				continue;
			}
			try
			{
				TileBuildResult item = DoWorkFromBackgroundThread(ref buildRequest, token);
				finalMainthreadWorkBag.Add(item);
			}
			catch (Exception exception2)
			{
				UnityEngine.Debug.LogException(exception2);
				try
				{
					finalMainthreadWorkBag.Add(new TileBuildResult(in buildRequest, TileBuildResultCode.UnknownError));
				}
				catch (Exception exception3)
				{
					UnityEngine.Debug.LogException(exception3);
				}
			}
		}
	}

	public bool EnqueueOnMainThread(RustNavmesh navmesh, int tx, int ty, bool synchronous = false)
	{
		using (TimeWarning.New("RustNav.BackgroundTileBuilders.Enqueue"))
		{
			(RustNavmesh, int, int) key = (navmesh, tx, ty);
			if (tileCancellations.TryGetValue(key, out var value))
			{
				value.Cancel();
			}
			if (navmesh.IsTileFarFromShore(tx, ty))
			{
				tileCancellations.Remove(key);
				Tile tile = navmesh.GetTile(tx, ty);
				if (tile != null && tile.hasData)
				{
					navmesh.JoinSaveForSynchronousMutation("Culling a navmesh tile that now lies out at sea");
				}
				navmesh.FailTile(tx, ty);
				return false;
			}
			TileCollectRequest collectRequest = new TileCollectRequest(tx, ty, navmesh);
			tileCancellations[key] = collectRequest.cancellation;
			BakeStats.OnTileQueued();
			if (synchronous)
			{
				navmesh.JoinSaveForSynchronousMutation("A synchronous navmesh tile rebuild");
				TileBuildRequest buildRequest = DoInitialWorkOnMainThread(in collectRequest);
				TileBuildResult buildResult = DoWorkFromBackgroundThread(ref buildRequest, CancellationToken.None);
				AddSingleBuiltTileOnMainThread(ref buildResult, countForCatchUp: false);
			}
			else
			{
				collectMainThreadWorkQueue.Enqueue(collectRequest);
			}
			return true;
		}
	}
}

