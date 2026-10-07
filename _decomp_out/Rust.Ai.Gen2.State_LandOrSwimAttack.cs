using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_LandOrSwimAttack : State_Attack
{
	public RootMotionData swimAttack;

	protected override RootMotionData GetAnimation()
	{
		if (WaterLevel.GetWaterDepth(Owner.transform.position, waves: false, volumes: false) > 0f)
		{
			return swimAttack;
		}
		return Animation;
	}
}

