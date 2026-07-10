using System;

using AOCCH.Scanning;

namespace AOCCH.Automation;

public sealed class PotCycleSnapshot
{
    public DateTimeOffset LastUpdated { get; init; }
    public bool HasKnownAnchor { get; init; }
    public uint LastObservedPotFateId { get; init; }
    public string LastObservedPotFateName { get; init; } = string.Empty;
    public DateTimeOffset LastObservedSpawnAt { get; init; }
    public uint CurrentActivePotFateId { get; init; }
    public string CurrentActivePotFateName { get; init; } = string.Empty;
    public ActivePotFate? CurrentActivePotFate { get; init; }
    public uint PredictedNextPotFateId { get; init; }
    public string PredictedNextPotFateName { get; init; } = string.Empty;
    public DateTimeOffset PredictedNextSpawnAt { get; init; }

    public bool HasPredictedNextPot
        => PredictedNextPotFateId != 0
            && PredictedNextSpawnAt != DateTimeOffset.MinValue;
}
