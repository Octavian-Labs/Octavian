namespace Rust.Ai.Gen2;

public class Trans_TargetIsUndergeared : FSMTransitionBase
{
	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_TargetIsUndergeared"))
		{
			if (!base.Senses.FindTarget(out var target))
			{
				return false;
			}
			if (!target.ToNonNpcPlayer(out var player))
			{
				return false;
			}
			foreach (Item item in player.inventory.containerBelt.itemList)
			{
				if (IsItemHighLevelWeapon(item))
				{
					return false;
				}
			}
			foreach (Item item2 in player.inventory.containerMain.itemList)
			{
				if (IsItemHighLevelWeapon(item2))
				{
					return false;
				}
			}
			return true;
		}
	}

	private bool IsItemHighLevelWeapon(Item item)
	{
		ItemMod[] itemMods = item.info.itemMods;
		for (int i = 0; i < itemMods.Length; i++)
		{
			if (itemMods[i].Is<ItemModEntity>(out var castedUnityObject) && castedUnityObject.entityPrefab.GetEntity().Is<BaseProjectile>(out var castedUnityObject2) && ((castedUnityObject2.primaryMagazine.definition.ammoTypes & AmmoTypes.PISTOL_9MM) == AmmoTypes.PISTOL_9MM || (castedUnityObject2.primaryMagazine.definition.ammoTypes & AmmoTypes.SHOTGUN_12GUAGE) == AmmoTypes.SHOTGUN_12GUAGE || (castedUnityObject2.primaryMagazine.definition.ammoTypes & AmmoTypes.RIFLE_556MM) == AmmoTypes.RIFLE_556MM || (castedUnityObject2.primaryMagazine.definition.ammoTypes & AmmoTypes.ROCKET) == AmmoTypes.ROCKET || (castedUnityObject2.primaryMagazine.definition.ammoTypes & AmmoTypes.MISSILE_SEEKING) == AmmoTypes.MISSILE_SEEKING))
			{
				return true;
			}
		}
		return false;
	}
}

