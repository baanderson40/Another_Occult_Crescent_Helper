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
        if (!scannerSnapshot.IsInSouthHorn)
        {
            logger.DebugThrottled("pot-cycle-outside-south-horn", WaitLogInterval, "Pot cycle tracker is idle because the player is outside South Horn.");
            return ClearCurrentActivePot(previous, now);
        }

        var activePotFate = scannerSnapshot.ActivePotFate;
        if (activePotFate == null)
        {
            logger.DebugThrottled("pot-cycle-no-active-pot", WaitLogInterval, previous.HasKnownAnchor
                ? $"Pot cycle tracker is waiting for the predicted pot window. nextPot={previous.PredictedNextPotFateName} nextSpawnAt={previous.PredictedNextSpawnAt:O}."
                : "Pot cycle tracker is waiting for the first observed pot anchor.");
            return ClearCurrentActivePot(previous, now);
        }

        logger.ResetThrottle("pot-cycle-outside-south-horn");
        logger.ResetThrottle("pot-cycle-no-active-pot");
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
        logger.Info($"[PotCycleTracker] op=anchor-observed pot=\"{activePotFate.Name}\" ({activePotFate.Id}) observedAt={now:O} nextPot=\"{nextPot?.Name ?? "unknown"}\" ({nextPot?.FateId ?? 0}) nextSpawnAt={(nextPot == null ? "none" : (now + PotCycleInterval).ToString("O"))}.");

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
