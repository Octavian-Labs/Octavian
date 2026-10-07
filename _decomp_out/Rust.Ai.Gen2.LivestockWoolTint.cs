using UnityEngine;

namespace Rust.Ai.Gen2;

public class LivestockWoolTint : MonoBehaviour, IClientComponent, IEffect
{
	[Tooltip("The emitters that throw wool. Each one's Start Color is multiplied by the animal's wool tint every time the effect plays.")]
	public ParticleSystem[] Emitters;

	[Tooltip("The shade of the animal's wool range the emitters take, matched by name the way a coat group is.")]
	public string Shade = "Fleece LOD";
}

