using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class LivestockSpecial
{
	[Tooltip("The animal's name, and its whole identity. Uniqueness is checked by this, so no two entries can share one.")]
	public string Name;

	[Tooltip("Which species this can land on. Empty means any livestock species, which is how one entry covers cows and sheep both.")]
	public LivestockSpecies[] Species;

	[Tooltip("Which sex this is. A gate rather than a stamp: changing sex means swapping the prefab, so an entry waits for an animal of the right sex instead.")]
	public LivestockAnimal.SexForm Sex;

	[Tooltip("The gene pairs this entry forces. Anything left out is still rolled, so no entry has to author a whole genome. Appearance rides on these too, so an entry that wants a colour forces the gene that makes it.")]
	public ForcedGene[] Genes;

	[Tooltip("Lets this entry force an allele below Ok. Off by default so a freshly added array element, which defaults to Bad, is caught rather than shipped.")]
	public bool AllowBadGenes;

	[Tooltip("How likely this one is relative to the others, once the rarity roll has passed at all.")]
	[Min(0f)]
	public float Weight = 1f;
}

