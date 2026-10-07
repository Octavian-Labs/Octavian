using System;

namespace Rust.Ai.Gen2.Nav;

[Flags]
public enum DtStatus : uint
{
	Failure = 0x80000000u,
	Success = 0x40000000u,
	InProgress = 0x20000000u,
	StatusDetailMask = 0xFFFFFFu,
	WrongMagic = 1u,
	WrongVersion = 2u,
	OutOfMemory = 4u,
	InvalidParam = 8u,
	BufferTooSmall = 0x10u,
	OutOfNodes = 0x20u,
	PartialResult = 0x40u,
	AlreadyOccupied = 0x80u,
	IterationLimit = 0x100u
}

