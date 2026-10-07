using System;
using System.Collections.Generic;
using System.Text;
using ConVar;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

[SoftRequireComponent(typeof(RustNavMeshAgent), typeof(RootMotionPlayer), typeof(SenseComponent))]
[SoftRequireComponent(typeof(BlackboardComponent), typeof(NPCEncounterTimer))]
public class FSMComponent : EntityComponent<BaseEntity>
{
	public class TickFSMWorkQueue : PersistentObjectWorkQueue<FSMComponent>
	{
		protected override void RunJob(FSMComponent component)
		{
			if (ShouldAdd(component) && component.isRunning)
			{
				component.Senses.Tick();
				if (component.TryGetComponent<NPCEncounterTimer>(out var component2))
				{
					component2.Tick();
				}
				component.Tick();
				if (component.TryGetComponent<NpcBarkComponent>(out var component3))
				{
					component3.Tick();
				}
				if (component.TryGetComponent<NPCNetworking>(out var component4))
				{
					component4.Tick();
				}
			}
		}

		protected override bool ShouldAdd(FSMComponent component)
		{
			if (base.ShouldAdd(component))
			{
				return component.baseEntity.IsValid();
			}
			return false;
		}
	}

	[Header("Mounting")]
	public State_MountVehicle mountVehicle = new State_MountVehicle();

	public State_Mounted mounted = new State_Mounted();

	public State_DismountVehicle dismountVehicle = new State_DismountVehicle();

	[NonSerialized]
	private Trans_Triggerable mountTrans;

	[NonSerialized]
	private Trans_Triggerable dismountTrans;

	private bool isRunning;

	private SenseComponent _senses;

	public const float minRefreshIntervalSeconds = 0f;

	public const float maxRefreshIntervalSeconds = 0.5f;

	private double? _lastTickTime;

	private double nextRefreshTime;

	private const int maxStateChangesPerTick = 3;

	private List<FSMStateBase> sameFrameStateChangesHistory = new List<FSMStateBase>();

	private FSMStateBase pendingStateChange;

	private FSMPayload pendingStateChangePayload;

	private FSMTransitionBase pendingStateChangeTransition;

	public static TickFSMWorkQueue defaultWorkQueue = new TickFSMWorkQueue();

	[NonSerialized]
	private State_DebugMoveToPosition debugMoveTo;

	[NonSerialized]
	private Trans_Triggerable debugMoveToTrans;

	[NonSerialized]
	private FSMStateBase debugMoveToReturnState;

	public bool SupportsMounting => mountTrans != null;

	public bool IsMounting => CurrentState == mountVehicle;

	public bool IsMounted => CurrentState == mounted;

	public bool IsDismounting => CurrentState == dismountVehicle;

	public FSMStateBase CurrentState { get; private set; }

	protected SenseComponent Senses => _senses ?? (_senses = base.baseEntity.GetComponent<SenseComponent>());

	private float RefreshInterval
	{
		get
		{
			if (!Senses.ShouldRefreshFast)
			{
				return 0.5f;
			}
			return 0f;
		}
	}

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

	public IReadOnlyList<FSMStateBase> StatesEnteredThisTick => sameFrameStateChangesHistory;

	public static float frameBudgetMs => AI.fsm_frametime;

	protected virtual TickFSMWorkQueue workQueue => defaultWorkQueue;

	public bool SupportsDebugMove => debugMoveTo != null;

	public bool IsDebugMoving
	{
		get
		{
			if (debugMoveTo != null)
			{
				return CurrentState == debugMoveTo;
			}
			return false;
		}
	}

	protected void RegisterMounting(FSMStateBase attachTo, FSMStateBase returnTo)
	{
		if (attachTo != null && returnTo != null)
		{
			mountTrans = new Trans_Triggerable();
			dismountTrans = new Trans_Triggerable();
			attachTo.AddChildren(mountVehicle, mounted, dismountVehicle);
			attachTo.AddTickTransition(mountVehicle, mountTrans);
			mountVehicle.AddFailureTransition(returnTo);
			mountVehicle.AddEndTransition(mounted);
			mounted.AddTickTransition(dismountVehicle, dismountTrans);
			mounted.AddEndTransition(returnTo);
			dismountVehicle.AddFailureTransition(mounted);
			dismountVehicle.AddEndTransition(returnTo);
		}
	}

