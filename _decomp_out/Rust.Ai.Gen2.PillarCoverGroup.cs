using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class PillarCoverGroup : CoverGroup
{
	[SerializeField]
	private float radius = 1f;

	[SerializeField]
	private float radiusOffset;

	private Vector3 position;

	private Quaternion rotation;

	public override void GenerateCovers(GameObject gameObject)
	{
		position = gameObject.transform.position;
		rotation = gameObject.transform.rotation;
		BaseEntity component = gameObject.GetComponent<BaseEntity>();
		MeshRenderer componentInChildren = gameObject.GetComponentInChildren<MeshRenderer>();
		if (component != null)
		{
			Bounds bounds = component.bounds;
			radius = Mathf.Max(bounds.extents.x, bounds.extents.z) - radiusOffset;
			isTall = bounds.size.y >= 1.8f;
		}
		else if (componentInChildren != null)
		{
			radius = componentInChildren.localBounds.extents.magnitude * componentInChildren.transform.lossyScale.x - radiusOffset;
			isTall = componentInChildren.localBounds.size.y * componentInChildren.transform.lossyScale.y >= 1.8f;
		}
	}

	public override bool GetCovers(Transform transform, List<Cover> covers, Vector3 from)
	{
		Vector3 vector = from - position;
		Cover.Peeks peeks = (isTall ? Cover.Peeks.Sides : Cover.Peeks.Up);
		if (rotation == Quaternion.identity)
		{
			float yaw = Mathf.Atan2(vector.x, vector.z) * 57.29578f;
			Vector3 vector2 = position + -vector.NormalizeXZ() * (radius + 0.5f);
			covers.Add(new Cover(vector2, yaw, peeks));
		}
		else
		{
			Vector3 vector3 = rotation * Vector3.forward;
			Vector3 vector4 = rotation * Vector3.right;
			Vector3 vector5 = new Vector3(Vector3.Dot(vector, vector4), 0f, Vector3.Dot(vector, vector3));
			float yaw2 = Mathf.Atan2(vector.x, vector.z) * 57.29578f;
			Vector3 vector6 = -vector5.normalized * (radius + 0.5f);
			Vector3 vector7 = position + vector4 * vector6.x + vector3 * vector6.z;
			covers.Add(new Cover(vector7, yaw2, peeks));
		}
		return true;
	}
}

