using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Rust.Ai.Gen2.Nav;

public readonly struct NavVector3 : IEquatable<NavVector3>
{
	public readonly Vector3 Value;

	public static readonly NavVector3 zero = new NavVector3(Vector3.zero);

	public static readonly NavVector3 up = new NavVector3(Vector3.up);

	public float x => Value.x;

	public float y => Value.y;

	public float z => Value.z;

	public NavVector3 normalized => new NavVector3(Value.normalized);

	public float magnitude => Value.magnitude;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public NavVector3(Vector3 positionNS)
	{
		Value = positionNS;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public NavVector3(float x, float y, float z)
	{
		Value = new Vector3(x, y, z);
	}

	public NavVector3 WithY(float newY)
	{
		return new NavVector3(Value.x, newY, Value.z);
	}

	public NavVector3 Flat()
	{
		return new NavVector3(Value.x, 0f, Value.z);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static NavVector3 operator +(NavVector3 a, NavVector3 b)
	{
		return new NavVector3(a.Value + b.Value);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static NavVector3 operator -(NavVector3 a, NavVector3 b)
	{
		return new NavVector3(a.Value - b.Value);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static NavVector3 operator *(NavVector3 positionNS, float scale)
	{
		return new NavVector3(positionNS.Value * scale);
	}

	public static NavVector3 operator *(Quaternion q, NavVector3 positionNS)
	{
		return new NavVector3(q * positionNS.Value);
	}

	public static NavVector3 operator /(NavVector3 positionNS, float scale)
	{
		return new NavVector3(positionNS.Value / scale);
	}

	public static bool operator ==(NavVector3 aNS, NavVector3 bNS)
	{
		return aNS.Value == bNS.Value;
	}

	public static bool operator !=(NavVector3 aNS, NavVector3 bNS)
	{
		return aNS.Value != bNS.Value;
	}

	public static explicit operator Vector3(NavVector3 positionNS)
	{
		return positionNS.Value;
	}

	public static Vector3 LookDirection(NavVector3 fromNS, NavVector3 toNS)
	{
		return (toNS.Value - fromNS.Value).normalized;
	}

	public static float Dot(NavVector3 aNS, NavVector3 bNS)
	{
		return Vector3.Dot(aNS.Value, bNS.Value);
	}

	public static NavVector3 Cross(NavVector3 aNS, NavVector3 bNS)
	{
		return new NavVector3(Vector3.Cross(aNS.Value, bNS.Value));
	}

	public static float Distance(NavVector3 aNS, NavVector3 bNS)
	{
		return Vector3.Distance(aNS.Value, bNS.Value);
	}

	public static float DistanceXZ(NavVector3 aNS, NavVector3 bNS)
	{
		return Vector3.Distance(aNS.Flat().Value, bNS.Flat().Value);
	}

	public NavVector3 NormalizeXZ()
	{
		return new NavVector3(new Vector3(Value.x, 0f, Value.z).normalized);
	}

	public static float SqrDistance(NavVector3 aNS, NavVector3 bNS)
	{
		return (aNS.Value - bNS.Value).sqrMagnitude;
	}

	public static NavVector3 Lerp(NavVector3 aNS, NavVector3 bNS, float t)
	{
		return new NavVector3(Vector3.Lerp(aNS.Value, bNS.Value, t));
	}

	public static NavVector3 MoveTowards(NavVector3 currentNS, NavVector3 targetNS, float maxDistanceDelta)
	{
		return new NavVector3(Vector3.MoveTowards(currentNS.Value, targetNS.Value, maxDistanceDelta));
	}

	public static NavVector3 RotateTowards(NavVector3 currentNS, NavVector3 targetNS, float maxRadiansDelta, float maxMagnitudeDelta)
	{
		return new NavVector3(Vector3.RotateTowards(currentNS.Value, targetNS.Value, maxRadiansDelta, maxMagnitudeDelta));
	}

	public static NavVector3 ClampMagnitude(NavVector3 vectorNS, float maxLength)
	{
		return new NavVector3(Vector3.ClampMagnitude(vectorNS.Value, maxLength));
	}

	public bool Equals(NavVector3 other)
	{
		return Value.Equals(other.Value);
	}

	public override bool Equals(object obj)
	{
		if (obj is NavVector3 other)
		{
			return Equals(other);
		}
		return false;
	}

	public override int GetHashCode()
	{
		return Value.GetHashCode();
	}

	public override string ToString()
	{
		return $"NS{Value}";
	}
}

