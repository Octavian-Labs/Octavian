using UnityEngine;

namespace Rust.Ai.Gen2;

public class LivestockTrophyAppearance : HuntingTrophyAppearance
{
	[Tooltip("The coat this head builds out of the genome, the same component the animal and its ragdoll carry.")]
	public LivestockPattern Pattern;
}

