using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public class Trans_TargetInRange : FSMTransitionBase
{
	[SerializeField]
	public float Range = 4f;

	[SerializeField]
	public float TimeToPredict;

	[Tooltip("Point on the NPC to measure from, in its local space, or zero for its origin. A long animal measures its attack range from out in front of itself rather than from its middle.")]
	[SerializeField]
	public Vector3 Offset = Vector3.zero;

	[Tooltip("How far above or below us the target may be, or zero for no bound. A range measured in three dimensions counts a storey of height as ordinary reach, so an attack that cannot swing upwards needs this as well as a range.")]
	[SerializeField]
	public float MaxHeightDifference;

	protected override bool EvaluateInternal(ref FSMPayload payload)
	{
		using (TimeWarning.New("Trans_TargetInRange"))
		{
			if (!base.Senses.FindTarget(out var target))
			{
				return false;
			}
			Vector3 position = target.transform.position;
			if (TimeToPredict > 0f && target.ToNonNpcPlayer(out var player))
			{
				Vector3 inferedVelocity = player.inferedVelocity;
				inferedVelocity = Vector3.ProjectOnPlane(inferedVelocity, Owner.transform.right);
				position += inferedVelocity * TimeToPredict;
			}
			Vector3 position2 = Owner.transform.position;
			if (Offset != Vector3.zero)
			{
				position2 += Owner.transform.TransformDirection(Offset);
			}
			if (MaxHeightDifference > 0f && Mathf.Abs(position.y - position2.y) > MaxHeightDifference)
			{
				return false;
			}
			return Vector3.Distance(position, position2) <= Range;
		}
	}

	public override string GetName()
	{
		string text = ((MaxHeightDifference > 0f) ? $" +-{MaxHeightDifference}m" : "");
		return string.Format("{0} {1}{2}m{3}", base.GetName(), Inverted ? ">=" : "<", Range, text);
	}
}

