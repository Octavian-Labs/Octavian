using UnityEngine;

namespace Rust.Ai.Gen2;

public class BasicAnimalFsm : FSMComponent
{
	public static class StateNames
	{
		public const string WaitForNavmesh = "WaitForNavmesh";

		public const string Roam = "Roam";

		public const string Idle = "Idle";

		public const string Sleep = "Sleep";

		public const string Chase = "Chase";

		public const string Attack = "Attack";

		public const string Cooldown = "Cooldown";

		public const string Flee = "Flee";

		public const string Dead = "Dead";
	}

	[Header("Basic animal")]
	[Tooltip("How close the animal has to get to bite, measured from its attack point. Also the distance the chase stops at, so it doubles as how close the animal comes to what it fights.")]
	[SerializeField]
	private float attackRange = 2f;

	[Tooltip("Where the bite reaches from, relative to the animal. Rotates with the animal, so a bear reaches further forwards than sideways.")]
	[SerializeField]
	private Vector3 attackOffset = Vector3.zero;

	[Tooltip("Minimum seconds between two bites.")]
	[SerializeField]
	private float attackIntervalSeconds = 1.5f;

	[Tooltip("How long the animal commits to one chase before backing off.")]
	[SerializeField]
	private float chaseGiveUpSeconds = 20f;

	[Tooltip("How long the animal stays uninterested after backing off, before it roams again.")]
	[SerializeField]
	private float cooldownSeconds = 5f;

	[Tooltip("Health fraction below which the animal breaks off a fight.")]
	[SerializeField]
	private float fleeBelowHealthFraction = 0.3f;

	[Tooltip("Chance of napping rather than idling at the end of a roam leg. Keep it low, a sleeping animal is a sitting duck.")]
	[SerializeField]
	[Range(0f, 1f)]
	private float sleepChance = 0.05f;

	public State_Roam roam = new State_Roam();

	public State_AnimalIdle idle = new State_AnimalIdle();

	public State_AnimalSleep sleep = new State_AnimalSleep();

	public State_AnimalChase chase = new State_AnimalChase();

	public State_AnimalBite attack = new State_AnimalBite();

	public State_Flee flee = new State_Flee();

	public State_Dead dead = new State_Dead();

	private Trans_TriggeredWithin AttackedTrans;

	private Trans_Triggerable_HitInfo DeathTrans;

