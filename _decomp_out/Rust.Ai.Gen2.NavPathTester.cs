using UnityEngine;

namespace Rust.Ai.Gen2;

[RequireComponent(typeof(RustNavMeshAgent))]
public class NavPathTester : MonoBehaviour
{
	[SerializeField]
	private NavPathTestType testType;

	[SerializeField]
	private Transform target;

	[SerializeField]
	private float sampleRadius = 10f;

	[SerializeField]
	private LayerMask traceMask = 1218519041;
}

