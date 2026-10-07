using UnityEngine.AI;

namespace Rust.Ai.Gen2.Nav;

public struct NavHit
{
	public NavVector3 position;

	public NavVector3 normal;

	public float distance;

	public int mask;

	public bool hit;

	public static NavHit FromUnity(in NavMeshHit unityHitNS)
	{
		NavHit result = default(NavHit);
		result.position = new NavVector3(unityHitNS.position);
		result.normal = new NavVector3(unityHitNS.normal);
		result.distance = unityHitNS.distance;
		result.mask = unityHitNS.mask;
		result.hit = unityHitNS.hit;
		return result;
	}

	public NavMeshHit ToUnity()
	{
		NavMeshHit result = default(NavMeshHit);
		result.position = position.Value;
		result.normal = normal.Value;
		result.distance = distance;
		result.mask = mask;
		result.hit = hit;
		return result;
	}
}

