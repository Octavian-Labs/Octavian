using UnityEngine;

namespace Rust.Ai.Gen2.Nav;

public struct NavGroundHit
{
	public NavVector3 point;

	public NavVector3 normal;

	public float distance;

	public Collider collider;

	public RaycastHit rawHitWS;

	public Transform transform
	{
		get
		{
			if (!(collider != null))
			{
				return null;
			}
			return collider.transform;
		}
	}
}

