using System;
using System.Linq;
using UnityEngine;

namespace Rust.Ai.Gen2;

public abstract class FSMTransitionBase
{
	[SerializeField]
	public bool Inverted;

	[NonSerialized]
	public BaseEntity Owner;

	private SenseComponent _senses;

	private RustNavMeshAgent _agent;

	protected SenseComponent Senses => _senses ?? (_senses = Owner.GetComponent<SenseComponent>());

	protected RustNavMeshAgent Agent => _agent ?? (_agent = Owner.GetComponent<RustNavMeshAgent>());

	public virtual void Init(BaseEntity owner)
	{
		Owner = owner;
	}

	public virtual void OnStateEnter()
	{
	}

	public virtual void OnStateExit()
	{
	}

	public virtual void OnTransitionTaken(FSMStateBase from, FSMStateBase to)
	{
	}

	public virtual void OnTransitionConfirmed(FSMStateBase entered)
	{
	}

	public bool Evaluate(ref FSMPayload payload)
	{
		if (!Inverted)
		{
			return EvaluateInternal(ref payload);
		}
		return !EvaluateInternal(ref payload);
	}

	protected virtual bool EvaluateInternal(ref FSMPayload payload)
	{
		return false;
	}

	public virtual string GetName()
	{
		return (Inverted ? "!" : "") + GetGenericTypeName(GetType());
	}

	protected static string GetGenericTypeName(Type type)
	{
		if (type.IsGenericType)
		{
			string name = type.Name;
			return (name[..name.IndexOf('`')] + "<" + string.Join(", ", type.GetGenericArguments().Select(GetGenericTypeName)) + ">").Replace("Trans_", "");
		}
		return type.Name.Replace("Trans_", "");
	}

	public virtual FSMTransitionBase Clone()
	{
		return (FSMTransitionBase)MemberwiseClone();
	}

	public static Trans_And operator &(FSMTransitionBase lhs, FSMTransitionBase rhs)
	{
		return new Trans_And { lhs, rhs };
	}

	public static Trans_Or operator |(FSMTransitionBase lhs, FSMTransitionBase rhs)
	{
		return new Trans_Or { lhs, rhs };
	}

	public static FSMTransitionBase operator ~(FSMTransitionBase instance)
	{
		instance.Inverted = !instance.Inverted;
		return instance;
	}
}

