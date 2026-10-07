using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using AOT;
using ConVar;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2.Nav;

public class RustNavigation : FacepunchBehaviour, IServerComponent
{
	public NavMeshBuildParams BuildParams = new NavMeshBuildParams(dummy: true);

	public NavMeshBuildParams BuildParamsHiRes = new NavMeshBuildParams(dummy: true);

	internal const string LOG_PREFIX = "[RustNav] ";

	private BackgroundTileBuilder tileBuilder;

	private RustNavmesh _defaultNavmesh;

	private HashSet<IndependantNavmesh> _navmeshes = new HashSet<IndependantNavmesh>();

	private static readonly RecastWrapper.LogCallback logMessageDelegate = LogMessage;

	private const int TUNNEL_REGION_CELL_SIZE = 32;

	private readonly Dictionary<(int x, int z), List<Bounds>> tunnelRegions = new Dictionary<(int, int), List<Bounds>>();

	private static readonly HashSet<BasePlayer> drawViewers = new HashSet<BasePlayer>();

	private static readonly Dictionary<IndependantNavmesh, int> drawNavIds = new Dictionary<IndependantNavmesh, int>();

	private static int nextDrawNavId = 1;

	public const int DefaultNavmeshDrawId = 0;

	public static RustNavigation Instance { get; private set; }

	public RustNavmesh DefaultNavmesh
	{
		get
		{
			if (!EnsureNewNavmesh())
			{
				return null;
			}
			return _defaultNavmesh;
		}
		private set
		{
			if (EnsureNewNavmesh())
			{
				_defaultNavmesh = value;
				if (_defaultNavmesh != null)
				{
					_defaultNavmesh.EmitTileChangeEvents = true;
					_defaultNavmesh.debugName = "default";
				}
				OnDefaultNavmeshInstanceChanged();
				RustNavDoorGates.ForgetPolyRefs();
			}
		}
	}

	private HashSet<IndependantNavmesh> Navmeshes
	{
		get
		{
			if (!EnsureNewNavmesh())
			{
				return null;
			}
			return _navmeshes;
		}
	}

	public static bool HasTunnelRegions { get; private set; }

	public bool IsNavmeshSaveInFlight
	{
		get
		{
			if (DefaultNavmesh != null)
			{
				return DefaultNavmesh.IsSaveInFlight;
			}
			return false;
		}
	}

	public NavmeshSaveCatchUpStats SaveGateCatchUp
	{
		get
		{
			if (tileBuilder == null)
			{
				return default(NavmeshSaveCatchUpStats);
			}
			return tileBuilder.SaveGateCatchUp;
		}
	}

	[MonoPInvokeCallback(typeof(RecastWrapper.LogCallback))]
	public static void LogMessage(string message)
	{
		if ((AI.logIssues || IsSaveOrLoadMessage(message)) && EnsureNewNavmesh())
		{
			UnityEngine.Debug.Log("[RustNav] DLL Log: " + message);
		}
	}

	private static bool IsSaveOrLoadMessage(string message)
	{
		if (message != null)
		{
			if (!message.StartsWith("SaveNavMesh", StringComparison.Ordinal) && !message.StartsWith("AppendNavMeshDelta", StringComparison.Ordinal))
			{
				return message.StartsWith("LoadNavMesh", StringComparison.Ordinal);
			}
			return true;
		}
		return false;
	}

	public static bool EnsureNewNavmesh()
	{
		if (AI.useUnityNavmesh)
		{
			LogError("Trying to use new navmesh despite -useOldNavmesh being set on server boot.");
		}
		return !AI.useUnityNavmesh;
	}

	public static bool EnsureUnityNavmesh()
	{
		if (!AI.useUnityNavmesh)
		{
			LogError("Trying to use unity navmesh despite -useOldNavmesh not being set on server boot.");
		}
		return AI.useUnityNavmesh;
	}

	private void Awake()
	{
		if (Instance == null)
		{
			Instance = this;
			RustNavDoorGates.Clear();
			if (!AI.useUnityNavmesh)
			{
				RecastWrapper.SetLogCallback(logMessageDelegate);
			}
		}
		else
		{
			LogWarning("Multiple RustNavigation instances detected. Destroying...");
			UnityEngine.Object.Destroy(base.gameObject);
		}
	}

