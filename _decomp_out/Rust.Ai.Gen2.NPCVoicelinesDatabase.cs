using System.Collections.Generic;
using ConVar;
using UnityEngine;

namespace Rust.Ai.Gen2;

[CreateAssetMenu(menuName = "Rust/AI/NPC voiceline db", fileName = "NPCVoicelineDB", order = 0)]
public class NPCVoicelinesDatabase : BaseScriptableObject
{
	public List<NPCVoiceline> voicelines = new List<NPCVoiceline>();

	private Dictionary<ENPCVoicelineCategory, List<NPCVoiceline>> categoryToVoiceline;

	public bool FindVoiceline(int index, out NPCVoiceline voiceline)
	{
		using (TimeWarning.New("NPCVoicelinesDatabase.FindVoiceline"))
		{
			if (index < 0 || index >= voicelines.Count)
			{
				voiceline = default(NPCVoiceline);
				if (AI.logIssues)
				{
					Debug.LogWarning($"NPCVoicelinesDatabase.FindAudioClip - index out of range: {index} / {voicelines.Count}");
				}
				return false;
			}
			voiceline = voicelines[index];
			return true;
		}
	}

	public bool GetVoicelinesByCategory(ENPCVoicelineCategory category, out List<NPCVoiceline> voicelinesInCategory)
	{
		using (TimeWarning.New("NPCVoicelinesDatabase.GetVoicelinesByCategory"))
		{
			if (categoryToVoiceline == null)
			{
				categoryToVoiceline = new Dictionary<ENPCVoicelineCategory, List<NPCVoiceline>>();
				foreach (NPCVoiceline voiceline in voicelines)
				{
					if (!categoryToVoiceline.TryGetValue(voiceline.category, out var value))
					{
						value = new List<NPCVoiceline>();
						categoryToVoiceline[voiceline.category] = value;
					}
					value.Add(voiceline);
				}
			}
			return categoryToVoiceline.TryGetValue(category, out voicelinesInCategory);
		}
	}
}

