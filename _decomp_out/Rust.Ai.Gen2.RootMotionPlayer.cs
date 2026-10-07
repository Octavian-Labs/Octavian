using System;
using ConVar;
using Development.Attributes;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

[SoftRequireComponent(typeof(RustNavMeshAgent))]
public class RootMotionPlayer : EntityComponent<BaseEntity>, IServerComponent
{
	public struct Warp
	{
		public float startTime;

		public float endTime;

		public Vector3 translationScale;

		public float rotationScale;

		public Warp(float startTime, float endTime, Vector3 translationScale, float rotationScale = 1f)
		{
			this.startTime = startTime;
			this.endTime = endTime;
			this.translationScale = translationScale;
			this.rotationScale = rotationScale;
		}
	}

	public class PlayServerState : Facepunch.Pool.IPooled
	{
		public AnimationClip animClip;

		public RootMotionData rmData;

		public float elapsedTime;

		public Vector3 initialLocation;

		public Quaternion initialRotation;

		public Action ServerTickAction;

		public Warp[] warps;

		public bool constrainToNavmesh;

		public Vector3 lastUnscaledOffset;

		public float lastUnscaledRotation;

		public bool isPlaying;

		public bool pauseAgent = true;

		public float playbackSpeed = 1f;

		public void Reset()
		{
			isPlaying = false;
			pauseAgent = true;
			playbackSpeed = 1f;
			rmData = null;
			animClip = null;
			initialRotation = Quaternion.identity;
			warps = null;
			constrainToNavmesh = true;
			elapsedTime = 0f;
			ServerTickAction = null;
			lastUnscaledOffset = Vector3.zero;
			lastUnscaledRotation = 0f;
		}

		void Facepunch.Pool.IPooled.EnterPool()
		{
			Reset();
		}

		void Facepunch.Pool.IPooled.LeavePool()
		{
			Reset();
		}

		[PoolAnalyzerGetWrapper]
		public static PlayServerState TakeFromPool(RootMotionData data, Transform transform)
		{
			PlayServerState playServerState = Facepunch.Pool.Get<PlayServerState>();
			playServerState.rmData = data;
			playServerState.initialLocation = transform.position;
			playServerState.initialRotation = transform.rotation;
			playServerState.SeedMotionAccumulators();
			return playServerState;
		}

		private void SeedMotionAccumulators()
		{
			if (!(rmData == null))
			{
				lastUnscaledOffset = new Vector3(Sample(rmData.xMotionCurve), Sample(rmData.yMotionCurve), Sample(rmData.zMotionCurve));
				lastUnscaledRotation = Sample(rmData.yRotationCurve);
			}
			static float Sample(AnimationCurve curve)
			{
				if (curve != null && curve.length != 0)
				{
					return curve.Evaluate(0f);
				}
				return 0f;
			}
		}

		public static PlayServerState TakeFromPool(AnimationClip data, Transform transform, float playbackSpeed = 1f, bool pauseAgent = true)
		{
			PlayServerState playServerState = Facepunch.Pool.Get<PlayServerState>();
			playServerState.animClip = data;
			playServerState.initialLocation = transform.position;
			playServerState.initialRotation = transform.rotation;
			playServerState.playbackSpeed = ((playbackSpeed > 0f) ? playbackSpeed : 1f);
			playServerState.pauseAgent = pauseAgent;
			return playServerState;
		}

		public int GetAnimHash()
		{
			if (rmData != null)
			{
				if (rmData.inPlaceAnimation == null)
				{
					Debug.LogError("RootMotionPlayer.PlayServer: rmData.inPlaceAnimation is null for " + rmData.name);
				}
				return Animator.StringToHash(rmData.inPlaceAnimation.name);
			}
			if (animClip == null)
			{
				Debug.LogError("RootMotionPlayer.PlayServer: animClip is null");
			}
			return Animator.StringToHash(animClip.name);
		}

		public float GetAnimLength()
		{
			if (!(rmData != null))
			{
				return animClip.length;
			}
			return rmData.inPlaceAnimation.length;
		}

		public bool Step(float deltaTime, ref Vector3 location, ref Quaternion rotation, float rootBoneLocalZOffset = 0f)
		{
			if (elapsedTime >= GetAnimLength())
			{
				return false;
			}
			deltaTime *= playbackSpeed;
			if (rmData == null)
			{
				elapsedTime += deltaTime;
				return elapsedTime < GetAnimLength() - 0.25f * playbackSpeed;
			}
			Vector3 zero = Vector3.zero;
			zero.x = rmData.xMotionCurve.Evaluate(elapsedTime);
			zero.y = (constrainToNavmesh ? 0f : rmData.yMotionCurve.Evaluate(elapsedTime));
			zero.z = rmData.zMotionCurve.Evaluate(elapsedTime);
			float num = rmData.yRotationCurve.Evaluate(elapsedTime);
			Vector3 vector = zero - lastUnscaledOffset;
			float num2 = num - lastUnscaledRotation;
			lastUnscaledOffset = zero;
			lastUnscaledRotation = num;
			if (warps != null)
			{
				Warp[] array = warps;
				for (int i = 0; i < array.Length; i++)
				{
					Warp warp = array[i];
					if (warp.startTime <= elapsedTime && elapsedTime <= warp.endTime)
					{
						vector.x *= warp.translationScale.x;
						vector.y *= warp.translationScale.y;
						vector.z *= warp.translationScale.z;
						num2 *= warp.rotationScale;
					}
				}
			}
			Vector3 vector2 = initialRotation * vector;
			location -= rotation * (Vector3.forward * (0f - rootBoneLocalZOffset));
			location += vector2;
			rotation *= Quaternion.Euler(0f, num2, 0f);
			location += rotation * (Vector3.forward * (0f - rootBoneLocalZOffset));
			elapsedTime += deltaTime;
			return elapsedTime < GetAnimLength() - 0.25f * playbackSpeed;
		}

