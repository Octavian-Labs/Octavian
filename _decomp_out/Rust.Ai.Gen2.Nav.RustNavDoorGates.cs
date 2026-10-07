using System;
using System.Collections.Generic;
using System.Diagnostics;
using ConVar;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2.Nav;

public static class RustNavDoorGates
{
	private sealed class DoorEntry
	{
		public Vector3 gridPosition;

		public Matrix4x4 boundsMatrix;

		public Bounds writeBounds;

		public bool boundsCached;

		public bool hasWriteBounds;

		public List<Door> owners;

		public bool ownersValid;

		public List<ulong> apertureRefs;

		public bool apertureRefsValid;

		public BakeCache bakeHiRes;

		public BakeCache bakeLoRes;
	}

	private struct BakeCache
	{
		public NavMeshBuildVolume[] volumes;

		public Matrix4x4 matrix;

		public float agentRadius;

		public int leafCount;

		public int count;

		public bool cached;
	}

	public static class Profile
	{
		public static bool enabled;

		public static long gatherTicks;

		public static long writeTicks;

		public static int writes;

		public static int groupDoors;

		public static double GatherMs => (double)gatherTicks * 1000.0 / (double)Stopwatch.Frequency;

		public static double WriteMs => (double)writeTicks * 1000.0 / (double)Stopwatch.Frequency;

		public static void Reset()
		{
			gatherTicks = 0L;
			writeTicks = 0L;
			writes = 0;
			groupDoors = 0;
		}
	}

	public struct BakeVolumes
	{
		private readonly NavMeshBuildVolume[] volumes;

		private readonly int leafCount;

		private readonly int count;

		public int Count => count;

		public BakeVolumes(NavMeshBuildVolume[] volumes, int leafCount, int count)
		{
			this.volumes = volumes;
			this.leafCount = leafCount;
			this.count = count;
		}

		public int CopyLeafVolumes(NavMeshBuildVolume[] dest, int destIndex)
		{
			for (int i = 0; i < leafCount; i++)
			{
				dest[destIndex + i] = volumes[i];
			}
			return leafCount;
		}

		public int CopyApertureVolume(NavMeshBuildVolume[] dest, int destIndex)
		{
			if (count <= leafCount)
			{
				return 0;
			}
			dest[destIndex] = volumes[leafCount];
			return 1;
		}
	}

	private static readonly Dictionary<Door, DoorEntry> allDoors = new Dictionary<Door, DoorEntry>();

	private static readonly SparseGrid<Door> doorGrid = new SparseGrid<Door>();

	public const float DoorReachMargin = 5f;

	private const int MaxOwnerPositions = 128;

	private static readonly Vector3[] ownerScratch = new Vector3[128];

	private const int MaxCachedPolyRefs = 64;

	private static readonly ulong[] refScratch = new ulong[64];

	private const int OwnersDeferred = -2;

	public static bool polyRefCacheEnabled = true;

	private static bool warnedOwnerOverflow;

	private const float DoorInteractPad = 1f;

	public static bool gatesEnabled = true;

	public static bool leafSlabsEnabled = true;

	public static float apertureHalfThickness = 0.5f;

	public static float apertureStepOver = 0.5f;

	public static bool ownershipEnabled = true;

	private static readonly HashSet<Door> pendingReassertSet = new HashSet<Door>();

	private static readonly List<Door> pendingReasserts = new List<Door>();

	private static readonly HashSet<Door> saveDeferred = new HashSet<Door>();

	public static bool HasGateDoors
	{
		get
		{
			if (gatesEnabled)
			{
				return allDoors.Count > 0;
			}
			return false;
		}
	}

	public static bool HasSaveDeferredDoors => saveDeferred.Count > 0;

	public static void InvalidateCaches()
	{
		foreach (KeyValuePair<Door, DoorEntry> allDoor in allDoors)
		{
			DoorEntry value = allDoor.Value;
			value.boundsCached = false;
			value.bakeHiRes.cached = false;
			value.bakeLoRes.cached = false;
			value.ownersValid = false;
			value.apertureRefsValid = false;
		}
	}

