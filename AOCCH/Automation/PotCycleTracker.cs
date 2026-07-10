using System;
using System.Collections.Generic;
using System.Linq;

using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Scanning;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class PotCycleTracker : IDisposable
{
    private static readonly TimeSpan PotCycleInterval = TimeSpan.FromMinutes(30);

    private readonly IFramework framework;
    private readonly OccultCrescentScanner scanner;
    private readonly AocchLogger logger;
    private readonly Dictionary<uint, PotFateData> potFatesById;
    private readonly object gate = new();

    private PotCycleSnapshot snapshot = new();
    private DateTimeOffset lastProcessedScannerUpdate = DateTimeOffset.MinValue;

    public PotCycleTracker(
        IFramework framework,
        OccultCrescentScanner scanner,
        OccultCrescentData data,
        AocchLogger logger)
    {
        this.framework = framework;
        this.scanner = scanner;
        this.logger = logger;
        potFatesById = data.PotFates.ToDictionary(potFate => potFate.FateId);

        framework.Update += OnFrameworkUpdate;
        logger.Info("Pot cycle tracker initialized.");
    }

    public PotCycleSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public bool HasKnownAnchor
        => Snapshot.HasKnownAnchor;

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        logger.Info("Pot cycle tracker stopped.");
    }

    public void Reset(string reason)
    {
        lock (gate)
        {
            snapshot = new PotCycleSnapshot
            {
                LastUpdated = DateTimeOffset.UtcNow,
            };
            lastProcessedScannerUpdate = DateTimeOffset.MinValue;
        }

        logger.Info($"Pot cycle tracker reset: {reason}");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var scannerSnapshot = scanner.Snapshot;
        if (scannerSnapshot.LastUpdated == DateTimeOffset.MinValue)
        {
            return;
        }

        lock (gate)
        {
            if (scannerSnapshot.LastUpdated <= lastProcessedScannerUpdate)
            {
                return;
            }

            lastProcessedScannerUpdate = scannerSnapshot.LastUpdated;
            snapshot = BuildSnapshot(scannerSnapshot, scannerSnapshot.LastUpdated, snapshot);
        }
    }

    private PotCycleSnapshot BuildSnapshot(ScannerSnapshot scannerSnapshot, DateTimeOffset now, PotCycleSnapshot previous)
    {
        if (!scannerSnapshot.IsInSouthHorn)
        {
            return ClearCurrentActivePot(previous, now);
        }

        var activePotFate = scannerSnapshot.ActivePotFate;
        if (activePotFate == null)
        {
            return ClearCurrentActivePot(previous, now);
        }

        if (previous.CurrentActivePotFateId == activePotFate.Id)
        {
            return new PotCycleSnapshot
            {
                LastUpdated = now,
                HasKnownAnchor = previous.HasKnownAnchor,
                LastObservedPotFateId = previous.LastObservedPotFateId,
                LastObservedPotFateName = previous.LastObservedPotFateName,
                LastObservedSpawnAt = previous.LastObservedSpawnAt,
                CurrentActivePotFateId = activePotFate.Id,
                CurrentActivePotFateName = activePotFate.Name,
                CurrentActivePotFate = activePotFate,
                PredictedNextPotFateId = previous.PredictedNextPotFateId,
                PredictedNextPotFateName = previous.PredictedNextPotFateName,
                PredictedNextSpawnAt = previous.PredictedNextSpawnAt,
            };
        }

        var nextPot = GetOppositePotFate(activePotFate.Id);
        logger.Info($"Pot anchor observed: {activePotFate.Name} ({activePotFate.Id}) at {now:O}.");

        return new PotCycleSnapshot
        {
            LastUpdated = now,
            HasKnownAnchor = true,
            LastObservedPotFateId = activePotFate.Id,
            LastObservedPotFateName = activePotFate.Name,
            LastObservedSpawnAt = now,
            CurrentActivePotFateId = activePotFate.Id,
            CurrentActivePotFateName = activePotFate.Name,
            CurrentActivePotFate = activePotFate,
            PredictedNextPotFateId = nextPot?.FateId ?? 0,
            PredictedNextPotFateName = nextPot?.Name ?? string.Empty,
            PredictedNextSpawnAt = nextPot == null ? DateTimeOffset.MinValue : now + PotCycleInterval,
        };
    }

    private PotCycleSnapshot ClearCurrentActivePot(PotCycleSnapshot previous, DateTimeOffset now)
        => new()
        {
            LastUpdated = now,
            HasKnownAnchor = previous.HasKnownAnchor,
            LastObservedPotFateId = previous.LastObservedPotFateId,
            LastObservedPotFateName = previous.LastObservedPotFateName,
            LastObservedSpawnAt = previous.LastObservedSpawnAt,
            PredictedNextPotFateId = previous.PredictedNextPotFateId,
            PredictedNextPotFateName = previous.PredictedNextPotFateName,
            PredictedNextSpawnAt = previous.PredictedNextSpawnAt,
        };

    private PotFateData? GetOppositePotFate(uint fateId)
        => potFatesById.Values.FirstOrDefault(potFate => potFate.FateId != fateId);
}
