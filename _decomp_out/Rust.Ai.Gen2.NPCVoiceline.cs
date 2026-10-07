using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public struct NPCVoiceline
{
	public ENPCVoicelineCategory category;

	public string text;

	public AudioClip audioClip;

	public ENpcVoicelineImportance importance;

	public bool otherNpcShouldSpeakFirst;

	public bool allowOkResponse;

	public int index;

	public float duration;
}

