using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class TreasureCofferFarmController : IDisposable
{
    private static int nextRunSequence;
    private const float MatchConfidenceRadius = 25f;
    private const float VisibleCofferAcquisitionDistance = 100f;
    private static readonly TimeSpan ApproachScanPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);

    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly DangerousTreasureTravelController dangerousTreasureTravelController;
    private readonly CofferInteractionController cofferInteractionController;
    private readonly OccultCrescentData data;
    private readonly VisibleCofferPositionOverrideStore overrideStore;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();
    private readonly Dictionary<string, VisibleCofferFarmSpotData> spotsByKey;

    private TreasureCofferFarmState state = TreasureCofferFarmState.Idle;
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

    public TreasureCofferFarmController(
        IFramework framework,
        IObjectTable objectTable,
        OccultCrescentScanner scanner,
        MovementController movementController,
        DangerousTreasureTravelController dangerousTreasureTravelController,
        CofferInteractionController cofferInteractionController,
        OccultCrescentData data,
        VisibleCofferPositionOverrideStore overrideStore,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.objectTable = objectTable;
        this.scanner = scanner;
        this.movementController = movementController;
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

    public bool Start()
    {
        if (IsRunning)
        {
            logger.Debug($"Visible coffer farm start ignored because a run is already active. state={State}.");
            return true;
        }

        if (configuration.ScannerOnlyMode)
        {
            SetFailure("Visible coffer farm start is blocked by scanner-only mode.");
            return false;
        }

        if (!scanner.Snapshot.IsInSouthHorn)
        {
            SetFailure("Visible coffer farm start requires South Horn.");
            return false;
        }

        if (data.VisibleCofferFarmRoute.Count == 0 || data.VisibleCofferFarmSpots.Count == 0)
        {
            SetFailure("Visible coffer farm data is missing route or spot entries.");
            return false;
        }

        lock (gate)
        {
            currentRunId = $"CofferFarm#{Interlocked.Increment(ref nextRunSequence)}";
            currentRouteIndex = -1;
            activeRouteEntry = null;
            activeSpot = null;
            activeResolvedPosition = Vector3.Zero;
            activeSpotUsesOverride = false;
            lastMatchedCoffer = null;
            lastVisibleCofferScanAt = DateTimeOffset.MinValue;
            lastMatchSource = string.Empty;
        }

        movementController.SetLogOwner(currentRunId);
        TransitionTo(TreasureCofferFarmState.Starting, $"Starting visible coffer farm route with {data.VisibleCofferFarmRoute.Count} entries.");
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

        TransitionTo(TreasureCofferFarmState.Stopped, reason, error: reason);
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = TreasureCofferFarmState.Idle;
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
        }

        logger.Info($"[CofferFarm] op=reset reason={reason}");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        if (IsRunning)
        {
            Stop("Visible coffer farm disposal");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsRunning)
        {
            return;
        }

        if (!scanner.Snapshot.IsInSouthHorn)
        {
            SetFailure("Left South Horn while visible coffer farm was active.");
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
                SetFailure($"Visible coffer farm route entry {routeEntry.Area}:{routeEntry.Label} is missing spot data.");
                return;
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
                }

                logger.Info($"{BuildLogTag()} op=spot-skip spot={spot.Area}:{spot.Label} aggroLevel={spot.AggroLevel} maxAggro={configuration.VisibleTreasureCofferMaximumAggroLevel} hideThreshold={(spot.HideThresholdDistance?.ToString() ?? "none")} reason=dangerous-or-over-threshold");
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
            SetFailure("Visible coffer farm lost its active spot before travel started.");
            return false;
        }

        var destination = ActiveResolvedPosition;
        var arrivalDistance = GetArrivalDistance(spot);
        movementController.SetLogOwner(currentRunId);
        if (!movementController.StartDirectMove($"Visible coffer route {spot.Label}", destination, arrivalDistance))
        {
            logger.Warning($"{BuildLogTag()} op=travel-start-failed spot={spot.Area}:{spot.Label} destination=<{destination.X:0.0}, {destination.Y:0.0}, {destination.Z:0.0}> arrivalDistance={arrivalDistance:0.0} reason={(movementController.LastError.Length == 0 ? $"Failed to start travel to visible coffer route spot {spot.Area}:{spot.Label}." : movementController.LastError)}");
            TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Skipping visible coffer route spot {spot.Area}:{spot.Label} because direct travel could not start.");
            return false;
        }

        TransitionTo(TreasureCofferFarmState.TravelingToSpot, $"Traveling to visible coffer route spot {spot.Area}:{spot.Label} with direct-only movement.");
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
                logger.Warning($"{BuildLogTag()} op=travel-failed spot={DescribeActiveSpot()} movementState={movementController.State} reason={(movementController.LastError.Length == 0 ? $"Failed to reach visible coffer route spot {DescribeActiveSpot()}." : movementController.LastError)}");
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Skipping visible coffer route spot {DescribeActiveSpot()} because direct travel failed.");
                return;
        }

        logger.DebugThrottled(
            "visible-coffer-farm-travel",
            WaitLogInterval,
            $"Visible coffer farm is traveling to {DescribeActiveSpot()}. movementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
    }

    private void OnArrivedAtSpot()
    {
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
        TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"No visible coffer matched {DescribeActiveSpot()} on final arrival scan; continuing to the next route entry.");
    }

    private void TickInteractingWithCoffer()
    {
        if (cofferInteractionController.IsRunning)
        {
            logger.DebugThrottled(
                "visible-coffer-farm-interaction",
                WaitLogInterval,
                $"Visible coffer farm is interacting with a matched coffer at {DescribeActiveSpot()}. interactionState={cofferInteractionController.State} attempts={cofferInteractionController.InteractionAttemptCount}.");
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
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Opened visible coffer at {DescribeActiveSpot()}; continuing to the next route entry.");
                return;
            case CofferInteractionResult.LostCoffer:
            case CofferInteractionResult.TimedOut:
                TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"Visible coffer interaction ended without a confirmed open at {DescribeActiveSpot()}; continuing to the next route entry.");
                return;
            case CofferInteractionResult.Stopped:
                TransitionTo(TreasureCofferFarmState.Stopped, cofferInteractionController.LastTransition, error: cofferInteractionController.LastError);
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
            logger.Warning($"{BuildLogTag()} op=return-start-failed spot={DescribeActiveSpot()} reason={(movementController.LastError.Length == 0 ? "Completed the visible coffer farm route, but failed to start Base Camp recovery with Return routing; retrying with direct recovery." : movementController.LastError)}");

            if (!movementController.RecoverToBaseCamp(allowReturn: false))
            {
                logger.Warning($"{BuildLogTag()} op=direct-recovery-start-failed spot={DescribeActiveSpot()} reason={(movementController.LastError.Length == 0 ? "Completed the visible coffer farm route, but failed to start Base Camp recovery." : movementController.LastError)}");
                TransitionTo(TreasureCofferFarmState.Completed, "Completed the visible coffer farm route, but failed to start Base Camp recovery.");
                return;
            }

            TransitionTo(TreasureCofferFarmState.ReturningToBase, "Completed the visible coffer farm route; returning to Base Camp with direct recovery after Return routing was unavailable.");
            return;
        }

        TransitionTo(TreasureCofferFarmState.ReturningToBase, "Completed the visible coffer farm route; returning to Base Camp with normal recovery routing.");
    }

    private void TickReturningToBase()
    {
        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Visible coffer route returned to Base Camp.");
                TransitionTo(TreasureCofferFarmState.Completed, "Completed the visible coffer farm route and returned to Base Camp.");
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                logger.Warning($"{BuildLogTag()} op=return-failed movementState={movementController.State} reason={(movementController.LastError.Length == 0 ? "Base Camp recovery failed after completing the visible coffer farm route." : movementController.LastError)}");
                TransitionTo(TreasureCofferFarmState.Completed, "Completed the visible coffer farm route, but Base Camp recovery failed.");
                return;
        }

        logger.DebugThrottled(
            "visible-coffer-farm-returning-base",
            WaitLogInterval,
            $"Visible coffer farm is returning to Base Camp. movementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
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

        lock (gate)
        {
            lastMatchedCoffer = confirmedCoffer;
            lastMatchSource = acquisitionSource;
        }

        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop($"Matched visible coffer for {spot.Area}:{spot.Label}.");
        }

        if (dangerousTreasureTravelController.IsRunning)
        {
            dangerousTreasureTravelController.Stop($"Matched visible coffer for {spot.Area}:{spot.Label}.");
        }

        var interactionMatch = new VisibleCofferMatch
        {
            CandidateKey = new TreasureCandidateKey
            {
                Label = spot.Label,
                CandidateKey = spot.Label,
            },
            Coffer = confirmedCoffer,
            MatchDistance = matchDistance,
            IsTrustworthy = matchDistance <= MatchConfidenceRadius,
            RequiresJumpAssist = string.Equals(spot.Note, "requires_jump", StringComparison.OrdinalIgnoreCase),
            AttributionReason = $"Matched visible coffer during {acquisitionSource} scan for {spot.Area}:{spot.Label}. routeDistance={matchDistance:0.0}y playerDistance={confirmedCoffer.DistanceToPlayer:0.0}y remainingToSpot={remainingDistanceToSpot:0.0}y acquisitionDistance={VisibleCofferAcquisitionDistance:0.0}y.",
        };

        logger.Info(
            $"{BuildLogTag()} op=coffer-match spot={spot.Area}:{spot.Label} source={acquisitionSource} baseId={confirmedCoffer.DataId} objectId={confirmedCoffer.GameObjectId:X} routeDistance={matchDistance:0.0}y playerDistance={confirmedCoffer.DistanceToPlayer:0.0}y pos=<{confirmedCoffer.Position.X:0.000}, {confirmedCoffer.Position.Y:0.000}, {confirmedCoffer.Position.Z:0.000}> name='{confirmedCoffer.Name}'");

        if (!cofferInteractionController.Start(interactionMatch))
        {
            if (cofferInteractionController.LastResult == CofferInteractionResult.LostCoffer)
            {
                logger.Warning($"{BuildLogTag()} op=interaction-start-lost spot={spot.Area}:{spot.Label} reason=matched-coffer-vanished");
                return false;
            }

            SetFailure(cofferInteractionController.LastError.Length == 0
                ? "Failed to start visible coffer interaction."
                : cofferInteractionController.LastError);
            return false;
        }

        TransitionTo(TreasureCofferFarmState.InteractingWithCoffer, $"Matched visible coffer for {spot.Area}:{spot.Label} via {acquisitionSource} scan; starting interaction.");
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
        if (requireApproachThreshold && remainingDistanceToSpot > VisibleCofferAcquisitionDistance)
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
        => spot.AggroLevel > configuration.VisibleTreasureCofferMaximumAggroLevel
            || (spot.HideThresholdDistance ?? 0) > 0;

    private bool ShouldSkipSpot(VisibleCofferFarmSpotData spot)
        => RequiresDangerousTravel(spot);

    private float GetArrivalDistance(VisibleCofferFarmSpotData? spot)
    {
        var configured = spot?.ArrivalDistance ?? configuration.ArrivalDistance;
        return Math.Clamp(configured, 5f, 50f);
    }

    private void SetFailure(string reason)
    {
        logger.Warning($"{BuildLogTag()} op=failure state={TreasureCofferFarmState.Failed} spot={DescribeActiveSpot()} reason={reason}");
        TransitionTo(TreasureCofferFarmState.Failed, reason, error: reason);
    }

    private void TransitionTo(TreasureCofferFarmState nextState, string reason, string? error = null)
    {
        TreasureCofferFarmState previousState;
        lock (gate)
        {
            previousState = state;
            state = nextState;
            lastTransition = reason;
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