	public static void ForgetPolyRefs()
	{
		foreach (KeyValuePair<Door, DoorEntry> allDoor in allDoors)
		{
			allDoor.Value.apertureRefsValid = false;
		}
	}

	public static void Clear()
	{
		foreach (DoorEntry value in allDoors.Values)
		{
			List<Door> obj = value.owners;
			if (obj != null)
			{
				Facepunch.Pool.FreeUnmanaged(ref obj);
			}
		}
		allDoors.Clear();
		doorGrid.Clear();
		pendingReassertSet.Clear();
		pendingReasserts.Clear();
		saveDeferred.Clear();
		Door.ClearNavGateCache();
	}

	public static void Register(Door door)
	{
		if (!AI.useUnityNavmesh && gatesEnabled && !(door == null) && door.IsNavGate && !PlayerBoat.IsPartOfPlayerBoat(door))
		{
			Vector3 position = door.transform.position;
			if (allDoors.TryAdd(door, new DoorEntry
			{
				gridPosition = position
			}))
			{
				doorGrid.Add(position, door);
				AddToNeighbourLists(door);
				Apply(door, topologyChanged: true);
			}
		}
	}

	public static void Unregister(Door door)
	{
		if (!(door == null) && allDoors.Remove(door, out var value))
		{
			doorGrid.Remove(value.gridPosition, door);
			RemoveFromNeighbourLists(door, value);
			if (value.owners != null)
			{
				Facepunch.Pool.FreeUnmanaged(ref value.owners);
			}
			if (value.apertureRefs != null)
			{
				Facepunch.Pool.FreeUnmanaged(ref value.apertureRefs);
			}
		}
	}

	public static void OnDoorToggled(Door door)
	{
		if (AI.useUnityNavmesh || !gatesEnabled || door == null || !allDoors.TryGetValue(door, out var value))
		{
			return;
		}
		if (PlayerBoat.IsPartOfPlayerBoat(door))
		{
			Unregister(door);
			return;
		}
		Vector3 position = door.transform.position;
		if ((position - value.gridPosition).sqrMagnitude > 1f)
		{
			doorGrid.Remove(value.gridPosition, door);
			RemoveFromNeighbourLists(door, value);
			doorGrid.Add(position, door);
			value.gridPosition = position;
			value.ownersValid = false;
			value.apertureRefsValid = false;
			AddToNeighbourLists(door);
		}
		Apply(door);
	}

	public static void CollectGateDoorsReaching(Bounds worldBounds, HashSet<Door> results)
	{
		if (!gatesEnabled || allDoors.Count == 0)
		{
			return;
		}
		worldBounds.Expand(10f);
		using PooledList<Door> pooledList = Facepunch.Pool.Get<PooledList<Door>>();
		doorGrid.GetInBounds(worldBounds, pooledList);
		foreach (Door item in pooledList)
		{
			if (!(item == null) && !item.IsDestroyed && DoorReachTouches(item, in worldBounds) && TryGetDoorWriteBounds(item, out var worldBounds2) && worldBounds2.Intersects(worldBounds) && !PlayerBoat.IsPartOfPlayerBoat(item))
			{
				results.Add(item);
			}
		}
	}

	public static BakeVolumes GetBakeVolumes(Door door, bool hiRes, float agentRadius)
	{
		Matrix4x4 localToWorld = door.transform.localToWorldMatrix;
		if (!allDoors.TryGetValue(door, out var value))
		{
			NavMeshBuildVolume[] buffer = null;
			int leafCount;
			int count = BuildBakeVolumes(door, in localToWorld, agentRadius, ref buffer, out leafCount);
			return new BakeVolumes(buffer, leafCount, count);
		}
		ref BakeCache reference = ref hiRes ? ref value.bakeHiRes : ref value.bakeLoRes;
		if (!reference.cached || reference.matrix != localToWorld || reference.agentRadius != agentRadius)
		{
			reference.count = BuildBakeVolumes(door, in localToWorld, agentRadius, ref reference.volumes, out reference.leafCount);
			reference.matrix = localToWorld;
			reference.agentRadius = agentRadius;
			reference.cached = true;
		}
		return new BakeVolumes(reference.volumes, reference.leafCount, reference.count);
	}