	public bool StartMount()
	{
		if (mountTrans == null || CurrentState == null || !isRunning)
		{
			return false;
		}
		mountTrans.Trigger();
		ForceTickOnTheNextUpdate();
		return true;
	}

	public bool StartDismount()
	{
		if (dismountTrans == null || !IsMounted)
		{
			return false;
		}
		dismountTrans.Trigger();
		ForceTickOnTheNextUpdate();
		return true;
	}

	public bool ResumeMounted()
	{
		if (mountTrans == null)
		{
			return false;
		}
		SetState(mounted);
		return true;
	}

	public void SetFsmActive(bool newActive)
	{
		if (newActive != isRunning)
		{
			isRunning = newActive;
			if (isRunning)
			{
				_lastTickTime = null;
				workQueue.Add(this);
			}
			else
			{
				workQueue.Remove(this);
			}
		}
	}

	public override void DestroyShared()
	{
		if (base.baseEntity.isServer)
		{
			SetFsmActive(newActive: false);
			base.DestroyShared();
		}
	}

	public static void ShowDebugInfoAroundLocation(BasePlayer player, float radius = 100f)
	{
		if (!player.IsValid())
		{
			return;
		}
		using PooledList<BaseEntity> pooledList = Facepunch.Pool.Get<PooledList<BaseEntity>>();
		BaseEntity.Query.Server.GetBrainsInSphere(player.transform.position, radius, pooledList);
		foreach (BaseEntity item in pooledList)
		{
			FSMComponent component = item.GetComponent<FSMComponent>();
			if (!(component == null) && component.CurrentState != null && component.isRunning)
			{
				string arg = ((item is IFSMDebugInfo iFSMDebugInfo) ? (component.CurrentState.Name + "\n" + iFSMDebugInfo.GetDebugInfo()) : component.CurrentState.Name);
				player.ClientRPC(RpcTarget.Player("CL_ShowStateDebugInfo", player), component.baseEntity.net.ID, arg);
			}
		}
	}

	protected void ForceTickOnTheNextUpdate()
	{
		nextRefreshTime = 0.0;
	}

	protected virtual void OnTicked(float deltaTime)
	{
	}

	public void Tick()
	{
		using (TimeWarning.New("FSMComponent.Tick"))
		{
			if (UnityEngine.Time.timeAsDouble < nextRefreshTime)
			{
				return;
			}
			nextRefreshTime = UnityEngine.Time.timeAsDouble + (double)RefreshInterval;
			float deltaTime = (float)(UnityEngine.Time.timeAsDouble - LastTickTime);
			LastTickTime = UnityEngine.Time.timeAsDouble;
			OnTicked(deltaTime);
			sameFrameStateChangesHistory.Clear();
			if (pendingStateChange != null)
			{
				SetState(pendingStateChange, pendingStateChangePayload, pendingStateChangeTransition);
			}
			else
			{
				if (CurrentState == null)
				{
					return;
				}
				FSMPayload payload = default(FSMPayload);
				using (TimeWarning.New("NormalTransitions"))
				{
					using PooledList<FSMStateBase> pooledList = Facepunch.Pool.Get<PooledList<FSMStateBase>>();
					CurrentState.FindAncestry(pooledList);
					foreach (FSMStateBase item in pooledList)
					{
						foreach (var (fSMTransitionBase, dstState) in item.transitions)
						{
							if (fSMTransitionBase.Owner == null)
							{
								fSMTransitionBase.Init(base.baseEntity);
							}
							if (fSMTransitionBase.Evaluate(ref payload) && !IsInheritedSelfTransition(item, dstState))
							{
								TakeTransition(fSMTransitionBase, dstState, payload);
								return;
							}
						}
					}
				}
				EFSMStateStatus currentStateStatus = EFSMStateStatus.None;
				using (TimeWarning.New("StateTick"))
				{
					using (TimeWarning.New(CurrentState.Name))
					{
						currentStateStatus = CurrentState.OnStateUpdate(deltaTime);
					}
				}
				EvaluateEndTransitions(currentStateStatus);
			}
		}
	}

