using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using ConVar;
using UnityEngine;

namespace Rust.Ai.Gen2.Nav;

public static class BakeStats
{
	public enum Stage
	{
		CollectTotal,
		CollectBounds,
		CollectOverlap,
		CollectColliders,
		CollectDoors,
		CollectDoorsQuery,
		CollectDoorsVolumes,
		WorkerTotal,
		WorkerTerrain,
		WorkerSources,
		WorkerYExtent,
		WorkerHeightField,
		WorkerCompact,
		WorkerPolymesh,
		WorkerDetail,
		WorkerNavData,
		MainAddTile,
		WorkerIdle,
		Count
	}

	public struct TileTiming
	{
		public long terrain;

		public long sources;

		public long yExtent;

		public long heightField;

		public long compact;

		public long polymesh;

		public long detail;

		public long navData;

		public int terrainTris;

		public int totalTris;

		public int sourceCount;

		public bool hiRes;

		public long WorkerSum => terrain + sources + yExtent + heightField + compact + polymesh + detail + navData;
	}

	private struct SlowTile
	{
		public int tx;

		public int ty;

		public TileTiming timing;
	}

	private static readonly string[] StageNames = new string[18]
	{
		"collect total (main)", "  bounds", "  physics overlap", "  collider processing", "  door volumes", "    door registry query", "    door hull copy", "worker total", "  terrain extract", "  source copy+transform",
		"  y extent", "  heightfield (native)", "  compact+erode+regions (native)", "  polymesh (native)", "  detail mesh (native)", "  navdata (native)", "add tile (main)", "worker idle (blocked on queue)"
	};

	private const int SlowTileCap = 16;

	private static readonly long[] stageTicks = new long[18];

	private static readonly long[] stageCounts = new long[18];

	private static readonly long[] resultCounts = new long[16];

	private static long supersededResults;

	private static long tilesQueued;

	private static long hiResTiles;

	private static long loResTiles;

	private static long ticksWithCollectWork;

	private static long ticksBudgetLimited;

	private static long maxCollectQueue;

	private static long maxWorkerQueue;

	private static long maxResultBag;

	private static double resetRealtime;

	private static long resetTimestamp;

	private static int workerCount;

	private static readonly object slowLock = new object();

	private static readonly SlowTile[] slowTiles = new SlowTile[16];

	private static int slowTileNum;

	private static bool Enabled => RustNav.bakeStatsEnabled;

	public static double TicksToMs(long ticks)
	{
		return (double)ticks * 1000.0 / (double)Stopwatch.Frequency;
	}

	public static long Timestamp()
	{
		return Stopwatch.GetTimestamp();
	}

	public static void Reset()
	{
		for (int i = 0; i < stageTicks.Length; i++)
		{
			stageTicks[i] = 0L;
			stageCounts[i] = 0L;
		}
		for (int j = 0; j < resultCounts.Length; j++)
		{
			resultCounts[j] = 0L;
		}
		supersededResults = 0L;
		tilesQueued = 0L;
		hiResTiles = 0L;
		loResTiles = 0L;
		ticksWithCollectWork = 0L;
		ticksBudgetLimited = 0L;
		maxCollectQueue = 0L;
		maxWorkerQueue = 0L;
		maxResultBag = 0L;
		lock (slowLock)
		{
			slowTileNum = 0;
		}
		resetRealtime = UnityEngine.Time.realtimeSinceStartupAsDouble;
		Interlocked.Exchange(ref resetTimestamp, Timestamp());
		workerCount = Mathf.Clamp(RustNav.numThreads, 1, SystemInfo.processorCount - 1);
	}

	public static double StageMs(Stage stage)
	{
		return TicksToMs(Interlocked.Read(ref stageTicks[(int)stage]));
	}

	public static long StageCount(Stage stage)
	{
		return Interlocked.Read(ref stageCounts[(int)stage]);
	}

	public static void AddStage(Stage stage, long ticks)
	{
		if (Enabled && ticks > 0)
		{
			Interlocked.Add(ref stageTicks[(int)stage], ticks);
			Interlocked.Increment(ref stageCounts[(int)stage]);
		}
	}

	public static void OnTileQueued()
	{
		if (Enabled)
		{
			Interlocked.Increment(ref tilesQueued);
		}
	}

	public static void AddWorkerIdle(long waitStartTs, long waitEndTs)
	{
		if (Enabled)
		{
			long num = Interlocked.Read(ref resetTimestamp);
			if (waitStartTs < num)
			{
				waitStartTs = num;
			}
			AddStage(Stage.WorkerIdle, waitEndTs - waitStartTs);
		}
	}

	public static void OnResult(int resultCode, bool superseded)
	{
		if (Enabled)
		{
			if (superseded)
			{
				Interlocked.Increment(ref supersededResults);
			}
			else if (resultCode >= 0 && resultCode < resultCounts.Length)
			{
				Interlocked.Increment(ref resultCounts[resultCode]);
			}
		}
	}

	public static void OnTileCollected(bool hiRes)
	{
		if (Enabled)
		{
			if (hiRes)
			{
				Interlocked.Increment(ref hiResTiles);
			}
			else
			{
				Interlocked.Increment(ref loResTiles);
			}
		}
	}

