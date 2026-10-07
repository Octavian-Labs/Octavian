using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class LivestockFleece : MonoBehaviour
{
	public enum Quadrant
	{
		FrontLeft,
		FrontRight,
		RearLeft,
		RearRight
	}

	public static readonly Vector3 QuadrantCentre = new Vector3(0f, 0f, 0.06f);

	public const float SeamWidth = 0.3f;

	public const float WaveLength = 0.2f;

	public const float WaveAmplitude = 0.13f;

	public const float SwellLength = 0.55f;

	public const float SwellAmplitude = 0.055f;

	private const float RightPhase = 0f;

	private const float FrontPhase = 1.5707963f;

	private const float MixU = 0.25f;

	private const float MixV = 1f;

	public const int AllQuadrants = 15;

	private const float Tau = MathF.PI * 2f;

	[Tooltip("Every renderer wearing the fleece, LODs included. Each carries the shaved shapes the importer bakes, so a shorn quarter stays bare at every distance.")]
	public SkinnedMeshRenderer[] Renderers;

	public static readonly string[] ShapeNames = new string[4] { "Shaved_FrontLeft", "Shaved_FrontRight", "Shaved_RearLeft", "Shaved_RearRight" };

	public static float QuadrantWeight(Vector3 localPosition, Quadrant quadrant)
	{
		Splits(localPosition, out var right, out var front);
		return Weight(quadrant, right, front);
	}

	private static float Weight(Quadrant quadrant, float right, float front)
	{
		return quadrant switch
		{
			Quadrant.FrontLeft => front * (1f - right), 
			Quadrant.FrontRight => front * right, 
			Quadrant.RearLeft => (1f - front) * (1f - right), 
			_ => (1f - front) * right, 
		};
	}

	private static void Splits(Vector3 localPosition, out float right, out float front)
	{
		float num = localPosition.x - QuadrantCentre.x - Wander(localPosition.y, localPosition.z, 0f);
		float num2 = localPosition.z - QuadrantCentre.z - Wander(num, localPosition.y, 1.5707963f);
		right = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(num / 0.3f + 0.5f));
		front = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(num2 / 0.3f + 0.5f));
	}

	private static float Wander(float u, float v, float phase)
	{
		float num = 0.25f * Mathf.Sin(MathF.PI * 2f * u / 0.2f + phase) + 1f * Mathf.Sin(MathF.PI * 2f * v / 0.2f + phase + 2.4f);
		float num2 = 0.25f * Mathf.Sin(MathF.PI * 2f * u / 0.55f + phase + 0.9f) + 1f * Mathf.Sin(MathF.PI * 2f * v / 0.55f + phase + 3.9f);
		return 0.065f * num + 0.0275f * num2;
	}

	public float WeightAt(Vector3 worldPoint, Quadrant quadrant)
	{
		return QuadrantWeight(base.transform.InverseTransformPoint(worldPoint), quadrant);
	}

	public Quadrant QuadrantAt(Vector3 worldPoint)
	{
		Splits(base.transform.InverseTransformPoint(worldPoint), out var right, out var front);
		Quadrant result = Quadrant.FrontLeft;
		float num = -1f;
		for (int i = 0; i < 4; i++)
		{
			float num2 = Weight((Quadrant)i, right, front);
			if (num2 > num)
			{
				num = num2;
				result = (Quadrant)i;
			}
		}
		return result;
	}
}

