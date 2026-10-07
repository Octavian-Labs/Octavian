using UnityEngine;

namespace Rust.Ai.Gen2;

public class NpcZoneComponent : EntityComponent<BaseEntity>, IServerComponent
{
	private bool hasAbandonnedZone;

	public NpcZone zone { get; private set; }

	public override void InitShared()
	{
		if (base.baseEntity.isServer)
		{
			zone = NpcZone.GetForPoint(base.baseEntity, base.baseEntity.CenterPoint());
			base.InitShared();
		}
	}

	public void AbandonZone()
	{
		hasAbandonnedZone = true;
	}

	public bool IsPointInsideZone(Vector3 point)
	{
		if (zone == null)
		{
			return true;
		}
		if (hasAbandonnedZone)
		{
			return true;
		}
		return zone.IsPointInside(base.baseEntity, point);
	}

	public bool IsInSameZone(BaseEntity other)
	{
		if (zone == null || other == null)
		{
			return false;
		}
		if (other.TryGetComponent<NpcZoneComponent>(out var component))
		{
			return zone == component.zone;
		}
		NpcZone forPoint = NpcZone.GetForPoint(other, other.CenterPoint());
		return zone == forPoint;
	}
}

