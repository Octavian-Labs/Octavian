using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

public readonly struct NpcNoiseEvent : IEquatable<NpcNoiseEvent>
{
	public readonly int Id;

	public readonly BaseEntity Initiator;

	public readonly Vector3 NoisePosition;

	public readonly Vector3 GuessedInitiatorPosition;

	public readonly NpcNoiseIntensity Intensity;

	public readonly double EventTime;

	public NpcNoiseEvent(int id, BaseEntity initiator, Vector3 position, Vector3 initiatorPosition, NpcNoiseIntensity intensity, double eventTime)
	{
		Id = id;
		Initiator = initiator;
		NoisePosition = position;
		GuessedInitiatorPosition = initiatorPosition;
		Intensity = intensity;
		EventTime = eventTime;
	}

	public bool Equals(NpcNoiseEvent other)
	{
		if (Id == other.Id)
		{
			double eventTime = EventTime;
			if (eventTime.Equals(other.EventTime) && Initiator == other.Initiator && Intensity == other.Intensity && NoisePosition.Equals(other.NoisePosition))
			{
				return GuessedInitiatorPosition.Equals(other.GuessedInitiatorPosition);
			}
		}
		return false;
	}

	public override int GetHashCode()
	{
		return Id;
	}

	public override bool Equals(object obj)
	{
		if (obj is NpcNoiseEvent other)
		{
			return Equals(other);
		}
		return false;
	}

	public static bool operator ==(NpcNoiseEvent left, NpcNoiseEvent right)
	{
		return left.Equals(right);
	}

	public static bool operator !=(NpcNoiseEvent left, NpcNoiseEvent right)
	{
		return !left.Equals(right);
	}
}

