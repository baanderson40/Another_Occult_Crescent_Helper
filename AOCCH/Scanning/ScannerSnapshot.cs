using System;
using System.Collections.Generic;

namespace AOCCH.Scanning;

public sealed class ScannerSnapshot
{
    public bool IsInSouthHorn { get; init; }
    public uint TerritoryTypeId { get; init; }
    public DateTimeOffset LastUpdated { get; init; }
    public IReadOnlyList<ActiveCriticalEncounter> CriticalEncounters { get; init; } = [];
    public IReadOnlyList<ActiveCriticalEncounter> UnknownCriticalEncounters { get; init; } = [];
    public IReadOnlyList<ActiveFate> Fates { get; init; } = [];
}
