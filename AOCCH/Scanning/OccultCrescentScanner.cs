using System;
using System.Collections.Generic;
using System.Linq;
using AOCCH.Data;
using AOCCH.Logging;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace AOCCH.Scanning;

public sealed class OccultCrescentScanner : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(500);

    private readonly IClientState clientState;
    private readonly IFateTable fateTable;
    private readonly IFramework framework;
    private readonly OccultCrescentData data;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private ScannerSnapshot snapshot = new()
    {
        LastUpdated = DateTimeOffset.MinValue,
    };

    private DateTimeOffset lastScanAt = DateTimeOffset.MinValue;
    private bool? lastSouthHornState;

    public OccultCrescentScanner(
        IClientState clientState,
        IFateTable fateTable,
        IFramework framework,
        OccultCrescentData data,
        AocchLogger logger)
    {
        this.clientState = clientState;
        this.fateTable = fateTable;
        this.framework = framework;
        this.data = data;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
        clientState.TerritoryChanged += OnTerritoryChanged;

        logger.Info("Occult Crescent scanner initialized in read-only mode.");
        RefreshSnapshot(force: true);
    }

    public ScannerSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        logger.Info("Occult Crescent scanner stopped.");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        RefreshSnapshot(force: false);
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        var isInSouthHorn = territoryType == data.TerritoryTypeId;
        logger.Info(isInSouthHorn
            ? $"Scanner detected entry into South Horn (territory {territoryType})."
            : $"Scanner detected territory change to {territoryType}; South Horn scanner is idle.");

        RefreshSnapshot(force: true);
    }

    private void RefreshSnapshot(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - lastScanAt < ScanInterval)
        {
            return;
        }

        lastScanAt = now;

        try
        {
            var territoryTypeId = clientState.TerritoryType;
            var isInSouthHorn = territoryTypeId == data.TerritoryTypeId;

            if (lastSouthHornState != isInSouthHorn)
            {
                lastSouthHornState = isInSouthHorn;
                logger.Debug(isInSouthHorn
                    ? "South Horn scanner is active."
                    : "South Horn scanner is waiting for the player to enter the zone.");
            }

            var criticalEncounters = new List<ActiveCriticalEncounter>();
            var unknownCriticalEncounters = new List<ActiveCriticalEncounter>();
            var fates = new List<ActiveFate>();

            if (isInSouthHorn)
            {
                ScanCriticalEncounters(criticalEncounters, unknownCriticalEncounters);
                ScanFates(fates);
            }

            var nextSnapshot = new ScannerSnapshot
            {
                IsInSouthHorn = isInSouthHorn,
                TerritoryTypeId = territoryTypeId,
                LastUpdated = now,
                CriticalEncounters = criticalEncounters,
                UnknownCriticalEncounters = unknownCriticalEncounters,
                Fates = fates,
            };

            lock (gate)
            {
                snapshot = nextSnapshot;
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Scanner update failed: {ex}");
        }
    }

    private unsafe void ScanCriticalEncounters(
        List<ActiveCriticalEncounter> criticalEncounters,
        List<ActiveCriticalEncounter> unknownCriticalEncounters)
    {
        var instance = PublicContentOccultCrescent.GetInstance();
        if (instance == null)
        {
            return;
        }

        foreach (var dynamicEvent in instance->DynamicEventContainer.Events.ToArray())
        {
            if (dynamicEvent.State == DynamicEventState.Inactive)
            {
                continue;
            }

            var metadata = data.CriticalEncounters.FirstOrDefault(encounter => encounter.Id == dynamicEvent.DynamicEventId);
            var activeEncounter = new ActiveCriticalEncounter
            {
                Id = dynamicEvent.DynamicEventId,
                Name = dynamicEvent.Name.ToString(),
                State = dynamicEvent.State.ToString(),
                Progress = dynamicEvent.Progress,
                StartTimestamp = dynamicEvent.StartTimestamp,
                HasKnownMetadata = metadata != null,
                PreferredAethernet = metadata?.PreferredAethernet ?? string.Empty,
                Priority = metadata?.Priority ?? 0,
            };

            if (metadata == null)
            {
                unknownCriticalEncounters.Add(activeEncounter);
                continue;
            }

            criticalEncounters.Add(activeEncounter);
        }

        criticalEncounters.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        unknownCriticalEncounters.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
    }

    private void ScanFates(List<ActiveFate> fates)
    {
        foreach (var fate in fateTable)
        {
            if (fate == null)
            {
                continue;
            }

            var metadata = data.Fates.FirstOrDefault(knownFate => knownFate.Id == fate.FateId);
            fates.Add(new ActiveFate
            {
                Id = fate.FateId,
                Name = fate.Name.ToString(),
                Progress = fate.Progress,
                Radius = fate.Radius,
                Position = fate.Position,
                HasKnownMetadata = metadata != null,
                Demiatma = metadata?.Demiatma ?? string.Empty,
                Note = metadata?.Note ?? string.Empty,
                PreferredAethernet = metadata?.Aethernet ?? string.Empty,
            });
        }

        fates.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
    }
}
