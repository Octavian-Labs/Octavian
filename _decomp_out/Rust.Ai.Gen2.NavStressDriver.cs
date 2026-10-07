using ConVar;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class NavStressDriver : MonoBehaviour
{
	private void Update()
	{
		NavStress.DriverUpdate();
	}
}

