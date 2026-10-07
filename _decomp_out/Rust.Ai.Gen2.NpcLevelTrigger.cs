using System.Collections.Generic;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class NpcLevelTrigger : TriggerBase, IServerComponent
{
	private HashSet<BasePlayer> playersInside = new HashSet<BasePlayer>();

	internal override GameObject InterestedInObject(GameObject obj)
	{
		obj = base.InterestedInObject(obj);
		if (obj == null)
		{
			return null;
		}
		BaseEntity baseEntity = obj.ToBaseEntity();
		if (baseEntity == null)
		{
			return null;
		}
		if (!baseEntity.IsNonNpcPlayer())
		{
			return null;
		}
		if (baseEntity.isClient)
		{
			return null;
		}
		return baseEntity.gameObject;
	}

	internal override void OnEntityEnter(BaseEntity ent)
	{
		base.OnEntityEnter(ent);
		if (ent.ToNonNpcPlayer(out var player))
		{
			playersInside.Add(player);
		}
	}

	internal override void OnEntityLeave(BaseEntity ent)
	{
		base.OnEntityLeave(ent);
		if (ent.ToNonNpcPlayer(out var player))
		{
			playersInside.Remove(player);
		}
	}

	private void OnDrawGizmosSelected()
	{
		if (!(base.transform.parent == null) && base.transform.parent.TryGetComponent<NpcLevelScript>(out var component))
		{
			component.OnDrawGizmosSelected();
		}
	}

	private void OnValidate()
	{
		if (!(base.transform.parent == null) && base.transform.parent.TryGetComponent<NpcLevelScript>(out var component) && !component.linkedTriggers.Contains(this))
		{
			component.linkedTriggers.Add(this);
		}
	}
}

