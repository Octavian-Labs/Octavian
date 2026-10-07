using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class BoxCoverGroup : CoverGroup
{
	[SerializeField]
	private Vector3 size = new Vector3(5f, 1.5f, 1f);

	private OBB obb;

	private static readonly (int x, int z)[] boxCorners = new(int, int)[4]
	{
		(1, 1),
		(1, -1),
		(-1, -1),
		(-1, 1)
	};

	public override void GenerateCovers(GameObject gameObject)
	{
		covers.Clear();
		BaseEntity component = gameObject.GetComponent<BaseEntity>();
		MeshRenderer componentInChildren = gameObject.GetComponentInChildren<MeshRenderer>();
		if (component != null)
		{
			Bounds bounds = component.bounds;
			obb = new OBB(gameObject.transform.position + bounds.center, bounds.size, gameObject.transform.rotation);
		}
		else if (componentInChildren != null)
		{
			obb = new OBB(gameObject.transform.position + componentInChildren.localBounds.center * componentInChildren.transform.lossyScale.x, componentInChildren.localBounds.size * componentInChildren.transform.lossyScale.x, gameObject.transform.rotation);
		}
		else
		{
			obb = new OBB(gameObject.transform.position + size.y * 0.5f * Vector3.up, size, gameObject.transform.rotation);
		}
		isTall = obb.extents.y * 2f >= 1.8f;
		for (int i = 0; i < boxCorners.Length; i++)
		{
			(int, int) tuple = boxCorners[i];
			(int, int) tuple2 = boxCorners[(i + 1) % boxCorners.Length];
			Vector3 point = obb.GetPoint(tuple.Item1, -1f, tuple.Item2);
			Vector3 point2 = obb.GetPoint(tuple2.Item1, -1f, tuple2.Item2);
			Vector3 normalized = (point2 - point).normalized;
			point += normalized * 0.875f;
			point2 -= normalized * 0.875f;
			int num = Mathf.FloorToInt(Vector3.Distance(point, point2) / 1f);
			for (int j = 0; j < num; j++)
			{
				Cover.Peeks peeks = Cover.Peeks.None;
				if (!isTall)
				{
					peeks |= Cover.Peeks.Up;
				}
				else
				{
					if (j == 0)
					{
						peeks |= Cover.Peeks.Right;
					}
					if (j == num - 1)
					{
						peeks |= Cover.Peeks.Left;
					}
				}
				if (peeks != 0)
				{
					Vector3 position = Vector3.Lerp(point, point2, (float)(j / (num - 1)));
					position += Vector3.Cross(normalized, Vector3.up).normalized * 0.5f;
					Cover item = new Cover(position, Mathf.Atan2(point2.x - point.x, point2.z - point.z) * 57.29578f + 90f, peeks);
					covers.Add(item);
				}
			}
		}
	}

	public override bool GetCovers(Transform transform, List<Cover> covers, Vector3 from)
	{
		covers.AddRange(base.covers);
		return base.covers.Count > 0;
	}
}

