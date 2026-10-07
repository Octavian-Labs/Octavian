using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UtilityJobs;

namespace Rust.Ai.Gen2;

public class NavStressOcean
{
	private NativeArray<int> originalTopology;

	private bool scoped;

	public void SetFlatOcean()
	{
		if (!scoped)
		{
			scoped = true;
			originalTopology = new NativeArray<int>(TerrainMeta.TopologyMap.src, Allocator.Persistent);
			TerrainMeta.TopologyMap.Push();
			FillJob<int> fillJob = default(FillJob<int>);
			fillJob.Values = TerrainMeta.TopologyMap.dst;
			fillJob.Value = 128;
			FillJob<int> jobData = fillJob;
			IJobExtensions.RunByRef(ref jobData);
			TerrainMeta.TopologyMap.Pop();
			TerrainMeta.Texturing.Setup();
			ref TerrainTexturing.ShoreData mapByRef = ref TerrainMeta.Texturing.GetMapByRef(isDeepSea: false);
			mapByRef.DefaultVector = new Vector4(1f, 1f, 1f, 1f);
			mapByRef.FillWithDefault();
		}
	}

	public void Restore()
	{
		if (scoped)
		{
			scoped = false;
			TerrainMeta.TopologyMap.Push();
			TerrainMeta.TopologyMap.dst.CopyFrom(originalTopology);
			TerrainMeta.TopologyMap.Pop();
			TerrainMeta.Texturing.Setup();
			NativeArrayEx.SafeDispose(ref originalTopology);
		}
	}
}

