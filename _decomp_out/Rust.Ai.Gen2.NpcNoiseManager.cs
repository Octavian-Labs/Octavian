using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Facepunch;
using Spatial;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class NpcNoiseManager : SingletonComponent<NpcNoiseManager>, IServerComponent
{
	private const float voiceChatEventMaxAge = 1f;

	private const float noiseMaxAge = 10f;

	private const float worldSize = 8096f;

	private const int cellSize = 32;

	private ConcurrentDictionary<BasePlayer, double> recentVoiceChatEvents = new ConcurrentDictionary<BasePlayer, double>();

	private Grid<NpcNoiseEvent> noiseGrid = new Grid<NpcNoiseEvent>();

	private Queue<NpcNoiseEvent> noises = new Queue<NpcNoiseEvent>();

	private double nextTickTime;

	private int nextNoiseId = 1;

	private Action _removeOldNoisesCallback;

	public void AddNoise(BaseEntity initiator, Vector3 position, NpcNoiseIntensity intensity, bool guessInitiatorPosition = false)
	{
		if (_removeOldNoisesCallback == null)
		{
			_removeOldNoisesCallback = RemoveOldNoises;
		}
		if (!IsInvoking(_removeOldNoisesCallback))
		{
			InvokeRepeating(_removeOldNoisesCallback, 0f, 0f);
		}
		NpcNoiseEvent npcNoiseEvent = new NpcNoiseEvent(nextNoiseId++, initiator, position, guessInitiatorPosition ? initiator.transform.position : position, intensity, Time.timeAsDouble);
		noiseGrid.Add(npcNoiseEvent, npcNoiseEvent.NoisePosition.x, npcNoiseEvent.NoisePosition.z);
		noises.Enqueue(npcNoiseEvent);
	}

	private void RemoveOldNoises()
	{
		using (TimeWarning.New("RemoveOldNoises"))
		{
			while (noises.Count > 0)
			{
				NpcNoiseEvent obj = noises.Peek();
				if (Time.timeAsDouble - obj.EventTime <= 10.0)
				{
					break;
				}
				noises.Dequeue();
				noiseGrid.Remove(obj);
			}
		}
	}

	public void GetNoisesAround(Vector3 position, float range, List<NpcNoiseEvent> results)
	{
		if (noiseGrid != null)
		{
			noiseGrid.Query(position.x, position.z, range, results);
		}
	}

	public void OnServerProjectileHit(BaseEntity entity, ServerProjectile projectile, RaycastHit hit)
	{
		AddNoise(entity, projectile.transform.position, NpcNoiseIntensity.High);
	}

	public void OnProjectileHit(BaseEntity entity, HitInfo hit)
	{
		using (TimeWarning.New("NpcNoiseManager.OnProjectileHit"))
		{
			if (hit.Weapon.Is<BaseProjectile>(out var _) || hit.Weapon.Is<BaseMelee>(out var _))
			{
				if (hit.Weapon.Is<BaseProjectile>(out var castedUnityObject3) && !hit.Weapon.Is<BowWeapon>(out var _))
				{
					AddNoise(entity, hit.HitPositionWorld, castedUnityObject3.IsSilenced() ? NpcNoiseIntensity.Medium : NpcNoiseIntensity.High, guessInitiatorPosition: true);
				}
				else
				{
					AddNoise(entity, hit.HitPositionWorld, NpcNoiseIntensity.Low);
				}
			}
		}
	}

	public void OnWeaponShot(BasePlayer player, BaseProjectile weapon)
	{
		using (TimeWarning.New("NpcNoiseManager.OnWeaponShot"))
		{
			NpcNoiseIntensity intensity = NpcNoiseIntensity.High;
			if (weapon.Is<BowWeapon>(out var _))
			{
				intensity = NpcNoiseIntensity.Low;
			}
			else if (weapon != null && weapon.IsSilenced())
			{
				intensity = NpcNoiseIntensity.Medium;
			}
			AddNoise(player, player.transform.position, intensity);
		}
	}

	public void OnNpcWeaponShot(BaseEntity npc, BaseEntity target, Vector3 impactLocation)
	{
		using (TimeWarning.New("NpcNoiseManager.OnNpcWeaponShot"))
		{
			AddNoise(target, impactLocation, NpcNoiseIntensity.High, guessInitiatorPosition: true);
		}
	}

	public void OnWeaponThrown(BasePlayer player, BaseMelee weapon, bool canAiHearIt)
	{
		if (canAiHearIt)
		{
			AddNoise(player, player.transform.position, NpcNoiseIntensity.Low);
		}
	}

	public void OnExplosion(BaseEntity creatorEntity, TimedExplosive explosive)
	{
		if (creatorEntity.IsValid())
		{
			AddNoise(creatorEntity, explosive.transform.position, NpcNoiseIntensity.High, guessInitiatorPosition: true);
		}
	}

	public void OnVoiceChat(BasePlayer player)
	{
		recentVoiceChatEvents[player] = Time.timeAsDouble;
		AddNoise(player, player.transform.position, NpcNoiseIntensity.Low);
	}

	public void OnMeleeHit(BaseMelee weapon, HitInfo info)
	{
		BasePlayer ownerPlayer = weapon.GetOwnerPlayer();
		if (ownerPlayer != null)
		{
			AddNoise(ownerPlayer, ownerPlayer.transform.position, NpcNoiseIntensity.Low);
		}
	}

	public bool HasPlayerSpokenNear(BaseEntity querier, BasePlayer targetPlayer, float maxDistance = 16f)
	{
		using (TimeWarning.New("NpcNoiseManager.HasPlayerSpokenNear"))
		{
			double value;
			return recentVoiceChatEvents.TryGetValue(targetPlayer, out value) && Vector3.Distance(querier.transform.position, targetPlayer.transform.position) <= maxDistance;
		}
	}

	public void Tick()
	{
		if (Time.timeAsDouble < nextTickTime)
		{
			return;
		}
		nextTickTime = Time.timeAsDouble + (double)UnityEngine.Random.Range(4f, 6f);
		using (TimeWarning.New("NpcNoiseManager.RemoveStaleEntries"))
		{
			using PooledList<BasePlayer> pooledList = Pool.Get<PooledList<BasePlayer>>();
			double value;
			foreach (KeyValuePair<BasePlayer, double> recentVoiceChatEvent in recentVoiceChatEvents)
			{
				recentVoiceChatEvent.Deconstruct(out var key, out value);
				BasePlayer basePlayer = key;
				double num = value;
				if (!basePlayer.IsValid() || Time.timeAsDouble - num > 1.0)
				{
					pooledList.Add(basePlayer);
				}
			}
			foreach (BasePlayer item in pooledList)
			{
				recentVoiceChatEvents.Remove(item, out value);
			}
		}
	}
}

