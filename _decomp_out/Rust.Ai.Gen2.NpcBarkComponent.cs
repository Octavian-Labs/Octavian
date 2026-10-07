using System;
using ConVar;
using Facepunch;
using Network;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class NpcBarkComponent : EntityComponent<BaseEntity>
{
	public NPCVoicelinesDatabase VoicelinesDatabase;

	private const float maxConversationDistance = 50f;

	private SenseComponent _senseComponent;

	private NpcZoneComponent _npcZoneComponent;

	private double? _lastTickTime;

	private float timeBeforeConversationResponse;

	private NPCVoiceline? conversationResponse;

	private SenseComponent SenseComponent => _senseComponent ?? (_senseComponent = base.baseEntity.GetComponent<SenseComponent>());

	private NpcZoneComponent NpcZoneComponent => _npcZoneComponent ?? (_npcZoneComponent = base.baseEntity.GetComponent<NpcZoneComponent>());

	private double LastTickTime
	{
		get
		{
			double valueOrDefault = _lastTickTime.GetValueOrDefault();
			if (!_lastTickTime.HasValue)
			{
				valueOrDefault = UnityEngine.Time.timeAsDouble;
				_lastTickTime = valueOrDefault;
				return valueOrDefault;
			}
			return valueOrDefault;
		}
		set
		{
			_lastTickTime = value;
		}
	}

	public override bool OnRpcMessage(BasePlayer player, uint rpc, Message msg)
	{
		using (TimeWarning.New("NpcBarkComponent.OnRpcMessage"))
		{
		}
		return base.OnRpcMessage(player, rpc, msg);
	}

	public bool PlayVoicelineFromCategory(ENPCVoicelineCategory category, BaseEntity targetAlly = null)
	{
		using (TimeWarning.New("NpcBarkComponent.PlayVoicelineFromCategory"))
		{
			if (!AI.npcBarksEnabled)
			{
				return false;
			}
			if (!VoicelinesDatabase.GetVoicelinesByCategory(category, out var voicelinesInCategory) || voicelinesInCategory.Count == 0)
			{
				return false;
			}
			using PooledList<int> pooledList = Facepunch.Pool.Get<PooledList<int>>();
			for (int i = 0; i < voicelinesInCategory.Count; i++)
			{
				pooledList.Add(i);
			}
			pooledList.Shuffle((uint)Environment.TickCount);
			NPCVoiceline? nPCVoiceline = null;
			int num = 0;
			foreach (int item in pooledList)
			{
				NPCVoiceline nPCVoiceline2 = voicelinesInCategory[item];
				if (nPCVoiceline2.importance == ENpcVoicelineImportance.Conversation)
				{
					num++;
				}
				else if (!nPCVoiceline.HasValue && SingletonComponent<NpcBarkManager>.Instance.CanPlay(base.baseEntity, nPCVoiceline2))
				{
					nPCVoiceline = nPCVoiceline2;
				}
			}
			if (!nPCVoiceline.HasValue)
			{
				return false;
			}
			if (num == 0 && !nPCVoiceline.Value.allowOkResponse)
			{
				return PlayVoiceline(nPCVoiceline.Value);
			}
			NPCVoiceline? nPCVoiceline3 = null;
			if (num > 0)
			{
				pooledList.Shuffle((uint)Environment.TickCount);
				foreach (int item2 in pooledList)
				{
					NPCVoiceline nPCVoiceline4 = voicelinesInCategory[item2];
					if (nPCVoiceline4.importance == ENpcVoicelineImportance.Conversation && SingletonComponent<NpcBarkManager>.Instance.CanPlay(base.baseEntity, nPCVoiceline4))
					{
						nPCVoiceline3 = nPCVoiceline4;
						break;
					}
				}
			}
			if (!nPCVoiceline3.HasValue)
			{
				if (!VoicelinesDatabase.GetVoicelinesByCategory(ENPCVoicelineCategory.Ok, out voicelinesInCategory))
				{
					return false;
				}
				pooledList.Clear();
				for (int j = 0; j < voicelinesInCategory.Count; j++)
				{
					pooledList.Add(j);
				}
				pooledList.Shuffle((uint)Environment.TickCount);
				foreach (int item3 in pooledList)
				{
					NPCVoiceline nPCVoiceline5 = voicelinesInCategory[item3];
					if (SingletonComponent<NpcBarkManager>.Instance.CanPlay(base.baseEntity, nPCVoiceline5))
					{
						nPCVoiceline3 = nPCVoiceline5;
						break;
					}
				}
			}
			if (!nPCVoiceline3.HasValue)
			{
				return false;
			}
			return PlayConversation(nPCVoiceline.Value, nPCVoiceline3.Value, targetAlly);
		}
	}

	private bool PlayConversation(NPCVoiceline starter, NPCVoiceline response, BaseEntity targetAlly = null)
	{
		if (!AI.npcBarksEnabled)
		{
			return false;
		}
		if (targetAlly == null && !NpcPushHelper.FindBestPartner(base.baseEntity.transform.position, SenseComponent, NpcZoneComponent, out targetAlly, 50f))
		{
			return false;
		}
		if (targetAlly == null)
		{
			Debug.LogError($"NpcBarkComponent.PlayConversation - {base.baseEntity} found invalid ally for conversation response: {starter.text} > {response.text}");
			return false;
		}
		if (!targetAlly.TryGetComponent<NpcBarkComponent>(out var component))
		{
			Debug.LogError($"NpcBarkComponent.PlayConversation - {base.baseEntity}'s ally {targetAlly} has no NpcBarkComponent to play conversation response after playing starter: {starter.text} > {response.text}");
			return false;
		}
		if (starter.duration == 0f)
		{
			Debug.LogError($"NpcBarkComponent.PlayConversation - {base.baseEntity} is trying to say something with a duration of 0: {starter.text}");
			return false;
		}
		if (!starter.otherNpcShouldSpeakFirst)
		{
			if (!PlayVoiceline(starter))
			{
				return false;
			}
			component.PlayConversationResponse(response, starter.duration + 0.25f);
			return true;
		}
		if (!component.PlayVoiceline(starter))
		{
			return false;
		}
		PlayConversationResponse(response, starter.duration + 0.25f);
		return true;
	}

	private bool PlayVoiceline(NPCVoiceline voiceline, bool setCooldown = true)
	{
		if (!SingletonComponent<NpcBarkManager>.Instance.CanPlay(base.baseEntity, voiceline))
		{
			return false;
		}
		base.baseEntity.ClientRPC(RpcTarget.NetworkGroup("CL_PlayBark"), voiceline.index);
		if (setCooldown)
		{
			SingletonComponent<NpcBarkManager>.Instance.OnPlay(base.baseEntity, voiceline);
		}
		return true;
	}

	private void PlayConversationResponse(NPCVoiceline voiceline, float delay)
	{
		conversationResponse = voiceline;
		timeBeforeConversationResponse = delay;
	}

	public void Tick()
	{
		using (TimeWarning.New("NpcBarkComponent.Tick"))
		{
			if (!AI.npcBarksEnabled)
			{
				return;
			}
			float num = (float)(UnityEngine.Time.timeAsDouble - LastTickTime);
			LastTickTime = UnityEngine.Time.timeAsDouble;
			if (conversationResponse.HasValue)
			{
				timeBeforeConversationResponse -= num;
				if (!(timeBeforeConversationResponse > 0f))
				{
					PlayVoiceline(conversationResponse.Value, setCooldown: false);
					timeBeforeConversationResponse = 0f;
					conversationResponse = null;
				}
			}
		}
	}
}