	public static void OnTileBuilt(int tx, int ty, in TileTiming timing)
	{
		if (!Enabled || (timing.WorkerSum == 0L && timing.totalTris == 0))
		{
			return;
		}
		AddStage(Stage.WorkerTerrain, timing.terrain);
		AddStage(Stage.WorkerSources, timing.sources);
		AddStage(Stage.WorkerYExtent, timing.yExtent);
		AddStage(Stage.WorkerHeightField, timing.heightField);
		AddStage(Stage.WorkerCompact, timing.compact);
		AddStage(Stage.WorkerPolymesh, timing.polymesh);
		AddStage(Stage.WorkerDetail, timing.detail);
		AddStage(Stage.WorkerNavData, timing.navData);
		long workerSum = timing.WorkerSum;
		lock (slowLock)
		{
			if (slowTileNum < 16)
			{
				slowTiles[slowTileNum].tx = tx;
				slowTiles[slowTileNum].ty = ty;
				slowTiles[slowTileNum].timing = timing;
				slowTileNum++;
				return;
			}
			int num = 0;
			long num2 = long.MaxValue;
			for (int i = 0; i < 16; i++)
			{
				long workerSum2 = slowTiles[i].timing.WorkerSum;
				if (workerSum2 < num2)
				{
					num2 = workerSum2;
					num = i;
				}
			}
			if (workerSum > num2)
			{
				slowTiles[num].tx = tx;
				slowTiles[num].ty = ty;
				slowTiles[num].timing = timing;
			}
		}
	}

	public static void OnMainThreadTick(int collectQueueDepth, int workerQueueDepth, int resultBagDepth, bool hadCollectWork, bool budgetLimited)
	{
		if (Enabled)
		{
			if (hadCollectWork)
			{
				ticksWithCollectWork++;
			}
			if (budgetLimited)
			{
				ticksBudgetLimited++;
			}
			if (collectQueueDepth > maxCollectQueue)
			{
				maxCollectQueue = collectQueueDepth;
			}
			if (workerQueueDepth > maxWorkerQueue)
			{
				maxWorkerQueue = workerQueueDepth;
			}
			if (resultBagDepth > maxResultBag)
			{
				maxResultBag = resultBagDepth;
			}
		}
	}

	public static string BuildReport()
	{
		StringBuilder stringBuilder = new StringBuilder(4096);
		double num = UnityEngine.Time.realtimeSinceStartupAsDouble - resetRealtime;
		stringBuilder.AppendLine($"=== RustNav bake stats ({num:F1}s since reset, {workerCount} workers) ===");
		stringBuilder.AppendLine($"tiles queued {tilesQueued}, collected {stageCounts[0]}, built results {stageCounts[16]}, superseded {supersededResults}");
		stringBuilder.AppendLine($"hi-res {hiResTiles} / lo-res {loResTiles}");
		stringBuilder.AppendLine("--- stages (total across tiles; worker stages overlap in wall time across threads) ---");
		for (int i = 0; i < 18; i++)
		{
			long num2 = stageCounts[i];
			if (num2 != 0L || stageTicks[i] != 0L)
			{
				double num3 = TicksToMs(stageTicks[i]);
				double num4 = ((num2 > 0) ? (num3 / (double)num2) : 0.0);
				stringBuilder.AppendLine($"{StageNames[i],-34} {num3 / 1000.0,9:F2}s  n={num2,-8} avg={num4,8:F3}ms");
			}
		}
		stringBuilder.AppendLine("--- results ---");
		string[] names = Enum.GetNames(typeof(BackgroundTileBuilder.TileBuildResultCode));
		for (int j = 0; j < names.Length && j < resultCounts.Length; j++)
		{
			if (resultCounts[j] != 0L)
			{
				stringBuilder.AppendLine($"{names[j],-30} {resultCounts[j]}");
			}
		}
		stringBuilder.AppendLine("--- main-thread throttle ---");
		stringBuilder.AppendLine($"ticks with collect work {ticksWithCollectWork}, budget-limited {ticksBudgetLimited} ({((ticksWithCollectWork > 0) ? (100.0 * (double)ticksBudgetLimited / (double)ticksWithCollectWork) : 0.0):F0}%)");
		stringBuilder.AppendLine($"max queue depths: collect {maxCollectQueue}, worker {maxWorkerQueue}, results {maxResultBag}");
		AppendSlowTiles(stringBuilder);
		return stringBuilder.ToString();
	}

	private static void AppendSlowTiles(StringBuilder sb)
	{
		lock (slowLock)
		{
			if (slowTileNum == 0)
			{
				return;
			}
			sb.AppendLine($"--- slowest {slowTileNum} tiles (worker ms: terrain/sources/yext/hf/compact/poly/detail/navdata) ---");
			for (int i = 0; i < slowTileNum - 1; i++)
			{
				int num = i;
				for (int j = i + 1; j < slowTileNum; j++)
				{
					if (slowTiles[j].timing.WorkerSum > slowTiles[num].timing.WorkerSum)
					{
						num = j;
					}
				}
				if (num != i)
				{
					ref SlowTile reference = ref slowTiles[i];
					ref SlowTile reference2 = ref slowTiles[num];
					SlowTile slowTile = slowTiles[num];
					SlowTile slowTile2 = slowTiles[i];
					reference = slowTile;
					reference2 = slowTile2;
				}
			}
			for (int k = 0; k < slowTileNum; k++)
			{
				ref SlowTile reference3 = ref slowTiles[k];
				ref TileTiming timing = ref reference3.timing;
				sb.AppendLine(string.Format("tile {0},{1} {2} {3,8:F1}ms  ", reference3.tx, reference3.ty, timing.hiRes ? "hi" : "lo", TicksToMs(timing.WorkerSum)) + $"{TicksToMs(timing.terrain):F1}/{TicksToMs(timing.sources):F1}/{TicksToMs(timing.yExtent):F1}/{TicksToMs(timing.heightField):F1}/" + $"{TicksToMs(timing.compact):F1}/{TicksToMs(timing.polymesh):F1}/{TicksToMs(timing.detail):F1}/{TicksToMs(timing.navData):F1}  " + $"tris={timing.totalTris} (terrain {timing.terrainTris}) sources={timing.sourceCount}");
			}
		}
	}
}

