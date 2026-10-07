using ConVar;
using Prefabs.Misc;
using UnityEngine;

namespace Rust.Ai.Gen2;

[SoftRequireComponent(typeof(Scientist2FSM))]
public class ScientistNPC2 : BaseNPC2
{
	public bool canBeHeadshot = true;

	public override bool IsAnimal => false;

	public override Vector3 ServerNavMeshPos
	{
		get
		{
			Vector3 serverWorldPosition = ServerWorldPosition;
			return WorldToNavMeshSpace.MultiplyPoint(serverWorldPosition);
		}
		set
		{
			ServerWorldPosition = NavMeshToWorldSpace.MultiplyPoint(value);
		}
	}

	public override Matrix4x4 WorldToNavMeshSpace
	{
		get
		{
			RustNavMeshAgent component;
			if (AI.useUnityNavmesh)
			{
				GhostShip ghostShip = GetParentEntity() as GhostShip;
				if ((bool)ghostShip)
				{
					return ghostShip.WorldToNavMeshSpace;
				}
			}
			else if (TryGetComponent<RustNavMeshAgent>(out component) && component.HasValidIndependantNavmesh)
			{
				return component.independantNavmesh.WorldToNavMatrix;
			}
			return base.WorldToNavMeshSpace;
		}
	}

	public override Matrix4x4 NavMeshToWorldSpace
	{
		get
		{
			RustNavMeshAgent component;
			if (AI.useUnityNavmesh)
			{
				GhostShip ghostShip = GetParentEntity() as GhostShip;
				if ((bool)ghostShip)
				{
					return ghostShip.NavMeshToWorldSpace;
				}
			}
			else if (TryGetComponent<RustNavMeshAgent>(out component) && component.HasValidIndependantNavmesh)
			{
				return component.independantNavmesh.NavToWorldMatrix;
			}
			return base.NavMeshToWorldSpace;
		}
	}

	public override string Categorize()
	{
		return "Scientist2";
	}

	public override void OnAttacked(HitInfo info)
	{
		base.OnAttacked(info);
	}

	public override void ScaleDamage(HitInfo info)
	{
		base.ScaleDamage(info);
		if (canBeHeadshot && (bool)info.damageProperties)
		{
			info.damageProperties.ScaleDamage(info);
		}
	}
}

