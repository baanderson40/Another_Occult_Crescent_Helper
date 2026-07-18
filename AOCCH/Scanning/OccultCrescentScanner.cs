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
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace AOCCH.Scanning;

public sealed class OccultCrescentScanner : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(500);
    private const float VisibleCofferDiagnosticRadius = 60f;
    private static readonly TimeSpan VisibleCofferDiagnosticLogInterval = TimeSpan.FromSeconds(5);
    private const float FateEntityDiagnosticPlayerRadius = 40f;
    private const float FateEntityDiagnosticPadding = 15f;
    private const int MaxFateEntityDiagnosticEntries = 8;
    private static readonly TimeSpan FateEntityDiagnosticLogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForayThreatDiagnosticLogInterval = TimeSpan.FromSeconds(5);

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

    private ScannerSnapshot snapshot = new()
    {
        LastUpdated = DateTimeOffset.MinValue,
    };

    private DateTimeOffset lastScanAt = DateTimeOffset.MinValue;
    private bool? lastSupportedTerritoryState;
    private string lastTerritoryKey = string.Empty;
    private string lastSelectionKey = string.Empty;
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
            var canRunVisibleCofferRoute = territory?.Features.VisibleCoffers == true;
            var canUseShopping = territory?.Features.Shopping == true;
            var canRunBuffRotation = territory?.Features.BuffRotation == true;

            if (isInSupportedTerritory)
            {
                if (canFarmCriticalEncounters)
                {
                    currentCriticalEncounterId = ScanCriticalEncounters(territory!, criticalEncounters, unknownCriticalEncounters);
                    currentCriticalEncounter = criticalEncounters.FirstOrDefault(encounter => encounter.Id == currentCriticalEncounterId)
                        ?? unknownCriticalEncounters.FirstOrDefault(encounter => encounter.Id == currentCriticalEncounterId);
                    selectedCriticalEncounter = SelectCriticalEncounter(criticalEncounters);
                }

                if (canFarmFates || canRunPotTreasure)
                {
                    ScanFates(territory!, canFarmFates, canRunPotTreasure, fates, potFates, out activePotFate);
                    selectedFate = canFarmFates ? SelectFate(fates) : null;
                }

                effectiveTarget = SelectEffectiveTarget(selectedCriticalEncounter, selectedFate);

                if (canRunPotTreasure)
                {
                    ScanTreasureBuff(out hasTreasureBuff, out treasureBuffRemainingSeconds);
                    ScanNearbyForayEntities(nearbyForayEntities, playerPosition);
                    LogForayThreatDiagnostics(playerForayLevel, nearbyForayEntities);
                }
                else
                {
                    TrackTreasureBuffState(false);
                }

                if (canRunVisibleCofferRoute)
                {
                    ScanVisibleCoffers(territory!, visibleCoffers);
                }

                if (canFarmFates)
                {
                    LogNearbyFateEntityDiagnostics(selectedFate, playerPosition);
                }
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
        bool canRunPotTreasure,
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
            var isPotFate = canRunPotTreasure && IsPotFate(fate.FateId);
            if (!canFarmFates && !isPotFate)
            {
                continue;
            }

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
            var liveTarget = TrySelectLiveFateTarget(fateLocation, fate.Radius, playerPosition);

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

    private void ScanVisibleCoffers(OccultCrescentTerritoryData territory, List<VisibleCoffer> visibleCoffers)
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

            if (!CofferRecognition.TryRecognize(territory.VisibleCoffers, objectEntry, out var recognitionSource)
                || !objectEntry.IsTargetable
                || !objectEntry.IsValid())
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
                ObjectKind = objectKind,
                RecognitionSource = recognitionSource,
                Position = objectEntry.Position,
                DistanceToPlayer = distanceToPlayer,
                IsTargetable = objectEntry.IsTargetable,
            });
        }

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

        if (visibleCoffers.Count == 0 && nearbyTreasureObjects.Count > 0)
        {
            logger.DebugThrottled(
                "visible-coffer-diagnostics",
                VisibleCofferDiagnosticLogInterval,
                $"[Scanner] op=visible-coffer-unrecognized territoryKey={territory.Key} entries={string.Join(" | ", nearbyTreasureObjects)}");
        }
    }

    private void ScanNearbyForayEntities(List<ForayThreatEntity> entities, Vector3? playerPosition)
    {
        if (!playerPosition.HasValue)
        {
            return;
        }

        var scanRadius = Math.Max(configuration.KnowledgeThreatExitDistance, configuration.KnowledgeThreatEnterDistance);
        foreach (var gameObject in objectTable)
        {
            if (gameObject is not IBattleNpc || gameObject is not ICharacter character
                || !character.IsValid() || !character.IsTargetable)
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
            logger.InfoThrottled(
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
                    $"name='{entity.Name}' objectId={entity.ObjectId:X} level={entity.KnowledgeLevel} distance={entity.DistanceToPlayer:0.0}y potThreat={entity.KnowledgeLevel >= potHideAtOrAbove} overworldThreat={entity.KnowledgeLevel >= visibleHideAtOrAbove}"));
        logger.InfoThrottled(
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

        var candidates = GetLiveFateTargetCandidates(selectedFate.Position, selectedFate.Radius, playerPosition)
            .Take(MaxFateEntityDiagnosticEntries)
            .Select(FormatLiveFateTargetCandidate)
            .ToArray();

        var details = candidates.Length == 0 ? "none" : string.Join(" | ", candidates);
        var selected = selectedFate.HasLiveTarget
            ? $"name='{selectedFate.LiveTargetName}' objectId={selectedFate.LiveTargetObjectId:X} pos={FormatVector3(selectedFate.LiveTargetPosition)} source=live-target"
            : "none source=fate-center";
        logger.DebugThrottled(
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

    private LiveFateTargetCandidate? TrySelectLiveFateTarget(Vector3 fatePosition, float fateRadius, Vector3? playerPosition)
        => GetLiveFateTargetCandidates(fatePosition, fateRadius, playerPosition).FirstOrDefault();

    private IEnumerable<LiveFateTargetCandidate> GetLiveFateTargetCandidates(Vector3 fatePosition, float fateRadius, Vector3? playerPosition)
    {
        var diagnosticRadius = MathF.Max(fateRadius + FateEntityDiagnosticPadding, FateEntityDiagnosticPlayerRadius);
        return objectTable
            .Where(gameObject => gameObject is IGameObject objectEntry && IsFateEntityDiagnosticCandidate(objectEntry, fatePosition, diagnosticRadius))
            .OfType<IGameObject>()
            .Select(objectEntry => new LiveFateTargetCandidate(
                objectEntry.GameObjectId,
                objectEntry.Name.ToString(),
                objectEntry.Position,
                objectEntry.ObjectKind.ToString(),
                objectEntry.BaseId,
                objectEntry.IsTargetable,
                CalculateFlatDistance(objectEntry.Position, fatePosition),
                playerPosition.HasValue ? CalculateFlatDistance(objectEntry.Position, playerPosition.Value) : float.MaxValue))
            .OrderBy(candidate => candidate.DistanceToFate)
            .ThenBy(candidate => candidate.DistanceToPlayer)
            .ThenBy(candidate => candidate.ObjectId);
    }

    private static bool IsFateEntityDiagnosticCandidate(IGameObject gameObject, Vector3 fatePosition, float diagnosticRadius)
    {
        if (!gameObject.IsValid() || gameObject.GameObjectId == 0 || gameObject.BaseId == 0)
        {
            return false;
        }

        var objectKind = gameObject.ObjectKind.ToString();
        if (!objectKind.StartsWith("Battle", StringComparison.OrdinalIgnoreCase)
            || !gameObject.IsTargetable)
        {
            return false;
        }

        return CalculateFlatDistance(gameObject.Position, fatePosition) <= diagnosticRadius;
    }

    private static string FormatLiveFateTargetCandidate(LiveFateTargetCandidate candidate)
        => $"name='{candidate.Name}' kind={candidate.Kind} baseId={candidate.BaseId} objectId={candidate.ObjectId:X} targetable={candidate.IsTargetable} distFate={candidate.DistanceToFate:0.0} distPlayer={candidate.DistanceToPlayer:0.0} pos={FormatVector3(candidate.Position)}";

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

    private sealed record LiveFateTargetCandidate(
        ulong ObjectId,
        string Name,
        Vector3 Position,
        string Kind,
        uint BaseId,
        bool IsTargetable,
        float DistanceToFate,
        float DistanceToPlayer);
}
