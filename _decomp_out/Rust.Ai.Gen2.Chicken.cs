using UnityEngine;

namespace Rust.Ai.Gen2;

[SoftRequireComponent(typeof(BasicAnimalFsm))]
public class Chicken : BaseNPC2
{
	public class EggDropWorkQueue : ObjectWorkQueue<Chicken>
	{
		protected override void RunJob(Chicken entity)
		{
			if (ShouldAdd(entity))
			{
				entity.CheckEggDrop();
			}
		}

		protected override bool ShouldAdd(Chicken entity)
		{
			if (base.ShouldAdd(entity))
			{
				return entity.IsValid();
			}
			return false;
		}
	}

	[ServerVar(Help = "Population active on the server, per square km", ShowInAdminUI = true)]
	public static float Population = 3f;

	public ItemDefinition EggDefinition;

	public float EggDropFrequency = 30f;

	public Vector3 EggDropLocalPos;

	public static EggDropWorkQueue EggWorkQueue = new EggDropWorkQueue();

	public override TraitFlag Traits => TraitFlag.Alive | TraitFlag.Animal | TraitFlag.Food | TraitFlag.Meat;

	public override void ServerInit()
	{
		base.ServerInit();
		if (EggDefinition != null)
		{
			InvokeRandomized(QueueEggDropCheck, EggDropFrequency, EggDropFrequency, EggDropFrequency * 0.5f);
		}
	}

	private void QueueEggDropCheck()
	{
		EggWorkQueue.Add(this);
	}

	private void CheckEggDrop()
	{
		if (base.isServer && Random.Range(0, 100) > 50 && base.transform != null && BaseNetworkable.HasCloseConnections(base.transform.position, 100f))
		{
			SpawnEgg();
		}
	}

	public void SpawnEgg()
	{
		if (EggDefinition != null)
		{
			ItemManager.Create(EggDefinition, 1, 0uL, isServerSide: true, 0uL).DropAtRest(base.transform.TransformPoint(EggDropLocalPos));
		}
	}

	public override string Categorize()
	{
		return "Chicken";
	}
}

