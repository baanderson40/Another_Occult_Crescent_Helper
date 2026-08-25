using System;
using System.Collections.Generic;
using System.Linq;
using AOCCH.Logging;
using AOCCH.Scanning;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class ForkedTowerTracker : IDisposable
{
    private const uint ForkedTowerCeId = 64;
    private static readonly TimeSpan TowerInterval = TimeSpan.FromMinutes(60);

    private readonly IFramework framework;
    private readonly OccultCrescentScanner scanner;
    private readonly AocchLogger logger;
    private readonly object gate = new();
    private readonly Dictionary<uint, ActiveCriticalEncounter> previousCriticalEncounters = [];
    private readonly Dictionary<uint, ActiveFate> previousFates = [];

    private ForkedTowerTrackerSnapshot snapshot = new();
    private DateTimeOffset lastProcessedScannerUpdate = DateTimeOffset.MinValue;
    private bool previousTowerActive;

    public ForkedTowerTracker(
        IFramework framework,
        OccultCrescentScanner scanner,
        AocchLogger logger)
    {
        this.framework = framework;
        this.scanner = scanner;
        this.logger = logger;
        framework.Update += OnFrameworkUpdate;
        logger.Info("[ForkedTowerTracker] op=init");
    }

    public ForkedTowerTrackerSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            snapshot = new ForkedTowerTrackerSnapshot
            {
                LastUpdated = DateTimeOffset.UtcNow,
                LastTransition = "Uncalibrated",
            };
            lastProcessedScannerUpdate = DateTimeOffset.MinValue;
            previousTowerActive = false;
            previousCriticalEncounters.Clear();
            previousFates.Clear();
        }

        logger.Info($"[ForkedTowerTracker] op=reset reason={reason}");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        logger.Info("[ForkedTowerTracker] op=stop");
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

    private ForkedTowerTrackerSnapshot BuildSnapshot(
        ScannerSnapshot scannerSnapshot,
        DateTimeOffset now,
        ForkedTowerTrackerSnapshot previous)
    {
        if (!string.Equals(scannerSnapshot.TerritoryKey, "northHorn", StringComparison.OrdinalIgnoreCase))
        {
            previousCriticalEncounters.Clear();
            previousFates.Clear();
            previousTowerActive = false;
            return new ForkedTowerTrackerSnapshot
            {
                LastUpdated = now,
                TerritoryKey = scannerSnapshot.TerritoryKey,
                LastTransition = "Outside North Horn",
            };
        }

        var towerActive = IsTowerActive(scannerSnapshot);
        var hasObservedTower = previous.HasObservedTower || towerActive;
        var hasKnownBaseline = previous.HasKnownBaseline;
        var lastTowerCompletedAt = previous.LastTowerCompletedAt;
        var estimatedNextTowerAt = previous.EstimatedNextTowerAt;
        var ceReductionCount = previous.CriticalEncounterReductionCount;
        var fateReductionCount = previous.FateReductionCount;
        var totalReductionMinutes = previous.TotalReductionMinutes;
        var lastTransition = previous.LastTransition;
        var lastCompletion = previous.LastCompletion;

        if (towerActive && !previousTowerActive)
        {
            lastTransition = "Forked Tower active";
            logger.Info("[ForkedTowerTracker] op=tower-active ceId=64");
        }

        if (!towerActive && previousTowerActive && hasObservedTower)
        {
            hasKnownBaseline = true;
            lastTowerCompletedAt = now;
            estimatedNextTowerAt = now + TowerInterval;
            ceReductionCount = 0;
            fateReductionCount = 0;
            totalReductionMinutes = 0;
            lastTransition = "Baseline established after Forked Tower ended";
            lastCompletion = string.Empty;
            logger.Info($"[ForkedTowerTracker] op=baseline-established completedAt={now:O} estimatedNext={estimatedNextTowerAt:O}");
        }
        else if (hasKnownBaseline && !towerActive)
        {
            foreach (var previousEncounter in previousCriticalEncounters.Values)
            {
                if (previousEncounter.Id != ForkedTowerCeId
                    && previousEncounter.IsBattle
                    && !scannerSnapshot.CriticalEncounters.Any(encounter => encounter.Id == previousEncounter.Id))
                {
                    estimatedNextTowerAt -= TimeSpan.FromMinutes(5);
                    ceReductionCount++;
                    totalReductionMinutes += 5;
                    lastCompletion = $"CE {previousEncounter.Name} ({previousEncounter.Id}) at {now:O}";
                    lastTransition = "CE completion reduced estimate";
                    logger.Info($"[ForkedTowerTracker] op=ce-completion id={previousEncounter.Id} name=\"{previousEncounter.Name}\" reductionMinutes=5 estimatedNext={estimatedNextTowerAt:O}");
                }
            }

            foreach (var previousFate in previousFates.Values)
            {
                if (previousFate.Progress >= 100
                    && !scannerSnapshot.Fates.Any(fate => fate.Id == previousFate.Id))
                {
                    estimatedNextTowerAt -= TimeSpan.FromMinutes(1);
                    fateReductionCount++;
                    totalReductionMinutes++;
                    lastCompletion = $"FATE {previousFate.Name} ({previousFate.Id}) at {now:O}";
                    lastTransition = "FATE completion reduced estimate";
                    logger.Info($"[ForkedTowerTracker] op=fate-completion id={previousFate.Id} name=\"{previousFate.Name}\" reductionMinutes=1 estimatedNext={estimatedNextTowerAt:O}");
                }
            }
        }

        previousCriticalEncounters.Clear();
        foreach (var encounter in scannerSnapshot.CriticalEncounters.Concat(scannerSnapshot.UnknownCriticalEncounters))
        {
            previousCriticalEncounters[encounter.Id] = encounter;
        }

        previousFates.Clear();
        foreach (var fate in scannerSnapshot.Fates)
        {
            previousFates[fate.Id] = fate;
        }

        previousTowerActive = towerActive;
        return new ForkedTowerTrackerSnapshot
        {
            LastUpdated = now,
            TerritoryKey = scannerSnapshot.TerritoryKey,
            HasObservedTower = hasObservedTower,
            TowerActive = towerActive,
            HasKnownBaseline = hasKnownBaseline,
            LastTowerCompletedAt = lastTowerCompletedAt,
            EstimatedNextTowerAt = estimatedNextTowerAt,
            CriticalEncounterReductionCount = ceReductionCount,
            FateReductionCount = fateReductionCount,
            TotalReductionMinutes = totalReductionMinutes,
            LastTransition = lastTransition,
            LastCompletion = lastCompletion,
        };
    }

    private static bool IsTowerActive(ScannerSnapshot scannerSnapshot)
        => scannerSnapshot.CurrentCriticalEncounterId == ForkedTowerCeId
            || scannerSnapshot.CriticalEncounters.Any(encounter => encounter.Id == ForkedTowerCeId);
}
