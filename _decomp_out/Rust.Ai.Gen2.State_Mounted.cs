using System;

namespace Rust.Ai.Gen2;

[Serializable]
public class State_Mounted : FSMStateBase
{
	public override EFSMStateStatus OnStateUpdate(float deltaTime)
	{
		if (!(Owner is BaseNPC2 { IsMounted: false }))
		{
			return EFSMStateStatus.None;
		}
		return EFSMStateStatus.Success;
	}
}

