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
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;

namespace AOCCH.Automation;

public sealed class TreasureCofferFarmController : IDisposable
{
    private enum HiddenTravelReadiness
    {
        Ready,
        Pending,
        Failed,
    }

    private readonly record struct WeatherCondition(byte? Id, bool IsUnsafe);

    private static int nextRunSequence;
    private const float MatchConfidenceRadius = 25f;
    private const float VisibleCofferAcquisitionDistance = 60f;
    private const float VisibleCofferApproachScanTriggerDistance = 40f;
    private const int RequiredInventoryFreeSlots = 3;
    private static readonly TimeSpan ApproachScanPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ArrivalMountTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ArrivalMountRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HiddenDismountTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HiddenDismountRequestInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HideStateSettleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HideReadyTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan HideDispatchRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HideVerifyTimeout = TimeSpan.FromSeconds(2);

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly GameActionController gameActionController;
    private readonly DeathRecoveryController deathRecoveryController;
    private readonly DangerousTreasureTravelController dangerousTreasureTravelController;
    private readonly CofferInteractionController cofferInteractionController;
    private readonly VisibleCofferPositionOverrideStore overrideStore;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

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
    private bool activeSpotWeatherForcedHidden;
    private bool activeAscentWeatherRecheckCompleted;
    private DateTimeOffset arrivalMountRequestedAt = DateTimeOffset.MinValue;
    private DateTimeOffset arrivalMountRetryAvailableAt = DateTimeOffset.MinValue;
    private DateTimeOffset hiddenDismountStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset hiddenDismountRequestAvailableAt = DateTimeOffset.MinValue;
    private DateTimeOffset hiddenHideReadyDeadlineAt = DateTimeOffset.MinValue;
    private DateTimeOffset hiddenHideDispatchRetryAt = DateTimeOffset.MinValue;
    private DateTimeOffset hiddenHideVerificationDeadlineAt = DateTimeOffset.MinValue;
    private bool hiddenHideVerificationRetryUsed;

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
        this.overrideStore = overrideStore;
        this.configuration = configuration;
        this.logger = logger;

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

    public bool Start(int? startRouteIndex = null, bool startedByFarmSession = false)
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

        if (!scanner.Snapshot.IsInSupportedTerritory || !scanner.Snapshot.CanRunVisibleCofferRoute)
        {
            SetFailure(scanner.Snapshot.IsInSupportedTerritory
                ? $"Overworld coffer route data is unavailable in {scanner.Snapshot.TerritoryDisplayName}."
                : "Overworld coffer route requires a supported Occult Crescent territory.");
            return false;
        }

        var territory = scanner.ActiveTerritoryData;
        if (territory == null)
        {
            SetFailure("Overworld coffer route requires a supported Occult Crescent territory.");
            return false;
        }

        var dependencyReport = Plugin.Current?.GetNormalAutomationDependencyReport();
        if (dependencyReport is { IsReady: false })
        {
            Plugin.Current?.TryOpenDependencyWindow();
            SetFailure(dependencyReport.FailureSummary);
            return false;
        }

        if (territory.VisibleCofferFarmRoute.Count == 0 || territory.VisibleCofferFarmSpots.Count == 0)
        {
            SetFailure("Overworld coffer route data is missing route or spot entries.");
            return false;
        }

        var validatedStartRouteIndex = startRouteIndex ?? 0;
        if (validatedStartRouteIndex < 0 || validatedStartRouteIndex >= territory.VisibleCofferFarmRoute.Count)
        {
            SetFailure($"Overworld coffer route start index {validatedStartRouteIndex} is out of range for {territory.VisibleCofferFarmRoute.Count} entries.");
            return false;
        }

        var startingRouteEntry = territory.VisibleCofferFarmRoute[validatedStartRouteIndex];

        lock (gate)
        {
            lastResult = TreasureCofferFarmResult.None;
            currentRunId = $"CofferFarm#{Interlocked.Increment(ref nextRunSequence)}";
            currentRouteIndex = validatedStartRouteIndex - 1;
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
            activeSpotWeatherForcedHidden = false;
            activeAscentWeatherRecheckCompleted = false;
            ResetArrivalMountState();
            ResetHiddenTravelReadiness();
        }