	private static int BuildBakeVolumes(Door door, in Matrix4x4 localToWorld, float agentRadius, ref NavMeshBuildVolume[] buffer, out int leafCount)
	{
		leafCount = 0;
		using PooledList<Bounds> pooledList = Facepunch.Pool.Get<PooledList<Bounds>>();
		door.GetNavLeafRestSlabs(pooledList);
		Bounds bakeLocal;
		bool flag = door.TryGetNavDoorwayBakeVolume(agentRadius, out bakeLocal);
		int num = pooledList.Count + (flag ? 1 : 0);
		if (num == 0)
		{
			return 0;
		}
		if (buffer == null || buffer.Length < num)
		{
			buffer = new NavMeshBuildVolume[num];
		}
		for (int i = 0; i < pooledList.Count; i++)
		{
			BuildDoorVolume(pooledList[i], in localToWorld, 6, out buffer[i]);
		}
		leafCount = pooledList.Count;
		if (flag)
		{
			BuildDoorVolume(in bakeLocal, in localToWorld, 3, out buffer[leafCount]);
		}
		return num;
	}

	private unsafe static void BuildDoorVolume(in Bounds apertureLocal, in Matrix4x4 localToWorld, int area, out NavMeshBuildVolume volume)
	{
		volume = default(NavMeshBuildVolume);
		Span<Vector2> span = stackalloc Vector2[8];
		float num = float.MaxValue;
		float num2 = float.MinValue;
		Vector3 extents = apertureLocal.extents;
		for (int i = 0; i < 8; i++)
		{
			Vector3 vector = new Vector3(((i & 1) != 0) ? extents.x : (0f - extents.x), ((i & 2) != 0) ? extents.y : (0f - extents.y), ((i & 4) != 0) ? extents.z : (0f - extents.z));
			Vector3 vector2 = localToWorld.MultiplyPoint3x4(apertureLocal.center + vector);
			span[i] = new Vector2(vector2.x, vector2.z);
			num = Mathf.Min(num, vector2.y);
			num2 = Mathf.Max(num2, vector2.y);
		}
		for (int j = 1; j < 8; j++)
		{
			Vector2 vector3 = span[j];
			int num3 = j - 1;
			while (num3 >= 0 && (span[num3].x > vector3.x || (span[num3].x == vector3.x && span[num3].y > vector3.y)))
			{
				span[num3 + 1] = span[num3];
				num3--;
			}
			span[num3 + 1] = vector3;
		}
		Span<Vector2> span2 = stackalloc Vector2[16];
		int num4 = 0;
		for (int k = 0; k < 8; k++)
		{
			while (num4 >= 2 && Cross(span2[num4 - 2], span2[num4 - 1], span[k]) <= 0f)
			{
				num4--;
			}
			span2[num4++] = span[k];
		}
		int num5 = num4 + 1;
		for (int num6 = 6; num6 >= 0; num6--)
		{
			while (num4 >= num5 && Cross(span2[num4 - 2], span2[num4 - 1], span[num6]) <= 0f)
			{
				num4--;
			}
			span2[num4++] = span[num6];
		}
		num4--;
		volume.nverts = Mathf.Min(num4, 8);
		for (int l = 0; l < volume.nverts; l++)
		{
			volume.verts[l * 3] = span2[l].x;
			volume.verts[l * 3 + 1] = 0f;
			volume.verts[l * 3 + 2] = span2[l].y;
		}
		volume.hmin = num;
		volume.hmax = num2;
		volume.area = area;
		static float Cross(Vector2 o, Vector2 a, Vector2 b)
		{
			return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
		}
	}

	public static void ReassertInBounds(Bounds worldBounds)
	{
		if (AI.useUnityNavmesh || !gatesEnabled || allDoors.Count == 0)
		{
			return;
		}
		worldBounds.Expand(10f);
		using PooledList<Door> pooledList = Facepunch.Pool.Get<PooledList<Door>>();
		doorGrid.GetInBounds(worldBounds, pooledList);
		using PooledList<Door> pooledList2 = Facepunch.Pool.Get<PooledList<Door>>();
		foreach (Door item in pooledList)
		{
			if (!(item == null) && !item.IsDestroyed && DoorReachTouches(item, in worldBounds) && TryGetDoorWriteBounds(item, out var worldBounds2) && worldBounds2.Intersects(worldBounds))
			{
				pooledList2.Add(item);
			}
		}
		ApplyDoors(pooledList2);
	}

