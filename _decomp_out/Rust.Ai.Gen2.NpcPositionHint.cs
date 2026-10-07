using UnityEngine;

namespace Rust.Ai.Gen2;

public class NpcPositionHint : MonoBehaviour, IServerComponent
{
	private void OnDrawGizmosSelected()
	{
		if (!(base.transform.parent == null) && base.transform.parent.TryGetComponent<NpcLevelScript>(out var component))
		{
			component.OnDrawGizmosSelected();
		}
	}

	private void OnValidate()
	{
		if (!(base.transform.parent == null) && base.transform.parent.TryGetComponent<NpcLevelScript>(out var component) && !component.positionHints.Contains(this))
		{
			component.positionHints.Add(this);
		}
	}
}

