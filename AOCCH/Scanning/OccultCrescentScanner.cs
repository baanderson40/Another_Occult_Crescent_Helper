using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using AOCCH.Data;
using AOCCH.Logging;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace AOCCH.Scanning;

public sealed class OccultCrescentScanner : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(500);
    private const float VisibleCofferDiagnosticRadius = 60f;
    private static readonly TimeSpan VisibleCofferDiagnosticLogInterval = TimeSpan.FromSeconds(5);
    private const uint TreasureBuffStatusId = 1531;

    private readonly IClientState clientState;
    private readonly IFateTable fateTable;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentData data;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly HashSet<uint> potFateIds;
    private readonly Dictionary<uint, PotFateData> potFatesById;
    private readonly object gate = new();

    private ScannerSnapshot snapshot = new()
    {
        LastUpdated = DateTimeOffset.MinValue,
    };

    private DateTimeOffset lastScanAt = DateTimeOffset.MinValue;
    private bool? lastSouthHornState;
    private string lastSelectionKey = string.Empty;
    private bool? lastTreasureBuffState;
    private bool pendingForceRefresh = true;

    public OccultCrescentScanner(
        IClientState clientState,
        IFateTable fateTable,
        IFramework framework,
        IObjectTable objectTable,
        OccultCrescentData data,
        Configuration configuration,
        CofferNameResolver cofferNameResolver,
        AocchLogger logger)
    {
        this.clientState = clientState;
        this.fateTable = fateTable;
        this.framework = framework;
        this.objectTable = objectTable;
        this.data = data;
        this.configuration = configuration;
        this.logger = logger;
        potFateIds = data.PotFates.Select(potFate => potFate.FateId).ToHashSet();
        potFatesById = data.PotFates.ToDictionary(potFate => potFate.FateId);

        framework.Update += OnFrameworkUpdate;
        clientState.TerritoryChanged += OnTerritoryChanged;

        logger.Info("Occult Crescent scanner initialized in read-only mode.");
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
        var force = pendingForceRefresh;
        pendingForceRefresh = false;
        RefreshSnapshot(force);
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        var isInSouthHorn = territoryType == data.TerritoryTypeId;
        logger.Info(isInSouthHorn
            ? $"Scanner detected entry into South Horn (territory {territoryType})."
            : $"Scanner detected territory change to {territoryType}; South Horn scanner is idle.");

        pendingForceRefresh = true;
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
            var potFates = new List<ActivePotFate>();
            var visibleCoffers = new List<VisibleCoffer>();
            uint currentCriticalEncounterId = 0;
            ActiveCriticalEncounter? currentCriticalEncounter = null;
            ActiveCriticalEncounter? selectedCriticalEncounter = null;
            ActiveFate? selectedFate = null;
            ActivePotFate? activePotFate = null;
            var hasTreasureBuff = false;
            var treasureBuffRemainingSeconds = 0f;
            var effectiveTarget = TargetSelection.None;

            if (isInSouthHorn)
            {
                currentCriticalEncounterId = ScanCriticalEncounters(criticalEncounters, unknownCriticalEncounters);
                ScanFates(fates, potFates, out activePotFate);
                currentCriticalEncounter = criticalEncounters.FirstOrDefault(encounter => encounter.Id == currentCriticalEncounterId)
                    ?? unknownCriticalEncounters.FirstOrDefault(encounter => encounter.Id == currentCriticalEncounterId);
                selectedCriticalEncounter = SelectCriticalEncounter(criticalEncounters);
                selectedFate = SelectFate(fates);
                effectiveTarget = SelectEffectiveTarget(selectedCriticalEncounter, selectedFate);
                ScanTreasureBuff(out hasTreasureBuff, out treasureBuffRemainingSeconds);
                ScanVisibleCoffers(visibleCoffers);
            }
            else
            {
                TrackTreasureBuffState(false);
            }

            var nextSnapshot = new ScannerSnapshot
            {
                IsInSouthHorn = isInSouthHorn,
                IsInCriticalEncounter = currentCriticalEncounterId != 0,
                TerritoryTypeId = territoryTypeId,
                CurrentCriticalEncounterId = currentCriticalEncounterId,
                LastUpdated = now,
                CriticalEncounters = criticalEncounters,
                UnknownCriticalEncounters = unknownCriticalEncounters,
                Fates = fates,
                PotFates = potFates,
                CurrentCriticalEncounter = currentCriticalEncounter,
                SelectedCriticalEncounter = selectedCriticalEncounter,
                SelectedFate = selectedFate,
                ActivePotFate = activePotFate,
                HasTreasureBuff = hasTreasureBuff,
                TreasureBuffRemainingSeconds = treasureBuffRemainingSeconds,
                VisibleCoffers = visibleCoffers,
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

    private unsafe uint ScanCriticalEncounters(
        List<ActiveCriticalEncounter> criticalEncounters,
        List<ActiveCriticalEncounter> unknownCriticalEncounters)
    {
        var instance = PublicContentOccultCrescent.GetInstance();
        if (instance == null)
        {
            return 0;
        }

        var currentCriticalEncounterId = instance->DynamicEventContainer.CurrentEventId;

        foreach (var dynamicEvent in instance->DynamicEventContainer.Events.ToArray())
        {
            if (dynamicEvent.State == DynamicEventState.Inactive)
            {
                continue;
            }

            var metadata = data.CriticalEncounters.FirstOrDefault(encounter => encounter.Id == dynamicEvent.DynamicEventId);
            var stateCode = (int)dynamicEvent.State;
            var isCandidate = metadata != null
                && configuration.EnableCriticalEngagementFarming
                && configuration.IsCriticalEncounterEnabled(dynamicEvent.DynamicEventId)
                && IsPreBattleCeState(stateCode);
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
        return currentCriticalEncounterId;
    }

    private void ScanFates(
        List<ActiveFate> fates,
        List<ActivePotFate> potFates,
        out ActivePotFate? activePotFate)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        var joinedFateId = GetJoinedFateId();
        activePotFate = null;

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
            var (fateLocation, locationSource) = ResolveFateLocation(fate, metadata);
            var isPotFate = IsPotFate(fate.FateId);
            var isExcluded = metadata == null
                || isPotFate
                || !configuration.EnableFateFarming
                || !configuration.IsFateEnabled(fate.FateId);

            switch (locationSource)
            {
                case "metadata_start_position":
                    logger.InfoThrottled(
                        $"fate-location-fallback-{fate.FateId}",
                        TimeSpan.FromSeconds(30),
                        $"Using metadata start position for FATE {name} ({fate.FateId}): {FormatVector3(fateLocation)}.");
                    break;
                case "zero_unresolved":
                    logger.WarningThrottled(
                        $"fate-location-unresolved-{fate.FateId}",
                        TimeSpan.FromSeconds(10),
                        $"FATE {name} ({fate.FateId}) has no usable live or metadata position.");
                    break;
            }

            var distanceToPlayer = playerPosition.HasValue
                ? CalculateFlatDistance(playerPosition.Value, fateLocation)
                : float.MaxValue;

            if (isPotFate)
            {
                potFatesById.TryGetValue(fate.FateId, out var potMetadata);
                var activePot = new ActivePotFate
                {
                    Id = fate.FateId,
                    Name = name,
                    State = stateText,
                    StateCode = stateCode,
                    IsInFate = joinedFateId != 0 && joinedFateId == fate.FateId,
                    Progress = fate.Progress,
                    Radius = fate.Radius,
                    Position = fateLocation,
                    DistanceToPlayer = distanceToPlayer,
                    PreferredAethernet = potMetadata?.PreferredAethernet ?? string.Empty,
                    CenterPosition = potMetadata?.CenterPosition.ToVector3() ?? fateLocation,
                    StagingPosition = potMetadata?.StagingPosition?.ToVector3(),
                };

                potFates.Add(activePot);
                if (activePotFate == null)
                {
                    activePotFate = activePot;
                }

                continue;
            }

            fates.Add(new ActiveFate
            {
                Id = fate.FateId,
                Name = name,
                State = stateText,
                StateCode = stateCode,
                IsInFate = joinedFateId != 0 && joinedFateId == fate.FateId,
                Progress = fate.Progress,
                Radius = fate.Radius,
                Position = fateLocation,
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
        potFates.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));

        if (activePotFate != null)
        {
            return;
        }
    }

    private void ScanTreasureBuff(out bool hasTreasureBuff, out float remainingTime)
    {
        hasTreasureBuff = false;
        remainingTime = 0f;

        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            TrackTreasureBuffState(false);
            return;
        }

        foreach (var status in player.StatusList)
        {
            if (status.StatusId != TreasureBuffStatusId)
            {
                continue;
            }

            hasTreasureBuff = true;
            remainingTime = status.RemainingTime;
            break;
        }

        TrackTreasureBuffState(hasTreasureBuff);
    }

    private void ScanVisibleCoffers(List<VisibleCoffer> visibleCoffers)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        var nearbyTreasureObjects = new List<string>();

        foreach (var gameObject in objectTable)
        {
            if (gameObject is not IGameObject objectEntry)
            {
                continue;
            }

            var objectKind = objectEntry.ObjectKind.ToString();
            var isTreasureKind = objectKind.StartsWith("Treasure", StringComparison.OrdinalIgnoreCase);
            if (isTreasureKind)
            {
                var treasureDistanceToPlayer = playerPosition.HasValue
                    ? CalculateFlatDistance(playerPosition.Value, objectEntry.Position)
                    : float.MaxValue;
                if (treasureDistanceToPlayer <= VisibleCofferDiagnosticRadius)
                {
                    nearbyTreasureObjects.Add(
                        $"name='{objectEntry.Name}' kind={objectKind} baseId={objectEntry.BaseId} objectId={objectEntry.GameObjectId:X} distance={treasureDistanceToPlayer:0.0}y targetable={objectEntry.IsTargetable} valid={objectEntry.IsValid()}");
                }
            }

            if (!IsVisibleCofferObject(objectEntry))
            {
                continue;
            }

            var distanceToPlayer = playerPosition.HasValue
                ? CalculateFlatDistance(playerPosition.Value, objectEntry.Position)
                : float.MaxValue;

            visibleCoffers.Add(new VisibleCoffer
            {
                GameObjectId = objectEntry.GameObjectId,
                DataId = objectEntry.BaseId,
                Name = objectEntry.Name.ToString(),
                Position = objectEntry.Position,
                DistanceToPlayer = distanceToPlayer,
            });
        }

        visibleCoffers.Sort((left, right) => left.DistanceToPlayer.CompareTo(right.DistanceToPlayer));

        if (visibleCoffers.Count == 0 && nearbyTreasureObjects.Count > 0)
        {
            logger.DebugThrottled(
                "visible-coffer-diagnostics",
                VisibleCofferDiagnosticLogInterval,
                $"Visible coffer scan found no recognized coffers, but nearby treasure-kind objects were present: {string.Join(" | ", nearbyTreasureObjects)}");
        }
    }

    private bool IsVisibleCofferObject(IGameObject gameObject)
    {
        var objectKind = gameObject.ObjectKind.ToString();
        return objectKind.StartsWith("Treasure", StringComparison.OrdinalIgnoreCase)
            && gameObject.IsTargetable
            && gameObject.IsValid();
    }

    private void TrackTreasureBuffState(bool hasTreasureBuff)
    {
        if (lastTreasureBuffState == hasTreasureBuff)
        {
            return;
        }

        if (!lastTreasureBuffState.HasValue)
        {
            lastTreasureBuffState = hasTreasureBuff;
            return;
        }

        lastTreasureBuffState = hasTreasureBuff;
        logger.Info(hasTreasureBuff
            ? "Treasure buff detected: Cache Me If You Can (1531)."
            : "Treasure buff cleared: Cache Me If You Can (1531).");
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
        if (!configuration.EnableCriticalEngagementFarming && !configuration.EnableFateFarming)
        {
            return TargetSelection.None;
        }

        if (configuration.EnableCriticalEngagementFarming && !configuration.EnableFateFarming && selectedCriticalEncounter != null)
        {
            return new TargetSelection
            {
                Kind = SelectedTargetKind.CriticalEncounter,
                CriticalEncounter = selectedCriticalEncounter,
                Reason = "CE priority",
            };
        }

        if (!configuration.EnableCriticalEngagementFarming && configuration.EnableFateFarming && selectedFate != null)
        {
            return new TargetSelection
            {
                Kind = SelectedTargetKind.Fate,
                Fate = selectedFate,
                Reason = configuration.FatePriority == FatePriority.Nearest ? "Nearest FATE" : "Lowest FATE progress",
            };
        }

        if (configuration.EnableCriticalEngagementFarming && configuration.EnableFateFarming && selectedCriticalEncounter != null && configuration.PrioritizeCe)
        {
            return new TargetSelection
            {
                Kind = SelectedTargetKind.CriticalEncounter,
                CriticalEncounter = selectedCriticalEncounter,
                Reason = selectedFate != null ? "CE preempted FATE" : "CE priority",
                WouldPreemptFate = selectedFate != null,
            };
        }

        if (configuration.EnableCriticalEngagementFarming && selectedCriticalEncounter != null && selectedFate == null)
        {
            return new TargetSelection
            {
                Kind = SelectedTargetKind.CriticalEncounter,
                CriticalEncounter = selectedCriticalEncounter,
                Reason = "CE priority",
            };
        }

        if (configuration.EnableFateFarming && selectedFate != null)
        {
            return new TargetSelection
            {
                Kind = SelectedTargetKind.Fate,
                Fate = selectedFate,
                Reason = configuration.FatePriority == FatePriority.Nearest ? "Nearest FATE" : "Lowest FATE progress",
            };
        }

        return TargetSelection.None;
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

    private static (Vector3 Position, string Source) ResolveFateLocation(object fate, FateData? metadata)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public;

        var locationProperty = fate.GetType().GetProperty("Location", Flags);
        if (locationProperty?.PropertyType == typeof(Vector3)
            && locationProperty.GetValue(fate) is Vector3 location)
        {
            if (!IsZeroVector(location))
            {
                return (location, "location");
            }
        }

        var positionProperty = fate.GetType().GetProperty("Position", Flags);
        if (positionProperty?.PropertyType == typeof(Vector3)
            && positionProperty.GetValue(fate) is Vector3 position)
        {
            if (!IsZeroVector(position))
            {
                return (position, "position");
            }
        }

        var metadataStartPosition = metadata?.StartPosition.ToVector3() ?? Vector3.Zero;
        if (!IsZeroVector(metadataStartPosition))
        {
            return (metadataStartPosition, "metadata_start_position");
        }

        return (Vector3.Zero, "zero_unresolved");
    }

    private static bool IsZeroVector(Vector3 value)
        => value.X == 0f && value.Y == 0f && value.Z == 0f;

    private static string FormatVector3(Vector3 value)
        => $"<{value.X:0.000}, {value.Y:0.000}, {value.Z:0.000}>";

    private static uint GetJoinedFateId()
    {
        unsafe
        {
            var fateManager = FateManager.Instance();
            if (fateManager == null || fateManager->FateJoined == 0)
            {
                return 0;
            }

            return fateManager->GetCurrentFateId();
        }
    }

    private bool IsPotFate(uint fateId)
        => potFateIds.Contains(fateId);

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
