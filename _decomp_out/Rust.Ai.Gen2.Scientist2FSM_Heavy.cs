using UnityEngine;

namespace Rust.Ai.Gen2;

[SoftRequireComponent(typeof(RustNavMeshAgent), typeof(RootMotionPlayer), typeof(SenseComponent))]
[SoftRequireComponent(typeof(NpcZoneComponent), typeof(NPCEncounterTimer), typeof(NpcShootingComponent))]
[SoftRequireComponent(typeof(BlackboardComponent), typeof(NpcBarkManager))]
public class Scientist2FSM_Heavy : FSMComponent
{
	public State_PatrolIdle idle = new State_PatrolIdle();

	public State_Patrol patrol = new State_Patrol();

	public State_Search search = new State_Search();

	public State_ScientistRush chase = new State_ScientistRush();

	public State_MoveToPointWithLosOnTarget popOut = new State_MoveToPointWithLosOnTarget();

	public State_Dead dead = new State_Dead();

	public State_ScientistSurprised surprised = new State_ScientistSurprised();

	private Trans_Triggerable_HitInfo HurtTrans;

	private Trans_Triggerable_HitInfo DeathTrans;

	public Trans_Triggerable RushPositionTrans;

	private NpcBarkComponent _barkComponent;

	private bool spokeDistractedByHeavyVoiceline;

	public GameObjectRef explosionPrefab;

	public GameObjectRef bulletEffect;

	public SoundDefinition backpackHitSound;

	public float bulletSpeed = 35f;

	public float backPackHealth = 110f;

	private NpcBarkComponent BarkComponent => _barkComponent ?? (_barkComponent = base.baseEntity.GetComponent<NpcBarkComponent>());

	public override void InitShared()
	{
		if (base.baseEntity.isServer)
		{
			State_Nothing state_Nothing = new State_Nothing
			{
				Name = "WaitForNavMesh"
			};
			HurtTrans = new Trans_Triggerable_HitInfo();
			DeathTrans = new Trans_Triggerable_HitInfo();
			RushPositionTrans = new Trans_Triggerable();
			State_Nothing obj = new State_Nothing
			{
				Name = "Root"
			};
			State_Nothing state_Nothing2 = new State_Nothing
			{
				Name = "Alive"
			};
			State_Nothing state_Nothing3 = new State_Nothing
			{
				Name = "OnNavmesh"
			};
			State_Nothing state_Nothing4 = new State_Nothing
			{
				Name = "Roaming"
			};
			State_Nothing state_Nothing5 = new State_Nothing
			{
				Name = "Has target"
			};
			State_Nothing state_Nothing6 = new State_Nothing
			{
				Name = "Searching"
			};
			State_Nothing state_Nothing7 = new State_Nothing
			{
				Name = "Shoot while still"
			};
			State_Search state_Search = search.Clone() as State_Search;
			state_Search.Name = "SearchPastLKP";
			state_Search.predict = true;
			Trans_CanSeeTarget trans_CanSeeTarget = new Trans_CanSeeTarget();
			FSMTransitionBase fSMTransitionBase = ~new Trans_CanSeeTarget();
			Trans_And dstState2Trans = new Trans_Cooldown
			{
				cooldown = 5f
			} & new Trans_TargetSurprised();
			_ = obj + (state_Nothing2.AddTickTransition(dead, DeathTrans) + state_Nothing.AddTickTransition(patrol, new Trans_IsNavmeshReady()) + (state_Nothing3 + (state_Nothing4.AddTickBranchingTrans(chase, new Trans_HasTarget(), surprised, dstState2Trans) + patrol.AddFailureTransition(dead, new Trans_Dead()).AddEndTransition(idle) + idle.AddTickTransition(patrol, new Trans_ElapsedTimeRandomized
			{
				MinDuration = 1.0,
				MaxDuration = 10.0
			})) + (state_Nothing5.AddTickTransition(patrol, ~new Trans_HasTarget()) + surprised.AddEndTransition(chase) + chase.AddTickTransition(state_Nothing7, trans_CanSeeTarget & new Trans_TargetLkpInRange
			{
				Range = 8f
			} & new Trans_IsMuzzleClear_Slow()).AddEndTransition(popOut, ~new Trans_TargetLkpInRange
			{
				Range = 3f,
				Predict = true
			}).AddEndTransition(state_Search) + popOut.AddEndTransition(state_Nothing7, trans_CanSeeTarget).AddEndTransition(state_Search) + state_Nothing7.AddTickTransition(chase, fSMTransitionBase | ~new Trans_TargetLkpInRange
			{
				Range = 10f
			} | ~new Trans_IsMuzzleClear_Slow()) + (state_Nothing6.AddTickTransition(chase, RushPositionTrans).AddTickBranchingTrans(chase, trans_CanSeeTarget, surprised, dstState2Trans).AddTickTransition(chase, new Trans_Cooldown
			{
				cooldown = 5f
			} & fSMTransitionBase & new Trans_HeardNoise())
				.AddFailureTransition(patrol) + state_Search.AddEndTransition(search, new Trans_Bark
			{
				category = ENPCVoicelineCategory.Search
			}) + search)))) + dead;
			RegisterDebugMoveTo(state_Nothing2);
			SetState(state_Nothing);
			SetFsmActive(newActive: true);
		}
	}

	public override void Hurt(HitInfo hitInfo)
	{
		HurtTrans.Trigger(hitInfo);
		if (hitInfo.HitBone == StringPool.Get("backpack_col"))
		{
			backPackHealth -= hitInfo.damageTypes.Total();
			hitInfo.damageTypes.Clear();
			if (explosionPrefab != null && backPackHealth <= 0f)
			{
				hitInfo.damageTypes.Add(DamageType.Explosion, 99999f);
				GameManager.server.CreateEntity(explosionPrefab.resourcePath, hitInfo.HitPositionWorld, Quaternion.LookRotation(hitInfo.HitNormalWorld)).Spawn();
			}
		}
		else if (!spokeDistractedByHeavyVoiceline && (hitInfo.damageTypes.Has(DamageType.Bullet) || hitInfo.damageTypes.Has(DamageType.Arrow)) && BarkComponent.PlayVoicelineFromCategory(ENPCVoicelineCategory.DistractedByHeavy))
		{
			spokeDistractedByHeavyVoiceline = true;
		}
	}

	public override bool OnDied(HitInfo hitInfo)
	{
		DeathTrans.Trigger(hitInfo);
		return false;
	}
}

