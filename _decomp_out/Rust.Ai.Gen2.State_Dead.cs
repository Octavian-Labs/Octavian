using System;
using ConVar;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_Dead : FSMStateBase
{
	[SerializeField]
	private string deathStatName;

	[SerializeField]
	private GameObjectRef CorpsePrefab;

	[SerializeField]
	private RootMotionData staticDeathAnim;

	[SerializeField]
	private RootMotionData forwardMotionDeathAnim;

	[SerializeField]
	private RootMotionData swimmingDeathAnim;

	[SerializeField]
	private float ragdollWhenAnimRemainingTimeIsBelow = 0.5f;

	[SerializeField]
	private LootContainer.LootSpawnSlot[] LootSpawnSlots;

	[Tooltip("Whether an animal that dies with nobody having attacked it - old age - lies down and goes quietly instead of playing its combat death. Needs the owner to provide a held sleeping pose; a species without one falls back to a death clip.")]
	[SerializeField]
	private bool peacefulNaturalDeath;

	[Tooltip("How long (in seconds) the animal lies there before the corpse drops, so it dies in its sleep rather than the instant it settles.")]
	[SerializeField]
	private float peacefulDeathSettleTime = 6f;

	private RootMotionPlayer.PlayServerState animState;

	private Action _startRagdollAction;

	private Action _killOwnerAction;

	public bool HasCorpse => CorpsePrefab.isValid;

	public GameObjectRef Corpse => CorpsePrefab;

	public bool HasDeathAnimation
	{
		get
		{
			if (!(staticDeathAnim != null) && !(forwardMotionDeathAnim != null))
			{
				if (Owner != null && Owner.HasFlag(BaseEntity.Flags.Reserved1))
				{
					return swimmingDeathAnim != null;
				}
				return false;
			}
			return true;
		}
	}

	public bool HasPeacefulDeathAnimation => peacefulNaturalDeath;

	private Action StartRagdollAction => StartRagdoll;

	private Action KillOwnerAction => KillOwner;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		bool flag = payload.hitInfo == null;
		if (flag && !peacefulNaturalDeath && AI.logIssues)
		{
			Debug.LogError($"Entering {base.Name} without HitInfo payload, this should not happen and may cause issues with stats tracking. Owner: {Owner}", Owner);
		}
		if (payload.hitInfo != null && payload.hitInfo.InitiatorPlayer != null && !payload.hitInfo.InitiatorPlayer.IsNpc)
		{
			BasePlayer initiatorPlayer = payload.hitInfo.InitiatorPlayer;
			if (Owner.Is<BaseNPC2>(out var castedUnityObject) && castedUnityObject.IsAnimal)
			{
				initiatorPlayer.GiveAchievement("KILL_ANIMAL");
			}
			if (!string.IsNullOrEmpty(deathStatName))
			{
				initiatorPlayer.stats.Add(deathStatName, 1, (Stats)5);
				initiatorPlayer.stats.Save();
			}
			if (Owner is BaseCombatEntity killed)
			{
				initiatorPlayer.LifeStoryKill(killed);
			}
		}
		if (!CorpsePrefab.isValid)
		{
			if (HasDeathAnimation)
			{
				PlayDeathAnim(payload, flag, KillOwnerAction);
			}
			else
			{
				Owner.Kill();
			}
			return base.OnStateEnter(payload);
		}
		if (Owner is INaturalDeathPose naturalDeathPose && ((flag && peacefulNaturalDeath) || naturalDeathPose.IsHoldingPose))
		{
			naturalDeathPose.EnterNaturalDeathPose();
			base.Agent.ResetPath();
			float num = (flag ? Mathf.Max(0f, peacefulDeathSettleTime) : 0f);
			Owner.Invoke(StartRagdollAction, num + AI.defaultInterpolationDelay);
			return base.OnStateEnter(payload);
		}
		PlayDeathAnim(payload, flag, StartRagdollAction);
		return base.OnStateEnter(payload);
	}

	private void KillOwner()
	{
		Owner.Kill();
	}

	private void PlayDeathAnim(FSMPayload payload, bool diedOfNaturalCauses, Action invokeAction)
	{
		RootMotionData rootMotionData = ((base.Agent.speed > base.Agent.GetSpeedForGait(RustNavMeshAgent.Speeds.Run) || diedOfNaturalCauses || Vector3.Dot(payload.hitInfo.attackNormal, Owner.transform.forward) >= 0f) ? (forwardMotionDeathAnim ?? staticDeathAnim) : (staticDeathAnim ?? forwardMotionDeathAnim));
		if (swimmingDeathAnim != null && base.Agent.canSwim && base.Agent.IsSwimming)
		{
			rootMotionData = swimmingDeathAnim;
		}
		if (rootMotionData != null)
		{
			animState = base.AnimPlayer.PlayServerAndTakeFromPool(rootMotionData);
			float num = Mathf.Max(0f, rootMotionData.inPlaceAnimation.length - ragdollWhenAnimRemainingTimeIsBelow);
			Owner.Invoke(invokeAction, num + AI.defaultInterpolationDelay);
		}
		else
		{
			StartRagdoll();
		}
	}

	private void StartRagdoll()
	{
		BaseCorpse baseCorpse = Owner.DropCorpse(CorpsePrefab.resourcePath);
		if (baseCorpse.Is<LootableCorpse>(out var castedUnityObject))
		{
			castedUnityObject.TakeFrom(Owner, CreateInventory());
			PrefabInformation prefabInformation = PrefabAttribute.server.Find<PrefabInformation>(Owner.prefabID);
			if (prefabInformation != null && prefabInformation.title.IsValid())
			{
				castedUnityObject.playerName = prefabInformation.title.translated;
			}
			else
			{
				castedUnityObject.playerName = Owner.GetType().Name;
			}
			castedUnityObject.Spawn();
			baseCorpse.TakeChildren(Owner);
			if (LootSpawnSlots.Length != 0)
			{
				LootContainer.LootSpawnSlot[] lootSpawnSlots = LootSpawnSlots;
				for (int i = 0; i < lootSpawnSlots.Length; i++)
				{
					LootContainer.LootSpawnSlot lootSpawnSlot = lootSpawnSlots[i];
					for (int j = 0; j < lootSpawnSlot.numberToSpawn; j++)
					{
						if (UnityEngine.Random.Range(0f, 1f) <= lootSpawnSlot.probability)
						{
							lootSpawnSlot.definition.SpawnIntoContainer(castedUnityObject.containers[0]);
						}
					}
				}
			}
		}
		else if (baseCorpse != null)
		{
			baseCorpse.Spawn();
			baseCorpse.TakeChildren(Owner);
		}
		Owner.Invoke(Owner.KillMessage, 0.5f);
	}

	public override void OnStateExit()
	{
		base.AnimPlayer.StopServerAndReturnToPool(ref animState);
		base.OnStateExit();
	}

	private ItemContainer CreateInventory()
	{
		ItemContainer itemContainer = new ItemContainer
		{
			entityOwner = Owner
		};
		itemContainer.ServerInitialize(null, 24);
		if (!itemContainer.uid.IsValid)
		{
			itemContainer.GiveUID();
		}
		return itemContainer;
	}
}

