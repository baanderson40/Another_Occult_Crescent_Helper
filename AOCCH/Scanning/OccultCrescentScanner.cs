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
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using TreasureFlags = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags;

namespace AOCCH.Scanning;

public sealed class OccultCrescentScanner : IDisposable
{
    private static readonly uint[] CriticalEncountersWithTreasureGuide = [48, 64, 65];
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(500);
    private const float ManualRevealAttributionRadius = 8f;
    private static readonly TimeSpan ManualRevealAttributionWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VisibleCofferDiagnosticLogInterval = TimeSpan.FromSeconds(5);
    private const float FateEntityDiagnosticPlayerRadius = 40f;
    private const float FateEntityDiagnosticPadding = 15f;
    private const int MaxFateEntityDiagnosticEntries = 8;
    private const int MaxSelectionCandidateDescriptions = 6;
    private static readonly TimeSpan FateEntityDiagnosticLogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForayThreatDiagnosticLogInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnknownMetadataWarningInterval = TimeSpan.FromSeconds(60);
    private static readonly InventoryType[] NormalInventoryContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private readonly IClientState clientState;
    private readonly IFateTable fateTable;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentDataCatalog catalog;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private HashSet<uint> potFateIds;
    private Dictionary<uint, PotFateData> potFatesById;
    private readonly object gate = new();
    private readonly Dictionary<ulong, TrackedVisibleCoffer> trackedVisibleCoffers = [];
    private readonly Dictionary<ulong, TrackedRevealCoffer> trackedRevealCoffers = [];
    private readonly Dictionary<uint, uint> previousInventoryItemCounts = [];
    private bool hasPreviousInventorySnapshot;

    public event Action<VisibleCoffer>? CofferOpened;

    private ScannerSnapshot snapshot = new()
    {
        LastUpdated = DateTimeOffset.MinValue,
    };

    private DateTimeOffset lastScanAt = DateTimeOffset.MinValue;
    private bool? lastSupportedTerritoryState;
    private string lastTerritoryKey = string.Empty;
    private string lastSelectionKey = string.Empty;
    private string lastEffectiveTargetKey = "none";
    private bool? lastTreasureBuffState;
    private int? lastPlayerForayLevel;
    private bool hasLastPlayerForayLevel;
    private bool pendingForceRefresh = true;