	public static void OnTileBuilt(RustNavmesh navmesh, int tx, int ty)
	{
		if (!gatesEnabled || allDoors.Count == 0)
		{
			return;
		}
		Bounds region = navmesh.rcCalcTileBounds(new Vector2Int(tx, ty));
		region.Expand(10f);
		using PooledList<Door> pooledList = Facepunch.Pool.Get<PooledList<Door>>();
		doorGrid.GetInBounds(region, pooledList);
		using (Facepunch.Pool.Get<PooledList<Door>>())
		{
			foreach (Door item in pooledList)
			{
				if (!(item == null) && !item.IsDestroyed && DoorReachTouches(item, in region) && TryGetDoorWriteBounds(item, out var worldBounds) && worldBounds.Intersects(region))
				{
					if (allDoors.TryGetValue(item, out var value))
					{
						value.apertureRefsValid = false;
					}
					bool num = ApertureState(item) == DoorPolyState.Blocked;
					bool flag = !item.HasNavSwingLeaf || !item.IsOpen();
					if (!(num && flag) && pendingReassertSet.Add(item))
					{
						pendingReasserts.Add(item);
					}
				}
			}
		}
	}

	public static void FlushTileReasserts(bool complete = true)
	{
		if (pendingReasserts.Count == 0)
		{
			return;
		}
		int num = pendingReasserts.Count;
		if (!complete && RustNav.doorReassertsPerTick > 0)
		{
			num = Mathf.Min(num, RustNav.doorReassertsPerTick);
		}
		using PooledList<Door> pooledList = Facepunch.Pool.Get<PooledList<Door>>();
		for (int i = 0; i < num; i++)
		{
			Door item = pendingReasserts[i];
			pooledList.Add(item);
			pendingReassertSet.Remove(item);
		}
		pendingReasserts.RemoveRange(0, num);
		ApplyDoors(pooledList);
	}

	public static void FlushSaveDeferredDoors()
	{
		if (saveDeferred.Count == 0)
		{
			return;
		}
		using PooledList<Door> pooledList = Facepunch.Pool.Get<PooledList<Door>>();
		foreach (Door item in saveDeferred)
		{
			pooledList.Add(item);
		}
		saveDeferred.Clear();
		ApplyDoors(pooledList);
	}

	public static void ReassertAll()
	{
		if (AI.useUnityNavmesh || !gatesEnabled)
		{
			return;
		}
		pendingReasserts.Clear();
		pendingReassertSet.Clear();
		saveDeferred.Clear();
		using PooledList<Door> pooledList = Facepunch.Pool.Get<PooledList<Door>>();
		foreach (KeyValuePair<Door, DoorEntry> allDoor in allDoors)
		{
			Door key = allDoor.Key;
			if (!(key == null) && !key.IsDestroyed)
			{
				pooledList.Add(key);
			}
		}
		ApplyDoors(pooledList);
	}

	private static DoorPolyState ApertureState(Door door)
	{
		if (door.HasFlag(BaseEntity.Flags.Open))
		{
			return DoorPolyState.Open;
		}
		if (!door.IsNpcOpenable)
		{
			return DoorPolyState.Blocked;
		}
		return DoorPolyState.NpcOpenable;
	}

	private static void Apply(Door door, bool topologyChanged = false)
	{
		if (Application.isLoadingSave)
		{
			return;
		}
		using PooledList<Door> pooledList = Facepunch.Pool.Get<PooledList<Door>>();
		pooledList.Add(door);
		ApplyDoors(pooledList, topologyChanged);
	}

