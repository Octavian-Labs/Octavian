using Network;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class Crabs : CritterAnimal
{
	[ServerVar(Help = "Population active on the server, per square km", ShowInAdminUI = true)]
	public static float Population = 2f;

	[Header("Movement Audio")]
	public SoundDefinition movementLoopDef;

	[Tooltip("How many moving crabs near the listener get their own scuttle loop.")]
	public int maxMovementVoices = 3;

	[Tooltip("A crab has to be walking at least this much, as a fraction of its full walk blend, before it makes any noise.")]
	public float minMoveToggleForSound = 0.1f;

	[Tooltip("Loop pitch from a crab at a standstill to one running flat out.")]
	public Vector2 movementPitchRange = new Vector2(0.9f, 1.15f);

	public float movementFadeInTime = 0.2f;

	public float movementFadeOutTime = 0.3f;

	public override bool OnRpcMessage(BasePlayer player, uint rpc, Message msg)
	{
		using (TimeWarning.New("Crabs.OnRpcMessage"))
		{
		}
		return base.OnRpcMessage(player, rpc, msg);
	}
}

