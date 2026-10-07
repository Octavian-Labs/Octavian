using System.Collections.Generic;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class NpcZone : MonoBehaviour, IServerComponent
{
	private static List<NpcZone> zones = new List<NpcZone>();

	public Bounds bounds = new Bounds(Vector3.zero, new Vector3(10f, 3.5f, 10f));

	public bool drawBounds = true;

	private void Awake()
	{
		zones.Add(this);
	}

	private void OnDestroy()
	{
		zones.Remove(this);
	}

	public bool IsPointInside(BaseEntity querier, Vector3 point)
	{
		using (TimeWarning.New("NpcZone.IsPointInside"))
		{
			return new OBB(base.transform.position, base.transform.lossyScale, base.transform.rotation, bounds).Contains(point);
		}
	}

	public static NpcZone GetForPoint(BaseEntity querier, Vector3 point, bool fallBackToNearest = false)
	{
		using (TimeWarning.New("NpcZone.GetForPoint"))
		{
			if (zones == null || zones.Count == 0)
			{
				return null;
			}
			foreach (NpcZone zone in zones)
			{
				if (!(zone == null) && zone.IsPointInside(querier, point))
				{
					return zone;
				}
			}
			if (!fallBackToNearest)
			{
				return null;
			}
			float num = float.PositiveInfinity;
			NpcZone result = zones[0];
			foreach (NpcZone zone2 in zones)
			{
				if (!(zone2 == null) && !(zone2.transform == null))
				{
					float num2 = Vector3.Distance(zone2.transform.position, point);
					if (num2 < num)
					{
						num = num2;
						result = zone2;
					}
				}
			}
			return result;
		}
	}
}