	private static void ApplyDoors(List<Door> seeds, bool applyNeighbours = false)
	{
		if (seeds.Count == 0)
		{
			return;
		}
		RustNavigation instance = RustNavigation.Instance;
		if (instance == null)
		{
			return;
		}
		long num = (Profile.enabled ? Stopwatch.GetTimestamp() : 0);
		using PooledList<Door> pooledList = Facepunch.Pool.Get<PooledList<Door>>();
		using PooledHashSet<Door> pooledHashSet = Facepunch.Pool.Get<PooledHashSet<Door>>();
		foreach (Door seed in seeds)
		{
			if (!(seed == null) && !seed.IsDestroyed && pooledHashSet.Add(seed))
			{
				pooledList.Add(seed);
			}
		}
		int count = pooledList.Count;
		if (count == 0)
		{
			return;
		}
		if (applyNeighbours)
		{
			for (int i = 0; i < count; i++)
			{
				List<Door> owners = GetOwners(pooledList[i]);
				if (owners == null)
				{
					continue;
				}
				for (int j = 0; j < owners.Count; j++)
				{
					Door item = owners[j];
					if (pooledHashSet.Add(item))
					{
						pooledList.Add(item);
					}
				}
			}
		}
		if (Profile.enabled)
		{
			Profile.gatherTicks += Stopwatch.GetTimestamp() - num;
			Profile.groupDoors += pooledList.Count;
		}
		RustNavmesh defaultNavmesh = instance.DefaultNavmesh;
		if (defaultNavmesh != null && defaultNavmesh.IsValid())
		{
			if (defaultNavmesh.IsSaveInFlight)
			{
				for (int k = 0; k < pooledList.Count; k++)
				{
					saveDeferred.Add(pooledList[k]);
				}
			}
			else
			{
				ApplyGroupToNavmesh(defaultNavmesh, null, pooledList);
			}
		}
		if (!IndependantNavmesh.AnyRegistered)
		{
			return;
		}
		using PooledList<IndependantNavmesh> pooledList2 = Facepunch.Pool.Get<PooledList<IndependantNavmesh>>();
		foreach (Door item2 in pooledList)
		{
			IndependantNavmesh independantNavmesh = null;
			if (item2 != null && !item2.IsDestroyed)
			{
				independantNavmesh = IndependantNavmesh.FindNavmeshAtPosition(item2.transform.position);
			}
			if (independantNavmesh == null || independantNavmesh.Navmesh == null || !independantNavmesh.Navmesh.IsValid())
			{
				independantNavmesh = null;
			}
			pooledList2.Add(independantNavmesh);
		}
		using PooledList<Door> pooledList3 = Facepunch.Pool.Get<PooledList<Door>>();
		using PooledHashSet<IndependantNavmesh> pooledHashSet2 = Facepunch.Pool.Get<PooledHashSet<IndependantNavmesh>>();
		for (int l = 0; l < pooledList.Count; l++)
		{
			IndependantNavmesh independantNavmesh2 = pooledList2[l];
			if (independantNavmesh2 == null || !pooledHashSet2.Add(independantNavmesh2))
			{
				continue;
			}
			pooledList3.Clear();
			for (int m = 0; m < pooledList.Count; m++)
			{
				if ((object)pooledList2[m] == independantNavmesh2)
				{
					pooledList3.Add(pooledList[m]);
				}
			}
			ApplyGroupToNavmesh(independantNavmesh2.Navmesh, independantNavmesh2, pooledList3);
		}
	}

