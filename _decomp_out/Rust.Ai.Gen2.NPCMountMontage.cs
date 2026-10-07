using UnityEngine;

namespace Rust.Ai.Gen2;

internal static class NPCMountMontage
{
	private const float MinimumMotion = 0.01f;

	private const float MinimumRotation = 1f;

	public static RootMotionPlayer.Warp BuildWarp(RootMotionData data, MountMontageWindow window, Quaternion initialRotation, Vector3 from, Vector3 to, Quaternion toRotation, float playbackSpeed = 1f)
	{
		GetTravelWindow(data, window, playbackSpeed, out var start, out var end, out var lastSampled);
		Vector3 vector = new Vector3(Travel(data.xMotionCurve, start, end), Travel(data.yMotionCurve, start, end), Travel(data.zMotionCurve, start, end));
		Vector3 vector2 = new Vector3(Unwarped(data.xMotionCurve, start, end, lastSampled), Unwarped(data.yMotionCurve, start, end, lastSampled), Unwarped(data.zMotionCurve, start, end, lastSampled));
		Vector3 vector3 = Quaternion.Inverse(initialRotation) * (to - from) - vector2;
		Vector3 translationScale = new Vector3(Scale(vector3.x, vector.x), Scale(vector3.y, vector.y), Scale(vector3.z, vector.z));
		float num = Mathf.DeltaAngle(initialRotation.eulerAngles.y, toRotation.eulerAngles.y) - Unwarped(data.yRotationCurve, start, end, lastSampled);
		float num2 = Travel(data.yRotationCurve, start, end);
		float rotationScale = ((Mathf.Abs(num2) < 1f) ? 1f : (num / num2));
		return new RootMotionPlayer.Warp(start, end, translationScale, rotationScale);
	}

	private static void GetTravelWindow(RootMotionData data, MountMontageWindow window, float playbackSpeed, out float start, out float end, out float lastSampled)
	{
		if (!window.IsAuthored)
		{
			window = MountMontageWindow.Whole;
		}
		float length = data.inPlaceAnimation.length;
		lastSampled = Mathf.Max(0f, length - 0.25f * playbackSpeed);
		end = Mathf.Min(window.end * length, lastSampled);
		start = Mathf.Clamp(window.start * length, 0f, end);
	}

	private static float Travel(AnimationCurve curve, float start, float end)
	{
		if (curve == null || curve.length == 0)
		{
			return 0f;
		}
		return curve.Evaluate(end) - curve.Evaluate(start);
	}

	private static float Unwarped(AnimationCurve curve, float start, float end, float lastSampled)
	{
		return Travel(curve, 0f, start) + Travel(curve, end, lastSampled);
	}

	private static float Scale(float wanted, float total)
	{
		if (!(Mathf.Abs(total) < 0.01f))
		{
			return wanted / total;
		}
		return 1f;
	}
}

