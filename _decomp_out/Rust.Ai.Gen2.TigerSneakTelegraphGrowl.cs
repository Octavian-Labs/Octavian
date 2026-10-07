using Network;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class TigerSneakTelegraphGrowl : EntityComponent<BaseEntity>
{
	[SerializeField]
	private SoundDefinition growlSound;

	[ServerVar(Help = "Minimum angle for the tiger to growl when stalking a player")]
	public static float minAngle = 60f;

	[ServerVar(Help = "Time between growls when stalking a player")]
	public static float minTimeBetweenGrowls = 5f;

	private static readonly float[] growlDistances = new float[3] { 50f, 30f, 20f };

	private SenseComponent _senses;

	private double? lastGrowlTime;

	private BasePlayer targetPlayer;

	private int numGrowlsForCurrentPlayer;

	private SenseComponent Senses => _senses ?? (_senses = GetComponent<SenseComponent>());

	public override bool OnRpcMessage(BasePlayer player, uint rpc, Message msg)
	{
		using (TimeWarning.New("TigerSneakTelegraphGrowl.OnRpcMessage"))
		{
		}
		return base.OnRpcMessage(player, rpc, msg);
	}

	public override void InitShared()
	{
		base.InitShared();
		if (base.baseEntity.isServer)
		{
			InvokeRepeating(Tick, 0f, 0.5f);
		}
	}

	public override void DestroyShared()
	{
		base.DestroyShared();
		if (base.baseEntity.isServer)
		{
			CancelInvoke(Tick);
			UpdateTarget(null);
		}
	}

	private void Tick()
	{
		Senses.FindTarget(out var target);
		UpdateTarget(target);
		if (!(targetPlayer == null) && (!lastGrowlTime.HasValue || !((double)Time.time - lastGrowlTime.Value < (double)minTimeBetweenGrowls)) && Senses.GetVisibilityStatus(target, out var status) && numGrowlsForCurrentPlayer < growlDistances.Length)
		{
			float num = growlDistances[numGrowlsForCurrentPlayer];
			if (!(Vector3.Distance(target.transform.position, base.transform.position) > num) && !(status.timeNotWatched <= 0f) && !Trans_IsInTargetViewCone.IsInTargetViewCone(Senses, minAngle))
			{
				base.baseEntity.ClientRPC(RpcTarget.NetworkGroup("CL_PlayTigerSneakTelegraphGrowl"));
				lastGrowlTime = Time.timeAsDouble;
				numGrowlsForCurrentPlayer++;
			}
		}
	}

	private void UpdateTarget(BaseEntity newTarget)
	{
		BasePlayer basePlayer = targetPlayer;
		targetPlayer = newTarget as BasePlayer;
		if (basePlayer != targetPlayer)
		{
			if (basePlayer.IsValid())
			{
				basePlayer.ClientRPC(RpcTarget.Player("CL_AddPredatorHuntingPlayer", basePlayer), arg1: false);
			}
			if (targetPlayer.IsValid())
			{
				targetPlayer.ClientRPC(RpcTarget.Player("CL_AddPredatorHuntingPlayer", targetPlayer), arg1: true);
			}
			lastGrowlTime = null;
			numGrowlsForCurrentPlayer = 0;
		}
	}
}