	private static void ApplyGroupToNavmesh(RustNavmesh navmesh, IndependantNavmesh independent, List<Door> doors)
	{
		long num = (Profile.enabled ? Stopwatch.GetTimestamp() : 0);
		using PooledList<Vector3> pooledList2 = Facepunch.Pool.Get<PooledList<Vector3>>();
		using PooledList<int> pooledList3 = Facepunch.Pool.Get<PooledList<int>>();
		using PooledList<int> pooledList = Facepunch.Pool.Get<PooledList<int>>();
		for (int i = 0; i < doors.Count; i++)
		{
			pooledList.Add(pooledList2.Count);
			pooledList3.Add(WillWriteFromRefCache(doors[i], independent) ? (-2) : GatherOwnerPositions(doors[i], independent, pooledList2));
		}
		if (Profile.enabled)
		{
			Profile.gatherTicks += Stopwatch.GetTimestamp() - num;
		}
		for (int j = 0; j < doors.Count; j++)
		{
			Door door = doors[j];
			if (!(door == null) && !door.IsDestroyed && door.TryGetNavLeafRegion(out var leafRegionLocal, out var localToWorld))
			{
				SetDoorObbFlags(navmesh, independent, in leafRegionLocal, in localToWorld, door, DoorPolyState.Open, 6, pooledList2, pooledList[j], pooledList3[j]);
			}
		}
		for (DoorPolyState doorPolyState = DoorPolyState.Open; doorPolyState <= DoorPolyState.Blocked; doorPolyState++)
		{
			for (int k = 0; k < doors.Count; k++)
			{
				Door door2 = doors[k];
				if (!(door2 == null) && !door2.IsDestroyed && ApertureState(door2) == doorPolyState && door2.TryGetNavDoorwayVolume(out var apertureLocal, out var localToWorld2))
				{
					SetDoorObbFlags(navmesh, independent, in apertureLocal, in localToWorld2, door2, doorPolyState, 3, pooledList2, pooledList[k], pooledList3[k]);
				}
			}
		}
		using PooledList<Bounds> pooledList4 = Facepunch.Pool.Get<PooledList<Bounds>>();
		for (int l = 0; l < doors.Count; l++)
		{
			Door door3 = doors[l];
			if (door3 == null || door3.IsDestroyed || !door3.HasNavSwingLeaf || !door3.IsOpen())
			{
				continue;
			}
			Matrix4x4 localToWorld3 = door3.transform.localToWorldMatrix;
			pooledList4.Clear();
			door3.GetNavLeafActiveSlabs(pooledList4);
			foreach (Bounds item in pooledList4)
			{
				SetDoorObbFlags(navmesh, independent, item, in localToWorld3, door3, DoorPolyState.Blocked, 6, pooledList2, pooledList[l], pooledList3[l]);
			}
		}
	}

	private static bool WillWriteFromRefCache(Door door, IndependantNavmesh independent)
	{
		if (!polyRefCacheEnabled || independent != null)
		{
			return false;
		}
		if (door == null || door.IsDestroyed || door.HasNavSwingLeaf)
		{
			return false;
		}
		if (allDoors.TryGetValue(door, out var value))
		{
			return value.apertureRefsValid;
		}
		return false;
	}

	private static int GatherOwnerPositions(Door door, IndependantNavmesh independent, List<Vector3> positions)
	{
		if (door == null || door.IsDestroyed)
		{
			return -1;
		}
		if (!allDoors.ContainsKey(door))
		{
			return -1;
		}
		if (!ownershipEnabled)
		{
			return 0;
		}
		List<Door> owners = GetOwners(door);
		if (owners == null)
		{
			return -1;
		}
		int num = 0;
		for (int i = 0; i < owners.Count; i++)
		{
			Door door2 = owners[i];
			if (door2 == null || door2.IsDestroyed)
			{
				continue;
			}
			Vector3 position = door2.transform.position;
			if (independent != null && IndependantNavmesh.FindNavmeshAtPosition(position) != independent)
			{
				continue;
			}
			if (num == 128)
			{
				if (!warnedOwnerOverflow)
				{
					warnedOwnerOverflow = true;
					UnityEngine.Debug.LogWarning($"RustNavDoorGates: door at {door.transform.position} has more than {128} owner candidates, ownership may misassign");
				}
				break;
			}
			positions.Add((independent != null) ? independent.TransformPointFromWorldSpaceToNavSpace(position).Value : position);
			num++;
		}
		return num;
	}

