using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentData data;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private ScannerSnapshot snapshot = new()
    {
        LastUpdated = DateTimeOffset.MinValue,
    };

    private DateTimeOffset lastScanAt = DateTimeOffset.MinValue;
    private bool? lastSouthHornState;
    private string lastSelectionKey = string.Empty;

    public OccultCrescentScanner(
        IClientState clientState,
        IFateTable fateTable,
        IFramework framework,
        IObjectTable objectTable,
        OccultCrescentData data,
        Configuration configuration,
        AocchLogger logger)
    {
        this.clientState = clientState;
        this.fateTable = fateTable;
        this.framework = framework;
        this.objectTable = objectTable;
        this.data = data;
        this.configuration = configuration;
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
            ActiveCriticalEncounter? selectedCriticalEncounter = null;
            ActiveFate? selectedFate = null;
            var effectiveTarget = TargetSelection.None;

            if (isInSouthHorn)
            {
                ScanCriticalEncounters(criticalEncounters, unknownCriticalEncounters);
                ScanFates(fates);
                selectedCriticalEncounter = SelectCriticalEncounter(criticalEncounters);
                selectedFate = SelectFate(fates);
                effectiveTarget = SelectEffectiveTarget(selectedCriticalEncounter, selectedFate);
            }

            var nextSnapshot = new ScannerSnapshot
            {
                IsInSouthHorn = isInSouthHorn,
                TerritoryTypeId = territoryTypeId,
                LastUpdated = now,
                CriticalEncounters = criticalEncounters,
                UnknownCriticalEncounters = unknownCriticalEncounters,
                Fates = fates,
                SelectedCriticalEncounter = selectedCriticalEncounter,
                SelectedFate = selectedFate,
                EffectiveTarget = effectiveTarget,
            };

            LogTargetSelectionIfChanged(nextSnapshot);

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
            var stateCode = (int)dynamicEvent.State;
            var isCandidate = metadata != null && IsPreBattleCeState(stateCode);
            var activeEncounter = new ActiveCriticalEncounter
            {
                Id = dynamicEvent.DynamicEventId,
                Name = dynamicEvent.Name.ToString(),
                State = dynamicEvent.State.ToString(),
                StateCode = stateCode,
                Progress = dynamicEvent.Progress,
                StartTimestamp = dynamicEvent.StartTimestamp,
                HasKnownMetadata = metadata != null,
                PreferredAethernet = metadata?.PreferredAethernet ?? string.Empty,
                Priority = metadata?.Priority ?? 0,
                EngageRadius = metadata?.EngageRadius ?? 0,
                StagingPoint = metadata?.StagingPoint.ToVector3() ?? default,
                IsCandidate = isCandidate,
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
        var excludedFates = ParseExcludedFates();
        var playerPosition = objectTable.LocalPlayer?.Position;

        foreach (var fate in fateTable)
        {
            if (fate == null)
            {
                continue;
            }

            var state = fate.State;
            var stateCode = (int)state;
            var stateText = state.ToString();
            if (!IsActiveFateState(stateText))
            {
                continue;
            }

            var metadata = data.Fates.FirstOrDefault(knownFate => knownFate.Id == fate.FateId);
            var name = fate.Name.ToString();
            var isExcluded = excludedFates.Contains(name);
            var distanceToPlayer = playerPosition.HasValue
                ? CalculateFlatDistance(playerPosition.Value, fate.Position)
                : float.MaxValue;

            fates.Add(new ActiveFate
            {
                Id = fate.FateId,
                Name = name,
                State = stateText,
                StateCode = stateCode,
                Progress = fate.Progress,
                Radius = fate.Radius,
                Position = fate.Position,
                DistanceToPlayer = distanceToPlayer,
                HasKnownMetadata = metadata != null,
                Demiatma = metadata?.Demiatma ?? string.Empty,
                Note = metadata?.Note ?? string.Empty,
                PreferredAethernet = metadata?.Aethernet ?? string.Empty,
                IsExcluded = isExcluded,
                IsCandidate = !isExcluded,
            });
        }

        fates.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
    }

    private ActiveCriticalEncounter? SelectCriticalEncounter(IReadOnlyList<ActiveCriticalEncounter> criticalEncounters)
        => criticalEncounters
            .Where(encounter => encounter.IsCandidate)
            .OrderByDescending(encounter => encounter.Priority)
            .ThenBy(encounter => encounter.StartTimestamp <= 0 ? long.MaxValue : encounter.StartTimestamp)
            .ThenBy(encounter => encounter.Name, StringComparer.Ordinal)
            .ThenBy(encounter => encounter.Id)
            .FirstOrDefault();

    private ActiveFate? SelectFate(IReadOnlyList<ActiveFate> fates)
    {
        var candidates = fates.Where(fate => fate.IsCandidate);

        return configuration.FatePriority == FatePriority.Nearest
            ? candidates
                .OrderBy(fate => fate.DistanceToPlayer)
                .ThenBy(fate => fate.Progress)
                .ThenBy(fate => fate.Name, StringComparer.Ordinal)
                .ThenBy(fate => fate.Id)
                .FirstOrDefault()
            : candidates
                .OrderBy(fate => fate.Progress)
                .ThenBy(fate => fate.DistanceToPlayer)
                .ThenBy(fate => fate.Name, StringComparer.Ordinal)
                .ThenBy(fate => fate.Id)
                .FirstOrDefault();
    }

    private TargetSelection SelectEffectiveTarget(ActiveCriticalEncounter? selectedCriticalEncounter, ActiveFate? selectedFate)
    {
        return configuration.FarmingMode switch
        {
            FarmingMode.CeOnly when selectedCriticalEncounter != null => new TargetSelection
            {
                Kind = SelectedTargetKind.CriticalEncounter,
                CriticalEncounter = selectedCriticalEncounter,
                Reason = "CE priority",
            },
            FarmingMode.FateOnly when selectedFate != null => new TargetSelection
            {
                Kind = SelectedTargetKind.Fate,
                Fate = selectedFate,
                Reason = configuration.FatePriority == FatePriority.Nearest ? "Nearest FATE" : "Lowest FATE progress",
            },
            FarmingMode.CeAndFate when selectedCriticalEncounter != null && configuration.PrioritizeCe => new TargetSelection
            {
                Kind = SelectedTargetKind.CriticalEncounter,
                CriticalEncounter = selectedCriticalEncounter,
                Reason = selectedFate != null ? "CE preempted FATE" : "CE priority",
                WouldPreemptFate = selectedFate != null,
            },
            FarmingMode.CeAndFate when selectedCriticalEncounter != null && selectedFate == null => new TargetSelection
            {
                Kind = SelectedTargetKind.CriticalEncounter,
                CriticalEncounter = selectedCriticalEncounter,
                Reason = "CE priority",
            },
            FarmingMode.CeAndFate when selectedFate != null => new TargetSelection
            {
                Kind = SelectedTargetKind.Fate,
                Fate = selectedFate,
                Reason = configuration.FatePriority == FatePriority.Nearest ? "Nearest FATE" : "Lowest FATE progress",
            },
            _ => TargetSelection.None,
        };
    }

    private void LogTargetSelectionIfChanged(ScannerSnapshot snapshot)
    {
        var key = BuildSelectionKey(snapshot.EffectiveTarget);
        if (key == lastSelectionKey)
        {
            return;
        }

        lastSelectionKey = key;
        switch (snapshot.EffectiveTarget.Kind)
        {
            case SelectedTargetKind.CriticalEncounter:
                var selectedCe = snapshot.EffectiveTarget.CriticalEncounter;
                if (selectedCe != null)
                {
                    logger.Info(
                        $"Selected target CE: {selectedCe.Name} ({selectedCe.Id}) " +
                        $"priority={selectedCe.Priority} reason={snapshot.EffectiveTarget.Reason} preemptFate={snapshot.EffectiveTarget.WouldPreemptFate}.");
                }

                break;
            case SelectedTargetKind.Fate:
                var selectedFate = snapshot.EffectiveTarget.Fate;
                if (selectedFate != null)
                {
                    logger.Info(
                        $"Selected target FATE: {selectedFate.Name} ({selectedFate.Id}) " +
                        $"progress={selectedFate.Progress}% distance={selectedFate.DistanceToPlayer:0.0} reason={snapshot.EffectiveTarget.Reason}.");
                }

                break;
            default:
                logger.Info("No eligible CE/FATE target selected.");
                break;
        }
    }

    private static bool IsPreBattleCeState(int stateCode)
        => stateCode > 0 && stateCode < 3;

    private static bool IsActiveFateState(string state)
        => !string.Equals(state, "Ended", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase);

    private HashSet<string> ParseExcludedFates()
        => configuration.ExcludedFates
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private static string BuildSelectionKey(TargetSelection selection)
        => selection.Kind switch
        {
            SelectedTargetKind.CriticalEncounter => $"ce:{selection.CriticalEncounter?.Id}:{selection.Reason}:{selection.WouldPreemptFate}",
            SelectedTargetKind.Fate => $"fate:{selection.Fate?.Id}:{selection.Reason}:{selection.WouldPreemptFate}",
            _ => "none",
        };
}
