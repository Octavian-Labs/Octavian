using Facepunch;

namespace Rust.Ai.Gen2;

public class Trans_Triggerable_HitInfo : Trans_Triggerable
{
	public virtual void Trigger(HitInfo originalHitInfo)
	{
		if (originalHitInfo == null)
		{
			Trigger(default(FSMPayload));
			return;
		}
		HitInfo hitInfo = Pool.Get<HitInfo>();
		hitInfo.CopyFrom(originalHitInfo);
		Trigger(new FSMPayload
		{
			hitInfo = hitInfo
		});
	}
}

