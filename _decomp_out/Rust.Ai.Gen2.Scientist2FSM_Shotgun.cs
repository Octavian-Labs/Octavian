namespace Rust.Ai.Gen2;

[SoftRequireComponent(typeof(RustNavMeshAgent), typeof(RootMotionPlayer), typeof(SenseComponent))]
[SoftRequireComponent(typeof(NpcZoneComponent), typeof(NPCEncounterTimer), typeof(NpcShootingComponent))]
[SoftRequireComponent(typeof(BlackboardComponent), typeof(NpcBarkManager))]
public class Scientist2FSM_Shotgun : FSMComponent
{
	public State_PatrolIdle idle = new State_PatrolIdle();

	public State_Patrol patrol = new State_Patrol();

	public State_Search search = new State_Search();

	public State_ScientistRush chase = new State_ScientistRush();

	public State_Dead dead = new State_Dead();

	public State_DogFight dogFight = new State_DogFight();

	public State_MoveToCoverHiddenFromTarget goHide = new State_MoveToCoverHiddenFromTarget();

	public State_StayInCover stayInCover = new State_StayInCover();

	public State_ScientistSurprised surprised = new State_ScientistSurprised();

	public State_Flank flank = new State_Flank();

	private Trans_Triggerable_HitInfo HurtTrans;

	private Trans_Triggerable_HitInfo DeathTrans;

	public Trans_Triggerable RushPositionTrans;

	private NpcBarkComponent _barkComponent;

	private NpcBarkComponent BarkComponent => _barkComponent ?? (_barkComponent = base.baseEntity.GetComponent<NpcBarkComponent>());

