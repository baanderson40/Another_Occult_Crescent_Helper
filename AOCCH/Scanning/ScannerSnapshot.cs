using System;
using System.Collections.Generic;
using System.Linq;

namespace AOCCH.Scanning;

public sealed class ScannerSnapshot
{
    public bool IsInSouthHorn { get; init; }
    public bool IsInCriticalEncounter { get; init; }
    public uint TerritoryTypeId { get; init; }
    public uint CurrentCriticalEncounterId { get; init; }
    public DateTimeOffset LastUpdated { get; init; }
    public IReadOnlyList<ActiveCriticalEncounter> CriticalEncounters { get; init; } = [];
    public IReadOnlyList<ActiveCriticalEncounter> UnknownCriticalEncounters { get; init; } = [];
    public IReadOnlyList<ActiveFate> Fates { get; init; } = [];
    public IReadOnlyList<ActivePotFate> PotFates { get; init; } = [];
    public ActiveCriticalEncounter? CurrentCriticalEncounter { get; init; }
    public ActiveCriticalEncounter? SelectedCriticalEncounter { get; init; }
    public ActiveFate? SelectedFate { get; init; }
    public ActivePotFate? ActivePotFate { get; init; }
    public bool HasTreasureBuff { get; init; }
    public float TreasureBuffRemainingSeconds { get; init; }
    public IReadOnlyList<VisibleCoffer> VisibleCoffers { get; init; } = [];
    public TargetSelection EffectiveTarget { get; init; } = TargetSelection.None;

    public ActiveCriticalEncounter? FindCriticalEncounter(uint id)
        => id == 0
            ? null
            : CriticalEncounters.FirstOrDefault(encounter => encounter.Id == id)
                ?? UnknownCriticalEncounters.FirstOrDefault(encounter => encounter.Id == id);

    public ActiveFate? FindFate(uint id)
        => id == 0
            ? null
            : Fates.FirstOrDefault(fate => fate.Id == id);

    public ActivePotFate? FindPotFate(uint id)
        => id == 0
            ? null
            : PotFates.FirstOrDefault(fate => fate.Id == id);
}
