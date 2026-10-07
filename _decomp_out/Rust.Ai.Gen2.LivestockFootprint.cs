using UnityEngine;

namespace Rust.Ai.Gen2;

public readonly struct LivestockFootprint
{
	public readonly Vector3 Centre;

	public readonly Vector3 Right;

	public readonly Vector3 Forward;

	public readonly Vector2 Extents;

	public LivestockFootprint(Vector3 centre, Vector3 forward, Vector2 extents)
	{
		Forward = ((forward.WithY(0f).sqrMagnitude > 0.0001f) ? forward.WithY(0f).normalized : Vector3.forward);
		Right = new Vector3(Forward.z, 0f, 0f - Forward.x);
		Centre = centre.WithY(0f);
		Extents = extents;
	}

	public static LivestockFootprint Of(BaseEntity entity, Vector3 position, Vector3 forward)
	{
		Bounds bounds = entity.bounds;
		LivestockFootprint livestockFootprint = new LivestockFootprint(position, forward, Vector2.zero);
		return new LivestockFootprint(position + livestockFootprint.Right * bounds.center.x + livestockFootprint.Forward * bounds.center.z, forward, new Vector2(bounds.extents.x, bounds.extents.z));
	}

	public static LivestockFootprint Of(BaseEntity entity)
	{
		return Of(entity, entity.transform.position, entity.transform.forward);
	}

	public static float Penetration(in LivestockFootprint a, in LivestockFootprint b)
	{
		Vector3 offset = b.Centre - a.Centre;
		return Mathf.Min(Mathf.Min(Mathf.Min(Overlap(a.Right, offset, in a, in b), Overlap(a.Forward, offset, in a, in b)), Overlap(b.Right, offset, in a, in b)), Overlap(b.Forward, offset, in a, in b));
	}

	private static float Overlap(Vector3 axis, Vector3 offset, in LivestockFootprint a, in LivestockFootprint b)
	{
		return a.Reach(axis) + b.Reach(axis) - Mathf.Abs(Vector3.Dot(offset, axis));
	}

	private float Reach(Vector3 axis)
	{
		return Extents.x * Mathf.Abs(Vector3.Dot(Right, axis)) + Extents.y * Mathf.Abs(Vector3.Dot(Forward, axis));
	}
}

