using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

[Serializable]
public struct Cover : IEquatable<Cover>
{
	[Flags]
	public enum Peeks
	{
		None = 0,
		Left = 1,
		Right = 2,
		Up = 4,
		Sides = 3,
		All = 7
	}

	public Vector3 position;

	public float yaw;

	public Peeks peeks;

	public const float sidePeekLength = 1f;

	private const int ShotTestLayer = 1486954497;

	public bool NeedDucking => (peeks & Peeks.Up) == Peeks.Up;

	public Cover(Vector3 position, float yaw, Peeks peeks)
	{
		this.position = position;
		this.yaw = yaw;
		this.peeks = peeks;
	}

	public bool ProtectsFrom(Vector3 threatLocation)
	{
		Vector3 vector = threatLocation - position;
		return Mathf.Abs(Mathf.DeltaAngle(Mathf.Atan2(vector.x, vector.z) * 57.29578f, yaw)) <= 50f;
	}

	public Peeks GetAnyPeek()
	{
		if ((peeks & Peeks.Left) == Peeks.Left)
		{
			return Peeks.Left;
		}
		if ((peeks & Peeks.Right) == Peeks.Right)
		{
			return Peeks.Right;
		}
		if ((peeks & Peeks.Up) == Peeks.Up)
		{
			return Peeks.Up;
		}
		return Peeks.None;
	}

	public Peeks GetFirstUnoccludedPeek(Vector3 target, BaseEntity entity = null)
	{
		if ((peeks & Peeks.Left) == Peeks.Left && !IsPeekOccluded(Peeks.Left, target, entity))
		{
			return Peeks.Left;
		}
		if ((peeks & Peeks.Right) == Peeks.Right && !IsPeekOccluded(Peeks.Right, target, entity))
		{
			return Peeks.Right;
		}
		if ((peeks & Peeks.Up) == Peeks.Up && !IsPeekOccluded(Peeks.Up, target, entity))
		{
			return Peeks.Up;
		}
		return Peeks.None;
	}

	public bool IsPeekOccluded(Peeks peek, Vector3 target, BaseEntity entity = null)
	{
		using (TimeWarning.New("Cover.IsPeekOccluded"))
		{
			Vector3 peekLocation = GetPeekLocation(peek);
			Vector3 vector = target - peekLocation;
			float magnitude = vector.magnitude;
			Vector3 direction = vector / magnitude;
			RaycastHit hitInfo;
			return GamePhysics.Trace(new Ray(peekLocation, direction), 0f, out hitInfo, magnitude, 1486954497, QueryTriggerInteraction.Ignore);
		}
	}

	public Vector3 GetPeekLocation(Peeks peek)
	{
		Vector3 forward = GetForward();
		Vector3 vector = new Vector3(forward.z, 0f, 0f - forward.x);
		Vector3 result = position + PlayerEyes.EyeOffset;
		if ((peek & Peeks.Right) == Peeks.Right)
		{
			result += vector * 1f;
		}
		if ((peek & Peeks.Left) == Peeks.Left)
		{
			result -= vector * 1f;
		}
		return result;
	}

	public Vector3 GetPeekGroundLocation(Peeks peek)
	{
		return GetPeekLocation(peek) - PlayerEyes.EyeOffset;
	}

	public Vector3 GetForward()
	{
		return new Vector3(Mathf.Sin(yaw * (MathF.PI / 180f)), 0f, Mathf.Cos(yaw * (MathF.PI / 180f)));
	}

	public bool Equals(Cover other)
	{
		return position == other.position;
	}

	public override bool Equals(object obj)
	{
		if (obj is Cover other)
		{
			return Equals(other);
		}
		return false;
	}

	public override int GetHashCode()
	{
		return position.GetHashCode();
	}

	public static bool operator ==(Cover left, Cover right)
	{
		return left.Equals(right);
	}

	public static bool operator !=(Cover left, Cover right)
	{
		return !(left == right);
	}
}

