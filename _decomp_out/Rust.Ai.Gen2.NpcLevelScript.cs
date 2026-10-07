using System.Collections.Generic;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class NpcLevelScript : TriggerBase, IServerComponent
{
	public List<NpcLevelTrigger> linkedTriggers = new List<NpcLevelTrigger>();

	public List<NpcPositionHint> positionHints = new List<NpcPositionHint>();

	public void OnDrawGizmosSelected()
	{
		foreach (NpcLevelTrigger linkedTrigger in linkedTriggers)
		{
			if (linkedTrigger.isActiveAndEnabled && linkedTrigger.TryGetComponent<BoxCollider>(out var component))
			{
				Gizmos.color = Color.cyan;
				Matrix4x4 matrix = Gizmos.matrix;
				Gizmos.matrix = Matrix4x4.TRS(component.transform.position, component.transform.rotation, component.transform.lossyScale);
				Gizmos.DrawWireCube(component.center, component.size);
				Gizmos.matrix = matrix;
			}
		}
		foreach (NpcPositionHint positionHint in positionHints)
		{
			if (positionHint == null || !positionHint.isActiveAndEnabled || positionHint is NpcGrenadePositionHint)
			{
				continue;
			}
			Vector3? vector = null;
			float num = float.PositiveInfinity;
			foreach (NpcLevelTrigger linkedTrigger2 in linkedTriggers)
			{
				if (!(linkedTrigger2 == null) && linkedTrigger2.isActiveAndEnabled && linkedTrigger2.TryGetComponent<Collider>(out var component2))
				{
					Vector3 vector2 = component2.ClosestPoint(positionHint.transform.position);
					float sqrMagnitude = (vector2 - positionHint.transform.position).sqrMagnitude;
					if (sqrMagnitude < num)
					{
						num = sqrMagnitude;
						vector = vector2;
					}
				}
			}
			Gizmos.color = Color.cyan;
			Gizmos.DrawWireSphere(positionHint.transform.position, 0.2f);
			Gizmos.DrawLine(positionHint.transform.position, positionHint.transform.position + 1.8f * Vector3.up);
			if (vector.HasValue)
			{
				Gizmos.DrawLine(positionHint.transform.position + 1.8f * Vector3.up, vector.Value);
			}
		}
		foreach (NpcPositionHint positionHint2 in positionHints)
		{
			if (positionHint2 == null || !positionHint2.isActiveAndEnabled || !(positionHint2 is NpcGrenadePositionHint npcGrenadePositionHint))
			{
				continue;
			}
			Gizmos.color = Color.red;
			Gizmos.DrawWireSphere(positionHint2.transform.position, 0.2f);
			Gizmos.DrawLine(positionHint2.transform.position, positionHint2.transform.position + 1.8f * Vector3.up);
			if (!(npcGrenadePositionHint.landingPoint == null))
			{
				Vector3 vector3 = npcGrenadePositionHint.transform.position + 1.8f * Vector3.up;
				Vector3 position = npcGrenadePositionHint.landingPoint.position;
				Vector3 vector4 = ((vector3 + position) * 0.5f).WithY(Mathf.Max(vector3.y, position.y) + npcGrenadePositionHint.apexHeight);
				int num2 = 20;
				Vector3 from = vector3;
				for (int i = 1; i <= num2; i++)
				{
					float t = (float)i / (float)num2;
					Vector3 vector5 = Vector3.Lerp(Vector3.Lerp(vector3, vector4, t), Vector3.Lerp(vector4, position, t), t);
					Gizmos.DrawLine(from, vector5);
					from = vector5;
				}
			}
		}
	}
}