	private static void SetDoorObbFlags(RustNavmesh navmesh, IndependantNavmesh independent, in Bounds localBounds, in Matrix4x4 localToWorld, Door door, DoorPolyState state, int area, List<Vector3> ownerPositions, int ownerStart, int ownerCount)
	{
		if (ownerCount < 0 && ownerCount != -2)
		{
			return;
		}
		Transform transform = door.transform;
		DoorEntry value = null;
		if (polyRefCacheEnabled && independent == null && area == 3)
		{
			allDoors.TryGetValue(door, out value);
		}
		if (value != null && value.apertureRefsValid)
		{
			int count = value.apertureRefs.Count;
			if (count == 0)
			{
				return;
			}
			long num = (Profile.enabled ? Stopwatch.GetTimestamp() : 0);
			for (int i = 0; i < count; i++)
			{
				refScratch[i] = value.apertureRefs[i];
			}
			bool num2 = navmesh.SetDoorPolyFlagsForRefs(refScratch, count, state, NavCenter(independent, localToWorld.MultiplyPoint3x4(localBounds.center)), DoorHalfExtents(in localBounds, transform), DoorAxisXZ(transform, independent));
			if (Profile.enabled)
			{
				Profile.writeTicks += Stopwatch.GetTimestamp() - num;
				Profile.writes++;
			}
			if (num2)
			{
				return;
			}
			value.apertureRefsValid = false;
		}
		if (ownerCount == -2)
		{
			using PooledList<Vector3> pooledList = Facepunch.Pool.Get<PooledList<Vector3>>();
			ownerCount = GatherOwnerPositions(door, independent, pooledList);
			if (ownerCount < 0)
			{
				return;
			}
			for (int j = 0; j < ownerCount; j++)
			{
				ownerScratch[j] = pooledList[j];
			}
		}
		else
		{
			for (int k = 0; k < ownerCount; k++)
			{
				ownerScratch[k] = ownerPositions[ownerStart + k];
			}
		}
		Vector3 worldCenter = localToWorld.MultiplyPoint3x4(localBounds.center);
		Vector3 worldSpaceDirection = transform.right;
		NavVector3 center = NavCenter(independent, worldCenter);
		if (independent != null)
		{
			worldSpaceDirection = independent.TransformDirectionFromWorldSpaceToNavSpace(worldSpaceDirection).Value;
		}
		Vector2 axisXZ = new Vector2(worldSpaceDirection.x, worldSpaceDirection.z);
		if (axisXZ.sqrMagnitude < 0.0001f)
		{
			axisXZ = Vector2.right;
		}
		axisXZ.Normalize();
		Vector3 lossyScale = transform.lossyScale;
		Vector3 halfExtents = new Vector3(localBounds.extents.x * Mathf.Abs(lossyScale.x), localBounds.extents.y * Mathf.Abs(lossyScale.y), localBounds.extents.z * Mathf.Abs(lossyScale.z)) + new Vector3(0.1f, 0.1f, 0.1f);
		ulong[] array = ((value != null) ? refScratch : null);
		long num3 = (Profile.enabled ? Stopwatch.GetTimestamp() : 0);
		navmesh.SetDoorPolyFlags(center, halfExtents, axisXZ, state, area, NavCenter(independent, transform.position), ownerScratch, ownerCount, array, (array != null) ? 64 : 0, out var outRefCount);
		if (Profile.enabled)
		{
			Profile.writeTicks += Stopwatch.GetTimestamp() - num3;
			Profile.writes++;
		}
		if (value != null && outRefCount >= 0 && outRefCount <= 64)
		{
			DoorEntry doorEntry = value;
			if (doorEntry.apertureRefs == null)
			{
				doorEntry.apertureRefs = Facepunch.Pool.Get<List<ulong>>();
			}
			value.apertureRefs.Clear();
			for (int l = 0; l < outRefCount; l++)
			{
				value.apertureRefs.Add(refScratch[l]);
			}
			value.apertureRefsValid = true;
		}
	}

	private static Vector3 DoorHalfExtents(in Bounds localBounds, Transform doorTransform)
	{
		Vector3 lossyScale = doorTransform.lossyScale;
		return new Vector3(localBounds.extents.x * Mathf.Abs(lossyScale.x), localBounds.extents.y * Mathf.Abs(lossyScale.y), localBounds.extents.z * Mathf.Abs(lossyScale.z)) + new Vector3(0.1f, 0.1f, 0.1f);
	}

	private static Vector2 DoorAxisXZ(Transform doorTransform, IndependantNavmesh independent)
	{
		Vector3 worldSpaceDirection = doorTransform.right;
		if (independent != null)
		{
			worldSpaceDirection = independent.TransformDirectionFromWorldSpaceToNavSpace(worldSpaceDirection).Value;
		}
		Vector2 result = new Vector2(worldSpaceDirection.x, worldSpaceDirection.z);
		if (result.sqrMagnitude < 0.0001f)
		{
			result = Vector2.right;
		}
		result.Normalize();
		return result;
	}

