using System;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

public struct FSMPayload : IDisposable
{
	public Vector3? position;

	public Vector3? velocity;

	public BaseEntity entity;

	public HitInfo hitInfo;

	public void CopyFrom(FSMPayload other)
	{
		position = other.position;
		velocity = other.velocity;
		entity = other.entity;
		hitInfo = other.hitInfo;
	}

	public void Dispose()
	{
		if (hitInfo != null)
		{
			Pool.Free(ref hitInfo);
		}
	}
}

