using UnityEngine;

namespace Rust.Ai.Gen2;

public class RootMotionTester : MonoBehaviour
{
	public RootMotionData anim;

	public Transform target;

	public float timeStep = 0.1f;

	public float trackingSpeed = 90f;

	public float trackingDuration = 1f;

	public float rotArrowLength = 0.2f;

	public float targetVelocityYaw = 45f;

	public float targetVelocityMagnitude = 5f;

	public int trackingStepIndex;

	public float parentFrontOffset;
}

