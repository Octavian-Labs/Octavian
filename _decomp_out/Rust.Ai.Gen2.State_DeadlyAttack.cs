using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_DeadlyAttack : State_Attack
{
	public float range = 4f;

	public SoundDefinition impactSound;

	private static readonly Vector3 force = new Vector3(15f, 3f, 15f);

	protected override void DoDamage()
	{
		if (!base.Senses.FindTarget(out var target) || !(target is BaseCombatEntity baseCombatEntity))
		{
			return;
		}
		float amount = Damage;
		if (target.IsNonNpcPlayer())
		{
			float num = baseCombatEntity.baseProtection.Get(DamageType);
			float num2 = (1f - num) * 1.2f;
			float num3 = Damage * num2;
			if (num == 0f && baseCombatEntity.health >= 30f && num3 > baseCombatEntity.health)
			{
				amount = (baseCombatEntity.health - 1f) * (1f / num2);
			}
		}
		baseCombatEntity.OnAttacked(amount, DamageType, Owner, ignoreShield: false);
		Owner.ClientRPC(RpcTarget.NetworkGroup("RPC_PlayNPCAttackImpactSound"));
		if (baseCombatEntity.ToNonNpcPlayer(out var player))
		{
			Vector3 vector = ((Vector3.Dot(Owner.transform.right, (player.transform.position - Owner.transform.position).NormalizeXZ()) > 0f) ? Owner.transform.right : (-Owner.transform.right));
			player.DoPush(Owner.transform.forward * force.z + vector * force.x + Vector3.up * force.y);
		}
	}
}