        movementController.SetLogOwner(currentRunId);
        logger.Info($"{BuildLogTag()} op=start territoryKey={scanner.Snapshot.TerritoryKey} supported={scanner.Snapshot.IsInSupportedTerritory} routeEntries={territory.VisibleCofferFarmRoute.Count} spotEntries={territory.VisibleCofferFarmSpots.Count} startIndex={validatedStartRouteIndex} startSpot={startingRouteEntry.Area}:{startingRouteEntry.Label} playerPos={FormatVector(objectTable.LocalPlayer?.Position)} inventoryRequiredFreeSlots={RequiredInventoryFreeSlots} startedByFarmSession={startedByFarmSession}");
        TransitionTo(TreasureCofferFarmState.Starting, startedByFarmSession
            ? $"Starting automatic overworld coffer route with {territory.VisibleCofferFarmRoute.Count} entries at {startingRouteEntry.Area}:{startingRouteEntry.Label}."
            : $"Starting overworld coffer route with {territory.VisibleCofferFarmRoute.Count} entries at {startingRouteEntry.Area}:{startingRouteEntry.Label}.");
        return true;
    }

    public void Stop(string reason)
    {
        logger.Info($"{BuildLogTag()} op=stop-request state={State} spot={DescribeActiveSpot()} movementState={movementController.State} dangerousState={dangerousTreasureTravelController.State} interactionState={cofferInteractionController.State} reason={reason}");

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
            activeSpotWeatherForcedHidden = false;
            activeAscentWeatherRecheckCompleted = false;
            ResetArrivalMountState();
            ResetHiddenTravelReadiness();
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
            var reason = $"Overworld coffer route stopped without resume because death recovery became active. state={deathRecoveryController.State}";
            Stop(reason);
            deathRecoveryController.RequestImmediateRelease(reason);
            return;
        }

        if (!scanner.Snapshot.IsInSupportedTerritory || !scanner.Snapshot.CanRunVisibleCofferRoute)
        {
            SetFailure("Overworld coffer route stopped because visible-coffer data became unavailable in the active territory.");
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
            case TreasureCofferFarmState.TravelingToThreatenedCoffer:
                TickTravelingToThreatenedCoffer();
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
            var territory = scanner.ActiveTerritoryData;
            if (territory == null)
            {
                SetFailure("Overworld coffer route lost its active territory data.");
                return;
            }

            if (nextIndex >= territory.VisibleCofferFarmRoute.Count)
            {
                BeginReturnToBase();
                return;
            }

            var routeEntry = territory.VisibleCofferFarmRoute[nextIndex];
            if (!GetSpotsByKey().TryGetValue(BuildKey(routeEntry.Area, routeEntry.Label), out var spot))
            {
                SetFailure($"Overworld coffer route entry {routeEntry.Area}:{routeEntry.Label} is missing spot data.");
                return;
            }

            if (ShouldSkipForSafetyRules(spot, out var routeRuleSkipReason))
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
                    activeSpotWeatherForcedHidden = false;
                    activeAscentWeatherRecheckCompleted = false;
                    ResetArrivalMountState();
                    ResetHiddenTravelReadiness();
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
                    activeSpotWeatherForcedHidden = ShouldForceHiddenForWeather(spot);
                    activeSpotRequiresHiddenTravel = RequiresHiddenTravel(spot);
                    activeAscentWeatherRecheckCompleted = false;
                    ResetArrivalMountState();
                    ResetHiddenTravelReadiness();
                }

                logger.Info($"{BuildLogTag()} op=spot-skip spot={spot.Area}:{spot.Label} aggroLevel={spot.AggroLevel} maxAggro={configuration.VisibleTreasureCofferMaximumAggroLevel} hideThreshold={(spot.HideThresholdDistance?.ToString() ?? "none")} reason=dangerous-visible-travel-disabled");
                continue;
            }

            var weatherForcedHidden = ShouldForceHiddenForWeather(spot);
            var resolvedPosition = spot.Position.ToVector3();
            var usesOverride = overrideStore.TryResolvePosition(scanner.Snapshot.TerritoryKey, spot.Area, spot.Label, out var overridePosition);
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
                activeSpotWeatherForcedHidden = weatherForcedHidden;
                activeSpotRequiresHiddenTravel = RequiresHiddenTravel(spot);
                activeAscentWeatherRecheckCompleted = false;
                ResetArrivalMountState();
                ResetHiddenTravelReadiness();
            }

            logger.Info($"{BuildLogTag()} op=route-entry-selected index={nextIndex} spot={spot.Area}:{spot.Label} canonicalPosition={FormatVector(spot.Position.ToVector3())} resolvedPosition={FormatVector(resolvedPosition)} usesOverride={usesOverride} aggroLevel={spot.AggroLevel} requiresHidden={activeSpotRequiresHiddenTravel} hiddenDecision={GetHiddenTravelDecision(spot)} weatherForcedHidden={weatherForcedHidden} weatherId={FormatWeatherId(GetWeatherCondition().Id)} routeOnly={spot.RouteOnly} previousThreshold={DescribeSpot(activePreviousThresholdSpot)}");

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
        var hiddenDecision = GetHiddenTravelDecision(spot);
        if (RequiresDangerousTravel(spot))
        {
            logger.Info($"{BuildLogTag()} op=travel-mode-select mode=dangerous-destination spot={DescribeActiveSpot()} aggroLevel={spot.AggroLevel} maxAggro={configuration.VisibleTreasureCofferMaximumAggroLevel} aggroExceededMax={aggroExceededMax} helperOverride={helperOverride} hiddenDecision={hiddenDecision} previousThresholdActive={previousThresholdActive} previousSpot={DescribeSpot(activePreviousThresholdSpot)} currentRequiresHidden={activeSpotRequiresHiddenTravel} playerPos={FormatVector(playerPosition)} destination={FormatVector(destination)} arrivalDistance={arrivalDistance:0.0}");
            return BeginDangerousTravelToActiveSpot(spot, destination, arrivalDistance);
        }

        if (previousThresholdActive)
        {
            logger.Info($"{BuildLogTag()} op=travel-mode-select mode=clear-previous-threshold spot={DescribeActiveSpot()} aggroLevel={spot.AggroLevel} maxAggro={configuration.VisibleTreasureCofferMaximumAggroLevel} aggroExceededMax={aggroExceededMax} helperOverride={helperOverride} hiddenDecision={hiddenDecision} previousThresholdActive=true previousSpot={DescribeSpot(activePreviousThresholdSpot)} currentRequiresHidden={activeSpotRequiresHiddenTravel} playerPos={FormatVector(playerPosition)} destination={FormatVector(destination)} arrivalDistance={arrivalDistance:0.0}");
            return BeginPreviousThresholdCarryoverTravel(spot, destination, arrivalDistance);
        }

        logger.Info($"{BuildLogTag()} op=travel-mode-select mode=normal spot={DescribeActiveSpot()} aggroLevel={spot.AggroLevel} maxAggro={configuration.VisibleTreasureCofferMaximumAggroLevel} aggroExceededMax={aggroExceededMax} helperOverride={helperOverride} hiddenDecision={hiddenDecision} previousThresholdActive=false previousSpot={DescribeSpot(activePreviousThresholdSpot)} currentRequiresHidden={activeSpotRequiresHiddenTravel} playerPos={FormatVector(playerPosition)} destination={FormatVector(destination)} arrivalDistance={arrivalDistance:0.0}");

        return StartNormalTravelToActiveSpot(spot, destination, arrivalDistance, "initial route start");
    }

    private bool StartNormalTravelToActiveSpot(VisibleCofferFarmSpotData spot, Vector3 destination, float arrivalDistance, string context)
    {
        var preferredAethernet = ResolvePreferredAethernetName(spot.Area);
        movementController.SetLogOwner(currentRunId);
        if (movementController.PlanRouteToLocation($"Overworld coffer route {spot.Label}", preferredAethernet, destination, arrivalDistance)
            && movementController.StartPlannedRoute())
        {
            TransitionTo(TreasureCofferFarmState.TravelingToSpot, $"Traveling to overworld coffer route spot {spot.Area}:{spot.Label} using territory route planning. context={context}");
            return true;
        }

        var failure = movementController.LastError.Length == 0
            ? $"Failed to start territory route planning for overworld coffer spot {spot.Area}:{spot.Label}."
            : movementController.LastError;
        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop($"Overworld coffer route planning failed. context={context}");
        }

        SetFailure($"Overworld coffer route could not start. territoryKey={scanner.Snapshot.TerritoryKey} routeIndex={CurrentRouteIndex} spot={spot.Area}:{spot.Label} context={context} preferredAethernet={preferredAethernet} reason={failure}");
        return false;
    }

    private bool BeginPreviousThresholdCarryoverTravel(VisibleCofferFarmSpotData spot, Vector3 destination, float arrivalDistance)
    {
        var hiddenReadiness = EnsureVisibleRouteHiddenReady($"threshold carryover for {spot.Area}:{spot.Label}");
        if (hiddenReadiness == HiddenTravelReadiness.Failed)
        {
            return false;
        }

        if (hiddenReadiness == HiddenTravelReadiness.Pending)
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
        var dangerousOptions = GetVisibleDangerousTravelOptions();
        var hasKnowledgeThreat = TryGetVisibleKnowledgeThreat(configuration.KnowledgeThreatEnterDistance, out var threat, out var hideAtOrAbove);
        KnowledgeThreatPolicy? knowledgeThreatPolicy = hasKnowledgeThreat && !RequiresHiddenTravel(spot)
            ? GetVisibleKnowledgeThreatPolicy()
            : null;
        logger.Info($"{BuildLogTag()} op=dangerous-travel-start spot={spot.Area}:{spot.Label} playerPos={FormatVector(playerPosition)} destination={FormatVector(destination)} arrivalDistance={arrivalDistance:0.0} aggroLevel={spot.AggroLevel} maxAggro={dangerousOptions.MaximumAggroLevel} hideThreshold={dangerousOptions.HideThresholdDistance} gearset={dangerousOptions.GearsetNumber} knowledgeThreat={hasKnowledgeThreat} threatEntity='{threat?.Name ?? "none"}' threatLevel={threat?.KnowledgeLevel ?? 0} hideAtOrAbove={hideAtOrAbove} previousThresholdSpot={DescribeSpot(activePreviousThresholdSpot)} previousCandidatePassed=none");
        if (!dangerousTreasureTravelController.Start("VisibleCofferFarm", null, dangerousSpot, destination, arrivalDistance, dangerousOptions, knowledgeThreatPolicy))
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
        if (TryStartKnowledgeThreatTravel())
        {
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

        if (EnsureVisibleRouteHiddenReady($"threshold carryover for {DescribeActiveSpot()}") != HiddenTravelReadiness.Ready)
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

            StartNormalTravelToActiveSpot(ActiveSpot!, ActiveResolvedPosition, GetArrivalDistance(ActiveSpot), "post-threshold route resume");
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
            $"Overworld coffer route is running dangerous travel to {DescribeActiveSpot()}. farmState={State} dangerousState={dangerousTreasureTravelController.State} playerPos={FormatVector(objectTable.LocalPlayer?.Position)} destination={FormatVector(ActiveResolvedPosition)} remainingDistance={CalculateFlatDistance(objectTable.LocalPlayer?.Position ?? ActiveResolvedPosition, ActiveResolvedPosition):0.0}y arrivalDistance={GetArrivalDistance(ActiveSpot):0.0}y movementState={movementController.State} pathBusy={movementController.IsPathBusy} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()} mounted={condition[ConditionFlag.Mounted]} stealthed={gameActionController.IsStealthed} inCombat={condition[ConditionFlag.InCombat]} transition={dangerousTreasureTravelController.LastTransition}.");
    }

    private void TickTravelingToThreatenedCoffer()
    {
        switch (dangerousTreasureTravelController.State)
        {
            case DangerousTreasureTravelState.Arrived:
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                logger.Info($"{BuildLogTag()} op=coffer-interaction-knowledge-threat-arrived spot={DescribeActiveSpot()} action=resume-hidden-interaction");
                TransitionTo(TreasureCofferFarmState.WaitingForInteractionHandoff, "Reached the matched coffer under live knowledge threat protection; resuming hidden interaction.");
                return;
            case DangerousTreasureTravelState.CandidateSkipped:
                var skipReason = dangerousTreasureTravelController.LastTransition;
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                logger.Warning($"{BuildLogTag()} op=coffer-interaction-knowledge-threat-terminal state=CandidateSkipped action=advance reason={skipReason}");
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, skipReason);
                return;
            case DangerousTreasureTravelState.Failed:
                var failureReason = dangerousTreasureTravelController.LastError.Length == 0
                    ? dangerousTreasureTravelController.LastTransition
                    : dangerousTreasureTravelController.LastError;
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                logger.Warning($"{BuildLogTag()} op=coffer-interaction-knowledge-threat-terminal state=Failed action=fail reason={failureReason}");
                SetFailure(failureReason);
                return;
            case DangerousTreasureTravelState.Stopped:
                var stoppedReason = dangerousTreasureTravelController.LastError.Length == 0
                    ? dangerousTreasureTravelController.LastTransition
                    : dangerousTreasureTravelController.LastError;
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                TransitionTo(TreasureCofferFarmState.Stopped, stoppedReason, error: stoppedReason, result: TreasureCofferFarmResult.Stopped);
                return;
        }

        logger.DebugThrottled(
            "visible-coffer-farm-threatened-interaction",
            WaitLogInterval,
            $"Overworld coffer interaction is moving hidden after a live knowledge threat. spot={DescribeActiveSpot()} dangerousState={dangerousTreasureTravelController.State} coffer={pendingInteractionMatch?.Coffer.GameObjectId:X} movementState={movementController.State} stealthed={gameActionController.IsStealthed}.");
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

        logger.Info($"{BuildLogTag()} op=final-scan-miss spot={DescribeActiveSpot()} arrivalDistance={GetArrivalDistance(ActiveSpot):0.0} acquisitionDistance={VisibleCofferAcquisitionDistance:0.0}y {DescribeVisibleCofferScanSummary(ActiveResolvedPosition)}");
        TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"No overworld coffer matched {DescribeActiveSpot()} on final arrival scan; continuing to the next route entry.");
    }

    private void TickInteractingWithCoffer()
    {
        if (cofferInteractionController.ActiveMatch is { } activeMatch
            && !activeMatch.MustStayHidden
            && cofferInteractionController.State is CofferInteractionState.ApproachingCoffer or CofferInteractionState.TargetingCoffer
            && TryStartThreatenedCofferTravel(activeMatch, "interaction-approach"))
        {
            return;
        }

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
                logger.Info($"{BuildLogTag()} op=interaction-terminal spot={DescribeActiveSpot()} result={cofferInteractionController.LastResult} state={cofferInteractionController.State} attempts={cofferInteractionController.InteractionAttemptCount} action=advance transition={cofferInteractionController.LastTransition} error={FormatValue(cofferInteractionController.LastError)}");
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
                logger.Info($"{BuildLogTag()} op=interaction-terminal spot={DescribeActiveSpot()} result={cofferInteractionController.LastResult} state={cofferInteractionController.State} attempts={cofferInteractionController.InteractionAttemptCount} action=advance transition={cofferInteractionController.LastTransition} error={FormatValue(cofferInteractionController.LastError)}");
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Overworld coffer interaction ended without a confirmed open at {DescribeActiveSpot()}; continuing to the next route entry.");
                return;
            case CofferInteractionResult.Stopped:
                logger.Info($"{BuildLogTag()} op=interaction-terminal spot={DescribeActiveSpot()} result={cofferInteractionController.LastResult} state={cofferInteractionController.State} attempts={cofferInteractionController.InteractionAttemptCount} action=stop transition={cofferInteractionController.LastTransition} error={FormatValue(cofferInteractionController.LastError)}");
                TransitionTo(TreasureCofferFarmState.Stopped, cofferInteractionController.LastTransition, error: cofferInteractionController.LastError, result: TreasureCofferFarmResult.Stopped);
                return;
            default:
                logger.Warning($"{BuildLogTag()} op=interaction-terminal spot={DescribeActiveSpot()} result={cofferInteractionController.LastResult} state={cofferInteractionController.State} attempts={cofferInteractionController.InteractionAttemptCount} action=fail transition={cofferInteractionController.LastTransition} error={FormatValue(cofferInteractionController.LastError)}");
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
            logger.DebugThrottled(
                $"visible-coffer-scan-skip-{CurrentRouteIndex}",
                TimeSpan.FromSeconds(2),
                $"{BuildLogTag()} op=coffer-scan-skipped spot={DescribeActiveSpot()} source={acquisitionSource} approachScan={requireApproachThreshold} remainingToSpot={remainingDistanceToSpot:0.0}y trigger={VisibleCofferApproachScanTriggerDistance:0.0}y playerPos={FormatVector(objectTable.LocalPlayer?.Position)}");
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
                WaitLogInterval,
                $"Overworld coffer route is waiting for vnavmesh to settle before interacting at {DescribeActiveSpot()}. movementState={movementController.State} pathBusy={movementController.IsPathBusy} playerPos={FormatVector(objectTable.LocalPlayer?.Position)} coffer={pendingMatch.Coffer.GameObjectId:X} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
            return;
        }

        if (!pendingMatch.MustStayHidden && TryStartThreatenedCofferTravel(pendingMatch, "interaction-handoff"))
        {
            return;
        }

        logger.Debug($"{BuildLogTag()} op=coffer-handoff-ready spot={DescribeActiveSpot()} movementState={movementController.State} pathBusy={movementController.IsPathBusy} playerPos={FormatVector(objectTable.LocalPlayer?.Position)} coffer={pendingMatch.Coffer.GameObjectId:X}");

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

    private bool TryStartThreatenedCofferTravel(VisibleCofferMatch match, string source)
    {
        var spot = ActiveSpot;
        if (spot == null
            || dangerousTreasureTravelController.IsRunning
            || !TryGetVisibleKnowledgeThreat(configuration.KnowledgeThreatEnterDistance, out var threat, out var hideAtOrAbove))
        {
            return false;
        }

        if (!configuration.UseNinjaForDangerousVisibleCoffers)
        {
            SetFailure($"Overworld coffer interaction encountered live knowledge threat {threat?.Name ?? "unknown"} but dangerous Ninja travel is disabled.");
            return true;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue)
        {
            SetFailure("Overworld coffer interaction lost player position while responding to a live knowledge threat.");
            return true;
        }

        if (cofferInteractionController.IsRunning)
        {
            cofferInteractionController.Stop("Live knowledge threat entered the coffer interaction Hide range.");
        }

        var hiddenMatch = new VisibleCofferMatch
        {
            Flow = match.Flow,
            CandidateKey = match.CandidateKey,
            Coffer = match.Coffer,
            MatchDistance = match.MatchDistance,
            IsTrustworthy = match.IsTrustworthy,
            RequiresJumpAssist = match.RequiresJumpAssist,
            MustStayHidden = true,
            HiddenContextReason = "live-knowledge-threat",
            DistanceToNearestOtherCandidate = match.DistanceToNearestOtherCandidate,
            AttributionReason = $"{match.AttributionReason} Live knowledge threat handoff source={source} entity='{threat?.Name ?? "unknown"}' entityLevel={threat?.KnowledgeLevel ?? 0} hideAtOrAbove={hideAtOrAbove}.",
        };
        lock (gate)
        {
            pendingInteractionMatch = hiddenMatch;
        }

        var dangerousCandidate = ToDangerousTravelCandidate(spot, playerPosition.Value, match.Coffer.Position);
        var dangerousOptions = GetVisibleDangerousTravelOptions();
        logger.Info($"{BuildLogTag()} op=coffer-interaction-knowledge-threat-enter source={source} spot={DescribeActiveSpot()} coffer={match.Coffer.GameObjectId:X} entity='{threat?.Name ?? "unknown"}' objectId={threat?.ObjectId:X} playerForayLevel={scanner.Snapshot.PlayerForayLevel?.ToString() ?? "unavailable"} offset={configuration.VisibleCofferKnowledgeHideOffset} entityLevel={threat?.KnowledgeLevel ?? 0} hideAtOrAbove={hideAtOrAbove} enterRange={configuration.KnowledgeThreatEnterDistance:0.0} exitRange={configuration.KnowledgeThreatExitDistance:0.0} distance={threat?.DistanceToPlayer:0.0}");
        if (!dangerousTreasureTravelController.Start("VisibleCofferInteraction", null, dangerousCandidate, match.Coffer.Position, 4.5f, dangerousOptions, GetVisibleKnowledgeThreatPolicy()))
        {
            SetFailure(dangerousTreasureTravelController.LastError.Length == 0
                ? "Failed to start Ninja/Hide travel for a threatened coffer interaction."
                : dangerousTreasureTravelController.LastError);
            return true;
        }

        TransitionTo(TreasureCofferFarmState.TravelingToThreatenedCoffer, $"Live knowledge threat interrupted the {source} coffer approach; moving hidden to the matched coffer.");
        return true;
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
            logger.DebugThrottled(
                $"visible-coffer-no-match-{CurrentRouteIndex}",
                TimeSpan.FromSeconds(2),
                $"{BuildLogTag()} op=coffer-scan-no-match spot={spot.Area}:{spot.Label} source={acquisitionSource} visibleCount={scanner.Snapshot.VisibleCoffers.Count} resolvedPosition={FormatVector(resolvedPosition)} remainingToSpot={remainingDistanceToSpot:0.0}y acquisitionRadius={scanRadius:0.0}y");
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

        if (!overrideStore.SaveConfirmedPosition(scanner.Snapshot.TerritoryKey, spot.Area, spot.Label, matched))
        {
            logger.Warning($"{BuildLogTag()} op=override-save-failed spot={spot.Area}:{spot.Label} reason=save-confirmed-position-returned-false");
        }
    }

    private bool RequiresDangerousTravel(VisibleCofferFarmSpotData spot)
    {
        if (spot.ForceUnhidden)
        {
            return false;
        }

        if (RequiresHiddenTravel(spot))
        {
            return true;
        }

        return KnowledgeThreatEvaluator.TryFindThreat(
            scanner.Snapshot,
            GetVisibleKnowledgeThreatPolicy(),
            configuration.KnowledgeThreatEnterDistance,
            out _,
            out _);
    }

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
        if (ReferenceEquals(spot, ActiveSpot) && activeSpotWeatherForcedHidden)
        {
            return true;
        }

        if (spot.ForceUnhidden)
        {
            return false;
        }

        if (spot.ForceHidden)
        {
            return true;
        }

        if (scanner.Snapshot.PlayerForayLevel.HasValue)
        {
            return false;
        }

        if (IsDangerousByAggro(spot))
        {
            return true;
        }

        return (spot.HideThresholdDistance ?? 0) > 0;
    }

    private KnowledgeThreatPolicy GetVisibleKnowledgeThreatPolicy()
        => new(
            configuration.VisibleCofferKnowledgeHideOffset,
            configuration.KnowledgeThreatEnterDistance,
            configuration.KnowledgeThreatExitDistance);

    private DangerousTreasureTravelOptions GetVisibleDangerousTravelOptions()
        => new(
            configuration.VisibleCofferNinjaGearsetNumber,
            configuration.VisibleCofferHideThresholdDistance,
            configuration.VisibleTreasureCofferMaximumAggroLevel);

    private bool TryGetVisibleKnowledgeThreat(float radius, out ForayThreatEntity? threat, out int hideAtOrAbove)
    {
        if (ActiveSpot?.ForceUnhidden == true)
        {
            threat = null;
            hideAtOrAbove = 0;
            return false;
        }

        return KnowledgeThreatEvaluator.TryFindThreat(scanner.Snapshot, GetVisibleKnowledgeThreatPolicy(), radius, out threat, out hideAtOrAbove);
    }

    private bool TryStartKnowledgeThreatTravel()
    {
        var spot = ActiveSpot;
        if (spot == null
            || dangerousTreasureTravelController.IsRunning
            || !TryGetVisibleKnowledgeThreat(configuration.KnowledgeThreatEnterDistance, out var threat, out var hideAtOrAbove))
        {
            return false;
        }

        if (!configuration.UseNinjaForDangerousVisibleCoffers)
        {
            SetFailure($"Overworld coffer route encountered live knowledge threat {threat?.Name ?? "unknown"} but dangerous Ninja travel is disabled.");
            return true;
        }

        movementController.Stop("Live knowledge threat entered the overworld coffer Hide range.");
        logger.Info($"{BuildLogTag()} op=knowledge-threat-enter mode=overworld-coffer spot={DescribeActiveSpot()} entity='{threat?.Name ?? "unknown"}' objectId={threat?.ObjectId:X} playerForayLevel={scanner.Snapshot.PlayerForayLevel?.ToString() ?? "unavailable"} offset={configuration.VisibleCofferKnowledgeHideOffset} entityLevel={threat?.KnowledgeLevel ?? 0} hideAtOrAbove={hideAtOrAbove} enterRange={configuration.KnowledgeThreatEnterDistance:0.0} exitRange={configuration.KnowledgeThreatExitDistance:0.0} distance={threat?.DistanceToPlayer:0.0}");
        BeginDangerousTravelToActiveSpot(spot, ActiveResolvedPosition, GetArrivalDistance(spot));
        return true;
    }

    private string GetHiddenTravelDecision(VisibleCofferFarmSpotData spot)
    {
        if (ReferenceEquals(spot, ActiveSpot) && activeSpotWeatherForcedHidden)
        {
            return "unsafe-weather";
        }

        if (spot.ForceUnhidden)
        {
            return "force-unhidden";
        }

        if (spot.ForceHidden)
        {
            return "force-hidden";
        }

        if (IsDangerousByAggro(spot))
        {
            return "aggro";
        }

        return (spot.HideThresholdDistance ?? 0) > 0 ? "threshold" : "none";
    }

    private bool IsDangerousByAggro(VisibleCofferFarmSpotData spot)
        => spot.AggroLevel > configuration.VisibleTreasureCofferMaximumAggroLevel;

    private static string GetHelperOverrideLabel(VisibleCofferFarmSpotData spot)
    {
        if (spot.ForceUnhidden)
        {
            return "forceUnhidden";
        }

        if (spot.ForceHidden)
        {
            return "forceHidden";
        }

        if (spot.HideOnArrival)
        {
            return "hideOnArrival";
        }

        return "none";
    }

    private bool ShouldSkipForSafetyRules(VisibleCofferFarmSpotData spot, out string reason)
    {
        var weather = GetWeatherCondition();
        if (configuration.SkipUnsafeWeatherRoutes && spot.SkipDuringUnsafeWeather && weather.IsUnsafe)
        {
            reason = $"unsafe-weather-skip:{FormatWeatherId(weather.Id)}";
            return true;
        }

        if (configuration.SkipUnsafeWeatherRoutes
            && spot.RainSensitive
            && weather.IsUnsafe
            && !configuration.UseNinjaForDangerousVisibleCoffers)
        {
            reason = $"unsafe-weather-ninja-disabled:{FormatWeatherId(weather.Id)}";
            return true;
        }

        if (configuration.SkipHighLevelCavernsDuringAshkin
            && spot.SkipDuringAshkin
            && IsAshkinTime())
        {
            reason = "ashkin-route-skip";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private bool ShouldForceHiddenForWeather(VisibleCofferFarmSpotData spot)
    {
        var weather = GetWeatherCondition();
        return configuration.SkipUnsafeWeatherRoutes
            && spot.RainSensitive
            && weather.IsUnsafe
            && configuration.UseNinjaForDangerousVisibleCoffers;
    }

    private unsafe WeatherCondition GetWeatherCondition()
    {
        var envManager = EnvManager.Instance();
        if (envManager == null)
        {
            return new WeatherCondition(null, IsUnsafe: true);
        }

        var weatherId = envManager->ActiveWeather;
        var unsafeWeatherIds = scanner.ActiveTerritoryData?.VisibleCoffers.UnsafeWeatherIds;
        return new WeatherCondition(weatherId, unsafeWeatherIds?.Contains(weatherId) == true);
    }

    private static string FormatWeatherId(byte? weatherId)
        => weatherId.HasValue ? weatherId.Value.ToString() : "unavailable";

    private bool IsAshkinTime()
    {
        var cofferData = scanner.ActiveTerritoryData?.VisibleCoffers;
        if (cofferData?.AshkinStartEorzeaMinute is not { } startMinute
            || cofferData.AshkinEndEorzeaMinute is not { } endMinute)
        {
            return false;
        }

        var eorzeaSecondsToday = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 3600L / 175L) % (24 * 60 * 60);
        var currentMinute = (int)(eorzeaSecondsToday / 60);
        return startMinute <= endMinute
            ? currentMinute >= startMinute && currentMinute < endMinute
            : currentMinute >= startMinute || currentMinute < endMinute;
    }

    private VisibleCofferFarmSpotData? GetPreviousThresholdSpot(int routeIndex)
    {
        if (routeIndex <= 0)
        {
            return null;
        }

        var territory = scanner.ActiveTerritoryData;
        if (territory == null)
        {
            return null;
        }

        var previousRouteEntry = territory.VisibleCofferFarmRoute[routeIndex - 1];
        if (!GetSpotsByKey().TryGetValue(BuildKey(previousRouteEntry.Area, previousRouteEntry.Label), out var previousSpot))
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

        if (configuration.SkipUnsafeWeatherRoutes && spot.RecheckAscentSafetyOnArrival)
        {
            if (!activeAscentWeatherRecheckCompleted)
            {
                var weather = GetWeatherCondition();
                if (weather.IsUnsafe)
                {
                    logger.Warning($"{BuildLogTag()} op=ascent-weather-recheck spot={DescribeActiveSpot()} weatherId={FormatWeatherId(weather.Id)} unsafe=true action=skip-ascent7-retain-hide");
                    TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Skipping the Ascent 7 route after unsafe weather recheck at {DescribeActiveSpot()}. weatherId={FormatWeatherId(weather.Id)}.");
                    return false;
                }

                activeAscentWeatherRecheckCompleted = true;
                logger.Info($"{BuildLogTag()} op=ascent-weather-recheck spot={DescribeActiveSpot()} weatherId={FormatWeatherId(weather.Id)} unsafe=false action=allow-unhide");
            }
        }

        if (spot.HideOnArrival)
        {
            if (EnsureVisibleRouteHiddenReady($"hide-on-arrival for {DescribeActiveSpot()}") != HiddenTravelReadiness.Ready)
            {
                logger.DebugThrottled(
                    "visible-coffer-farm-arrival-action",
                    WaitLogInterval,
                    $"Overworld coffer route is waiting to apply hide-on-arrival at {DescribeActiveSpot()}. mounted={condition[ConditionFlag.Mounted]} stealthed={gameActionController.IsStealthed} canUseHide={gameActionController.CanUseHide()} changeableState=\"{gameActionController.GetChangeableStateSummary()}\".");
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

            var now = DateTimeOffset.UtcNow;
            if (arrivalMountRequestedAt == DateTimeOffset.MinValue)
            {
                arrivalMountRequestedAt = now;
            }

            if (now - arrivalMountRequestedAt >= ArrivalMountTimeout)
            {
                SetFailure($"Timed out mounting on arrival at {DescribeActiveSpot()}. mounted={condition[ConditionFlag.Mounted]} stealthed={gameActionController.IsStealthed} changeableState={gameActionController.GetChangeableStateSummary()}");
                return false;
            }

            if (now >= arrivalMountRetryAvailableAt)
            {
                arrivalMountRetryAvailableAt = now + ArrivalMountRetryInterval;
                if (gameActionController.TryExecuteGeneralAction(GameActionController.MountActionId, $"mount-on-arrival for {DescribeActiveSpot()}"))
                {
                    logger.Info($"{BuildLogTag()} op=route-helper-action action=mount-on-arrival result=requested spot={DescribeActiveSpot()} nextAttemptAt={arrivalMountRetryAvailableAt:O}");
                }
            }

            logger.DebugThrottled(
                "visible-coffer-farm-arrival-action",
                WaitLogInterval,
                $"Overworld coffer route is waiting to mount on arrival at {DescribeActiveSpot()}. elapsed={(now - arrivalMountRequestedAt).TotalSeconds:0.0}s timeout={ArrivalMountTimeout.TotalSeconds:0.0}s nextAttemptIn={Math.Max(0, (arrivalMountRetryAvailableAt - now).TotalSeconds):0.0}s mounted={condition[ConditionFlag.Mounted]} stealthed={gameActionController.IsStealthed} inCombat={condition[ConditionFlag.InCombat]} movementState={movementController.State}.");
            return false;
        }

        ResetArrivalMountState();
        logger.ResetThrottle("visible-coffer-farm-arrival-action");
        logger.ResetThrottle("visible-coffer-farm-ascent-safety");
        return true;
    }

    private void ResetArrivalMountState()
    {
        arrivalMountRequestedAt = DateTimeOffset.MinValue;
        arrivalMountRetryAvailableAt = DateTimeOffset.MinValue;
    }

    private HiddenTravelReadiness EnsureVisibleRouteHiddenReady(string context)
    {
        var now = DateTimeOffset.UtcNow;
        if (condition[ConditionFlag.InCombat])
        {
            SetFailure($"Combat started while hidden visible-route travel was required. context={context} spot={DescribeActiveSpot()} previousSpot={DescribeSpot(activePreviousThresholdSpot)}");
            return HiddenTravelReadiness.Failed;
        }

        if (condition[ConditionFlag.Mounted])
        {
            if (hiddenDismountStartedAt == DateTimeOffset.MinValue)
            {
                hiddenDismountStartedAt = now;
            }

            if (now - hiddenDismountStartedAt >= HiddenDismountTimeout)
            {
                SetFailure($"Timed out dismounting before hidden visible-route travel. context={context} spot={DescribeActiveSpot()} previousSpot={DescribeSpot(activePreviousThresholdSpot)} {gameActionController.GetChangeableStateSummary()}");
                return HiddenTravelReadiness.Failed;
            }

            if (now >= hiddenDismountRequestAvailableAt)
            {
                hiddenDismountRequestAvailableAt = now + HiddenDismountRequestInterval;
                if (gameActionController.TryExecuteGeneralAction(GameActionController.DismountActionId, $"overworld coffer hidden travel for {DescribeActiveSpot()}"))
                {
                    logger.Info($"{BuildLogTag()} op=hidden-travel-dismount-request context={context} spot={DescribeActiveSpot()} nextAttemptAt={hiddenDismountRequestAvailableAt:O}");
                }
                else
                {
                    logger.DebugThrottled("visible-coffer-farm-hidden-dismount-dispatch", TimeSpan.FromSeconds(1), $"Overworld coffer route could not dispatch dismount before hidden travel. context={context} spot={DescribeActiveSpot()} retryAt={hiddenDismountRequestAvailableAt:O} changeableState=\"{gameActionController.GetChangeableStateSummary()}\"");
                }
            }

            logger.DebugThrottled("visible-coffer-farm-hidden-ready", WaitLogInterval, $"Overworld coffer route is waiting to dismount before hidden travel. context={context} spot={DescribeActiveSpot()} previousSpot={DescribeSpot(activePreviousThresholdSpot)} elapsed={(now - hiddenDismountStartedAt).TotalSeconds:0.0}s timeout={HiddenDismountTimeout.TotalSeconds:0.0}s nextAttemptIn={Math.Max(0, (hiddenDismountRequestAvailableAt - now).TotalSeconds):0.0}s changeableState=\"{gameActionController.GetChangeableStateSummary()}\"");
            return HiddenTravelReadiness.Pending;
        }

        if (hiddenDismountStartedAt != DateTimeOffset.MinValue && now - hiddenDismountStartedAt < HideStateSettleDelay)
        {
            logger.DebugThrottled("visible-coffer-farm-hidden-settle", TimeSpan.FromMilliseconds(250), $"Overworld coffer route is settling after dismount before Hide. context={context} spot={DescribeActiveSpot()} elapsed={(now - hiddenDismountStartedAt).TotalSeconds:0.00}s required={HideStateSettleDelay.TotalSeconds:0.00}s.");
            return HiddenTravelReadiness.Pending;
        }

        if (gameActionController.IsStealthed)
        {
            ResetHiddenTravelReadiness();
            return HiddenTravelReadiness.Ready;
        }

        if (hiddenHideVerificationDeadlineAt != DateTimeOffset.MinValue)
        {
            if (now < hiddenHideVerificationDeadlineAt)
            {
                logger.DebugThrottled("visible-coffer-farm-hidden-verify", WaitLogInterval, $"Overworld coffer route is waiting for Hide confirmation before hidden travel. context={context} spot={DescribeActiveSpot()} deadlineIn={(hiddenHideVerificationDeadlineAt - now).TotalSeconds:0.0}s stealthed={gameActionController.IsStealthed} mounted={condition[ConditionFlag.Mounted]}.");
                return HiddenTravelReadiness.Pending;
            }

            if (!hiddenHideVerificationRetryUsed)
            {
                hiddenHideVerificationRetryUsed = true;
                hiddenHideVerificationDeadlineAt = DateTimeOffset.MinValue;
                hiddenHideDispatchRetryAt = now + HideDispatchRetryDelay;
                logger.Warning($"{BuildLogTag()} op=hidden-travel-hide-verify-timeout context={context} spot={DescribeActiveSpot()} action=retry retryAt={hiddenHideDispatchRetryAt:O}");
                return HiddenTravelReadiness.Pending;
            }

            SetFailure($"Hide did not apply before hidden visible-route travel after two attempts. context={context} spot={DescribeActiveSpot()} previousSpot={DescribeSpot(activePreviousThresholdSpot)} mounted={condition[ConditionFlag.Mounted]} stealthed={gameActionController.IsStealthed} changeableState={gameActionController.GetChangeableStateSummary()}");
            return HiddenTravelReadiness.Failed;
        }

        if (hiddenHideReadyDeadlineAt == DateTimeOffset.MinValue)
        {
            hiddenHideReadyDeadlineAt = now + HideReadyTimeout;
        }

        if (now >= hiddenHideReadyDeadlineAt)
        {
            SetFailure($"Hide did not become ready before hidden visible-route travel within {HideReadyTimeout.TotalSeconds:0.0}s. context={context} spot={DescribeActiveSpot()} previousSpot={DescribeSpot(activePreviousThresholdSpot)} currentClassJob={gameActionController.CurrentClassJobId} changeableState={gameActionController.GetChangeableStateSummary()}");
            return HiddenTravelReadiness.Failed;
        }

        if (now < hiddenHideDispatchRetryAt)
        {
            return HiddenTravelReadiness.Pending;
        }

        if (!gameActionController.IsPlayerInChangeableState() || !gameActionController.CanUseHide())
        {
            logger.DebugThrottled("visible-coffer-farm-hidden-ready", WaitLogInterval, $"Overworld coffer route is waiting for Hide before hidden travel. context={context} spot={DescribeActiveSpot()} deadlineIn={(hiddenHideReadyDeadlineAt - now).TotalSeconds:0.0}s currentClassJob={gameActionController.CurrentClassJobId} mounted={condition[ConditionFlag.Mounted]} stealthed={gameActionController.IsStealthed} canUseHide={gameActionController.CanUseHide()} changeableState=\"{gameActionController.GetChangeableStateSummary()}\"");
            return HiddenTravelReadiness.Pending;
        }

        if (!gameActionController.TryExecuteAction(GameActionController.HideActionId, $"overworld coffer hidden travel for {DescribeActiveSpot()}"))
        {
            hiddenHideDispatchRetryAt = now + HideDispatchRetryDelay;
            logger.DebugThrottled("visible-coffer-farm-hidden-dispatch", TimeSpan.FromSeconds(1), $"Overworld coffer route received an ambiguous Hide dispatch result before hidden travel. context={context} spot={DescribeActiveSpot()} retryAt={hiddenHideDispatchRetryAt:O} changeableState=\"{gameActionController.GetChangeableStateSummary()}\"");
            return HiddenTravelReadiness.Pending;
        }

        hiddenHideVerificationDeadlineAt = now + HideVerifyTimeout;
        logger.Info($"{BuildLogTag()} op=hidden-travel-hide-request context={context} spot={DescribeActiveSpot()} verifyDeadline={hiddenHideVerificationDeadlineAt:O}");
        return HiddenTravelReadiness.Pending;
    }

    private void ResetHiddenTravelReadiness()
    {
        hiddenDismountStartedAt = DateTimeOffset.MinValue;
        hiddenDismountRequestAvailableAt = DateTimeOffset.MinValue;
        hiddenHideReadyDeadlineAt = DateTimeOffset.MinValue;
        hiddenHideDispatchRetryAt = DateTimeOffset.MinValue;
        hiddenHideVerificationDeadlineAt = DateTimeOffset.MinValue;
        hiddenHideVerificationRetryUsed = false;
        ResetHiddenTravelThrottles();
    }

    private void ResetHiddenTravelThrottles()
    {
        logger.ResetThrottle("visible-coffer-farm-hidden-ready");
        logger.ResetThrottle("visible-coffer-farm-hidden-dismount-dispatch");
        logger.ResetThrottle("visible-coffer-farm-hidden-settle");
        logger.ResetThrottle("visible-coffer-farm-hidden-dispatch");
        logger.ResetThrottle("visible-coffer-farm-hidden-verify");
    }

    private bool HandleDangerousTravelTerminalResult()
    {
        switch (dangerousTreasureTravelController.State)
        {
            case DangerousTreasureTravelState.Arrived:
                OnArrivedAtSpot();

                // Arrival helpers such as mount-on-arrival may need multiple updates.
                // Keep the terminal result until the farm has committed its continuation.
                if (State == TreasureCofferFarmState.TravelingToDangerousSpot)
                {
                    logger.DebugThrottled(
                        "visible-coffer-farm-dangerous-arrival-pending",
                        WaitLogInterval,
                        $"Overworld coffer route is retaining dangerous arrival while helpers finish at {DescribeActiveSpot()}. farmState={State} mounted={condition[ConditionFlag.Mounted]} stealthed={gameActionController.IsStealthed} movementState={movementController.State} transition={LastTransition}.");
                    return true;
                }

                dangerousTreasureTravelController.AcknowledgeTerminalState();
                logger.ResetThrottle("visible-coffer-farm-dangerous-travel");
                logger.ResetThrottle("visible-coffer-farm-dangerous-arrival-pending");
                logger.Info($"{BuildLogTag()} op=dangerous-travel-terminal spot={DescribeActiveSpot()} controllerState=Arrived result=Arrived action=arrival-complete nextFarmState={State} transition={LastTransition}");
                logger.Info($"{BuildLogTag()} op=dangerous-travel-consumed spot={DescribeActiveSpot()} result=Arrived nextFarmState={State} transition={LastTransition}");
                return true;
            case DangerousTreasureTravelState.CandidateSkipped:
                var skipReason = dangerousTreasureTravelController.LastTransition;
                logger.Info($"{BuildLogTag()} op=dangerous-travel-terminal spot={DescribeActiveSpot()} controllerState=CandidateSkipped result={dangerousTreasureTravelController.LastResult} action=advance transition={skipReason} error={FormatValue(dangerousTreasureTravelController.LastError)}");
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                logger.ResetThrottle("visible-coffer-farm-dangerous-travel");
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, skipReason);
                return true;
            case DangerousTreasureTravelState.Failed:
                var failureReason = dangerousTreasureTravelController.LastError.Length == 0
                    ? dangerousTreasureTravelController.LastTransition
                    : dangerousTreasureTravelController.LastError;
                logger.Warning($"{BuildLogTag()} op=dangerous-travel-terminal spot={DescribeActiveSpot()} controllerState=Failed result={dangerousTreasureTravelController.LastResult} action=fail transition={dangerousTreasureTravelController.LastTransition} error={FormatValue(dangerousTreasureTravelController.LastError)}");
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                logger.ResetThrottle("visible-coffer-farm-dangerous-travel");
                SetFailure(failureReason);
                return true;
            case DangerousTreasureTravelState.Stopped:
                var stoppedReason = dangerousTreasureTravelController.LastError.Length == 0
                    ? dangerousTreasureTravelController.LastTransition
                    : dangerousTreasureTravelController.LastError;
                logger.Info($"{BuildLogTag()} op=dangerous-travel-terminal spot={DescribeActiveSpot()} controllerState=Stopped result={dangerousTreasureTravelController.LastResult} action=stop transition={dangerousTreasureTravelController.LastTransition} error={FormatValue(dangerousTreasureTravelController.LastError)}");
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

    private string ResolvePreferredAethernetName(string area)
        => scanner.ActiveTerritoryData?.VisibleCoffers.AreaAethernetMappings
            .FirstOrDefault(mapping => string.Equals(mapping.Area, area, StringComparison.OrdinalIgnoreCase))?.Aethernet
            ?? string.Empty;

    private Dictionary<string, VisibleCofferFarmSpotData> GetSpotsByKey()
        => scanner.ActiveTerritoryData?.VisibleCofferFarmSpots.ToDictionary(spot => BuildKey(spot.Area, spot.Label), StringComparer.OrdinalIgnoreCase) ?? [];
}
