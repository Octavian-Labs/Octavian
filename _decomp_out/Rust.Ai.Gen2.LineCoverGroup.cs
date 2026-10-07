using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class LineCoverGroup : CoverGroup
{
	[SerializeField]
	private float length = 5f;

	[SerializeField]
	private float thickness = 0.5f;

	[SerializeField]
	private bool rotate90;

	public static Vector3 GetCircleInscribedInCorner(Vector3 corner, Vector3 dir1, Vector3 dir2, float radius)
	{
		dir1 = dir1.NormalizeXZ();
		dir2 = dir2.NormalizeXZ();
		float num = Vector3.Angle(dir1, dir2);
		float num2 = radius / Mathf.Sin(num * 0.5f * (MathF.PI / 180f));
		Vector3 normalized = (dir1 + dir2).normalized;
		return corner + normalized * num2;
	}

	public static (Cover, Cover) GetLineCovers(Vector3 a, Vector3 b, Vector3 from)
	{
		bool flag = Vector3.Cross(b - a, from - a).y < 0f;
		Vector3 circleInscribedInCorner = GetCircleInscribedInCorner(a, a - from, b - a, 0.5f);
		float y = Quaternion.LookRotation(from - a).eulerAngles.y;
		Cover item = new Cover(circleInscribedInCorner, y, flag ? Cover.Peeks.Left : Cover.Peeks.Right);
		Vector3 circleInscribedInCorner2 = GetCircleInscribedInCorner(b, b - from, a - b, 0.5f);
		float y2 = Quaternion.LookRotation(from - b).eulerAngles.y;
		Cover item2 = new Cover(circleInscribedInCorner2, y2, (!flag) ? Cover.Peeks.Left : Cover.Peeks.Right);
		return (item, item2);
	}

	public override bool GetCovers(Transform transform, List<Cover> covers, Vector3 threatLocation)
	{
		Vector3 onNormal = (rotate90 ? transform.right : transform.forward);
		Vector3 vector = (threatLocation - transform.position).NormalizeXZ();
		onNormal = Vector3.Project(vector, onNormal).normalized;
		Vector3 vector2 = new Vector3(onNormal.z, 0f, 0f - onNormal.x);
		if (Vector3.Dot(onNormal, vector) < 0.5f)
		{
			return false;
		}
		Vector3 vector3 = -onNormal * thickness;
		float num = length - 1f + 0.25f;
		if (num < 0f)
		{
			return false;
		}
		if (num <= 0.5f)
		{
			num = length;
		}
		Vector3 a = transform.position + vector3 + vector2 * num * 0.5f;
		Vector3 b = transform.position + vector3 - vector2 * num * 0.5f;
		(Cover, Cover) lineCovers = GetLineCovers(a, b, threatLocation);
		covers.Add(lineCovers.Item1);
		covers.Add(lineCovers.Item2);
		return true;
	}
}

