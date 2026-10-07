using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rust.Ai.Gen2;

public abstract class FSMStateBase
{
	[NonSerialized]
	public BaseEntity Owner;

	[SerializeField]
	private string _Name;

	[NonSerialized]
	public FSMStateBase parent;

	public List<(FSMTransitionBase transition, FSMStateBase dstState)> transitions = new List<(FSMTransitionBase, FSMStateBase)>();

	public List<(FSMTransitionBase transition, FSMStateBase dstState, EFSMStateStatus status)> endTransitions = new List<(FSMTransitionBase, FSMStateBase, EFSMStateStatus)>();

	private SenseComponent _senses;

	private RustNavMeshAgent _agent;

	private RootMotionPlayer _animPlayer;

	private BlackboardComponent _blackboard;

	public string Name
	{
		get
		{
			if (string.IsNullOrEmpty(_Name))
			{
				_Name = GetType().Name.Replace("State_", "");
			}
			return _Name;
		}
		set
		{
			_Name = value;
		}
	}

	protected SenseComponent Senses => _senses ?? (_senses = Owner.GetComponent<SenseComponent>());

	protected RustNavMeshAgent Agent => _agent ?? (_agent = Owner.GetComponent<RustNavMeshAgent>());

	protected RootMotionPlayer AnimPlayer => _animPlayer ?? (_animPlayer = Owner.GetComponent<RootMotionPlayer>());

	protected BlackboardComponent Blackboard => _blackboard ?? (_blackboard = Owner.GetComponent<BlackboardComponent>());

	public virtual EFSMStateStatus OnStateEnter(FSMPayload payload)
	{
		return EFSMStateStatus.None;
	}

	public virtual EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		return EFSMStateStatus.None;
	}

	public virtual void OnStateExit()
	{
	}

	protected T GetRootFSM<T>() where T : FSMComponent
	{
		return Owner.GetComponent<T>();
	}

	public virtual FSMStateBase Clone()
	{
		FSMStateBase obj = (FSMStateBase)MemberwiseClone();
		obj.transitions = new List<(FSMTransitionBase, FSMStateBase)>();
		obj.endTransitions = new List<(FSMTransitionBase, FSMStateBase, EFSMStateStatus)>();
		return obj;
	}

	public void FindAncestry(List<FSMStateBase> ancestry)
	{
		using (TimeWarning.New("FindAncestry"))
		{
			FindAncestryRecursive(ancestry);
		}
	}

	private void FindAncestryRecursive(List<FSMStateBase> ancestry)
	{
		parent?.FindAncestryRecursive(ancestry);
		ancestry.Add(this);
	}

	public FSMStateBase AddEndTransition(FSMStateBase dstState, FSMTransitionBase transition = null)
	{
		if (endTransitions == null)
		{
			endTransitions = new List<(FSMTransitionBase, FSMStateBase, EFSMStateStatus)>();
		}
		endTransitions.Add((transition, dstState, EFSMStateStatus.Success | EFSMStateStatus.Failure));
		return this;
	}

	public FSMStateBase AddFailureTransition(FSMStateBase dstState, FSMTransitionBase transition = null)
	{
		if (endTransitions == null)
		{
			endTransitions = new List<(FSMTransitionBase, FSMStateBase, EFSMStateStatus)>();
		}
		endTransitions.Add((transition, dstState, EFSMStateStatus.Failure));
		return this;
	}

	public FSMStateBase AddTickTransition(FSMStateBase dstState, FSMTransitionBase transition)
	{
		if (transitions == null)
		{
			transitions = new List<(FSMTransitionBase, FSMStateBase)>();
		}
		transitions.Add((transition, dstState));
		return this;
	}

	public FSMStateBase AddTickBranchingTrans(FSMStateBase dstState1, FSMTransitionBase sharedTransition, FSMStateBase dstState2, FSMTransitionBase dstState2Trans)
	{
		if (transitions == null)
		{
			transitions = new List<(FSMTransitionBase, FSMStateBase)>();
		}
		transitions.Add((new Trans_And { sharedTransition, dstState2Trans }, dstState2));
		transitions.Add((sharedTransition, dstState1));
		return this;
	}

	public FSMStateBase AddChild(FSMStateBase child)
	{
		child.parent = this;
		return child;
	}

	public FSMStateBase AddChildren(FSMStateBase child1, FSMStateBase child2 = null, FSMStateBase child3 = null, FSMStateBase child4 = null, FSMStateBase child5 = null, FSMStateBase child6 = null, FSMStateBase child7 = null, FSMStateBase child8 = null, FSMStateBase child9 = null, FSMStateBase child10 = null)
	{
		AddChild(child1);
		if (child2 != null)
		{
			AddChild(child2);
		}
		if (child3 != null)
		{
			AddChild(child3);
		}
		if (child4 != null)
		{
			AddChild(child4);
		}
		if (child5 != null)
		{
			AddChild(child5);
		}
		if (child6 != null)
		{
			AddChild(child6);
		}
		if (child7 != null)
		{
			AddChild(child7);
		}
		if (child8 != null)
		{
			AddChild(child8);
		}
		if (child9 != null)
		{
			AddChild(child9);
		}
		if (child10 != null)
		{
			AddChild(child10);
		}
		return this;
	}

	public static FSMStateBase operator +(FSMStateBase parent, FSMStateBase child)
	{
		parent.AddChild(child);
		return parent;
	}
}