	private void EvaluateEndTransitions(EFSMStateStatus currentStateStatus)
	{
		using (TimeWarning.New("EndTransitions"))
		{
			if (currentStateStatus == EFSMStateStatus.None)
			{
				return;
			}
			FSMPayload payload = default(FSMPayload);
			using PooledList<FSMStateBase> pooledList = Facepunch.Pool.Get<PooledList<FSMStateBase>>();
			CurrentState.FindAncestry(pooledList);
			foreach (FSMStateBase item in pooledList)
			{
				foreach (var (fSMTransitionBase, dstState, eFSMStateStatus) in item.endTransitions)
				{
					if (eFSMStateStatus != (EFSMStateStatus.Success | EFSMStateStatus.Failure) && eFSMStateStatus != currentStateStatus)
					{
						continue;
					}
					bool flag = true;
					if (fSMTransitionBase != null)
					{
						if (fSMTransitionBase.Owner == null)
						{
							fSMTransitionBase.Init(base.baseEntity);
						}
						flag = fSMTransitionBase.Evaluate(ref payload);
					}
					if (flag && !IsInheritedSelfTransition(item, dstState))
					{
						TakeTransition(fSMTransitionBase, dstState, payload);
						ForceTickOnTheNextUpdate();
						return;
					}
				}
			}
		}
	}

	private bool IsInheritedSelfTransition(FSMStateBase declaringState, FSMStateBase dstState)
	{
		if (dstState == CurrentState)
		{
			return declaringState != dstState;
		}
		return false;
	}

	protected virtual FSMStateBase RedirectStateChange(FSMStateBase newState)
	{
		return newState;
	}

	private void TakeTransition(FSMTransitionBase transition, FSMStateBase dstState, FSMPayload payload)
	{
		dstState.Owner = base.baseEntity;
		FSMStateBase fSMStateBase = RedirectStateChange(dstState);
		if (fSMStateBase != dstState)
		{
			SetState(fSMStateBase, payload);
			return;
		}
		transition?.OnTransitionTaken(CurrentState, dstState);
		SetState(dstState, payload, transition);
	}

