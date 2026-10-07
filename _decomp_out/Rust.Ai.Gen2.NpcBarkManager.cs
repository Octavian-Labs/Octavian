using ConVar;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class NpcBarkManager : SingletonComponent<NpcBarkManager>, IServerComponent
{
	public NPCVoicelinesDatabase voicelinesDatabase;

	private const float minTimeBetweenStarterVoicelines = 5f;

	private const float minTimeBetweenExactSameVoiceline = 300f;

	private const float minTimeBetweenStartersOfSameCategory = 60f;

	private SparseGrid<(int, double)> voicelineHistory = new SparseGrid<(int, double)>();

	public bool CanPlay(BaseEntity source, NPCVoiceline voiceline)
	{
		using (TimeWarning.New("NpcBarkManager.CanPlay"))
		{
			if (source == null || voiceline.category == ENPCVoicelineCategory.None)
			{
				if (AI.logIssues)
				{
					Debug.LogError($"NpcBarkManager: CanPlay called with null source or invalid voiceline. index: {voiceline.index}");
				}
				return false;
			}
			double timeAsDouble = UnityEngine.Time.timeAsDouble;
			using PooledList<(int, double)> pooledList = Facepunch.Pool.Get<PooledList<(int, double)>>();
			voicelineHistory.GetNeighboors(source.transform.position, pooledList);
			foreach (var (num, num2) in pooledList)
			{
				if (!voicelinesDatabase.FindVoiceline(num, out var voiceline2))
				{
					if (AI.logIssues)
					{
						Debug.LogError($"NpcBarkManager: CanPlay - voiceline {num} present in history not found in db.");
					}
					continue;
				}
				float num3 = (float)(timeAsDouble - num2);
				if (voiceline.importance != ENpcVoicelineImportance.Conversation && num3 < 5f)
				{
					return false;
				}
				if (voiceline2.category == voiceline.category && voiceline.importance == ENpcVoicelineImportance.Flavour && num3 < 60f)
				{
					return false;
				}
				if (voiceline2.index != voiceline.index || !(num3 < 300f))
				{
					continue;
				}
				return false;
			}
			return true;
		}
	}

	public void OnPlay(BaseEntity source, NPCVoiceline voiceline)
	{
		using (TimeWarning.New("NpcBarkManager.OnPlay"))
		{
			double timeAsDouble = UnityEngine.Time.timeAsDouble;
			voicelineHistory.Add(source.transform.position, (voiceline.index, timeAsDouble));
		}
	}
}

