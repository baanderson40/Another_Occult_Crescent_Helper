using System;

namespace AOCCH.Data;

public sealed class CofferPositionOverride
{
    public uint FateId { get; init; }
    public string GroupKey { get; init; } = string.Empty;
    public string CandidateKey { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public Vector3Data ObservedPosition { get; init; } = new();
    public uint ObservedDataId { get; init; }
    public DateTimeOffset LastConfirmedAt { get; init; }
}

public sealed class CofferPositionOverrideFile
{
    public System.Collections.Generic.List<CofferPositionOverride> Overrides { get; init; } = [];
}