	private static NavVector3 NavCenter(IndependantNavmesh independent, Vector3 worldCenter)
	{
		if (!(independent != null))
		{
			return new NavVector3(worldCenter);
		}
		return independent.TransformPointFromWorldSpaceToNavSpace(worldCenter);
	}

	private static List<Door> GetOwners(Door door)
	{
		if (!allDoors.TryGetValue(door, out var value))
		{
			return null;
		}
		if (value.ownersValid)
		{
			return value.owners;
		}
		DoorEntry doorEntry = value;
		if (doorEntry.owners == null)
		{
			doorEntry.owners = Facepunch.Pool.Get<List<Door>>();
		}
		value.owners.Clear();
		value.ownersValid = true;
		if (TryGetDoorWriteBounds(door, out var worldBounds))
		{
			Bounds bounds = worldBounds;
			bounds.Expand(12f);
			using PooledList<Door> pooledList = Facepunch.Pool.Get<PooledList<Door>>();
			doorGrid.GetInBounds(bounds, pooledList);
			foreach (Door item in pooledList)
			{
				if (!(item == door) && !(item == null) && !item.IsDestroyed && DoorReachTouches(item, in worldBounds) && TryGetDoorWriteBounds(item, out var worldBounds2) && worldBounds.Intersects(worldBounds2))
				{
					value.owners.Add(item);
				}
			}
		}
		return value.owners;
	}

	private static void AddToNeighbourLists(Door door)
	{
		if (!ownershipEnabled)
		{
			return;
		}
		List<Door> owners = GetOwners(door);
		if (owners == null)
		{
			return;
		}
		for (int i = 0; i < owners.Count; i++)
		{
			Door door2 = owners[i];
			if (!(door2 == null) && allDoors.TryGetValue(door2, out var value) && value.ownersValid && !value.owners.Contains(door))
			{
				value.owners.Add(door);
			}
		}
	}

	private static void RemoveFromNeighbourLists(Door door, DoorEntry entry)
	{
		if (entry.owners == null)
		{
			return;
		}
		for (int i = 0; i < entry.owners.Count; i++)
		{
			Door door2 = entry.owners[i];
			if (!(door2 == null) && allDoors.TryGetValue(door2, out var value))
			{
				value.owners?.Remove(door);
			}
		}
	}

	private static bool DoorReachTouches(Door door, in Bounds region)
	{
		if (!allDoors.TryGetValue(door, out var value))
		{
			return true;
		}
		Vector3 gridPosition = value.gridPosition;
		if (gridPosition.x >= region.min.x - 6f && gridPosition.x <= region.max.x + 6f && gridPosition.y >= region.min.y - 6f && gridPosition.y <= region.max.y + 6f && gridPosition.z >= region.min.z - 6f)
		{
			return gridPosition.z <= region.max.z + 6f;
		}
		return false;
	}

	private static bool TryGetDoorWriteBounds(Door door, out Bounds worldBounds)
	{
		if (!allDoors.TryGetValue(door, out var value))
		{
			return BuildDoorWriteBounds(door, door.transform.localToWorldMatrix, out worldBounds);
		}
		Matrix4x4 localToWorld = door.transform.localToWorldMatrix;
		if (value.boundsCached && value.boundsMatrix == localToWorld)
		{
			worldBounds = value.writeBounds;
			return value.hasWriteBounds;
		}
		bool flag = BuildDoorWriteBounds(door, in localToWorld, out worldBounds);
		value.boundsCached = true;
		value.boundsMatrix = localToWorld;
		value.writeBounds = worldBounds;
		value.hasWriteBounds = flag;
		return flag;
	}

	private static bool BuildDoorWriteBounds(Door door, in Matrix4x4 localToWorld, out Bounds worldBounds)
	{
		if (!door.TryGetNavDoorwayVolume(out var apertureLocal, out var localToWorld2))
		{
			worldBounds = default(Bounds);
			return false;
		}
		worldBounds = apertureLocal.Transform(localToWorld);
		if (door.TryGetNavLeafRegion(out var leafRegionLocal, out localToWorld2))
		{
			worldBounds.Encapsulate(leafRegionLocal.Transform(localToWorld));
		}
		worldBounds.Expand(2f);
		return true;
	}
}