	private void ValidateNavmeshes()
	{
		Log("Navmesh tile validation is enabled. This may impact performance.");
		if (DefaultNavmesh != null && DefaultNavmesh.NavMeshHandle != IntPtr.Zero)
		{
			RecastWrapper.ValidateNavMesh(DefaultNavmesh.NavMeshHandle);
		}
		foreach (IndependantNavmesh navmesh in Navmeshes)
		{
			if (navmesh != null && navmesh.Navmesh != null && navmesh.Navmesh.NavMeshHandle != IntPtr.Zero)
			{
				RecastWrapper.ValidateNavMesh(navmesh.Navmesh.NavMeshHandle);
			}
		}
	}

	public IEnumerator BootstrapBuildNavMesh()
	{
		if (!AI.useUnityNavmesh)
		{
			yield return new WaitUntil(() => !AiManager.nav_disable && AI.move);
			if (DefaultNavmesh == null)
			{
				RebuildDefaultNavmesh();
			}
		}
	}

	public void RebuildDefaultNavmesh(bool synchronous = false)
	{
		if (!EnsureNewNavmesh())
		{
			return;
		}
		if (tileBuilder == null)
		{
			tileBuilder = new BackgroundTileBuilder();
		}
		BakeStats.Reset();
		BackgroundTileBuilder backgroundTileBuilder = tileBuilder;
		bool synchronous2 = synchronous;
		RustNavmesh rustNavmesh = new RustNavmesh(backgroundTileBuilder, null, null, null, shouldBuild: true, synchronous2, forceHiRes: false, cullTilesFarFromShore: true);
		if (rustNavmesh == null || !rustNavmesh.IsValid())
		{
			LogError("Failed to build default navmesh");
			return;
		}
		if (DefaultNavmesh != null)
		{
			DefaultNavmesh.Dispose();
		}
		DefaultNavmesh = rustNavmesh;
		RustNavDoorGates.ReassertAll();
	}

	public bool HasPendingTiles()
	{
		if (tileBuilder == null)
		{
			return false;
		}
		List<(RustNavmesh, int, int)> obj = Facepunch.Pool.Get<List<(RustNavmesh, int, int)>>();
		tileBuilder.GetPendingTilesOnMainThread(obj);
		bool result = obj.Count > 0;
		Facepunch.Pool.FreeUnmanaged(ref obj);
		return result;
	}

	public void RebuildTileSynchronous(RustNavmesh navmesh, int tx, int ty)
	{
		if (tileBuilder != null && navmesh != null)
		{
			tileBuilder.EnqueueOnMainThread(navmesh, tx, ty, synchronous: true);
			RustNavDoorGates.FlushTileReasserts();
		}
	}

	public void AddNavmesh(IndependantNavmesh navmesh)
	{
		if (EnsureNewNavmesh())
		{
			if (tileBuilder == null)
			{
				tileBuilder = new BackgroundTileBuilder();
			}
			Navmeshes.Add(navmesh);
			navmesh.Rebuild(tileBuilder);
		}
	}

	public void RemoveNavmesh(IndependantNavmesh navmesh)
	{
		if (EnsureNewNavmesh())
		{
			Navmeshes.Remove(navmesh);
		}
	}

	public void AddTunnelRegion(Bounds worldBounds)
	{
		GetTunnelCellRange(worldBounds, out var minX, out var maxX, out var minZ, out var maxZ);
		for (int i = minX; i <= maxX; i++)
		{
			for (int j = minZ; j <= maxZ; j++)
			{
				if (!tunnelRegions.TryGetValue((i, j), out var value))
				{
					value = new List<Bounds>();
					tunnelRegions.Add((i, j), value);
				}
				if (!value.Contains(worldBounds))
				{
					value.Add(worldBounds);
				}
			}
		}
		HasTunnelRegions = tunnelRegions.Count > 0;
	}

	public void ClearTunnelRegions()
	{
		tunnelRegions.Clear();
		HasTunnelRegions = false;
	}

