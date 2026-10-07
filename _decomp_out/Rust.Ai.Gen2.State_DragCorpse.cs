using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_DragCorpse : FSMStateBase
{
	[SerializeField]
	protected RootMotionData Animation;

	private const int numLoops = 6;

	private int currentLoop;

	private BaseCorpse corpse;

	private RootMotionPlayer.PlayServerState animState;

	private Action _updateCorpsePositionAction;

	private Action UpdateCorpsePositionAction => OnStateFixedUpdate;

	public override EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		if (!base.Senses.FindFood(out var food))
		{
			return EFSMStateStatus.Failure;
		}
		if (!(food is BaseCorpse food2))
		{
			return EFSMStateStatus.Failure;
		}
		if (!base.Senses.FindTarget(out var _))
		{
			return EFSMStateStatus.Failure;
		}
		if (!SingletonComponent<NpcFoodManager>.Instance.Remove(food2))
		{
			return EFSMStateStatus.Failure;
		}
		corpse = food2;
		using (BaseEntity.FlagsUpdateScope flagsUpdateScope = corpse.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
		{
			flagsUpdateScope.Set(BaseEntity.Flags.Reserved3, b: true);
		}
		Owner.InvokeRepeatingFixedTime(UpdateCorpsePositionAction);
		animState = base.AnimPlayer.PlayServerAndTakeFromPool(Animation);
		return base.OnStateEnter(payload);
	}

	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (!corpse.IsValid() || corpse.IsDead())
		{
			return EFSMStateStatus.Failure;
		}
		if (!base.Senses.FindTargetPosition(out var targetPosition))
		{
			return EFSMStateStatus.Failure;
		}
		if (animState != null)
		{
			Quaternion to = Quaternion.LookRotation((targetPosition - Owner.transform.position).WithY(0f));
			animState.initialRotation = Quaternion.RotateTowards(animState.initialRotation, to, Time.deltaTime * 60f);
			Owner.transform.rotation = animState.initialRotation;
		}
		if (!animState.isPlaying)
		{
			currentLoop++;
			if (currentLoop >= 6)
			{
				return EFSMStateStatus.Success;
			}
			base.AnimPlayer.StopServerAndReturnToPool(ref animState, interrupt: false);
			animState = base.AnimPlayer.PlayServerAndTakeFromPool(Animation);
		}
		return base.OnStateUpdate(deltaTime);
	}

	private void OnStateFixedUpdate()
	{
		if (corpse.IsValid() && !corpse.IsDead())
		{
			Rigidbody component = corpse.GetComponent<Rigidbody>();
			if ((object)component != null)
			{
				component.MovePosition(Owner.transform.position + Owner.transform.forward * 1.6f + Owner.transform.up * 0.6f);
				component.linearVelocity = Vector3.zero;
				component.angularVelocity = Vector3.zero;
			}
		}
	}

	public override void OnStateExit()
	{
		base.OnStateExit();
		base.AnimPlayer.StopServerAndReturnToPool(ref animState);
		currentLoop = 0;
		if (corpse.IsValid())
		{
			SingletonComponent<NpcFoodManager>.Instance.Add(corpse);
			using BaseEntity.FlagsUpdateScope flagsUpdateScope = corpse.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate);
			flagsUpdateScope.Set(BaseEntity.Flags.Reserved3, b: false);
		}
		corpse = null;
		Owner.CancelInvokeFixedTime(UpdateCorpsePositionAction);
	}
}