		public Quaternion Track(Vector3 ownerPos, Vector3 targetPos, Quaternion rotation, float trackingSpeed, float deltaTime)
		{
			Vector3 forward = (targetPos - ownerPos).NormalizeXZ();
			initialRotation = Quaternion.RotateTowards(initialRotation, Quaternion.LookRotation(forward, Vector3.up), trackingSpeed * deltaTime);
			rotation *= Quaternion.Inverse(rotation) * initialRotation;
			return rotation;
		}
	}

	[SerializeField]
	private float rootBoneLocalZOffset;

	private RustNavMeshAgent _agent;

	private PlayServerState currentPlayState;

	private Action _playServerTickAction;

	private RustNavMeshAgent Agent => _agent ?? (_agent = base.baseEntity.GetComponent<RustNavMeshAgent>());

	private bool MovesOnNavmesh
	{
		get
		{
			if (base.baseEntity is BaseNPC2 baseNPC)
			{
				return baseNPC.MovesOnNavmesh;
			}
			return true;
		}
	}

	private Action PlayServerTickAction => PlayServerTick;

	public PlayServerState PlayServerAndTakeFromPool(RootMotionData data)
	{
		PlayServerState playServerState = PlayServerState.TakeFromPool(data, base.baseEntity.transform);
		PlayServer(playServerState);
		return playServerState;
	}

	public PlayServerState PlayServerAndTakeFromPool(AnimationClip data, float playbackSpeed = 1f, bool pauseAgent = true)
	{
		PlayServerState playServerState = PlayServerState.TakeFromPool(data, base.baseEntity.transform, playbackSpeed, pauseAgent);
		PlayServer(playServerState);
		return playServerState;
	}

	public void PlayServer(PlayServerState state)
	{
		if (state.rmData == null && state.animClip == null)
		{
			if (AI.logIssues)
			{
				Debug.LogError("RootMotionPlayer.PlayServer: state.rmData and state.animClip are both null");
			}
			return;
		}
		if (currentPlayState != null)
		{
			StopServer(currentPlayState);
		}
		currentPlayState = state;
		currentPlayState.isPlaying = true;
		base.baseEntity.ClientRPC(RpcTarget.NetworkGroup("CL_PlayMontageDelayed"), currentPlayState.GetAnimHash());
		if (currentPlayState.pauseAgent && MovesOnNavmesh)
		{
			Agent.Pause(this);
		}
		base.baseEntity.InvokeRepeating(PlayServerTickAction, 0f, 0f);
	}

	public void PlayServerAdditive(AnimationClip animClip)
	{
		base.baseEntity.ClientRPC(RpcTarget.NetworkGroup("CL_PlayAdditiveMontage"), Animator.StringToHash(animClip.name));
	}

	private void PlayServerTick()
	{
		using (TimeWarning.New("RootMotionPlayer:PlayServerTick"))
		{
			Vector3 location = base.baseEntity.transform.position;
			Quaternion rotation = base.baseEntity.transform.rotation;
			bool num = !currentPlayState.Step(UnityEngine.Time.deltaTime, ref location, ref rotation, rootBoneLocalZOffset);
			if (currentPlayState.rmData != null)
			{
				if (currentPlayState.constrainToNavmesh)
				{
					Agent.Move(Agent.WorldToNavSpace(location) - Agent.WorldToNavSpace(base.baseEntity.transform.position));
				}
				else
				{
					base.baseEntity.transform.position = location;
				}
				base.baseEntity.transform.rotation = rotation;
			}
			if (num)
			{
				StopServer(currentPlayState, interrupt: false);
				currentPlayState = null;
			}
		}
	}

	public void Track(Vector3 targetPos, float trackingSpeed = 45f, float? timeStep = null)
	{
		if (currentPlayState != null && !(currentPlayState.rmData == null))
		{
			if (!timeStep.HasValue)
			{
				timeStep = UnityEngine.Time.deltaTime;
			}
			base.baseEntity.transform.rotation = currentPlayState.Track(base.transform.position, targetPos, base.baseEntity.transform.rotation, trackingSpeed, timeStep.Value);
		}
	}

	private void StopServer(PlayServerState state, bool interrupt = true)
	{
		if (state != null && state.isPlaying)
		{
			state.isPlaying = false;
			if (state == currentPlayState)
			{
				base.baseEntity.CancelInvoke(PlayServerTickAction);
				Agent.Unpause(this);
				currentPlayState = null;
			}
			if (interrupt)
			{
				base.baseEntity.ClientRPC(RpcTarget.NetworkGroup("CL_StopMontage"));
			}
		}
	}

	public void StopServerAndReturnToPool(ref PlayServerState state, bool interrupt = true)
	{
		if (state != null)
		{
			StopServer(state, interrupt);
			Facepunch.Pool.Free(ref state);
		}
	}
}

