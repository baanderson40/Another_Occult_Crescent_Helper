using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class TreasureCofferFarmController : IDisposable
{
    private static int nextRunSequence;
    private const float MatchConfidenceRadius = 25f;
    private const float VisibleCofferAcquisitionDistance = 60f;
    private const float VisibleCofferApproachScanTriggerDistance = 40f;
    private const int RequiredInventoryFreeSlots = 3;
    private static readonly TimeSpan ApproachScanPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly GameActionController gameActionController;
    private readonly DeathRecoveryController deathRecoveryController;
    private readonly DangerousTreasureTravelController dangerousTreasureTravelController;
    private readonly CofferInteractionController cofferInteractionController;
    private readonly OccultCrescentData data;
    private readonly VisibleCofferPositionOverrideStore overrideStore;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();
    private readonly Dictionary<string, VisibleCofferFarmSpotData> spotsByKey;

    private TreasureCofferFarmState state = TreasureCofferFarmState.Idle;
    private TreasureCofferFarmResult lastResult;
    private string currentRunId = string.Empty;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private int currentRouteIndex = -1;
    private VisibleCofferFarmRouteEntryData? activeRouteEntry;
    private VisibleCofferFarmSpotData? activeSpot;
    private Vector3 activeResolvedPosition;
    private bool activeSpotUsesOverride;
    private VisibleCoffer? lastMatchedCoffer;
    private DateTimeOffset lastVisibleCofferScanAt = DateTimeOffset.MinValue;
    private string lastMatchSource = string.Empty;
    private VisibleCofferMatch? pendingInteractionMatch;
    private VisibleCofferFarmSpotData? activePreviousThresholdSpot;
    private bool activeSpotRequiresHiddenTravel;

    public TreasureCofferFarmController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        OccultCrescentScanner scanner,
        MovementController movementController,
        GameActionController gameActionController,
        DeathRecoveryController deathRecoveryController,
        DangerousTreasureTravelController dangerousTreasureTravelController,
        CofferInteractionController cofferInteractionController,
        OccultCrescentData data,
        VisibleCofferPositionOverrideStore overrideStore,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.scanner = scanner;
        this.movementController = movementController;
        this.gameActionController = gameActionController;
        this.deathRecoveryController = deathRecoveryController;
        this.dangerousTreasureTravelController = dangerousTreasureTravelController;
        this.cofferInteractionController = cofferInteractionController;
        this.data = data;
        this.overrideStore = overrideStore;
        this.configuration = configuration;
        this.logger = logger;
        spotsByKey = data.VisibleCofferFarmSpots.ToDictionary(spot => BuildKey(spot.Area, spot.Label), StringComparer.OrdinalIgnoreCase);

        framework.Update += OnFrameworkUpdate;
    }

    public TreasureCofferFarmState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public bool IsRunning
        => State is not TreasureCofferFarmState.Idle
            and not TreasureCofferFarmState.Completed
            and not TreasureCofferFarmState.Stopped
            and not TreasureCofferFarmState.Failed;

    public string LastTransition
    {
        get
        {
            lock (gate)
            {
                return lastTransition;
            }
        }
    }

    public string LastError
    {
        get
        {
            lock (gate)
            {
                return lastError;
            }
        }
    }

    public TreasureCofferFarmResult LastResult
    {
        get
        {
            lock (gate)
            {
                return lastResult;
            }
        }
    }

    public int CurrentRouteIndex
    {
        get
        {
            lock (gate)
            {
                return currentRouteIndex;
            }
        }
    }

    public VisibleCofferFarmRouteEntryData? ActiveRouteEntry
    {
        get
        {
            lock (gate)
            {
                return activeRouteEntry;
            }
        }
    }

    public VisibleCofferFarmSpotData? ActiveSpot
    {
        get
        {
            lock (gate)
            {
                return activeSpot;
            }
        }
    }

    public Vector3 ActiveResolvedPosition
    {
        get
        {
            lock (gate)
            {
                return activeResolvedPosition;
            }
        }
    }

    public bool ActiveSpotUsesOverride
    {
        get
        {
            lock (gate)
            {
                return activeSpotUsesOverride;
            }
        }
    }

    public VisibleCoffer? LastMatchedCoffer
    {
        get
        {
            lock (gate)
            {
                return lastMatchedCoffer;
            }
        }
    }

    public VisibleCofferPositionOverride? LastSavedOverride
        => overrideStore.LastSavedOverride;

    public bool Start(bool startedByFarmSession = false)
    {
        if (IsRunning)
        {
            logger.Debug($"Overworld coffer route start ignored because a run is already active. state={State}.");
            return true;
        }

        if (configuration.ScannerOnlyMode)
        {
            SetFailure("Overworld coffer route start is blocked by scanner-only mode.");
            return false;
        }

        if (!scanner.Snapshot.IsInSouthHorn)
        {
            SetFailure("Overworld coffer route start requires South Horn.");
            return false;
        }

        if (data.VisibleCofferFarmRoute.Count == 0 || data.VisibleCofferFarmSpots.Count == 0)
        {
            SetFailure("Overworld coffer route data is missing route or spot entries.");
            return false;
        }

        lock (gate)
        {
            lastResult = TreasureCofferFarmResult.None;
            currentRunId = $"CofferFarm#{Interlocked.Increment(ref nextRunSequence)}";
            currentRouteIndex = -1;
            activeRouteEntry = null;
            activeSpot = null;
            activeResolvedPosition = Vector3.Zero;
            activeSpotUsesOverride = false;
            lastMatchedCoffer = null;
            lastVisibleCofferScanAt = DateTimeOffset.MinValue;
            lastMatchSource = string.Empty;
            pendingInteractionMatch = null;
            activePreviousThresholdSpot = null;
            activeSpotRequiresHiddenTravel = false;
        }

        movementController.SetLogOwner(currentRunId);
        TransitionTo(TreasureCofferFarmState.Starting, startedByFarmSession
            ? $"Starting automatic overworld coffer route with {data.VisibleCofferFarmRoute.Count} entries."
            : $"Starting overworld coffer route with {data.VisibleCofferFarmRoute.Count} entries.");
        return true;
    }

    public void Stop(string reason)
    {
        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop(reason);
        }

        if (dangerousTreasureTravelController.IsRunning)
        {
            dangerousTreasureTravelController.Stop(reason);
        }

        if (cofferInteractionController.IsRunning)
        {
            cofferInteractionController.Stop(reason);
        }

        lock (gate)
        {
            pendingInteractionMatch = null;
        }

        TransitionTo(TreasureCofferFarmState.Stopped, reason, error: reason, result: TreasureCofferFarmResult.Stopped);
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = TreasureCofferFarmState.Idle;
            lastResult = TreasureCofferFarmResult.None;
            currentRunId = string.Empty;
            lastTransition = "Idle";
            lastError = string.Empty;
            currentRouteIndex = -1;
            activeRouteEntry = null;
            activeSpot = null;
            activeResolvedPosition = Vector3.Zero;
            activeSpotUsesOverride = false;
            lastMatchedCoffer = null;
            lastVisibleCofferScanAt = DateTimeOffset.MinValue;
            lastMatchSource = string.Empty;
            pendingInteractionMatch = null;
            activePreviousThresholdSpot = null;
            activeSpotRequiresHiddenTravel = false;
        }

        logger.Info($"[CofferFarm] op=reset reason={reason}");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        if (IsRunning)
        {
            Stop("Overworld coffer route disposal");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsRunning)
        {
            return;
        }

        if (deathRecoveryController.State != DeathRecoveryState.Idle)
        {
            Stop($"Overworld coffer route interrupted because death recovery became active. state={deathRecoveryController.State}");
            return;
        }

        if (!scanner.Snapshot.IsInSouthHorn)
        {
            SetFailure("Left South Horn while the overworld coffer route was active.");
            return;
        }

        switch (State)
        {
            case TreasureCofferFarmState.Starting:
            case TreasureCofferFarmState.AdvancingRoute:
                AdvanceRoute();
                break;
            case TreasureCofferFarmState.TravelingToSpot:
                TickTravelingToSpot();
                break;
            case TreasureCofferFarmState.ClearingPreviousHideThreshold:
                TickClearingPreviousHideThreshold();
                break;
            case TreasureCofferFarmState.TravelingToDangerousSpot:
                TickTravelingToDangerousSpot();
                break;
            case TreasureCofferFarmState.WaitingForInteractionHandoff:
                TickWaitingForInteractionHandoff();
                break;
            case TreasureCofferFarmState.InteractingWithCoffer:
                TickInteractingWithCoffer();
                break;
            case TreasureCofferFarmState.ReturningToBase:
                TickReturningToBase();
                break;
        }
    }

    private void AdvanceRoute()
    {
        while (true)
        {
            var nextIndex = CurrentRouteIndex + 1;
            if (nextIndex >= data.VisibleCofferFarmRoute.Count)
            {
                BeginReturnToBase();
                return;
            }

            var routeEntry = data.VisibleCofferFarmRoute[nextIndex];
            if (!spotsByKey.TryGetValue(BuildKey(routeEntry.Area, routeEntry.Label), out var spot))
            {
                SetFailure($"Overworld coffer route entry {routeEntry.Area}:{routeEntry.Label} is missing spot data.");
                return;
            }

            if (ShouldSkipForRouteRules(spot, out var routeRuleSkipReason))
            {
                lock (gate)
                {
                    currentRouteIndex = nextIndex;
                    activeRouteEntry = routeEntry;
                    activeSpot = spot;
                    activeResolvedPosition = spot.Position.ToVector3();
                    activeSpotUsesOverride = false;
                    lastMatchedCoffer = null;
                    lastVisibleCofferScanAt = DateTimeOffset.MinValue;
                    lastMatchSource = string.Empty;
                    pendingInteractionMatch = null;
                    activePreviousThresholdSpot = null;
                    activeSpotRequiresHiddenTravel = false;
                }

                logger.Info($"{BuildLogTag()} op=spot-skip spot={spot.Area}:{spot.Label} reason={routeRuleSkipReason}");
                continue;
            }

            if (ShouldSkipSpot(spot))
            {
                lock (gate)
                {
                    currentRouteIndex = nextIndex;
                    activeRouteEntry = routeEntry;
                    activeSpot = spot;
                    activeResolvedPosition = spot.Position.ToVector3();
                    activeSpotUsesOverride = false;
                    lastMatchedCoffer = null;
                    lastVisibleCofferScanAt = DateTimeOffset.MinValue;
                    lastMatchSource = string.Empty;
                    pendingInteractionMatch = null;
                    activePreviousThresholdSpot = GetPreviousThresholdSpot(nextIndex);
                    activeSpotRequiresHiddenTravel = RequiresHiddenTravel(spot);
                }

                logger.Info($"{BuildLogTag()} op=spot-skip spot={spot.Area}:{spot.Label} aggroLevel={spot.AggroLevel} maxAggro={configuration.VisibleTreasureCofferMaximumAggroLevel} hideThreshold={(spot.HideThresholdDistance?.ToString() ?? "none")} reason=dangerous-visible-travel-disabled");
                continue;
            }

            var resolvedPosition = spot.Position.ToVector3();
            var usesOverride = overrideStore.TryResolvePosition(spot.Area, spot.Label, out var overridePosition);
            if (usesOverride)
            {
                resolvedPosition = overridePosition;
            }

            lock (gate)
            {
                currentRouteIndex = nextIndex;
                activeRouteEntry = routeEntry;
                activeSpot = spot;
                activeResolvedPosition = resolvedPosition;
                activeSpotUsesOverride = usesOverride;
                lastMatchedCoffer = null;
                lastVisibleCofferScanAt = DateTimeOffset.MinValue;
                lastMatchSource = string.Empty;
                pendingInteractionMatch = null;
                activePreviousThresholdSpot = GetPreviousThresholdSpot(nextIndex);
                activeSpotRequiresHiddenTravel = RequiresHiddenTravel(spot);
            }

            if (BeginTravelToActiveSpot())
            {
                return;
            }

            if (State == TreasureCofferFarmState.Failed || State == TreasureCofferFarmState.Stopped)
            {
                return;
            }
        }
    }

    private bool BeginTravelToActiveSpot()
    {
        var spot = ActiveSpot;
        if (spot == null)
        {
            SetFailure("Overworld coffer route lost its active spot before travel started.");
            return false;
        }

        if (!TryVerifyInventorySpaceForActiveSpot("before routing to the next overworld coffer candidate"))
        {
            return false;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        var destination = ActiveResolvedPosition;
        var arrivalDistance = GetArrivalDistance(spot);
        var previousThresholdActive = playerPosition.HasValue && IsWithinHideThreshold(activePreviousThresholdSpot, playerPosition.Value);
        var aggroExceededMax = IsDangerousByAggro(spot);
        var helperOverride = GetHelperOverrideLabel(spot);
        if (RequiresDangerousTravel(spot))
        {
            logger.Info($"{BuildLogTag()} op=travel-mode-select mode=dangerous-destination spot={DescribeActiveSpot()} aggroLevel={spot.AggroLevel} maxAggro={configuration.VisibleTreasureCofferMaximumAggroLevel} aggroExceededMax={aggroExceededMax} helperOverride={helperOverride} previousThresholdActive={previousThresholdActive} previousSpot={DescribeSpot(activePreviousThresholdSpot)} currentRequiresHidden={activeSpotRequiresHiddenTravel} playerPos={FormatVector(playerPosition)} destination={FormatVector(destination)} arrivalDistance={arrivalDistance:0.0}");
            return BeginDangerousTravelToActiveSpot(spot, destination, arrivalDistance);
        }

        if (previousThresholdActive)
        {
            logger.Info($"{BuildLogTag()} op=travel-mode-select mode=clear-previous-threshold spot={DescribeActiveSpot()} aggroLevel={spot.AggroLevel} maxAggro={configuration.VisibleTreasureCofferMaximumAggroLevel} aggroExceededMax={aggroExceededMax} helperOverride={helperOverride} previousThresholdActive=true previousSpot={DescribeSpot(activePreviousThresholdSpot)} currentRequiresHidden={activeSpotRequiresHiddenTravel} playerPos={FormatVector(playerPosition)} destination={FormatVector(destination)} arrivalDistance={arrivalDistance:0.0}");
            return BeginPreviousThresholdCarryoverTravel(spot, destination, arrivalDistance);
        }

        logger.Info($"{BuildLogTag()} op=travel-mode-select mode=normal spot={DescribeActiveSpot()} aggroLevel={spot.AggroLevel} maxAggro={configuration.VisibleTreasureCofferMaximumAggroLevel} aggroExceededMax={aggroExceededMax} helperOverride={helperOverride} previousThresholdActive=false previousSpot={DescribeSpot(activePreviousThresholdSpot)} currentRequiresHidden={activeSpotRequiresHiddenTravel} playerPos={FormatVector(playerPosition)} destination={FormatVector(destination)} arrivalDistance={arrivalDistance:0.0}");

        movementController.SetLogOwner(currentRunId);
        if (!movementController.StartDirectMove($"Overworld coffer route {spot.Label}", destination, arrivalDistance))
        {
            logger.Warning($"{BuildLogTag()} op=travel-start-failed spot={spot.Area}:{spot.Label} destination=<{destination.X:0.0}, {destination.Y:0.0}, {destination.Z:0.0}> arrivalDistance={arrivalDistance:0.0} reason={(movementController.LastError.Length == 0 ? $"Failed to start travel to overworld coffer route spot {spot.Area}:{spot.Label}." : movementController.LastError)}");
            TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Skipping overworld coffer route spot {spot.Area}:{spot.Label} because direct travel could not start.");
            return false;
        }

        TransitionTo(TreasureCofferFarmState.TravelingToSpot, $"Traveling to overworld coffer route spot {spot.Area}:{spot.Label} with direct-only movement.");
        return true;
    }

    private bool BeginPreviousThresholdCarryoverTravel(VisibleCofferFarmSpotData spot, Vector3 destination, float arrivalDistance)
    {
        if (!EnsureVisibleRouteHiddenReady($"threshold carryover for {spot.Area}:{spot.Label}"))
        {
            TransitionTo(TreasureCofferFarmState.ClearingPreviousHideThreshold, $"Preparing Hide while leaving previous threshold before traveling to {spot.Area}:{spot.Label}.");
            return true;
        }

        movementController.SetLogOwner(currentRunId);
        if (!movementController.StartDirectMove($"Hidden threshold carryover for {spot.Label}", destination, arrivalDistance, shouldMountBeforeStep: false))
        {
            SetFailure(movementController.LastError.Length == 0
                ? $"Failed to start hidden threshold carryover travel for {spot.Area}:{spot.Label}."
                : movementController.LastError);
            return false;
        }

        logger.Info($"{BuildLogTag()} op=previous-threshold-start spot={DescribeActiveSpot()} previousSpot={DescribeSpot(activePreviousThresholdSpot)} threshold={GetHideThresholdDistance(activePreviousThresholdSpot):0.0} destination={FormatVector(destination)} arrivalDistance={arrivalDistance:0.0}");
        TransitionTo(TreasureCofferFarmState.ClearingPreviousHideThreshold, $"Traveling hidden while leaving previous threshold toward {spot.Area}:{spot.Label}.");
        return true;
    }

    private bool BeginDangerousTravelToActiveSpot(VisibleCofferFarmSpotData spot, Vector3 destination, float arrivalDistance)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue)
        {
            SetFailure($"Overworld coffer route could not start dangerous travel for {spot.Area}:{spot.Label} because the player position is unavailable.");
            return false;
        }

        var dangerousSpot = ToDangerousTravelCandidate(spot, playerPosition.Value, destination);
        var dangerousOptions = new DangerousTreasureTravelOptions(
            configuration.VisibleCofferNinjaGearsetNumber,
            configuration.VisibleCofferHideThresholdDistance,
            configuration.VisibleTreasureCofferMaximumAggroLevel);
        if (!dangerousTreasureTravelController.Start(null, dangerousSpot, destination, arrivalDistance, dangerousOptions))
        {
            if (dangerousTreasureTravelController.LastResult == DangerousTreasureTravelResult.CandidateSkipped)
            {
                logger.Warning($"{BuildLogTag()} op=dangerous-travel-skip spot={spot.Area}:{spot.Label} reason={dangerousTreasureTravelController.LastTransition}");
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, dangerousTreasureTravelController.LastTransition);
                return false;
            }

            SetFailure(dangerousTreasureTravelController.LastError.Length == 0
                ? $"Failed to start dangerous overworld coffer travel for {spot.Area}:{spot.Label}."
                : dangerousTreasureTravelController.LastError);
            return false;
        }

        TransitionTo(TreasureCofferFarmState.TravelingToDangerousSpot, $"Traveling to dangerous overworld coffer route spot {spot.Area}:{spot.Label} with Ninja/Hide movement.");
        return true;
    }

    private void TickTravelingToSpot()
    {
        if (TryStartInteractionForActiveSpot(requireApproachThreshold: true, acquisitionSource: "approach"))
        {
            return;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                OnArrivedAtSpot();
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                logger.Warning($"{BuildLogTag()} op=travel-failed spot={DescribeActiveSpot()} movementState={movementController.State} reason={(movementController.LastError.Length == 0 ? $"Failed to reach overworld coffer route spot {DescribeActiveSpot()}." : movementController.LastError)}");
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Skipping overworld coffer route spot {DescribeActiveSpot()} because direct travel failed.");
                return;
        }

        logger.DebugThrottled(
            "visible-coffer-farm-travel",
            WaitLogInterval,
            $"Overworld coffer route is traveling to {DescribeActiveSpot()}. movementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
    }

    private void TickClearingPreviousHideThreshold()
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue)
        {
            SetFailure($"Overworld coffer route lost player position while clearing the previous hide threshold for {DescribeActiveSpot()}.");
            return;
        }

        if (!EnsureVisibleRouteHiddenReady($"threshold carryover for {DescribeActiveSpot()}"))
        {
            return;
        }

        if (!IsWithinHideThreshold(activePreviousThresholdSpot, playerPosition.Value))
        {
            logger.Info($"{BuildLogTag()} op=previous-threshold-cleared spot={DescribeActiveSpot()} previousSpot={DescribeSpot(activePreviousThresholdSpot)} playerPos={FormatVector(playerPosition)} destination={FormatVector(ActiveResolvedPosition)}");
            logger.ResetThrottle("visible-coffer-farm-threshold-carryover");
            movementController.Stop($"Cleared previous hide threshold while routing to {DescribeActiveSpot()}.");
            activePreviousThresholdSpot = null;
            if (RequiresDangerousTravel(ActiveSpot!))
            {
                BeginDangerousTravelToActiveSpot(ActiveSpot!, ActiveResolvedPosition, GetArrivalDistance(ActiveSpot));
                return;
            }

            movementController.SetLogOwner(currentRunId);
            if (!movementController.StartDirectMove($"Overworld coffer route {ActiveSpot!.Label}", ActiveResolvedPosition, GetArrivalDistance(ActiveSpot)))
            {
                SetFailure(movementController.LastError.Length == 0
                    ? $"Failed to resume normal travel after clearing the previous hide threshold for {DescribeActiveSpot()}."
                    : movementController.LastError);
                return;
            }

            TransitionTo(TreasureCofferFarmState.TravelingToSpot, $"Cleared previous hide threshold; resuming normal travel to {DescribeActiveSpot()}.");
            return;
        }

        if (TryStartInteractionForActiveSpot(requireApproachThreshold: true, acquisitionSource: "approach"))
        {
            return;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                OnArrivedAtSpot();
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                logger.Warning($"{BuildLogTag()} op=previous-threshold-travel-failed spot={DescribeActiveSpot()} movementState={movementController.State} reason={(movementController.LastError.Length == 0 ? $"Failed hidden threshold carryover travel for {DescribeActiveSpot()}." : movementController.LastError)}");
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Skipping overworld coffer route spot {DescribeActiveSpot()} because hidden threshold carryover travel failed.");
                return;
        }

        logger.DebugThrottled(
            "visible-coffer-farm-threshold-carryover",
            WaitLogInterval,
            $"Overworld coffer route is clearing the previous hide threshold while traveling to {DescribeActiveSpot()}. previousSpot={DescribeSpot(activePreviousThresholdSpot)} distanceToPrevious={(activePreviousThresholdSpot == null ? float.NaN : CalculateFlatDistance(playerPosition.Value, activePreviousThresholdSpot.Position.ToVector3())):0.0} threshold={GetHideThresholdDistance(activePreviousThresholdSpot):0.0} remainingToSpot={CalculateFlatDistance(playerPosition.Value, ActiveResolvedPosition):0.0} movementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
    }

    private void TickTravelingToDangerousSpot()
    {
        if (TryStartInteractionForActiveSpot(requireApproachThreshold: true, acquisitionSource: "approach"))
        {
            return;
        }

        if (HandleDangerousTravelTerminalResult())
        {
            return;
        }

        logger.DebugThrottled(
            "visible-coffer-farm-dangerous-travel",
            WaitLogInterval,
            $"Overworld coffer route is running dangerous travel to {DescribeActiveSpot()}. dangerousState={dangerousTreasureTravelController.State} transition={dangerousTreasureTravelController.LastTransition}.");
    }

    private void OnArrivedAtSpot()
    {
        if (!TryHandleArrivalActions())
        {
            return;
        }

        if (ActiveSpot?.RouteOnly == true)
        {
            TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Reached helper route spot {DescribeActiveSpot()}; continuing to the next route entry.");
            return;
        }

        if (TryStartInteractionForActiveSpot(requireApproachThreshold: false, acquisitionSource: "final"))
        {
            return;
        }

        logger.Info($"{BuildLogTag()} op=final-scan-miss spot={DescribeActiveSpot()} arrivalDistance={GetArrivalDistance(ActiveSpot):0.0} acquisitionDistance={VisibleCofferAcquisitionDistance:0.0}y");
        TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"No overworld coffer matched {DescribeActiveSpot()} on final arrival scan; continuing to the next route entry.");
    }

    private void TickInteractingWithCoffer()
    {
        if (cofferInteractionController.IsRunning)
        {
            logger.DebugThrottled(
                "visible-coffer-farm-interaction",
                WaitLogInterval,
                $"Overworld coffer route is interacting with a matched coffer at {DescribeActiveSpot()}. interactionState={cofferInteractionController.State} attempts={cofferInteractionController.InteractionAttemptCount}.");
            return;
        }

        switch (cofferInteractionController.LastResult)
        {
            case CofferInteractionResult.Opened:
                if (LastMatchedCoffer != null)
                {
                    logger.Info(
                        $"{BuildLogTag()} op=coffer-opened spot={DescribeActiveSpot()} source={FormatValue(lastMatchSource)} baseId={LastMatchedCoffer.DataId} objectId={LastMatchedCoffer.GameObjectId:X} pos=<{LastMatchedCoffer.Position.X:0.000}, {LastMatchedCoffer.Position.Y:0.000}, {LastMatchedCoffer.Position.Z:0.000}> name='{LastMatchedCoffer.Name}'");
                }

                PersistActiveSpotOverride();
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Opened overworld coffer at {DescribeActiveSpot()}; continuing to the next route entry.");
                return;
            case CofferInteractionResult.LostCoffer:
            case CofferInteractionResult.TimedOut:
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Overworld coffer interaction ended without a confirmed open at {DescribeActiveSpot()}; continuing to the next route entry.");
                return;
            case CofferInteractionResult.Stopped:
                TransitionTo(TreasureCofferFarmState.Stopped, cofferInteractionController.LastTransition, error: cofferInteractionController.LastError, result: TreasureCofferFarmResult.Stopped);
                return;
            default:
                SetFailure(cofferInteractionController.LastError.Length == 0
                    ? cofferInteractionController.LastTransition
                    : cofferInteractionController.LastError);
                return;
        }
    }

    private void BeginReturnToBase()
    {
        if (!movementController.RecoverToBaseCamp())
        {
            logger.Warning($"{BuildLogTag()} op=return-start-failed spot={DescribeActiveSpot()} reason={(movementController.LastError.Length == 0 ? "Completed the overworld coffer route, but failed to start Base Camp recovery with Return routing; retrying with direct recovery." : movementController.LastError)}");

            if (!movementController.RecoverToBaseCamp(allowReturn: false))
            {
                logger.Warning($"{BuildLogTag()} op=direct-recovery-start-failed spot={DescribeActiveSpot()} reason={(movementController.LastError.Length == 0 ? "Completed the overworld coffer route, but failed to start Base Camp recovery." : movementController.LastError)}");
                TransitionTo(TreasureCofferFarmState.Completed, "Completed the overworld coffer route, but failed to start Base Camp recovery.", result: TreasureCofferFarmResult.CompletedWithoutBaseRecovery);
                return;
            }

            TransitionTo(TreasureCofferFarmState.ReturningToBase, "Completed the overworld coffer route; returning to Base Camp with direct recovery after Return routing was unavailable.");
            return;
        }

        TransitionTo(TreasureCofferFarmState.ReturningToBase, "Completed the overworld coffer route; returning to Base Camp with normal recovery routing.");
    }

    private void TickReturningToBase()
    {
        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Overworld coffer route returned to Base Camp.");
                TransitionTo(TreasureCofferFarmState.Completed, "Completed the overworld coffer route and returned to Base Camp.", result: TreasureCofferFarmResult.ReturnedToBase);
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                logger.Warning($"{BuildLogTag()} op=return-failed movementState={movementController.State} reason={(movementController.LastError.Length == 0 ? "Base Camp recovery failed after completing the overworld coffer route." : movementController.LastError)}");
                TransitionTo(TreasureCofferFarmState.Completed, "Completed the overworld coffer route, but Base Camp recovery failed.", result: TreasureCofferFarmResult.CompletedWithoutBaseRecovery);
                return;
        }

        logger.DebugThrottled(
            "visible-coffer-farm-returning-base",
            WaitLogInterval,
            $"Overworld coffer route is returning to Base Camp. movementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
    }

    private bool TryStartInteractionForActiveSpot(bool requireApproachThreshold, string acquisitionSource)
    {
        var spot = ActiveSpot;
        if (spot == null || spot.RouteOnly)
        {
            return false;
        }

        if (!ShouldRunVisibleCofferScan(requireApproachThreshold, out var remainingDistanceToSpot))
        {
            return false;
        }

        if (!TryMatchVisibleCoffer(spot, ActiveResolvedPosition, VisibleCofferAcquisitionDistance, acquisitionSource, remainingDistanceToSpot, out var matchedCoffer, out var matchDistance))
        {
            return false;
        }

        var confirmedCoffer = matchedCoffer!;
        var playerPosition = objectTable.LocalPlayer?.Position ?? confirmedCoffer.Position;

        lock (gate)
        {
            lastMatchedCoffer = confirmedCoffer;
            lastMatchSource = acquisitionSource;
        }

        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop($"Matched overworld coffer for {spot.Area}:{spot.Label}.");
        }

        if (dangerousTreasureTravelController.IsRunning)
        {
            dangerousTreasureTravelController.Stop($"Matched overworld coffer for {spot.Area}:{spot.Label}.");
        }

        var interactionMatch = new VisibleCofferMatch
        {
            Flow = CofferInteractionFlow.VisibleRoute,
            CandidateKey = new TreasureCandidateKey
            {
                Label = spot.Label,
                CandidateKey = spot.Label,
            },
            Coffer = confirmedCoffer,
            MatchDistance = matchDistance,
            IsTrustworthy = matchDistance <= MatchConfidenceRadius,
            RequiresJumpAssist = string.Equals(spot.Note, "requires_jump", StringComparison.OrdinalIgnoreCase),
            MustStayHidden = MustStayHiddenDuringInteraction(playerPosition),
            HiddenContextReason = BuildHiddenInteractionReason(playerPosition),
            AttributionReason = $"Matched overworld coffer during {acquisitionSource} scan for {spot.Area}:{spot.Label}. routeDistance={matchDistance:0.0}y playerDistance={confirmedCoffer.DistanceToPlayer:0.0}y remainingToSpot={remainingDistanceToSpot:0.0}y acquisitionDistance={VisibleCofferAcquisitionDistance:0.0}y.",
        };

        logger.Info($"{BuildLogTag()} op=hidden-context-eval spot={spot.Area}:{spot.Label} source={acquisitionSource} mustStayHidden={interactionMatch.MustStayHidden} hiddenReason={FormatValue(interactionMatch.HiddenContextReason)} previousThresholdActive={IsWithinHideThreshold(activePreviousThresholdSpot, playerPosition)} currentThresholdActive={IsWithinHideThreshold(ActiveSpot, playerPosition)} playerDistance={confirmedCoffer.DistanceToPlayer:0.0}y remainingToSpot={remainingDistanceToSpot:0.0}y");

        logger.Info(
            $"{BuildLogTag()} op=coffer-match spot={spot.Area}:{spot.Label} source={acquisitionSource} baseId={confirmedCoffer.DataId} objectId={confirmedCoffer.GameObjectId:X} routeDistance={matchDistance:0.0}y playerDistance={confirmedCoffer.DistanceToPlayer:0.0}y pos=<{confirmedCoffer.Position.X:0.000}, {confirmedCoffer.Position.Y:0.000}, {confirmedCoffer.Position.Z:0.000}> name='{confirmedCoffer.Name}'");

        lock (gate)
        {
            pendingInteractionMatch = interactionMatch;
        }

        logger.Info($"{BuildLogTag()} op=coffer-handoff-pending spot={spot.Area}:{spot.Label} source={acquisitionSource} reason=waiting-for-vnavmesh-settle");
        logger.Info($"{BuildLogTag()} op=coffer-handoff-mode spot={spot.Area}:{spot.Label} source={acquisitionSource} mode={(interactionMatch.MustStayHidden ? "hidden" : "normal")} reason={FormatValue(interactionMatch.HiddenContextReason)}");
        TransitionTo(TreasureCofferFarmState.WaitingForInteractionHandoff, $"Matched overworld coffer for {spot.Area}:{spot.Label} via {acquisitionSource} scan; waiting for movement handoff before interaction.");
        return true;
    }

    private void TickWaitingForInteractionHandoff()
    {
        VisibleCofferMatch? pendingMatch;
        lock (gate)
        {
            pendingMatch = pendingInteractionMatch;
        }

        if (pendingMatch == null)
        {
            SetFailure("Overworld coffer interaction handoff was missing its pending match.");
            return;
        }

        if (movementController.IsPathBusy)
        {
            logger.DebugThrottled(
                "visible-coffer-farm-handoff",
                TimeSpan.FromMilliseconds(250),
                $"Overworld coffer route is waiting for vnavmesh to settle before interacting at {DescribeActiveSpot()}. movementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
            return;
        }

        if (!TryVerifyInventorySpaceForActiveSpot("before opening the matched overworld coffer"))
        {
            lock (gate)
            {
                pendingInteractionMatch = null;
            }

            return;
        }

        logger.Info($"{BuildLogTag()} op=coffer-handoff-start spot={DescribeActiveSpot()} candidate={pendingMatch.CandidateKey.Label} flow={pendingMatch.Flow}");
        if (!cofferInteractionController.Start(pendingMatch))
        {
            lock (gate)
            {
                pendingInteractionMatch = null;
            }

            if (cofferInteractionController.LastResult == CofferInteractionResult.LostCoffer)
            {
                logger.Warning($"{BuildLogTag()} op=interaction-start-lost spot={DescribeActiveSpot()} reason=matched-coffer-vanished");
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Matched overworld coffer vanished before interaction could start at {DescribeActiveSpot()}; continuing to the next route entry.");
                return;
            }

            SetFailure(cofferInteractionController.LastError.Length == 0
                ? "Failed to start overworld coffer interaction."
                : cofferInteractionController.LastError);
            return;
        }

        lock (gate)
        {
            pendingInteractionMatch = null;
        }

        TransitionTo(TreasureCofferFarmState.InteractingWithCoffer, $"Matched overworld coffer for {DescribeActiveSpot()} via handoff; starting interaction.");
    }

    private bool ShouldRunVisibleCofferScan(bool requireApproachThreshold, out float remainingDistanceToSpot)
    {
        remainingDistanceToSpot = float.MaxValue;
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue)
        {
            return false;
        }

        remainingDistanceToSpot = CalculateFlatDistance(playerPosition.Value, ActiveResolvedPosition);
        if (requireApproachThreshold && remainingDistanceToSpot > VisibleCofferApproachScanTriggerDistance)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (requireApproachThreshold && now - lastVisibleCofferScanAt < ApproachScanPollInterval)
        {
            return false;
        }

        lock (gate)
        {
            lastVisibleCofferScanAt = now;
        }

        return true;
    }

    private bool TryMatchVisibleCoffer(
        VisibleCofferFarmSpotData spot,
        Vector3 resolvedPosition,
        float scanRadius,
        string acquisitionSource,
        float remainingDistanceToSpot,
        out VisibleCoffer? matchedCoffer,
        out float matchDistance)
    {
        var best = scanner.Snapshot.VisibleCoffers
            .Select(coffer => new
            {
                Coffer = coffer,
                PlayerDistance = coffer.DistanceToPlayer,
                Distance = CalculateFlatDistance(coffer.Position, resolvedPosition),
            })
            .Where(entry => entry.PlayerDistance <= scanRadius)
            .OrderBy(entry => entry.PlayerDistance)
            .ThenBy(entry => entry.Distance)
            .FirstOrDefault();

        if (best == null)
        {
            matchedCoffer = null;
            matchDistance = float.MaxValue;
            return false;
        }

        var detectedLogPrefix = acquisitionSource == "final"
            ? "VISIBLE_COFFER_FINAL_SCAN_MATCH"
            : "VISIBLE_COFFER_EARLY_DETECTED";
        logger.Info(
            $"{BuildLogTag()} op={FormatValue(detectedLogPrefix)} spot={spot.Area}:{spot.Label} source={acquisitionSource} baseId={best.Coffer.DataId} objectId={best.Coffer.GameObjectId:X} routeDistance={best.Distance:0.0}y playerDistance={best.Coffer.DistanceToPlayer:0.0}y remainingToSpot={remainingDistanceToSpot:0.0}y pos=<{best.Coffer.Position.X:0.000}, {best.Coffer.Position.Y:0.000}, {best.Coffer.Position.Z:0.000}> name='{best.Coffer.Name}'");

        if (best.Distance > MatchConfidenceRadius)
        {
            var rejectedLogPrefix = acquisitionSource == "final"
                ? "VISIBLE_COFFER_FINAL_SCAN_REJECTED"
                : "VISIBLE_COFFER_EARLY_REJECTED";
            logger.Info(
                $"{BuildLogTag()} op={FormatValue(rejectedLogPrefix)} spot={spot.Area}:{spot.Label} source={acquisitionSource} baseId={best.Coffer.DataId} objectId={best.Coffer.GameObjectId:X} routeDistance={best.Distance:0.0}y playerDistance={best.Coffer.DistanceToPlayer:0.0}y remainingToSpot={remainingDistanceToSpot:0.0}y trustRadius={MatchConfidenceRadius:0.0}y pos=<{best.Coffer.Position.X:0.000}, {best.Coffer.Position.Y:0.000}, {best.Coffer.Position.Z:0.000}> name='{best.Coffer.Name}'");
            matchedCoffer = null;
            matchDistance = best.Distance;
            return false;
        }

        matchedCoffer = best.Coffer;
        matchDistance = best.Distance;
        return true;
    }

    private void PersistActiveSpotOverride()
    {
        var spot = ActiveSpot;
        var matched = LastMatchedCoffer;
        if (spot == null || matched == null)
        {
            return;
        }

        if (!overrideStore.SaveConfirmedPosition(spot.Area, spot.Label, matched))
        {
            logger.Warning($"{BuildLogTag()} op=override-save-failed spot={spot.Area}:{spot.Label} reason=save-confirmed-position-returned-false");
        }
    }

    private bool RequiresDangerousTravel(VisibleCofferFarmSpotData spot)
        => RequiresHiddenTravel(spot);

    private bool ShouldSkipSpot(VisibleCofferFarmSpotData spot)
        => RequiresDangerousTravel(spot)
            && !configuration.UseNinjaForDangerousVisibleCoffers;

    private float GetArrivalDistance(VisibleCofferFarmSpotData? spot)
    {
        var configured = spot?.ArrivalDistance ?? configuration.ArrivalDistance;
        return Math.Clamp(configured, 5f, 50f);
    }

    private bool TryVerifyInventorySpaceForActiveSpot(string context)
    {
        if (!InventorySpaceVerifier.TryGetFreeNormalInventorySlots(out var freeSlots, out var inventoryError))
        {
            var reason = inventoryError.Length == 0
                ? $"Automatic overworld coffer route stopped {context} because inventory space could not be verified for {DescribeActiveSpot()}."
                : $"Automatic overworld coffer route stopped {context} because inventory space could not be verified for {DescribeActiveSpot()}. verification={inventoryError}.";
            logger.Warning($"{BuildLogTag()} op=inventory-space-unverified spot={DescribeActiveSpot()} requiredFreeSlots={RequiredInventoryFreeSlots} context=\"{context}\" verification={FormatValue(inventoryError)}");
            TransitionTo(TreasureCofferFarmState.Stopped, reason, error: reason, result: TreasureCofferFarmResult.Stopped);
            return false;
        }

        if (freeSlots >= RequiredInventoryFreeSlots)
        {
            return true;
        }

        var stopReason = $"Automatic overworld coffer route stopped {context} because inventory only has {freeSlots} free slot(s) and requires at least {RequiredInventoryFreeSlots} for {DescribeActiveSpot()}.";
        logger.Warning($"{BuildLogTag()} op=inventory-space-insufficient spot={DescribeActiveSpot()} requiredFreeSlots={RequiredInventoryFreeSlots} freeSlots={freeSlots} context=\"{context}\"");
        TransitionTo(TreasureCofferFarmState.Stopped, stopReason, error: stopReason, result: TreasureCofferFarmResult.Stopped);
        return false;
    }

    private void SetFailure(string reason)
    {
        logger.Warning($"{BuildLogTag()} op=failure state={TreasureCofferFarmState.Failed} spot={DescribeActiveSpot()} reason={reason}");
        TransitionTo(TreasureCofferFarmState.Failed, reason, error: reason, result: TreasureCofferFarmResult.Failed);
    }

    private void TransitionTo(TreasureCofferFarmState nextState, string reason, string? error = null, TreasureCofferFarmResult? result = null)
    {
        TreasureCofferFarmState previousState;
        lock (gate)
        {
            previousState = state;
            state = nextState;
            lastTransition = reason;
            if (result.HasValue)
            {
                lastResult = result.Value;
            }
            else if (nextState == TreasureCofferFarmState.Starting)
            {
                lastResult = TreasureCofferFarmResult.None;
            }
            if (error != null)
            {
                lastError = error;
            }
            else if (nextState is not TreasureCofferFarmState.Failed and not TreasureCofferFarmState.Stopped)
            {
                lastError = string.Empty;
            }
        }

        logger.Info($"{BuildLogTag()} op=transition from={previousState} to={nextState} spot={DescribeActiveSpot()} reason={reason}");
    }

    private string BuildLogTag()
        => currentRunId.Length == 0 ? "[CofferFarm]" : $"[CofferFarm run={currentRunId}]";

    private string DescribeActiveSpot()
        => ActiveSpot == null ? "none" : $"{ActiveSpot.Area}:{ActiveSpot.Label}";

    private string DescribeVisibleCofferScanSummary(Vector3 resolvedPosition)
    {
        var visibleCoffers = scanner.Snapshot.VisibleCoffers;
        if (visibleCoffers.Count == 0)
        {
            return "visibleCount=0.";
        }

        var nearestPlayer = visibleCoffers
            .Where(coffer => coffer.DistanceToPlayer <= VisibleCofferAcquisitionDistance)
            .OrderBy(coffer => coffer.DistanceToPlayer)
            .FirstOrDefault();
        var nearestRoute = visibleCoffers
            .Select(coffer => new
            {
                Coffer = coffer,
                Distance = CalculateFlatDistance(coffer.Position, resolvedPosition),
            })
            .Where(entry => entry.Coffer.DistanceToPlayer <= VisibleCofferAcquisitionDistance)
            .OrderBy(entry => entry.Distance)
            .FirstOrDefault();

        var nearestPlayerText = nearestPlayer == null
            ? "none"
            : $"{nearestPlayer.Name} ({nearestPlayer.GameObjectId:X}) playerDistance={nearestPlayer.DistanceToPlayer:0.0}y";
        var nearestRouteText = nearestRoute == null
            ? "none"
            : $"{nearestRoute.Coffer.Name} ({nearestRoute.Coffer.GameObjectId:X}) routeDistance={nearestRoute.Distance:0.0}y playerDistance={nearestRoute.Coffer.DistanceToPlayer:0.0}y";

        return $"visibleCount={visibleCoffers.Count} nearestPlayer={nearestPlayerText} nearestRoute={nearestRouteText} lastMatchSource={FormatValue(lastMatchSource)}.";
    }

    private static string FormatValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value;

    private static string BuildKey(string area, string label)
        => $"{area}:{label}";

    private bool RequiresHiddenTravel(VisibleCofferFarmSpotData spot)
    {
        if (IsDangerousByAggro(spot))
        {
            return true;
        }

        if (spot.ForceHidden)
        {
            return true;
        }

        if (spot.ForceUnhidden)
        {
            return false;
        }

        return (spot.HideThresholdDistance ?? 0) > 0;
    }

    private bool IsDangerousByAggro(VisibleCofferFarmSpotData spot)
        => spot.AggroLevel > configuration.VisibleTreasureCofferMaximumAggroLevel;

    private static string GetHelperOverrideLabel(VisibleCofferFarmSpotData spot)
    {
        if (spot.ForceHidden)
        {
            return "forceHidden";
        }

        if (spot.ForceUnhidden)
        {
            return "forceUnhidden";
        }

        if (spot.HideOnArrival)
        {
            return "hideOnArrival";
        }

        return "none";
    }

    private bool ShouldSkipForRouteRules(VisibleCofferFarmSpotData spot, out string reason)
    {
        if (configuration.SkipHighLevelCavernsDuringAshkin
            && string.Equals(spot.Area, "Crystallized Caverns", StringComparison.OrdinalIgnoreCase)
            && spot.AggroLevel >= 20
            && IsAshkinTime())
        {
            reason = "ashkin-high-caverns-skip";
            return true;
        }

        if (spot.SpecialBranch is { Length: > 0 } branch && !IsSpecialBranchActive(branch))
        {
            reason = $"special-branch-inactive:{branch}";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private bool IsSpecialBranchActive(string branch)
        => branch switch
        {
            "ascent5_only" => true,
            "ascent7" => configuration.UseNinjaForDangerousVisibleCoffers && !IsAshkinTime(),
            _ => true,
        };

    private static bool IsAshkinTime()
    {
        var eorzeaSecondsToday = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 3600L / 175L) % 86400L;
        return eorzeaSecondsToday >= (22 * 3600 + 30 * 60)
            || eorzeaSecondsToday < (4 * 3600);
    }

    private VisibleCofferFarmSpotData? GetPreviousThresholdSpot(int routeIndex)
    {
        if (routeIndex <= 0)
        {
            return null;
        }

        var previousRouteEntry = data.VisibleCofferFarmRoute[routeIndex - 1];
        if (!spotsByKey.TryGetValue(BuildKey(previousRouteEntry.Area, previousRouteEntry.Label), out var previousSpot))
        {
            return null;
        }

        if (previousSpot.DisableExitHideThreshold || !RequiresHiddenTravel(previousSpot))
        {
            return null;
        }

        return previousSpot;
    }

    private bool MustStayHiddenDuringInteraction(Vector3 playerPosition)
        => activeSpotRequiresHiddenTravel
            || IsWithinHideThreshold(activePreviousThresholdSpot, playerPosition)
            || IsWithinHideThreshold(ActiveSpot, playerPosition);

    private string BuildHiddenInteractionReason(Vector3 playerPosition)
    {
        if (activeSpotRequiresHiddenTravel)
        {
            return $"destination-hidden:{DescribeActiveSpot()}";
        }

        if (IsWithinHideThreshold(activePreviousThresholdSpot, playerPosition))
        {
            return $"previous-threshold:{DescribeSpot(activePreviousThresholdSpot)}";
        }

        if (IsWithinHideThreshold(ActiveSpot, playerPosition))
        {
            return $"current-threshold:{DescribeActiveSpot()}";
        }

        return string.Empty;
    }

    private int GetHideThresholdDistance(VisibleCofferFarmSpotData? spot)
        => Math.Max(10, spot?.HideThresholdDistance ?? configuration.VisibleCofferHideThresholdDistance);

    private bool IsWithinHideThreshold(VisibleCofferFarmSpotData? spot, Vector3 playerPosition)
    {
        if (spot == null || spot.DisableExitHideThreshold || !RequiresHiddenTravel(spot))
        {
            return false;
        }

        return CalculateFlatDistance(playerPosition, spot.Position.ToVector3()) <= GetHideThresholdDistance(spot);
    }

    private bool TryHandleArrivalActions()
    {
        var spot = ActiveSpot;
        if (spot == null)
        {
            return true;
        }

        if (spot.RecheckAscentSafetyOnArrival)
        {
            logger.Info($"{BuildLogTag()} op=route-helper-action action=recheck-ascent-safety-on-arrival result=deferred spot={DescribeActiveSpot()} reason=ashkin-only-branch-port");
        }

        if (spot.HideOnArrival)
        {
            if (!EnsureVisibleRouteHiddenReady($"hide-on-arrival for {DescribeActiveSpot()}"))
            {
                logger.DebugThrottled(
                    "visible-coffer-farm-arrival-action",
                    TimeSpan.FromMilliseconds(250),
                    $"Overworld coffer route is waiting to apply hide-on-arrival at {DescribeActiveSpot()}.");
                return false;
            }

            logger.Info($"{BuildLogTag()} op=route-helper-action action=hide-on-arrival result=applied spot={DescribeActiveSpot()}");
        }

        if (spot.MountOnArrival && !condition[ConditionFlag.Mounted])
        {
            if (condition[ConditionFlag.InCombat])
            {
                SetFailure($"Could not mount on arrival at {DescribeActiveSpot()} because combat started.");
                return false;
            }

            if (!gameActionController.TryExecuteGeneralAction(GameActionController.MountActionId, $"mount-on-arrival for {DescribeActiveSpot()}"))
            {
                logger.DebugThrottled(
                    "visible-coffer-farm-arrival-action",
                    TimeSpan.FromMilliseconds(250),
                    $"Overworld coffer route is waiting to mount on arrival at {DescribeActiveSpot()}.");
                return false;
            }

            logger.Info($"{BuildLogTag()} op=route-helper-action action=mount-on-arrival result=requested spot={DescribeActiveSpot()}");
            return false;
        }

        logger.ResetThrottle("visible-coffer-farm-arrival-action");
        return true;
    }

    private bool EnsureVisibleRouteHiddenReady(string context)
    {
        if (condition[ConditionFlag.InCombat])
        {
            SetFailure($"Combat started while hidden visible-route travel was required. context={context} spot={DescribeActiveSpot()} previousSpot={DescribeSpot(activePreviousThresholdSpot)}");
            return false;
        }

        if (condition[ConditionFlag.Mounted])
        {
            if (!gameActionController.TryExecuteGeneralAction(GameActionController.DismountActionId, $"overworld coffer hidden travel for {DescribeActiveSpot()}"))
            {
                logger.DebugThrottled(
                    "visible-coffer-farm-hidden-ready",
                    TimeSpan.FromMilliseconds(250),
                    $"Overworld coffer route is waiting to dismount before hidden travel. context={context} spot={DescribeActiveSpot()} previousSpot={DescribeSpot(activePreviousThresholdSpot)}");
                return false;
            }

            logger.DebugThrottled(
                "visible-coffer-farm-hidden-ready",
                TimeSpan.FromMilliseconds(250),
                $"Overworld coffer route sent a dismount request before hidden travel. context={context} spot={DescribeActiveSpot()} previousSpot={DescribeSpot(activePreviousThresholdSpot)}");
            return false;
        }

        if (gameActionController.IsStealthed)
        {
            logger.ResetThrottle("visible-coffer-farm-hidden-ready");
            return true;
        }

        if (!gameActionController.CanUseHide())
        {
            logger.DebugThrottled(
                "visible-coffer-farm-hidden-ready",
                TimeSpan.FromMilliseconds(250),
                $"Overworld coffer route is waiting for Hide before hidden travel. context={context} spot={DescribeActiveSpot()} currentClassJob={gameActionController.CurrentClassJobId}");
            return false;
        }

        if (!gameActionController.TryExecuteAction(GameActionController.HideActionId, $"overworld coffer hidden travel for {DescribeActiveSpot()}"))
        {
            SetFailure($"Failed to use Hide before hidden visible-route travel. context={context} spot={DescribeActiveSpot()} previousSpot={DescribeSpot(activePreviousThresholdSpot)}");
            return false;
        }

        logger.DebugThrottled(
            "visible-coffer-farm-hidden-ready",
            TimeSpan.FromMilliseconds(250),
            $"Overworld coffer route requested Hide before hidden travel. context={context} spot={DescribeActiveSpot()} previousSpot={DescribeSpot(activePreviousThresholdSpot)}");
        return false;
    }

    private bool HandleDangerousTravelTerminalResult()
    {
        switch (dangerousTreasureTravelController.State)
        {
            case DangerousTreasureTravelState.Arrived:
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                logger.ResetThrottle("visible-coffer-farm-dangerous-travel");
                OnArrivedAtSpot();
                return true;
            case DangerousTreasureTravelState.CandidateSkipped:
                var skipReason = dangerousTreasureTravelController.LastTransition;
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                logger.ResetThrottle("visible-coffer-farm-dangerous-travel");
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, skipReason);
                return true;
            case DangerousTreasureTravelState.Failed:
                var failureReason = dangerousTreasureTravelController.LastError.Length == 0
                    ? dangerousTreasureTravelController.LastTransition
                    : dangerousTreasureTravelController.LastError;
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                logger.ResetThrottle("visible-coffer-farm-dangerous-travel");
                SetFailure(failureReason);
                return true;
            case DangerousTreasureTravelState.Stopped:
                var stoppedReason = dangerousTreasureTravelController.LastError.Length == 0
                    ? dangerousTreasureTravelController.LastTransition
                    : dangerousTreasureTravelController.LastError;
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                logger.ResetThrottle("visible-coffer-farm-dangerous-travel");
                TransitionTo(TreasureCofferFarmState.Stopped, stoppedReason, error: stoppedReason, result: TreasureCofferFarmResult.Stopped);
                return true;
            default:
                return false;
        }
    }

    private TreasureCofferCandidateData ToDangerousTravelCandidate(VisibleCofferFarmSpotData spot, Vector3 playerPosition, Vector3 destination)
    {
        var distanceToDestination = CalculateFlatDistance(playerPosition, destination);
        var hideThresholdDistance = Math.Max(GetHideThresholdDistance(spot), (int)MathF.Ceiling(distanceToDestination + 5f));
        return new TreasureCofferCandidateData
        {
            CandidateKey = BuildKey(spot.Area, spot.Label),
            Label = $"{spot.Area}:{spot.Label}",
            Position = spot.Position,
            AggroLevel = Math.Max(spot.AggroLevel, configuration.VisibleTreasureCofferMaximumAggroLevel + 1),
            HideThresholdDistance = hideThresholdDistance,
            Notes = spot.Note,
        };
    }

    private static string DescribeSpot(VisibleCofferFarmSpotData? spot)
        => spot == null ? "none" : $"{spot.Area}:{spot.Label}";

    private static string FormatVector(Vector3 value)
        => $"<{value.X:0.000}, {value.Y:0.000}, {value.Z:0.000}>";

    private static string FormatVector(Vector3? value)
        => value.HasValue ? FormatVector(value.Value) : "none";

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private static string ResolvePreferredAethernetName(string area)
        => area switch
        {
            "Southdown Heath" => "BaseCamp",
            "Lost Citadel" => "BaseCamp",
            "Shadowed City" => "Eldergrowth",
            "Eldergrowth" => "Eldergrowth",
            "Stonemarsh" => "Stonemarsh",
            "Heathcliff" => "Stonemarsh",
            "Abandoned Ascent" => "Stonemarsh",
            "Crystallized Caverns" => "CrystallizedCaverns",
            "Vanishing Slope" => "TheWanderersHaven",
            "The Wanderer's Haven" => "TheWanderersHaven",
            _ => string.Empty,
        };
}
