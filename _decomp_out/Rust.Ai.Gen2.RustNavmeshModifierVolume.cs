using UnityEngine;

namespace Rust.Ai.Gen2;

public class RustNavmeshModifierVolume : MonoBehaviour, IServerComponent
{
	public static SparseGrid<RustNavmeshModifierVolume> AllModifierVolumes = new SparseGrid<RustNavmeshModifierVolume>();

	private static int liveCount;

	public static bool HasAny => liveCount > 0;

	private void Awake()
	{
		AllModifierVolumes.Add(base.transform.position, this);
		liveCount++;
	}

	private void OnDestroy()
	{
		AllModifierVolumes.Remove(base.transform.position, this);
		liveCount--;
	}
}