    public OccultCrescentScanner(
        IClientState clientState,
        IFateTable fateTable,
        IFramework framework,
        IObjectTable objectTable,
        OccultCrescentDataCatalog catalog,
        Configuration configuration,
        AocchLogger logger)
    {
        this.clientState = clientState;
        this.fateTable = fateTable;
        this.framework = framework;
        this.objectTable = objectTable;
        this.catalog = catalog;
        this.configuration = configuration;
        this.logger = logger;
        potFateIds = [];
        potFatesById = [];

        framework.Update += OnFrameworkUpdate;
        clientState.TerritoryChanged += OnTerritoryChanged;

        logger.Info("[Scanner] op=init mode=read-only");
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

    public bool TryGetNearestForayThreat(Vector3 origin, float scanRadius, out ForayThreatEntity threat)
    {
        threat = new ForayThreatEntity();
        ForayThreatEntity? nearest = null;

        foreach (var gameObject in objectTable)
        {
            if (gameObject is not IBattleNpc || gameObject is not ICharacter character
                || !character.IsValid() || !character.IsTargetable || !IsHostile(character))
            {
                continue;
            }

            var distance = CalculateFlatDistance(origin, character.Position);
            if (distance > scanRadius || TryGetForayLevel(character) is not { } knowledgeLevel || knowledgeLevel < 1)
            {
                continue;
            }

            if (nearest == null || distance < nearest.DistanceToPlayer)
            {
                nearest = new ForayThreatEntity
                {
                    ObjectId = character.GameObjectId,
                    BaseId = character.BaseId,
                    Name = character.Name.ToString(),
                    Position = character.Position,
                    KnowledgeLevel = knowledgeLevel,
                    DistanceToPlayer = distance,
                };
            }
        }

        if (nearest == null)
        {
            return false;
        }

        threat = nearest;
        return true;
    }

    public OccultCrescentTerritoryData? ActiveTerritoryData
        => catalog.GetTerritoryOrNull(clientState.TerritoryType);

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        logger.Info("[Scanner] op=stop");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var force = pendingForceRefresh;
        pendingForceRefresh = false;
        RefreshSnapshot(force);
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        trackedVisibleCoffers.Clear();
        trackedRevealCoffers.Clear();
        previousInventoryItemCounts.Clear();
        hasPreviousInventorySnapshot = false;
        var territory = catalog.GetTerritoryOrNull(territoryType);
        logger.Info(territory != null
            ? $"[Scanner] op=territory-change territoryId={territoryType} territoryKey={territory.Key} supported=true state=entered"
            : $"[Scanner] op=territory-change territoryId={territoryType} territoryKey=unsupported supported=false state=unsupported");

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
            var territory = catalog.GetTerritoryOrNull(territoryTypeId);
            var isInSupportedTerritory = territory != null;
            var territoryKey = territory?.Key ?? string.Empty;
            var playerPosition = objectTable.LocalPlayer?.Position;
            var playerForayLevel = isInSupportedTerritory ? TryGetForayLevel(objectTable.LocalPlayer) : null;

            if (!string.Equals(lastTerritoryKey, territoryKey, StringComparison.OrdinalIgnoreCase))
            {
                lastTerritoryKey = territoryKey;
                RebuildTerritoryCaches(territory);
                lastSelectionKey = string.Empty;
                lastEffectiveTargetKey = "none";
                lastTreasureBuffState = null;
            }

            if (lastSupportedTerritoryState != isInSupportedTerritory)
            {
                lastSupportedTerritoryState = isInSupportedTerritory;
                logger.Debug(isInSupportedTerritory
                    ? $"{territory!.DisplayName} scanner is active."
                    : "Occult Crescent scanner is waiting for the player to enter a supported territory.");
            }

            var criticalEncounters = new List<ActiveCriticalEncounter>();
            var unknownCriticalEncounters = new List<ActiveCriticalEncounter>();
            var fates = new List<ActiveFate>();
            var potFates = new List<ActivePotFate>();
            var detectedTreasures = new List<DetectedTreasure>();
            var visibleCoffers = new List<VisibleCoffer>();
            var nearbyForayEntities = new List<ForayThreatEntity>();
            uint currentCriticalEncounterId = 0;
            ActiveCriticalEncounter? currentCriticalEncounter = null;
            ActiveCriticalEncounter? selectedCriticalEncounter = null;
            ActiveFate? selectedFate = null;
            ActivePotFate? activePotFate = null;
            var hasTreasureBuff = false;
            var treasureBuffRemainingSeconds = 0f;
            var effectiveTarget = TargetSelection.None;

            var canFarmCriticalEncounters = territory?.Features.CriticalEncounters == true;
            var canFarmFates = territory?.Features.Fates == true;
            var canRunPotTreasure = territory?.Features.PotTreasure == true;
            var canTrackPotCycle = territory?.Features.PotCycleTracking == true || canRunPotTreasure;
            var canRunVisibleCofferRoute = territory?.Features.VisibleCoffers == true;
            var canUseShopping = territory?.Features.Shopping == true;
            var canRunBuffRotation = territory?.Features.BuffRotation == true;

            if (isInSupportedTerritory)
            {
                // Keep the live event lists complete for diagnostics. Feature flags only
                // affect candidate selection and automation, not event discovery.
                currentCriticalEncounterId = ScanCriticalEncounters(territory!, canFarmCriticalEncounters, criticalEncounters, unknownCriticalEncounters);
                currentCriticalEncounter = criticalEncounters.FirstOrDefault(encounter => encounter.Id == currentCriticalEncounterId)
                    ?? unknownCriticalEncounters.FirstOrDefault(encounter => encounter.Id == currentCriticalEncounterId);
                selectedCriticalEncounter = canFarmCriticalEncounters ? SelectCriticalEncounter(criticalEncounters) : null;

                ScanFates(territory!, canFarmFates, canTrackPotCycle, fates, potFates, out activePotFate);
                selectedFate = canFarmFates ? SelectFate(fates) : null;

                effectiveTarget = SelectEffectiveTarget(selectedCriticalEncounter, selectedFate);
                LogUnknownActiveMetadata(territory!, unknownCriticalEncounters, fates);

                if (canRunPotTreasure)
                {
                    ScanTreasureBuff(out hasTreasureBuff, out treasureBuffRemainingSeconds);
                }
                else
                {
                    TrackTreasureBuffState(false);
                }

                if (canRunPotTreasure || canRunVisibleCofferRoute)
                {
                    ScanNearbyForayEntities(nearbyForayEntities, playerPosition);
                    LogForayThreatDiagnostics(playerForayLevel, nearbyForayEntities);
                }

                if (configuration.EnableOverworldTreasureGuide)
                    ScanDetectedTreasures(
                        detectedTreasures,
                        playerPosition,
                        CriticalEncountersWithTreasureGuide.Contains(currentCriticalEncounterId));

                var canSubmitCofferObservations = configuration.EnableCofferObservationSubmission
                    && (territory!.VisibleCoffers.ObjectKinds.Count > 0
                        || territory.VisibleCoffers.BaseIds.Count > 0);
                if (canRunVisibleCofferRoute || canSubmitCofferObservations)
                    ScanVisibleCoffers(territory!, visibleCoffers, canRunVisibleCofferRoute);

                var diagnosticFate = fates.FirstOrDefault(fate => fate.IsInFate) ?? fates.FirstOrDefault();
                LogNearbyFateEntityDiagnostics(diagnosticFate, playerPosition);
            }
            else
            {
                TrackTreasureBuffState(false);
            }

            TrackPlayerForayLevel(playerForayLevel);

            var nextSnapshot = new ScannerSnapshot
            {
                IsInSupportedTerritory = isInSupportedTerritory,
                IsInCriticalEncounter = currentCriticalEncounterId != 0,
                TerritoryTypeId = territoryTypeId,
                TerritoryKey = territoryKey,
                TerritoryDisplayName = territory?.DisplayName ?? string.Empty,
                CanFarmFates = canFarmFates,
                CanFarmCriticalEncounters = canFarmCriticalEncounters,
                CanRunVisibleCofferRoute = canRunVisibleCofferRoute,
                CanTrackPotCycle = canTrackPotCycle,
                CanRunPotTreasure = canRunPotTreasure,
                CanUseShopping = canUseShopping,
                CanRunBuffRotation = canRunBuffRotation,
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
                DetectedTreasures = detectedTreasures,
                VisibleCoffers = visibleCoffers,
                PlayerForayLevel = playerForayLevel,
                NearbyForayEntities = nearbyForayEntities,
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
        OccultCrescentTerritoryData territory,
        bool canFarmCriticalEncounters,
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

            var metadata = territory.CriticalEncounters.FirstOrDefault(encounter => encounter.Id == dynamicEvent.DynamicEventId);
            var stateCode = (int)dynamicEvent.State;
            var isCandidate = metadata != null
                && canFarmCriticalEncounters
                && configuration.EnableCriticalEngagementFarming
                && configuration.IsCriticalEncounterEnabled(territory.Key, dynamicEvent.DynamicEventId)
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
        OccultCrescentTerritoryData territory,
        bool canFarmFates,
        bool canTrackPotCycle,
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

            var metadata = territory.Fates.FirstOrDefault(knownFate => knownFate.Id == fate.FateId);
            var name = fate.Name.ToString();
            var (fateLocation, locationSource) = ResolveFateLocation(fate, metadata);
            var isPotFate = IsPotFate(fate.FateId);

            var isExcluded = metadata == null
                || isPotFate
                || !canFarmFates
                || !configuration.EnableFateFarming
                || !configuration.IsFateEnabled(territory.Key, fate.FateId);

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
            var liveTarget = TrySelectLiveFateTarget(fate.FateId, fateLocation, playerPosition);

            if (canTrackPotCycle && isPotFate)
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
                HasLiveTarget = liveTarget != null,
                LiveTargetObjectId = liveTarget?.ObjectId ?? 0,
                LiveTargetName = liveTarget?.Name ?? string.Empty,
                LiveTargetPosition = liveTarget?.Position ?? Vector3.Zero,
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

        var statusId = ActiveTerritoryData?.PotTreasure.TreasureBuffStatusId ?? 0;
        if (statusId == 0)
        {
            TrackTreasureBuffState(false);
            return;
        }

        foreach (var status in player.StatusList)
        {
            if (status.StatusId != statusId)
            {
                continue;
            }

            hasTreasureBuff = true;
            remainingTime = status.RemainingTime;
            break;
        }

        TrackTreasureBuffState(hasTreasureBuff);
    }

    private void ScanDetectedTreasures(
        List<DetectedTreasure> detectedTreasures,
        Vector3? playerPosition,
        bool includeNonTargetableTreasures)
    {
        foreach (var gameObject in objectTable)
        {
            if (gameObject is not IGameObject objectEntry
                || !objectEntry.IsValid()
                || !objectEntry.ObjectKind.ToString().StartsWith("Treasure", StringComparison.OrdinalIgnoreCase)
                || (!objectEntry.IsTargetable && !includeNonTargetableTreasures))
            {
                continue;
            }

            if (TreasureObjectState.TryReadTreasureFlags(objectEntry, out var treasureFlags)
                && treasureFlags.HasFlag(TreasureFlags.Opened))
            {
                continue;
            }

            detectedTreasures.Add(new DetectedTreasure
            {
                GameObjectId = objectEntry.GameObjectId,
                DataId = objectEntry.BaseId,
                Name = objectEntry.Name.ToString(),
                ObjectKind = objectEntry.ObjectKind.ToString(),
                Position = objectEntry.Position,
                DistanceToPlayer = playerPosition.HasValue
                    ? CalculateFlatDistance(playerPosition.Value, objectEntry.Position)
                    : float.MaxValue,
                IsTargetable = objectEntry.IsTargetable,
            });
        }

        detectedTreasures.Sort((left, right) => left.DistanceToPlayer.CompareTo(right.DistanceToPlayer));
    }

    private void ScanVisibleCoffers(
        OccultCrescentTerritoryData territory,
        List<VisibleCoffer> visibleCoffers,
        bool includeRouteCandidates)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        var activeTreasureObjectIds = new HashSet<ulong>();
        var recognizedObjectIds = new HashSet<ulong>();

        foreach (var gameObject in objectTable)
        {
            if (gameObject is not IGameObject objectEntry)
            {
                continue;
            }

            var objectKind = objectEntry.ObjectKind.ToString();
            var isTreasureKind = objectKind.StartsWith("Treasure", StringComparison.OrdinalIgnoreCase);

            if (!objectEntry.IsValid())
            {
                continue;
            }

            var isOverworldCoffer = CofferRecognition.TryRecognize(
                territory.VisibleCoffers,
                objectEntry,
                out var recognitionSource);
            var revealRecognitionSource = string.Empty;
            var isRevealCoffer = !isOverworldCoffer
                && CofferRecognition.TryRecognizePotReveal(
                    territory.VisibleCoffers,
                    objectEntry,
                    out revealRecognitionSource);
            if (!isOverworldCoffer && !isRevealCoffer)
            {
                continue;
            }

            recognizedObjectIds.Add(objectEntry.GameObjectId);

            var distanceToPlayer = playerPosition.HasValue
                ? CalculateFlatDistance(playerPosition.Value, objectEntry.Position)
                : float.MaxValue;
            var cofferPosition = objectEntry.Position;
            if (isRevealCoffer
                && playerPosition.HasValue
                && MathF.Abs(cofferPosition.Y + 500f) < 0.5f)
            {
                cofferPosition = new Vector3(cofferPosition.X, playerPosition.Value.Y, cofferPosition.Z);
            }

            var coffer = new VisibleCoffer
            {
                GameObjectId = objectEntry.GameObjectId,
                DataId = objectEntry.BaseId,
                Name = objectEntry.Name.ToString(),
                ObjectKind = objectKind,
                RecognitionSource = recognitionSource,
                Position = cofferPosition,
                DistanceToPlayer = distanceToPlayer,
                IsTargetable = objectEntry.IsTargetable,
            };

            var isOpened = isOverworldCoffer
                && TreasureObjectState.TryReadTreasureFlags(objectEntry, out var treasureFlags)
                && treasureFlags.HasFlag(TreasureFlags.Opened);
            if (isOverworldCoffer)
            {
                var hasPrevious = trackedVisibleCoffers.TryGetValue(objectEntry.GameObjectId, out var previous);
                if (hasPrevious
                    && (previous.DataId != coffer.DataId || !string.Equals(previous.Name, coffer.Name, StringComparison.Ordinal)))
                {
                    hasPrevious = false;
                }

                if (hasPrevious && !previous.IsOpened && isOpened)
                {
                    NotifyCofferOpened(coffer);
                }

                trackedVisibleCoffers[objectEntry.GameObjectId] = new TrackedVisibleCoffer(coffer.DataId, coffer.Name, isOpened);
                if (includeRouteCandidates && !isOpened && objectEntry.IsTargetable)
                {
                    visibleCoffers.Add(coffer);
                }
            }
            else
            {
                trackedRevealCoffers[objectEntry.GameObjectId] = new TrackedRevealCoffer(
                    coffer,
                    DateTimeOffset.UtcNow,
                    revealRecognitionSource);
            }

            if (isTreasureKind && !isOpened)
            {
                activeTreasureObjectIds.Add(objectEntry.GameObjectId);
            }
        }

        foreach (var objectId in trackedVisibleCoffers.Keys.Where(objectId => !recognizedObjectIds.Contains(objectId)).ToList())
        {
            trackedVisibleCoffers.Remove(objectId);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var objectId in trackedRevealCoffers
                     .Where(entry => now - entry.Value.LastSeenAt > ManualRevealAttributionWindow)
                     .Select(entry => entry.Key)
                     .ToList())
        {
            trackedRevealCoffers.Remove(objectId);
        }

        DetectManualRevealInventoryDelta(playerPosition);

        visibleCoffers.Sort((left, right) => left.DistanceToPlayer.CompareTo(right.DistanceToPlayer));

        if (visibleCoffers.Count > 0)
        {
            var summary = string.Join(
                " | ",
                visibleCoffers.Take(8).Select(coffer =>
                    $"localizedName='{coffer.Name}' objectId={coffer.GameObjectId:X} recognition={coffer.RecognitionSource} objectKind={coffer.ObjectKind} baseId={coffer.DataId} dist={coffer.DistanceToPlayer:0.0}y pos=<{coffer.Position.X:0.0},{coffer.Position.Y:0.0},{coffer.Position.Z:0.0}> targetable={coffer.IsTargetable}"));
            logger.DebugThrottled(
                "visible-coffer-scan-results",
                VisibleCofferDiagnosticLogInterval,
                $"[Scanner] op=visible-coffer-scan territoryKey={territory.Key} count={visibleCoffers.Count} entries={summary}");
        }

        if (activeTreasureObjectIds.Count > 0)
        {
            logger.DebugThrottled(
                "visible-coffer-observation-results",
                VisibleCofferDiagnosticLogInterval,
                $"[Scanner] op=coffer-observation-scan territoryKey={territory.Key} recognized={activeTreasureObjectIds.Count} routeCandidates={visibleCoffers.Count} routeEnabled={includeRouteCandidates}");
        }
    }