	public void SetState(FSMStateBase newState, FSMPayload payload = default(FSMPayload), FSMTransitionBase takenBy = null)
	{
		using (TimeWarning.New("SetState"))
		{
			newState = RedirectStateChange(newState);
			newState.Owner = base.baseEntity;
			pendingStateChange = null;
			pendingStateChangePayload = default(FSMPayload);
			pendingStateChangeTransition = null;
			sameFrameStateChangesHistory.Add(newState);
			if (sameFrameStateChangesHistory.Count > 3)
			{
				pendingStateChange = newState;
				pendingStateChangePayload = payload;
				pendingStateChangeTransition = takenBy;
				if (!AI.logIssues)
				{
					return;
				}
				StringBuilder obj = Facepunch.Pool.Get<StringBuilder>();
				obj.AppendFormat("[FSM] Possible endless recursion detected from {0} to {1} on {2}\n", CurrentState?.Name, newState.Name, base.baseEntity);
				foreach (FSMStateBase item5 in sameFrameStateChangesHistory)
				{
					obj.AppendFormat("{0} -> ", item5.Name);
				}
				Debug.LogWarning(obj);
				Facepunch.Pool.FreeUnmanaged(ref obj);
				return;
			}
			if (CurrentState != null)
			{
				using (TimeWarning.New("Transitions OnStateExit"))
				{
					using PooledList<FSMStateBase> pooledList = Facepunch.Pool.Get<PooledList<FSMStateBase>>();
					CurrentState.FindAncestry(pooledList);
					foreach (FSMStateBase item6 in pooledList)
					{
						foreach (var endTransition in item6.endTransitions)
						{
							FSMTransitionBase item = endTransition.transition;
							if (item != null && item.Owner == null)
							{
								item.Init(base.baseEntity);
							}
							item?.OnStateExit();
						}
						foreach (var transition in item6.transitions)
						{
							FSMTransitionBase item2 = transition.transition;
							if (item2 != null && item2.Owner == null)
							{
								item2.Init(base.baseEntity);
							}
							item2.OnStateExit();
						}
					}
				}
				using (TimeWarning.New("OnStateExit"))
				{
					using (TimeWarning.New(CurrentState.Name))
					{
						CurrentState.OnStateExit();
					}
				}
			}
			CurrentState = newState;
			using (TimeWarning.New("Transitions OnStateEnter"))
			{
				using PooledList<FSMStateBase> pooledList2 = Facepunch.Pool.Get<PooledList<FSMStateBase>>();
				CurrentState.FindAncestry(pooledList2);
				foreach (FSMStateBase item7 in pooledList2)
				{
					foreach (var endTransition2 in item7.endTransitions)
					{
						FSMTransitionBase item3 = endTransition2.transition;
						if (item3 != null && item3.Owner == null)
						{
							item3.Init(base.baseEntity);
						}
						item3?.OnStateEnter();
					}
					foreach (var transition2 in item7.transitions)
					{
						FSMTransitionBase item4 = transition2.transition;
						if (item4 != null && item4.Owner == null)
						{
							item4.Init(base.baseEntity);
						}
						item4.OnStateEnter();
					}
				}
			}
			using (TimeWarning.New("OnStateEnter"))
			{
				using (TimeWarning.New(CurrentState.Name))
				{
					EFSMStateStatus eFSMStateStatus = CurrentState.OnStateEnter(payload);
					payload.Dispose();
					if (eFSMStateStatus != EFSMStateStatus.Failure)
					{
						takenBy?.OnTransitionConfirmed(CurrentState);
					}
					EvaluateEndTransitions(eFSMStateStatus);
				}
			}
		}
	}

	protected void RegisterDebugMoveTo(FSMStateBase attachTo)
	{
		if (attachTo != null)
		{
			debugMoveTo = new State_DebugMoveToPosition
			{
				Name = "DebugMoveTo"
			};
			debugMoveToTrans = new Trans_Triggerable();
			attachTo.AddChild(debugMoveTo);
			attachTo.AddTickTransition(debugMoveTo, debugMoveToTrans);
		}
	}

	public bool DebugMoveTo(Vector3 worldPosition, RustNavMeshAgent.Speeds gait, out string error)
	{
		error = null;
		if (debugMoveTo == null)
		{
			error = base.baseEntity.ShortPrefabName + "'s FSM has no debug move state registered";
			return false;
		}
		if (CurrentState == null || !isRunning)
		{
			error = base.baseEntity.ShortPrefabName + "'s FSM is not running - it may be asleep, try ai.sleepwake 0";
			return false;
		}
		if (!base.baseEntity.TryGetComponent<RustNavMeshAgent>(out var component))
		{
			error = base.baseEntity.ShortPrefabName + " has no RustNavMeshAgent";
			return false;
		}
		Vector3 positionWS = base.baseEntity.WorldToNavMeshSpace.MultiplyPoint(worldPosition);
		if (!component.SamplePosition(positionWS, out var _, 5f))
		{
			error = $"No navmesh within {5f}m of that position";
			return false;
		}
		debugMoveTo.destinationWS = worldPosition;
		debugMoveTo.speed = gait;
		if (!IsDebugMoving)
		{
			debugMoveToReturnState = CurrentState;
		}
		debugMoveTo.endTransitions.Clear();
		debugMoveTo.AddEndTransition(debugMoveToReturnState);
		debugMoveToTrans.Trigger();
		ForceTickOnTheNextUpdate();
		return true;
	}

	public bool DebugRelease()
	{
		if (!IsDebugMoving)
		{
			return false;
		}
		SetState(debugMoveToReturnState);
		return true;
	}
}

