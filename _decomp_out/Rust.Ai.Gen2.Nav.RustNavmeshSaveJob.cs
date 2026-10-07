using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace Rust.Ai.Gen2.Nav;

public class RustNavmeshSaveJob
{
	private volatile bool finished;

	private Thread thread;

	private NavmeshSaveThreadResult result;

	private string finalPath;

	private string tempPath;

	private IntPtr navHandle;

	private IntPtr payload;

	private int payloadSize;

	private int flags;

	private int threads;

	private int debugDelayMs;

	private IntPtr dirtyCoords;

	private int dirtyCount;

	private string deltaPath;

	private long deltaExpectedLength;

	private NavMeshBuildParams buildParams;

	private Vector3 boundsMin;

	private Vector3 boundsMax;

	public bool TryBegin(string finalPath, string tempPath, IntPtr navHandle, in NavMeshBuildParams buildParams, Vector3 boundsMin, Vector3 boundsMax, IntPtr payload, int payloadSize, int flags, int threads, int debugDelayMs, IntPtr dirtyCoords, int dirtyCount, string deltaPath, long deltaExpectedLength)
	{
		if (thread != null)
		{
			return false;
		}
		this.dirtyCoords = dirtyCoords;
		this.dirtyCount = dirtyCount;
		this.deltaPath = deltaPath;
		this.deltaExpectedLength = deltaExpectedLength;
		this.finalPath = finalPath;
		this.tempPath = tempPath;
		this.navHandle = navHandle;
		this.buildParams = buildParams;
		this.boundsMin = boundsMin;
		this.boundsMax = boundsMax;
		this.payload = payload;
		this.payloadSize = payloadSize;
		this.flags = flags;
		this.threads = threads;
		this.debugDelayMs = debugDelayMs;
		result = default(NavmeshSaveThreadResult);
		finished = false;
		try
		{
			thread = new Thread(RunOnSaveThread)
			{
				IsBackground = true,
				Name = "RustNavSave"
			};
			thread.Start();
		}
		catch (Exception exception)
		{
			thread = null;
			this.payload = IntPtr.Zero;
			this.dirtyCoords = IntPtr.Zero;
			UnityEngine.Debug.LogException(exception);
			return false;
		}
		return true;
	}

	public bool TryReapIfFinished(out NavmeshSaveThreadResult result)
	{
		if (thread == null || !finished)
		{
			result = default(NavmeshSaveThreadResult);
			return false;
		}
		result = Join();
		return true;
	}

	public NavmeshSaveThreadResult Join()
	{
		thread.Join();
		thread = null;
		return result;
	}

	private void RunOnSaveThread()
	{
		long timestamp = Stopwatch.GetTimestamp();
		try
		{
			result.stats.threadId = Thread.CurrentThread.ManagedThreadId;
			if (debugDelayMs > 0)
			{
				Thread.Sleep(debugDelayMs);
			}
			if (deltaPath != null)
			{
				AppendWarning(RustNavmesh.DeleteStaleTempFile(tempPath));
				if (TryAppendDelta(timestamp))
				{
					return;
				}
			}
			bool flag = WriteFullFile();
			if (!flag)
			{
				string note;
				bool num = RustNavmesh.CreateMissingDirectory(tempPath, out note);
				AppendWarning(note);
				if (num)
				{
					flag = WriteFullFile();
				}
			}
			result.stats.threadMs = BakeStats.TicksToMs(Stopwatch.GetTimestamp() - timestamp);
			if (!flag)
			{
				result.stats.failure = "the write to " + tempPath + " failed";
				return;
			}
			long timestamp2 = Stopwatch.GetTimestamp();
			result.stats.succeeded = RustNavmesh.MoveSavedFileIntoPlace(tempPath, finalPath, out result.stats.replaceMode, out var note2);
			result.stats.replaceMs = BakeStats.TicksToMs(Stopwatch.GetTimestamp() - timestamp2);
			if (result.stats.succeeded)
			{
				result.stats.bytes = (result.stats.phasesValid ? result.stats.phases.fileBytes : RustNavmesh.FileLengthOrZero(finalPath));
				AppendWarning(note2);
			}
			else
			{
				result.stats.failure = note2;
			}
		}
		catch (Exception ex)
		{
			result.stats.succeeded = false;
			result.stats.failure = ex.ToString();
			UnityEngine.Debug.LogException(ex);
		}
		finally
		{
			if (dirtyCoords != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(dirtyCoords);
				dirtyCoords = IntPtr.Zero;
			}
			if (payload != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(payload);
				payload = IntPtr.Zero;
			}
			finished = true;
		}
	}

	private bool WriteFullFile()
	{
		return RecastWrapper.SaveNavMeshWithPhases(tempPath, navHandle, in buildParams, in boundsMin, in boundsMax, payload, payloadSize, flags, threads, out result.stats.phases, out result.stats.phasesValid);
	}

	private bool TryAppendDelta(long start)
	{
		if (!RecastWrapper.TryAppendNavMeshDelta(deltaPath, deltaExpectedLength, navHandle, dirtyCoords, dirtyCount, payload, payloadSize, flags, threads, out result.stats.phases))
		{
			if (RecastWrapper.HasAppendNavMeshDelta)
			{
				AppendWarning("RustNative refused to append to " + deltaPath + ", see its reason above, so the whole file is rewritten.");
			}
			return false;
		}
		result.stats.wroteDelta = true;
		result.stats.phasesValid = true;
		result.stats.threadMs = BakeStats.TicksToMs(Stopwatch.GetTimestamp() - start);
		result.stats.bytes = result.stats.phases.fileBytes;
		result.stats.succeeded = true;
		return true;
	}

	private void AppendWarning(string note)
	{
		if (!string.IsNullOrEmpty(note))
		{
			result.warning = ((result.warning == null) ? note : (result.warning + " " + note));
		}
	}
}

