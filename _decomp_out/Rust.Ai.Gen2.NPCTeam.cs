using System.Collections.Generic;
using UnityEngine;

namespace Rust.Ai.Gen2;

[CreateAssetMenu(menuName = "Rust/AI/NPC team", fileName = "NPCTeam_new", order = 0)]
public class NPCTeam : BaseScriptableObject
{
	[Tooltip("What members of this team do about a player they sense.")]
	public NPCStance players;

	[Tooltip("Teams this team goes for. How is up to each animal's brain, a wolf hunts where a bull only answers a grudge.")]
	public List<NPCTeam> attacks = new List<NPCTeam>();

	[Tooltip("Teams this team runs from. A team on both lists is feared.")]
	public List<NPCTeam> fears = new List<NPCTeam>();

	[Tooltip("What members of this team do about something that hurt them and that the lists above say to ignore. A teammate is never answered.")]
	public NPCStance attackers = NPCStance.Attack;

	public NPCStance StanceToward(NPCTeam other)
	{
		if (other == null || other == this)
		{
			return NPCStance.Ignore;
		}
		if (fears.Contains(other))
		{
			return NPCStance.Fear;
		}
		if (attacks.Contains(other))
		{
			return NPCStance.Attack;
		}
		return NPCStance.Ignore;
	}
}