    private void NotifyCofferOpened(VisibleCoffer coffer)
    {
        try
        {
            CofferOpened?.Invoke(coffer);
        }
        catch (Exception ex)
        {
            logger.Error($"[Scanner] op=coffer-open-notification-failed objectId={coffer.GameObjectId:X} error={ex}");
        }
    }

    private readonly record struct TrackedVisibleCoffer(uint DataId, string Name, bool IsOpened);

    private readonly record struct TrackedRevealCoffer(
        VisibleCoffer Coffer,
        DateTimeOffset LastSeenAt,
        string RecognitionSource);

    private unsafe void DetectManualRevealInventoryDelta(Vector3? playerPosition)
    {
        if (!configuration.EnableCofferObservationSubmission)
        {
            previousInventoryItemCounts.Clear();
            hasPreviousInventorySnapshot = false;
            return;
        }

        if (!TryCaptureNormalInventorySnapshot(out var currentItemCounts))
        {
            hasPreviousInventorySnapshot = false;
            return;
        }

        if (!hasPreviousInventorySnapshot)
        {
            previousInventoryItemCounts.Clear();
            foreach (var pair in currentItemCounts)
            {
                previousInventoryItemCounts[pair.Key] = pair.Value;
            }

            hasPreviousInventorySnapshot = true;
            return;
        }

        var hasPositiveDelta = currentItemCounts.Any(pair =>
            pair.Value > previousInventoryItemCounts.GetValueOrDefault(pair.Key));
        previousInventoryItemCounts.Clear();
        foreach (var pair in currentItemCounts)
        {
            previousInventoryItemCounts[pair.Key] = pair.Value;
        }

        if (!hasPositiveDelta || !playerPosition.HasValue)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var candidate = trackedRevealCoffers.Values
            .Where(entry => now - entry.LastSeenAt <= ManualRevealAttributionWindow)
            .Select(entry => new
            {
                entry.Coffer,
                entry.RecognitionSource,
                Distance = CalculateFlatDistance(playerPosition.Value, entry.Coffer.Position),
            })
            .Where(entry => entry.Distance <= ManualRevealAttributionRadius)
            .OrderBy(entry => entry.Distance)
            .FirstOrDefault();
        if (candidate == null)
        {
            logger.Debug("[Scanner] op=manual-reveal-inventory-delta ignored=true reason=no-nearby-reveal-coffer");
            return;
        }

        trackedRevealCoffers.Remove(candidate.Coffer.GameObjectId);
        logger.Info(
            $"[Scanner] op=manual-reveal-open-confirmed method=inventory-delta objectId={candidate.Coffer.GameObjectId:X} baseId={candidate.Coffer.DataId} " +
            $"position=<{candidate.Coffer.Position.X:0.0},{candidate.Coffer.Position.Y:0.0},{candidate.Coffer.Position.Z:0.0}> " +
            $"distance={candidate.Distance:0.0}y recognition={candidate.RecognitionSource}");
        NotifyCofferOpened(candidate.Coffer);
    }