	public bool IsInTunnelRegion(Bounds worldBounds)
	{
		if (tunnelRegions.Count == 0)
		{
			return false;
		}
		GetTunnelCellRange(worldBounds, out var minX, out var maxX, out var minZ, out var maxZ);
		Vector3 min = worldBounds.min;
		Vector3 max = worldBounds.max;
		for (int i = minX; i <= maxX; i++)
		{
			for (int j = minZ; j <= maxZ; j++)
			{
				if (!tunnelRegions.TryGetValue((i, j), out var value))
				{
					continue;
				}
				foreach (Bounds item in value)
				{
					Vector3 min2 = item.min;
					Vector3 max2 = item.max;
					if (min2.x <= max.x && max2.x >= min.x && min2.z <= max.z && max2.z >= min.z)
					{
						return true;
					}
				}
			}
		}
		return false;
	}

	private static void GetTunnelCellRange(Bounds worldBounds, out int minX, out int maxX, out int minZ, out int maxZ)
	{
		minX = Mathf.FloorToInt(worldBounds.min.x / 32f);
		maxX = Mathf.FloorToInt(worldBounds.max.x / 32f);
		minZ = Mathf.FloorToInt(worldBounds.min.z / 32f);
		maxZ = Mathf.FloorToInt(worldBounds.max.z / 32f);
	}

	public void Tick()
	{
		if (!AI.useUnityNavmesh)
		{
			if (DefaultNavmesh != null)
			{
				DefaultNavmesh.PollSaveOnMainThread();
			}
			if (tileBuilder != null)
			{
				tileBuilder.TickOnMainThread();
			}
		}
	}

	private void OnDestroy()
	{
		if (Instance == this)
		{
			Instance = null;
		}
		if (!AI.useUnityNavmesh)
		{
			if (DefaultNavmesh != null)
			{
				DefaultNavmesh.Dispose();
				DefaultNavmesh = null;
			}
			if (tileBuilder != null)
			{
				tileBuilder.Dispose();
				tileBuilder = null;
			}
		}
	}

	public static void Log(string message)
	{
		DebugEx.Log("[RustNav] " + message);
	}

	public static void LogError(string message)
	{
		UnityEngine.Debug.LogError("[RustNav] " + message);
	}

	public static void LogWarning(string message)
	{
		DebugEx.LogWarning("[RustNav] " + message);
	}

	public static string GetNavMeshSavePath()
	{
		return Path.ChangeExtension(Path.Combine(ConVar.Server.rootFolder, World.SaveFileName), ".navmesh");
	}

	public bool Save(string path)
	{
		using (TimeWarning.New("RustNavigation.Save"))
		{
			if (!CanSave())
			{
				return false;
			}
			Log("Saving navmesh to path: " + path + "...");
			return DefaultNavmesh.Save(path);
		}
	}

	public bool BeginSave(string path)
	{
		using (TimeWarning.New("RustNavigation.BeginSave"))
		{
			if (!CanSave())
			{
				return false;
			}
			if (DefaultNavmesh.BeginSave(path))
			{
				Log("Saving navmesh to path: " + path + "...");
				return true;
			}
			if (DefaultNavmesh.IsSaveInFlight)
			{
				Log("A navmesh save is still in flight, this cycle's write is queued behind it");
			}
			return false;
		}
	}

	public void JoinSave()
	{
		if (!AI.useUnityNavmesh && DefaultNavmesh != null)
		{
			DefaultNavmesh.JoinSaveOnMainThread();
		}
	}

	public void JoinSaveAndLandParkedTiles()
	{
		if (!AI.useUnityNavmesh && DefaultNavmesh != null)
		{
			DefaultNavmesh.JoinSaveAndLandParkedTilesOnMainThread();
		}
	}

	public void FlushSaves()
	{
		if (!AI.useUnityNavmesh && DefaultNavmesh != null)
		{
			DefaultNavmesh.FlushSavesOnMainThread();
		}
	}

	private bool CanSave()
	{
		if (!EnsureNewNavmesh())
		{
			return false;
		}
		return DefaultNavmesh != null;
	}

