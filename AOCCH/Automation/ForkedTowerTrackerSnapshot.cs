using System;

namespace AOCCH.Automation;

public sealed class ForkedTowerTrackerSnapshot
{
    public DateTimeOffset LastUpdated { get; init; }
    public string TerritoryKey { get; init; } = string.Empty;
    public bool HasObservedTower { get; init; }
    public bool TowerActive { get; init; }
    public bool HasKnownBaseline { get; init; }
    public DateTimeOffset LastTowerCompletedAt { get; init; }
    public DateTimeOffset EstimatedNextTowerAt { get; init; }
    public int CriticalEncounterReductionCount { get; init; }
    public int FateReductionCount { get; init; }
    public int TotalReductionMinutes { get; init; }
    public string LastTransition { get; init; } = "Uncalibrated";
    public string LastCompletion { get; init; } = string.Empty;
}
