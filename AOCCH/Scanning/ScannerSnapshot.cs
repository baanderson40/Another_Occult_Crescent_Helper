using System;
using System.Collections.Generic;
using System.Linq;

namespace AOCCH.Scanning;

public sealed class ScannerSnapshot
{
    public bool IsInSupportedTerritory { get; init; }
    public bool IsInCriticalEncounter { get; init; }
    public uint TerritoryTypeId { get; init; }
    public string TerritoryKey { get; init; } = string.Empty;
    public string TerritoryDisplayName { get; init; } = string.Empty;
    public bool CanFarmFates { get; init; }
    public bool CanFarmCriticalEncounters { get; init; }
    public bool CanRunVisibleCofferRoute { get; init; }
    public bool CanTrackPotCycle { get; init; }
    public bool CanRunPotTreasure { get; init; }
    public bool CanUseShopping { get; init; }
    public bool CanRunBuffRotation { get; init; }
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
    public IReadOnlyList<DetectedTreasure> DetectedTreasures { get; init; } = [];
    public IReadOnlyList<VisibleCoffer> VisibleCoffers { get; init; } = [];
    public int? PlayerForayLevel { get; init; }
    public IReadOnlyList<ForayThreatEntity> NearbyForayEntities { get; init; } = [];
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

    public FateRunTarget? FindFateRunTarget(uint id, bool isPotTarget)
        => isPotTarget
            ? FindPotFate(id)?.ToFateRunTarget()
            : FindFate(id)?.ToFateRunTarget();
}