	public bool Load(string path, bool synchronous = false)
	{
		using (TimeWarning.New("RustNavigation.Load"))
		{
			if (!EnsureNewNavmesh())
			{
				return false;
			}
			Log($"Loading navmesh from path: {path} (synchronous: {synchronous})...");
			JoinSave();
			if (!File.Exists(path))
			{
				LogWarning("Navmesh file not found at path: " + path);
				return false;
			}
			if (tileBuilder == null)
			{
				tileBuilder = new BackgroundTileBuilder();
			}
			RustNavmesh rustNavmesh = RustNavmesh.Load(path, tileBuilder, synchronous, cullTilesFarFromShore: true);
			if (rustNavmesh == null || !rustNavmesh.IsValid())
			{
				return false;
			}
			if (DefaultNavmesh != null)
			{
				DefaultNavmesh.Dispose();
			}
			DefaultNavmesh = rustNavmesh;
			rustNavmesh.ResetDoorPolyFlagsToOpen();
			RustNavDoorGates.ReassertAll();
			RustNavMeshAgent.RebindAgentsAfterNavmeshSwap();
			if (AI.checkTileValid)
			{
				ValidateNavmeshes();
			}
			return true;
		}
	}

	public void ResetTileBuilder()
	{
		if (!EnsureNewNavmesh())
		{
			return;
		}
		using PooledList<(RustNavmesh, int, int)> pooledList = Facepunch.Pool.Get<PooledList<(RustNavmesh, int, int)>>();
		if (tileBuilder != null)
		{
			tileBuilder.GetPendingTilesOnMainThread(pooledList);
			tileBuilder.Dispose();
		}
		tileBuilder = new BackgroundTileBuilder();
		if (DefaultNavmesh != null)
		{
			DefaultNavmesh.SetTileBuilder(tileBuilder);
		}
		foreach (IndependantNavmesh navmesh in Navmeshes)
		{
			if (navmesh != null && navmesh.Navmesh != null)
			{
				navmesh.Navmesh.SetTileBuilder(tileBuilder);
			}
		}
		foreach (var item in pooledList)
		{
			tileBuilder.EnqueueOnMainThread(item.Item1, item.Item2, item.Item3);
		}
	}

	public bool IsDefaultNavmeshBuilt()
	{
		using (TimeWarning.New("RustNavigation.IsDefaultNavmeshBuilt"))
		{
			if (!EnsureNewNavmesh())
			{
				return false;
			}
			return DefaultNavmesh != null && DefaultNavmesh.IsBuilt();
		}
	}

	public string ReportNavmeshStats()
	{
		using PooledList<RustNavmesh> pooledList = Facepunch.Pool.Get<PooledList<RustNavmesh>>();
		if (_defaultNavmesh != null && _defaultNavmesh.IsValid())
		{
			pooledList.Add(_defaultNavmesh);
		}
		foreach (IndependantNavmesh navmesh in _navmeshes)
		{
			if (navmesh != null && navmesh.Navmesh != null && navmesh.Navmesh.IsValid())
			{
				pooledList.Add(navmesh.Navmesh);
			}
		}
		for (int i = 0; i < pooledList.Count - 1; i++)
		{
			int num = i;
			for (int j = i + 1; j < pooledList.Count; j++)
			{
				if (pooledList[j].workerBuildTicks > pooledList[num].workerBuildTicks)
				{
					num = j;
				}
			}
			if (num != i)
			{
				int index = i;
				PooledList<RustNavmesh> pooledList2 = pooledList;
				int index2 = num;
				RustNavmesh rustNavmesh = pooledList[num];
				RustNavmesh rustNavmesh2 = pooledList[i];
				RustNavmesh rustNavmesh4 = (pooledList[index] = rustNavmesh);
				rustNavmesh4 = (pooledList2[index2] = rustNavmesh2);
			}
		}
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine($"=== RustNav navmeshes ({pooledList.Count}) by accumulated worker build time ===");
		if (!RustNav.bakeStatsEnabled)
		{
			stringBuilder.AppendLine("(worker times only accumulate while rustnav.bakestatsenabled is true)");
		}
		double num2 = 1000.0 / (double)Stopwatch.Frequency;
		foreach (RustNavmesh item in pooledList)
		{
			double num3 = (double)item.workerBuildTicks * num2;
			string text = ((item.lastFullBuildSeconds >= 0.0) ? $"{item.lastFullBuildSeconds:F2}s" : "building");
			stringBuilder.AppendLine($"{item.debugName,-40} worker {num3,10:F1}ms  tiles {item.NumBuiltTiles,6}/{item.TotalTiles,-6} first full build {text}");
		}
		AppendLastSaveLine(stringBuilder);
		return stringBuilder.ToString();
	}