    private static unsafe bool TryCaptureNormalInventorySnapshot(out Dictionary<uint, uint> itemCounts)
    {
        itemCounts = [];
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return false;
        }

        foreach (var containerType in NormalInventoryContainers)
        {
            var container = inventoryManager->GetInventoryContainer(containerType);
            if (container == null || !container->IsLoaded || container->Size <= 0 || container->Items == null)
            {
                itemCounts.Clear();
                return false;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->IsEmpty() || item->ItemId == 0)
                {
                    continue;
                }

                var quantity = checked((uint)item->Quantity);
                itemCounts[item->ItemId] = itemCounts.TryGetValue(item->ItemId, out var count)
                    ? count + quantity
                    : quantity;
            }
        }

        return true;
    }

    private void ScanNearbyForayEntities(List<ForayThreatEntity> entities, Vector3? playerPosition)
    {
        if (!playerPosition.HasValue)
        {
            return;
        }

        var scanRadius = Math.Max(
            Math.Max(configuration.KnowledgeThreatExitDistance, configuration.KnowledgeThreatEnterDistance),
            KnowledgeThreatEvaluator.OccultIsleblazerUnhideDistance);
        foreach (var gameObject in objectTable)
        {
            if (gameObject is not IBattleNpc || gameObject is not ICharacter character
                || !character.IsValid() || !character.IsTargetable)
            {
                continue;
            }

            if (!IsHostile(character))
            {
                continue;
            }

            var distance = CalculateFlatDistance(playerPosition.Value, character.Position);
            if (distance > scanRadius || TryGetForayLevel(character) is not { } knowledgeLevel || knowledgeLevel < 1)
            {
                continue;
            }

            entities.Add(new ForayThreatEntity
            {
                ObjectId = character.GameObjectId,
                BaseId = character.BaseId,
                Name = character.Name.ToString(),
                Position = character.Position,
                KnowledgeLevel = knowledgeLevel,
                DistanceToPlayer = distance,
            });
        }

        entities.Sort((left, right) => left.DistanceToPlayer.CompareTo(right.DistanceToPlayer));
    }

    private void TrackPlayerForayLevel(int? playerForayLevel)
    {
        if (hasLastPlayerForayLevel && lastPlayerForayLevel == playerForayLevel)
        {
            return;
        }

        hasLastPlayerForayLevel = true;
        lastPlayerForayLevel = playerForayLevel;
        logger.Info(playerForayLevel.HasValue
            ? $"[Scanner] op=foray-player-level state=available level={playerForayLevel.Value}"
            : "[Scanner] op=foray-player-level state=unavailable");
    }

    private void LogForayThreatDiagnostics(int? playerForayLevel, IReadOnlyList<ForayThreatEntity> entities)
    {
        if (!playerForayLevel.HasValue)
        {
            logger.VerboseThrottled(
                "foray-threat-scan",
                ForayThreatDiagnosticLogInterval,
                "[Scanner] op=foray-threat-scan playerLevel=unavailable entities=0 reason=player-foray-unavailable");
            return;
        }

        var potHideAtOrAbove = Math.Clamp(playerForayLevel.Value + configuration.PotKnowledgeHideOffset, 1, 28);
        var visibleHideAtOrAbove = Math.Clamp(playerForayLevel.Value + configuration.VisibleCofferKnowledgeHideOffset, 1, 28);
        var entitySummary = entities.Count == 0
            ? "none"
            : string.Join(
                " | ",
                entities.Select(entity =>
                    $"name='{entity.Name}' objectId={entity.ObjectId:X} baseId={entity.BaseId} level={entity.KnowledgeLevel} distance={entity.DistanceToPlayer:0.0}y potThreat={entity.KnowledgeLevel >= potHideAtOrAbove} overworldThreat={entity.KnowledgeLevel >= visibleHideAtOrAbove}"));
        logger.VerboseThrottled(
            "foray-threat-scan",
            ForayThreatDiagnosticLogInterval,
            $"[Scanner] op=foray-threat-scan playerLevel={playerForayLevel.Value} potOffset={configuration.PotKnowledgeHideOffset} potHideAtOrAbove={potHideAtOrAbove} overworldOffset={configuration.VisibleCofferKnowledgeHideOffset} overworldHideAtOrAbove={visibleHideAtOrAbove} enterRange={configuration.KnowledgeThreatEnterDistance:0.0}y exitRange={configuration.KnowledgeThreatExitDistance:0.0}y entities={entities.Count} entries={entitySummary}");
    }

    private static unsafe int? TryGetForayLevel(ICharacter? character)
    {
        if (character == null)
        {
            return null;
        }

        var characterPointer = (Character*)character.Address;
        if (characterPointer == null || characterPointer->VirtualTable == null)
        {
            return null;
        }

        var forayInfo = characterPointer->GetForayInfo();
        if (forayInfo != null)
        {
            return forayInfo->Level;
        }

        if (character is not IBattleNpc)
        {
            return null;
        }

        return ((BattleChara*)characterPointer)->ForayInfo.Level;
    }

    private static unsafe bool IsHostile(ICharacter character)
    {
        var nativeCharacter = (Character*)character.Address;
        return nativeCharacter != null && nativeCharacter->IsHostile;
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
            ? $"[Scanner] op=treasure-buff state=detected statusId={ActiveTerritoryData?.PotTreasure.TreasureBuffStatusId ?? 0}"
            : $"[Scanner] op=treasure-buff state=cleared statusId={ActiveTerritoryData?.PotTreasure.TreasureBuffStatusId ?? 0}");
    }

    private void LogNearbyFateEntityDiagnostics(ActiveFate? selectedFate, Vector3? playerPosition)
    {
        if (selectedFate == null || !playerPosition.HasValue)
        {
            return;
        }

        var playerDistanceToFate = CalculateFlatDistance(playerPosition.Value, selectedFate.Position);
        var diagnosticRadius = MathF.Max(selectedFate.Radius + FateEntityDiagnosticPadding, FateEntityDiagnosticPlayerRadius);
        if (playerDistanceToFate > diagnosticRadius)
        {
            return;
        }

        var candidates = GetLiveFateTargetCandidates(selectedFate.Id, selectedFate.Position, playerPosition)
            .Take(MaxFateEntityDiagnosticEntries)
            .Select(FormatLiveFateTargetCandidate)
            .ToArray();

        var details = candidates.Length == 0 ? "none" : string.Join(" | ", candidates);
        var selected = selectedFate.HasLiveTarget
            ? $"name='{selectedFate.LiveTargetName}' objectId={selectedFate.LiveTargetObjectId:X} fateId={selectedFate.Id} pos={FormatVector3(selectedFate.LiveTargetPosition)} source=live-target"
            : "none source=fate-center";
        logger.VerboseThrottled(
            $"fate-entity-diag-{selectedFate.Id}",
            FateEntityDiagnosticLogInterval,
            $"[Scanner] op=fate-entity-diagnostics fate=\"{selectedFate.Name}\" ({selectedFate.Id}) playerDistance={playerDistanceToFate:0.0} radius={diagnosticRadius:0.0} selected={selected} candidates={details}");
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
        LogSelectionCompetitionIfEffectiveTargetChanged(snapshot);
        switch (snapshot.EffectiveTarget.Kind)
        {
            case SelectedTargetKind.CriticalEncounter:
                var selectedCe = snapshot.EffectiveTarget.CriticalEncounter;
                if (selectedCe != null)
                {
                    logger.Info(
                        $"[Scanner] op=target-selected kind=CE target=\"{selectedCe.Name}\" ({selectedCe.Id}) priority={selectedCe.Priority} reason={snapshot.EffectiveTarget.Reason} preemptFate={snapshot.EffectiveTarget.WouldPreemptFate}");
                }

                break;
            case SelectedTargetKind.Fate:
                var selectedFate = snapshot.EffectiveTarget.Fate;
                if (selectedFate != null)
                {
                    logger.Info(
                        $"[Scanner] op=target-selected kind=FATE target=\"{selectedFate.Name}\" ({selectedFate.Id}) progress={selectedFate.Progress}% distance={selectedFate.DistanceToPlayer:0.0} reason={snapshot.EffectiveTarget.Reason}");
                }

                break;
            default:
                logger.Info($"[Scanner] op=no-target {BuildNoSelectionReason(snapshot)}");
                break;
        }
    }

    private void LogUnknownActiveMetadata(
        OccultCrescentTerritoryData territory,
        IReadOnlyList<ActiveCriticalEncounter> unknownCriticalEncounters,
        IReadOnlyList<ActiveFate> fates)
    {
        var unknownFates = fates.Where(fate => !fate.HasKnownMetadata).ToArray();
        if (unknownCriticalEncounters.Count == 0 && unknownFates.Length == 0)
        {
            return;
        }

        var encounters = unknownCriticalEncounters
            .Take(MaxSelectionCandidateDescriptions / 2)
            .Select(encounter => $"CE:{encounter.Name} ({encounter.Id}) state={encounter.State}");
        var fateDescriptions = unknownFates
            .Take(MaxSelectionCandidateDescriptions / 2)
            .Select(fate => $"FATE:{fate.Name} ({fate.Id}) state={fate.State}");
        var descriptions = string.Join(" | ", encounters.Concat(fateDescriptions));
        logger.WarningThrottled(
            $"unknown-active-metadata-{territory.Key}",
            UnknownMetadataWarningInterval,
            $"[Scanner] op=unknown-active-metadata territoryKey={territory.Key} unknownCes={unknownCriticalEncounters.Count} unknownFates={unknownFates.Length} entries={descriptions}");
    }

    private void LogSelectionCompetitionIfEffectiveTargetChanged(ScannerSnapshot snapshot)
    {
        var effectiveTargetKey = BuildEffectiveTargetKey(snapshot.EffectiveTarget);
        if (effectiveTargetKey == lastEffectiveTargetKey)
        {
            return;
        }

        lastEffectiveTargetKey = effectiveTargetKey;
        var ceCandidates = snapshot.CriticalEncounters
            .Where(encounter => encounter.IsCandidate)
            .OrderByDescending(encounter => encounter.Priority)
            .ThenBy(encounter => encounter.StartTimestamp <= 0 ? long.MaxValue : encounter.StartTimestamp)
            .ThenBy(encounter => encounter.Name, StringComparer.Ordinal)
            .ThenBy(encounter => encounter.Id)
            .Take(MaxSelectionCandidateDescriptions / 2)
            .Select(encounter => $"CE:{encounter.Name} ({encounter.Id}) priority={encounter.Priority}");
        var fateCandidates = snapshot.Fates
            .Where(fate => fate.IsCandidate)
            .OrderBy(fate => configuration.FatePriority == FatePriority.Nearest ? fate.DistanceToPlayer : fate.Progress)
            .ThenBy(fate => configuration.FatePriority == FatePriority.Nearest ? fate.Progress : fate.DistanceToPlayer)
            .ThenBy(fate => fate.Name, StringComparer.Ordinal)
            .ThenBy(fate => fate.Id)
            .Take(MaxSelectionCandidateDescriptions / 2)
            .Select(fate => $"FATE:{fate.Name} ({fate.Id}) progress={fate.Progress}% distance={fate.DistanceToPlayer:0.0}");
        var candidates = string.Join(" | ", ceCandidates.Concat(fateCandidates));
        var target = snapshot.EffectiveTarget.Kind switch
        {
            SelectedTargetKind.CriticalEncounter => $"CE:{snapshot.EffectiveTarget.CriticalEncounter?.Id}",
            SelectedTargetKind.Fate => $"FATE:{snapshot.EffectiveTarget.Fate?.Id}",
            _ => "none",
        };

        logger.Debug(
            $"[Scanner] op=selection-competition effectiveTarget={target} reason={snapshot.EffectiveTarget.Reason} ceCandidates={snapshot.CriticalEncounters.Count(encounter => encounter.IsCandidate)} fateCandidates={snapshot.Fates.Count(fate => fate.IsCandidate)} prioritizeCe={configuration.PrioritizeCe} fatePriority={configuration.FatePriority} candidates={(string.IsNullOrEmpty(candidates) ? "none" : candidates)}");
    }

    private string BuildNoSelectionReason(ScannerSnapshot snapshot)
    {
        var knownCeCount = snapshot.CriticalEncounters.Count;
        var unknownCeCount = snapshot.UnknownCriticalEncounters.Count;
        var ceCandidateCount = snapshot.CriticalEncounters.Count(encounter => encounter.IsCandidate);
        var ceConfigDisabledCount = snapshot.CriticalEncounters.Count(encounter => encounter.HasKnownMetadata && !encounter.IsCandidate);
        var fateCount = snapshot.Fates.Count;
        var fateCandidateCount = snapshot.Fates.Count(fate => fate.IsCandidate);
        var fateExcludedCount = snapshot.Fates.Count(fate => fate.IsExcluded);
        var fateUnknownCount = snapshot.Fates.Count(fate => !fate.HasKnownMetadata);

        if (!configuration.EnableCriticalEngagementFarming && !configuration.EnableFateFarming)
        {
            return "reason=both-disabled.";
        }

        if (!configuration.EnableCriticalEngagementFarming)
        {
            return $"reason=ce-disabled fateEnabled=true fateCandidates={fateCandidateCount}/{fateCount} excludedFates={fateExcludedCount} unknownFates={fateUnknownCount}.";
        }

        if (!configuration.EnableFateFarming)
        {
            return $"reason=fate-disabled ceEnabled=true ceCandidates={ceCandidateCount}/{knownCeCount} unknownCes={unknownCeCount} nonCandidateCes={ceConfigDisabledCount}.";
        }

        if (ceCandidateCount == 0 && fateCandidateCount == 0)
        {
            return $"reason=no-candidates ceCandidates=0/{knownCeCount} unknownCes={unknownCeCount} nonCandidateCes={ceConfigDisabledCount} fateCandidates=0/{fateCount} excludedFates={fateExcludedCount} unknownFates={fateUnknownCount}.";
        }

        if (ceCandidateCount == 0)
        {
            return $"reason=no-ce-candidate ceCandidates=0/{knownCeCount} unknownCes={unknownCeCount} nonCandidateCes={ceConfigDisabledCount} fateCandidates={fateCandidateCount}/{fateCount} excludedFates={fateExcludedCount} unknownFates={fateUnknownCount}.";
        }

        if (fateCandidateCount == 0)
        {
            return $"reason=no-fate-candidate ceCandidates={ceCandidateCount}/{knownCeCount} unknownCes={unknownCeCount} nonCandidateCes={ceConfigDisabledCount} fateCandidates=0/{fateCount} excludedFates={fateExcludedCount} unknownFates={fateUnknownCount}.";
        }

        return $"reason=selection-resolved-none ceCandidates={ceCandidateCount}/{knownCeCount} unknownCes={unknownCeCount} nonCandidateCes={ceConfigDisabledCount} fateCandidates={fateCandidateCount}/{fateCount} excludedFates={fateExcludedCount} unknownFates={fateUnknownCount} prioritizeCe={configuration.PrioritizeCe} fatePriority={configuration.FatePriority}.";
    }

    private static bool IsPreBattleCeState(int stateCode)
        => stateCode > 0 && stateCode < 3;

    private LiveFateTargetCandidate? TrySelectLiveFateTarget(uint fateId, Vector3 fatePosition, Vector3? playerPosition)
        => GetLiveFateTargetCandidates(fateId, fatePosition, playerPosition).FirstOrDefault();

    private IEnumerable<LiveFateTargetCandidate> GetLiveFateTargetCandidates(uint fateId, Vector3 fatePosition, Vector3? playerPosition)
    {
        return objectTable
            .Where(gameObject => gameObject is IGameObject objectEntry && IsFateEntityDiagnosticCandidate(objectEntry, fateId))
            .OfType<IGameObject>()
            .Select(objectEntry => new LiveFateTargetCandidate(
                objectEntry.GameObjectId,
                objectEntry.Name.ToString(),
                objectEntry.Position,
                objectEntry.ObjectKind.ToString(),
                objectEntry.BaseId,
                GetBattleNpcFateId(objectEntry),
                objectEntry.IsTargetable,
                CalculateFlatDistance(objectEntry.Position, fatePosition),
                playerPosition.HasValue ? CalculateFlatDistance(objectEntry.Position, playerPosition.Value) : float.MaxValue))
            .OrderBy(candidate => candidate.DistanceToFate)
            .ThenBy(candidate => candidate.DistanceToPlayer)
            .ThenBy(candidate => candidate.ObjectId);
    }

    private static bool IsFateEntityDiagnosticCandidate(IGameObject gameObject, uint fateId)
    {
        if (!gameObject.IsValid()
            || gameObject.GameObjectId == 0
            || gameObject.BaseId == 0
            || gameObject is not IBattleNpc
            || !gameObject.IsTargetable)
        {
            return false;
        }

        if (fateId > ushort.MaxValue || GetBattleNpcFateId(gameObject) != fateId)
        {
            return false;
        }

        return true;
    }

    private static unsafe ushort GetBattleNpcFateId(IGameObject gameObject)
    {
        if (gameObject is not IBattleNpc || gameObject.Address == IntPtr.Zero)
        {
            return 0;
        }

        var character = (Character*)gameObject.Address;
        return character->VirtualTable != null ? character->FateId : (ushort)0;
    }

    private static string FormatLiveFateTargetCandidate(LiveFateTargetCandidate candidate)
        => $"name='{candidate.Name}' kind={candidate.Kind} baseId={candidate.BaseId} objectId={candidate.ObjectId:X} fateId={candidate.FateId} targetable={candidate.IsTargetable} distFate={candidate.DistanceToFate:0.0} distPlayer={candidate.DistanceToPlayer:0.0} pos={FormatVector3(candidate.Position)}";

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

    private void RebuildTerritoryCaches(OccultCrescentTerritoryData? territory)
    {
        if (territory == null)
        {
            potFateIds = [];
            potFatesById = [];
            return;
        }

        potFateIds = territory.PotFates.Select(potFate => potFate.FateId).ToHashSet();
        potFatesById = territory.PotFates.ToDictionary(potFate => potFate.FateId);
    }

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

    private static string BuildEffectiveTargetKey(TargetSelection selection)
        => selection.Kind switch
        {
            SelectedTargetKind.CriticalEncounter => $"ce:{selection.CriticalEncounter?.Id}",
            SelectedTargetKind.Fate => $"fate:{selection.Fate?.Id}",
            _ => "none",
        };

    private sealed record LiveFateTargetCandidate(
        ulong ObjectId,
        string Name,
        Vector3 Position,
        string Kind,
        uint BaseId,
        ushort FateId,
        bool IsTargetable,
        float DistanceToFate,
        float DistanceToPlayer);
}
