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
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);

    private readonly IFramework framework;
    private readonly OccultCrescentScanner scanner;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private PotCycleSnapshot snapshot = new();
    private DateTimeOffset lastProcessedScannerUpdate = DateTimeOffset.MinValue;

    public PotCycleTracker(
        IFramework framework,
        OccultCrescentScanner scanner,
        AocchLogger logger)
    {
        this.framework = framework;
        this.scanner = scanner;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
        logger.Info("[PotCycleTracker] op=init");
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
        logger.Info("[PotCycleTracker] op=stop");
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

        logger.Info($"[PotCycleTracker] op=reset reason={reason}");
    }

    public void ResetInstanceState(string reason)
        => Reset(reason);

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
        if (!scannerSnapshot.IsInSupportedTerritory || !scannerSnapshot.CanTrackPotCycle)
        {
            return ClearCurrentActivePot(previous, now, scannerSnapshot);
        }

        var activePotFate = scannerSnapshot.ActivePotFate;
        if (activePotFate == null)
        {
            logger.VerboseThrottled("pot-cycle-no-active-pot", WaitLogInterval, previous.HasKnownAnchor
                ? $"Pot cycle tracker is waiting for the predicted pot window. nextPot={previous.PredictedNextPotFateName} nextSpawnAt={previous.PredictedNextSpawnAt:O}."
                : "Pot cycle tracker is waiting for the first observed pot anchor.");
            return ClearCurrentActivePot(previous, now, scannerSnapshot);
        }

        logger.ResetThrottle("pot-cycle-no-active-pot");
        var sameTerritory = string.Equals(previous.TerritoryKey, scannerSnapshot.TerritoryKey, StringComparison.OrdinalIgnoreCase)
            && previous.TerritoryTypeId == scannerSnapshot.TerritoryTypeId;
        if (sameTerritory && previous.CurrentActivePotFateId == activePotFate.Id)
        {
            return new PotCycleSnapshot
            {
                LastUpdated = now,
                TerritoryKey = scannerSnapshot.TerritoryKey,
                TerritoryTypeId = scannerSnapshot.TerritoryTypeId,
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
        logger.Info($"[PotCycleTracker] op=anchor-observed pot=\"{activePotFate.Name}\" ({activePotFate.Id}) observedAt={now:O} nextPot=\"{nextPot?.Name ?? "unknown"}\" ({nextPot?.FateId ?? 0}) nextSpawnAt={(nextPot == null ? "none" : (now + PotCycleInterval).ToString("O"))}.");

        return new PotCycleSnapshot
        {
            LastUpdated = now,
            TerritoryKey = scannerSnapshot.TerritoryKey,
            TerritoryTypeId = scannerSnapshot.TerritoryTypeId,
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

    private static PotCycleSnapshot ClearCurrentActivePot(PotCycleSnapshot previous, DateTimeOffset now, ScannerSnapshot scannerSnapshot)
    {
        var sameTerritory = string.Equals(previous.TerritoryKey, scannerSnapshot.TerritoryKey, StringComparison.OrdinalIgnoreCase)
            && previous.TerritoryTypeId == scannerSnapshot.TerritoryTypeId;
        return new PotCycleSnapshot
        {
            LastUpdated = now,
            TerritoryKey = scannerSnapshot.TerritoryKey,
            TerritoryTypeId = scannerSnapshot.TerritoryTypeId,
            HasKnownAnchor = sameTerritory && previous.HasKnownAnchor,
            LastObservedPotFateId = sameTerritory ? previous.LastObservedPotFateId : 0,
            LastObservedPotFateName = sameTerritory ? previous.LastObservedPotFateName : string.Empty,
            LastObservedSpawnAt = sameTerritory ? previous.LastObservedSpawnAt : DateTimeOffset.MinValue,
            PredictedNextPotFateId = sameTerritory ? previous.PredictedNextPotFateId : 0,
            PredictedNextPotFateName = sameTerritory ? previous.PredictedNextPotFateName : string.Empty,
            PredictedNextSpawnAt = sameTerritory ? previous.PredictedNextSpawnAt : DateTimeOffset.MinValue,
        };
    }

    private PotFateData? GetOppositePotFate(uint fateId)
        => scanner.ActiveTerritoryData?.PotFates.FirstOrDefault(potFate => potFate.FateId != fateId);
}