	public override void InitShared()
	{
		if (base.baseEntity.isServer)
		{
			State_Nothing state_Nothing = new State_Nothing
			{
				Name = "WaitForNavmesh"
			};
			State_Nothing state_Nothing2 = new State_Nothing
			{
				Name = "Cooldown"
			};
			roam.Name = "Roam";
			idle.Name = "Idle";
			sleep.Name = "Sleep";
			chase.Name = "Chase";
			attack.Name = "Attack";
			attack.Range = attackRange;
			attack.AttackOffset = attackOffset;
			flee.Name = "Flee";
			dead.Name = "Dead";
			SetCombatMovement(chase, chase.speed, attackRange);
			SetCombatMovement(attack, chase.speed, attackRange);
			attack.offNavmeshSampleRadius = chase.offNavmeshSampleRadius;
			DeathTrans = new Trans_Triggerable_HitInfo();
			AttackedTrans = new Trans_TriggeredWithin();
			Trans_Triggerable EncounterEndTrans = new Trans_Triggerable();
			base.baseEntity.GetComponent<NPCEncounterTimer>().onShouldGiveUp.AddListener(delegate
			{
				EncounterEndTrans.Trigger();
			});
			FSMTransitionBase transition = new Trans_TargetInRange
			{
				Range = attackRange,
				Offset = attackOffset
			} & new Trans_Cooldown
			{
				cooldown = attackIntervalSeconds
			} & new Trans_HasStraightPathToTarget();
			State_Nothing state_Nothing3 = new State_Nothing();
			state_Nothing3.Name = "Root";
			State_Nothing state_Nothing4 = new State_Nothing
			{
				Name = "Alive"
			};
			State_Nothing state_Nothing5 = new State_Nothing
			{
				Name = "OnNavmesh"
			};
			State_Nothing state_Nothing6 = new State_Nothing
			{
				Name = "Passive"
			};
			State_Nothing state_Nothing7 = new State_Nothing
			{
				Name = "Combat"
			};
			state_Nothing3.AddChildren(state_Nothing4.AddTickTransition(dead, DeathTrans).AddChildren(state_Nothing.AddTickTransition(roam, new Trans_IsNavmeshReady()), state_Nothing5.AddTickTransition(state_Nothing, new Trans_IsNavmeshReady
			{
				Inverted = true
			}).AddChildren(state_Nothing6.AddTickTransition(flee, new Trans_And
			{
				new Trans_IsAfraidOfTarget(),
				new Trans_HasTarget()
			}).AddTickTransition(chase, new Trans_And
			{
				new Trans_HasTarget(),
				new Trans_IsInWater_Slow
				{
					minDepth = 1f,
					Inverted = true
				},
				new Trans_IsTargetInWater
				{
					Inverted = true
				}
			}).AddChildren(roam.AddFailureTransition(dead, new Trans_Dead()).AddEndTransition(sleep, new Trans_RandomChance
			{
				Chance = sleepChance
			}).AddEndTransition(idle), sleep.AddEndTransition(roam), idle.AddEndTransition(roam)), state_Nothing7.AddTickTransition(state_Nothing2, new Trans_HasTarget
			{
				Inverted = true
			}).AddTickTransition(flee, EncounterEndTrans).AddTickTransition(flee, new Trans_IsHealthBelowPercentage
			{
				percentage = fleeBelowHealthFraction
			})
				.AddTickTransition(flee, new Trans_TargetIsInSafeZone())
				.AddTickTransition(flee, new Trans_IsInWater_Slow
				{
					minDepth = 1f
				} | new Trans_IsTargetInWater())
				.AddChildren(chase.AddTickTransition(attack, transition).AddTickTransition(state_Nothing2, new Trans_ElapsedTime
				{
					Duration = chaseGiveUpSeconds
				}).AddTickTransition(flee, ~new Trans_CanReachTarget_Slow())
					.AddFailureTransition(flee), attack.AddEndTransition(chase)), state_Nothing2.AddTickTransition(flee, new Trans_And
			{
				new Trans_IsAfraidOfTarget(),
				new Trans_HasTarget()
			}).AddTickTransition(chase, AttackedTrans).AddTickTransition(roam, new Trans_ElapsedTime
			{
				Duration = cooldownSeconds
			}), flee.AddFailureTransition(chase, new Trans_And
			{
				new Trans_HasTarget(),
				new Trans_CanReachTarget_Slow(),
				new Trans_IsInWater_Slow
				{
					minDepth = 1f,
					Inverted = true
				},
				new Trans_IsTargetInWater
				{
					Inverted = true
				}
			}).AddEndTransition(roam))), dead);
			RegisterDebugMoveTo(state_Nothing4);
			SetState(state_Nothing);
			SetFsmActive(newActive: true);
		}
	}

	public override void Hurt(HitInfo hitInfo)
	{
		if (base.Senses.CanTarget(hitInfo.Initiator))
		{
			AttackedTrans.Trigger();
			if (base.CurrentState != dead)
			{
				ForceTickOnTheNextUpdate();
			}
		}
	}

	public override bool OnDied(HitInfo hitInfo)
	{
		DeathTrans.Trigger(hitInfo);
		return false;
	}

	private static void SetCombatMovement(State_AnimalChase state, RustNavMeshAgent.Speeds gait, float stoppingDistance)
	{
		state.speed = gait;
		state.stopAtDestination = false;
		state.stoppingDistanceOverride = stoppingDistance;
		state.succeedWhenDestinationIsReached = false;
	}
}

