using System.Collections.Generic;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class NpcSleepingComponent : EntityComponent<BaseEntity>, IAISleepable, IServerComponent
{
	public List<Component> componentsToSleep = new List<Component>();

	private bool sleeping;

	private AIInformationZone infoZone;

	public override void InitShared()
	{
		base.InitShared();
		infoZone = AIInformationZone.GetForPoint(base.transform.position, fallBackToNearest: false);
		if (infoZone != null)
		{
			infoZone.RegisterSleepableEntity(this);
		}
	}

	public override void DestroyShared()
	{
		if (infoZone == null)
		{
			infoZone = AIInformationZone.GetForPoint(base.transform.position);
		}
		if (infoZone != null)
		{
			infoZone.UnregisterSleepableEntity(this);
		}
		base.DestroyShared();
	}

	bool IAISleepable.AllowedToSleep()
	{
		return true;
	}

	void IAISleepable.SleepAI()
	{
		SetSleeping(newSleeping: true);
	}

	void IAISleepable.WakeAI()
	{
		SetSleeping(newSleeping: false);
	}

	private void SetSleeping(bool newSleeping)
	{
		if (sleeping == newSleeping)
		{
			return;
		}
		sleeping = newSleeping;
		foreach (Component item in componentsToSleep)
		{
			if (!(item == null))
			{
				if (item is FSMComponent fSMComponent)
				{
					fSMComponent.SetFsmActive(!newSleeping);
				}
				else if (item is MonoBehaviour monoBehaviour)
				{
					monoBehaviour.enabled = !newSleeping;
				}
			}
		}
		base.baseEntity.limitNetworking = newSleeping;
	}
}