	public override void InitShared()
	{
		if (base.baseEntity.isServer)
		{
			State_Nothing state_Nothing = new State_Nothing
			{
				Name = "WaitForNavMesh"
			};
			State_Nothing state_Nothing2 = new State_Nothing
			{
				Name = "ReadyToChase"
			};
			HurtTrans = new Trans_Triggerable_HitInfo();
			DeathTrans = new Trans_Triggerable_HitInfo();
			RushPositionTrans = new Trans_Triggerable();
			State_Nothing obj = new State_Nothing
			{
				Name = "Root"
			};
			State_Nothing state_Nothing3 = new State_Nothing
			{
				Name = "Alive"
			};
			State_Nothing state_Nothing4 = new State_Nothing
			{
				Name = "OnNavmesh"
			};
			State_Nothing state_Nothing5 = new State_Nothing
			{
				Name = "Roaming"
			};
			State_Nothing state_Nothing6 = new State_Nothing
			{
				Name = "Has target"
			};
			State_Nothing state_Nothing7 = new State_Nothing
			{
				Name = "Searching"
			};
			State_Nothing state_Nothing8 = new State_Nothing
			{
				Name = "See target switch"
			};
			State_Search state_Search = search.Clone() as State_Search;
			state_Search.Name = "SearchPastLKP";
			state_Search.predict = true;
			FSMTransitionBase fSMTransitionBase = ~new Trans_IsReloading();
			Trans_IsReloading trans_IsReloading = new Trans_IsReloading();
			Trans_CanSeeTarget trans_CanSeeTarget = new Trans_CanSeeTarget();
			FSMTransitionBase fSMTransitionBase2 = ~new Trans_CanSeeTarget();
			float num = 7f;
			Trans_And dstState2Trans = new Trans_Cooldown
			{
				cooldown = 5f
			} & new Trans_TargetLkpInRange
			{
				Range = num + 1f
			} & new Trans_TargetSurprised();
			Trans_And trans_And = new Trans_Cooldown
			{
				cooldown = 5f
			} & new Trans_IsTargetLkpInOurZone();
			Trans_And transition = new Trans_Cooldown
			{
				cooldown = 5f
			} & ~new Trans_CanSeeTarget() & (new Trans_TargetIsLowHealth() | new Trans_TargetIsUndergeared());
			_ = obj + (state_Nothing3.AddTickTransition(dead, DeathTrans) + state_Nothing.AddTickTransition(patrol, new Trans_IsNavmeshReady()) + (state_Nothing4 + (state_Nothing5.AddTickBranchingTrans(goHide, new Trans_HasTarget(), surprised, dstState2Trans) + patrol.AddFailureTransition(dead, new Trans_Dead()).AddEndTransition(idle) + idle.AddTickTransition(patrol, new Trans_ElapsedTimeRandomized
			{
				MinDuration = 1.0,
				MaxDuration = 10.0
			})) + (state_Nothing6.AddTickTransition(patrol, ~new Trans_HasTarget()) + (state_Nothing2.AddTickTransition(chase, fSMTransitionBase & RushPositionTrans) + state_Nothing8.AddTickTransition(goHide, trans_IsReloading | ~new Trans_TargetLkpInRange
			{
				Range = 30f
			}).AddTickTransition(chase, new Trans_TargetLkpInRange
			{
				Range = 20f
			}).AddTickTransition(dogFight, new Trans_TargetLkpInRange
			{
				Range = num
			}) + dogFight.AddTickTransition(goHide, ~new Trans_TargetLkpInRange
			{
				Range = 50f
			} | (trans_IsReloading & new Trans_Bark
			{
				category = ENPCVoicelineCategory.Reload
			})).AddTickTransition(chase, transition).AddTickTransition(chase, new Trans_ElapsedTimeRandomized
			{
				MinDuration = 1.0,
				MaxDuration = 4.0
			} & fSMTransitionBase)
				.AddFailureTransition(goHide) + goHide.AddTickBranchingTrans(dogFight, fSMTransitionBase & trans_CanSeeTarget & new Trans_TargetInRange
			{
				Range = 3f
			}, surprised, dstState2Trans).AddTickTransition(chase, transition).AddFailureTransition(chase)
				.AddEndTransition(stayInCover, ~new Trans_TargetLkpInRange
				{
					Range = 50f
				} | trans_IsReloading | new Trans_RandomChance
				{
					Chance = 0.5f
				})
				.AddEndTransition(flank, trans_And) + stayInCover.AddTickBranchingTrans(chase, trans_CanSeeTarget | HurtTrans | (new Trans_IsTargetRunning() & new Trans_TargetInRange
			{
				Range = 20f
			}), goHide, trans_IsReloading).AddTickBranchingTrans(chase, new Trans_TargetLkpInRange
			{
				Range = 50f
			} & fSMTransitionBase & new Trans_ElapsedTimeRandomized
			{
				MinDuration = 5.0,
				MaxDuration = 10.0
			}, flank, trans_And).AddTickBranchingTrans(state_Nothing8, trans_CanSeeTarget, surprised, dstState2Trans) + flank.AddTickBranchingTrans(state_Nothing8, trans_CanSeeTarget, surprised, dstState2Trans).AddFailureTransition(chase).AddEndTransition(state_Search) + (state_Nothing7.AddTickBranchingTrans(state_Nothing8, trans_CanSeeTarget, surprised, dstState2Trans).AddTickTransition(flank, new Trans_Cooldown
			{
				cooldown = 5f
			} & fSMTransitionBase2 & new Trans_HeardNoise()).AddFailureTransition(goHide) + state_Search.AddEndTransition(search, new Trans_Bark
			{
				category = ENPCVoicelineCategory.Search
			}) + search) + surprised.AddEndTransition(state_Nothing8)) + chase.AddTickBranchingTrans(dogFight, new Trans_CanSeeTarget() & new Trans_TargetLkpInRange
			{
				Range = num
			}, surprised, dstState2Trans).AddFailureTransition(goHide).AddEndTransition(state_Search)))) + dead;
			RegisterDebugMoveTo(state_Nothing3);
			SetState(state_Nothing);
			SetFsmActive(newActive: true);
		}
	}

	public override void Hurt(HitInfo hitInfo)
	{
		HurtTrans.Trigger(hitInfo);
	}

	public override bool OnDied(HitInfo hitInfo)
	{
		DeathTrans.Trigger(hitInfo);
		return false;
	}
}

