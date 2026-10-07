using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[CreateAssetMenu(menuName = "Rust/AI/Livestock Species")]
public class LivestockSpecies : ScriptableObject
{
	[Header("Adult")]
	[Tooltip("The adult female. Set to the same prefab as Adult Male when one prefab covers both sexes.")]
	public GameObjectRef AdultFemale;

	[Tooltip("The adult male. Set to the same prefab as Adult Female when one prefab covers both sexes.")]
	public GameObjectRef AdultMale;

	[Header("Child")]
	[Tooltip("The newborn female. Set to the same prefab as Child Male unless the sexes need different models this young.")]
	public GameObjectRef ChildFemale;

	[Tooltip("The newborn male. Set to the same prefab as Child Female unless the sexes need different models this young.")]
	public GameObjectRef ChildMale;

	[Header("Value")]
	[Tooltip("What a perfectly kept adult of this species fetches at a livestock vendor, as a multiple of that vendor's reward amount. Cattle are the reference at 1.")]
	[Min(0f)]
	public float SaleValueScale = 1f;

	[Header("Status Panel")]
	[Tooltip("Shows this species' gene discs and inbred warning on the status panel whatever livestock.panelGenes says. A species left unticked follows the convar.")]
	public bool AlwaysShowGenes;

	[NonSerialized]
	private float childTimeToGrow = -1f;

	public float ChildTimeToGrow
	{
		get
		{
			if (childTimeToGrow < 0f)
			{
				childTimeToGrow = ResolveChildTimeToGrow();
			}
			return childTimeToGrow;
		}
	}

	public GameObjectRef AdultFor(bool male)
	{
		if (!male)
		{
			return AdultFemale;
		}
		return AdultMale;
	}

	public GameObjectRef ChildFor(bool male)
	{
		if (!male)
		{
			return ChildFemale;
		}
		return ChildMale;
	}

	public GameObjectRef For(LivestockAnimal.AgeStage age, bool male)
	{
		if (age != 0)
		{
			return AdultFor(male);
		}
		return ChildFor(male);
	}

	private float ResolveChildTimeToGrow()
	{
		GameObjectRef childFemale = ChildFemale;
		if (childFemale == null || !childFemale.isValid)
		{
			return 0f;
		}
		GameObject gameObject = GameManager.server.FindPrefab(childFemale.resourcePath);
		if (gameObject == null || !gameObject.TryGetComponent<LivestockAnimal>(out var component))
		{
			return 0f;
		}
		return component.TimeToGrow;
	}
}

