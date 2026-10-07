using System;
using System.Diagnostics;
using UnityEngine;

namespace Rust.Ai.Gen2;

public static class LivestockProfiler
{
	public enum Section
	{
		Needs,
		Fsm,
		Count
	}

	public readonly struct Scope : IDisposable
	{
		private readonly Section section;

		private readonly long started;

		public Scope(Section section)
		{
			this.section = section;
			started = (Enabled ? Stopwatch.GetTimestamp() : 0);
		}

		public void Dispose()
		{
			if (Enabled)
			{
				Record(section, Stopwatch.GetTimestamp() - started);
			}
		}
	}

	public static bool Enabled;

	private static readonly long[] elapsed = new long[2];

	private static readonly int[] calls = new int[2];

	private static int firstFrame = -1;

	private static int lastFrame = -1;

	public static int FramesMeasured
	{
		get
		{
			if (firstFrame >= 0)
			{
				return lastFrame - firstFrame + 1;
			}
			return 0;
		}
	}

	public static void Reset()
	{
		for (int i = 0; i < elapsed.Length; i++)
		{
			elapsed[i] = 0L;
			calls[i] = 0;
		}
		firstFrame = -1;
		lastFrame = -1;
	}

	public static Scope Sample(Section section)
	{
		return new Scope(section);
	}

	public static double MicrosecondsPerFrame(Section section)
	{
		int framesMeasured = FramesMeasured;
		if (framesMeasured <= 0)
		{
			return 0.0;
		}
		return TotalMicroseconds(section) / (double)framesMeasured;
	}

	public static double MicrosecondsPerCall(Section section)
	{
		int num = calls[(int)section];
		if (num <= 0)
		{
			return 0.0;
		}
		return TotalMicroseconds(section) / (double)num;
	}

	public static double TotalMicroseconds(Section section)
	{
		return (double)elapsed[(int)section] * 1000000.0 / (double)Stopwatch.Frequency;
	}

	public static int CallsMeasured(Section section)
	{
		return calls[(int)section];
	}

	private static void Record(Section section, long ticks)
	{
		elapsed[(int)section] += ticks;
		calls[(int)section]++;
		int frameCount = Time.frameCount;
		if (firstFrame < 0)
		{
			firstFrame = frameCount;
		}
		lastFrame = frameCount;
	}
}

