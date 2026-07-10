using System;

namespace AOCCH.Scanning;

public sealed class PotAnchorObservation
{
    public uint FateId { get; init; }
    public string FateName { get; init; } = string.Empty;
    public DateTimeOffset ObservedAt { get; init; }
}
