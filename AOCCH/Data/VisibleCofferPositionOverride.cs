using System;

namespace AOCCH.Data;

public sealed class VisibleCofferPositionOverride
{
    public string TerritoryKey { get; init; } = string.Empty;
    public string Area { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public Vector3Data ObservedPosition { get; init; } = new();
    public uint ObservedDataId { get; init; }
    public string ObservedObjectName { get; init; } = string.Empty;
    public DateTimeOffset LastConfirmedAt { get; init; }
}

public sealed class VisibleCofferPositionOverrideFile
{
    public System.Collections.Generic.List<VisibleCofferPositionOverride> Overrides { get; init; } = [];
}
