using UnityEngine;

namespace Rust.Ai.Gen2;

public class LivestockSexParts : MonoBehaviour, IClientComponent
{
	[Tooltip("Renderers only a male shows, LODs included. A ram's horns.")]
	public Renderer[] MaleOnly;

	[Tooltip("Renderers only a female shows, LODs included.")]
	public Renderer[] FemaleOnly;
}

