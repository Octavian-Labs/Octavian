using FIMSpace.FLook;
using FIMSpace.FSpine;
using FIMSpace.FTail;
using Network;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class NPCAnimController : EntityComponent<BaseEntity>, IClientComponent
{
	public enum AnimatorType
	{
		NoStrafe,
		Strafe
	}

	[SerializeField]
	private string animationsPrefix = "wolf_";

	[SerializeField]
	private string[] animationBlacklist = new string[4] { "prowl", "walk", "trot", "run" };

	[ClientVar(ClientAdmin = true)]
	public static float lookInterpSpeed = 3f;

	[ClientVar(ClientAdmin = true)]
	public static float spineInterpSpeed = 3f;

	[SerializeField]
	private AnimatorType animatorType;

	[SerializeField]
	private Animator animator;

	[SerializeField]
	private FSpineAnimator spineAnimator;

	[SerializeField]
	private FLookAnimator lookAnimator;

	[SerializeField]
	private TailAnimator2 tailAnimator;

	[SerializeField]
	private float maxPitchToConformToSlope = 30f;

	[SerializeField]
	private bool onlyConformPitchToSlope = true;

	[SerializeField]
	private float posInterpSpeed = 10f;

	[SerializeField]
	private float rotInterpSpeed = 2f;

	[SerializeField]
	private float snapVerticalOffset;

	[SerializeField]
	public bool enableLookAtDuringLocomotion = true;

	[SerializeField]
	public bool enableLookAtDuringProwl = true;

	[SerializeField]
	public bool canSwim;

	[SerializeField]
	public bool forceDisableGroundSnap;

	[Tooltip("Animator state holding the settled sleeping pose, for an animal already asleep the first time we see it. Without it a newborn calf stands up and plays its own lie down.")]
	[SerializeField]
	private string sleepingPoseState = "sleeping";

	[Tooltip("Animator state holding the settled lying pose, for an animal already resting.")]
	[SerializeField]
	private string restingPoseState = "cow_lay_idle";

	[Tooltip("Animator state holding the settled lying pose of a pregnant animal.")]
	[SerializeField]
	private string pregnantPoseState = "cow_lay_idle_moo";

	[SerializeField]
	private AnimationClip[] animationsWithLookAt;

	[SerializeField]
	private AnimationClip[] animationsWithSpineDeform;

	[SerializeField]
	private float walkSpeedMultiplier = 1f;

	public string AnimationsPrefix => animationsPrefix;

	public string[] AnimationBlacklist => animationBlacklist;

	public override bool OnRpcMessage(BasePlayer player, uint rpc, Message msg)
	{
		using (TimeWarning.New("NPCAnimController.OnRpcMessage"))
		{
		}
		return base.OnRpcMessage(player, rpc, msg);
	}
}