	private void AppendLastSaveLine(StringBuilder sb)
	{
		if (_defaultNavmesh != null)
		{
			sb.AppendLine($"navmesh save in flight: {IsNavmeshSaveInFlight}");
			sb.AppendLine("last navmesh save: " + _defaultNavmesh.LastSaveStats.Describe());
			if (tileBuilder != null)
			{
				sb.AppendLine(tileBuilder.DescribeSaveGateCatchUp());
			}
		}
	}

	public void RebuildTilesInBounds(Bounds rebuildBounds, bool synchronous = false)
	{
		using (TimeWarning.New("RustNavigation.RebuildTilesInBounds"))
		{
			if (!EnsureNewNavmesh())
			{
				return;
			}
			using PooledList<IndependantNavmesh> pooledList = Facepunch.Pool.Get<PooledList<IndependantNavmesh>>();
			IndependantNavmesh.FindNavmeshesInBounds(rebuildBounds, pooledList);
			foreach (IndependantNavmesh item in pooledList)
			{
				item.RebuildTilesInBounds(rebuildBounds, synchronous);
			}
			if (IsDefaultNavmeshBuilt())
			{
				DefaultNavmesh.RebuildTilesInBounds(rebuildBounds, synchronous);
			}
			if (synchronous)
			{
				RustNavDoorGates.FlushTileReasserts();
			}
		}
	}

	public void RebuildTilesUnder(BaseEntity entity)
	{
		Bounds rebuildBounds = entity.WorldSpaceBounds().ToBounds();
		using PooledList<Collider> pooledList = Facepunch.Pool.Get<PooledList<Collider>>();
		entity.GetComponentsInChildren(pooledList);
		foreach (Collider item in pooledList)
		{
			if (item.enabled && !item.isTrigger && (0x41218101 & (1 << item.gameObject.layer)) != 0)
			{
				rebuildBounds.Encapsulate(item.bounds);
			}
		}
		RebuildTilesInBounds(rebuildBounds);
	}

	public static int GetDrawNavId(IndependantNavmesh navmesh)
	{
		if (navmesh == null)
		{
			return 0;
		}
		if (!drawNavIds.TryGetValue(navmesh, out var value))
		{
			value = nextDrawNavId++;
			drawNavIds[navmesh] = value;
		}
		return value;
	}

	public static void AddDrawViewer(BasePlayer player)
	{
		if (!(player == null))
		{
			drawViewers.Add(player);
		}
	}

	public static void RemoveDrawViewer(BasePlayer player)
	{
		if (!(player == null))
		{
			drawViewers.Remove(player);
		}
	}

	public static bool IsDrawViewer(BasePlayer player)
	{
		if (player != null)
		{
			return drawViewers.Contains(player);
		}
		return false;
	}

	public static void NotifyDefaultNavmeshTileChanged(int tx, int ty)
	{
		if (drawViewers.Count == 0 || Instance == null)
		{
			return;
		}
		RustNavmesh defaultNavmesh = Instance._defaultNavmesh;
		if (defaultNavmesh == null || !defaultNavmesh.IsValid())
		{
			return;
		}
		Bounds tileBounds = defaultNavmesh.rcCalcTileBounds(new Vector2Int(tx, ty));
		foreach (BasePlayer drawViewer in drawViewers)
		{
			if (!(drawViewer == null) && ViewerCoversTile(drawViewer, tileBounds))
			{
				drawViewer.MarkNavmeshTileDirty(tx, ty);
			}
		}
	}

	private static void OnDefaultNavmeshInstanceChanged()
	{
		foreach (BasePlayer drawViewer in drawViewers)
		{
			if (!(drawViewer == null))
			{
				drawViewer.ResetNavmeshDrawState();
			}
		}
	}

	private static bool ViewerCoversTile(BasePlayer viewer, Bounds tileBounds)
	{
		float drawRadius = RustNav.drawRadius;
		return new Bounds(viewer.transform.position, new Vector3(drawRadius * 2f, tileBounds.size.y, drawRadius * 2f)).Intersects(tileBounds);
	}
}

