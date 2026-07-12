using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class TreasureCofferFarmController : IDisposable
{
    private const float MatchConfidenceRadius = 25f;
    private const float VisibleCofferScanRadius = 60f;
    private const float ApproachScanTriggerDistance = 40f;
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
            currentRouteIndex = -1;
            activeRouteEntry = null;
            activeSpot = null;
            activeResolvedPosition = Vector3.Zero;
            activeSpotUsesOverride = false;
            lastMatchedCoffer = null;
            lastVisibleCofferScanAt = DateTimeOffset.MinValue;
            lastMatchSource = string.Empty;
        }

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

        logger.Info($"Visible coffer farm reset: {reason}");
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
        }
    }

    private void AdvanceRoute()
    {
        while (true)
        {
            var nextIndex = CurrentRouteIndex + 1;
            if (nextIndex >= data.VisibleCofferFarmRoute.Count)
            {
                TransitionTo(TreasureCofferFarmState.Completed, "Completed the visible coffer farm route.");
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

                logger.Info($"Skipping visible coffer route spot {spot.Area}:{spot.Label} because it exceeds the configured visible coffer aggro threshold or requires dangerous travel. aggroLevel={spot.AggroLevel} maxAggro={configuration.VisibleTreasureCofferMaximumAggroLevel} hideThreshold={(spot.HideThresholdDistance?.ToString() ?? "none")}.");
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
        var arrivalDistance = spot.ArrivalDistance ?? Math.Max(1f, configuration.ArrivalDistance);
        var description = $"Visible coffer route {spot.Label}";

        var preferredAethernet = ResolvePreferredAethernetName(spot.Area);
        if (!movementController.PlanRouteToLocation(description, preferredAethernet, destination, arrivalDistance))
        {
            SetFailure(movementController.LastError.Length == 0
                ? $"Failed to plan travel to visible coffer route spot {spot.Area}:{spot.Label}."
                : movementController.LastError);
            return false;
        }

        if (!movementController.StartPlannedRoute())
        {
            SetFailure(movementController.LastError.Length == 0
                ? $"Failed to start travel to visible coffer route spot {spot.Area}:{spot.Label}."
                : movementController.LastError);
            return false;
        }

        TransitionTo(TreasureCofferFarmState.TravelingToSpot, $"Traveling to visible coffer route spot {spot.Area}:{spot.Label}.");
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
                SetFailure(movementController.LastError.Length == 0
                    ? $"Failed to reach visible coffer route spot {DescribeActiveSpot()}."
                    : movementController.LastError);
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

        if (TryStartInteractionForActiveSpot(requireApproachThreshold: false, acquisitionSource: "arrival"))
        {
            return;
        }

        TransitionTo(TreasureCofferFarmState.AdvancingRoute, $"No visible coffer matched {DescribeActiveSpot()} on immediate arrival scan; continuing to the next route entry.");
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
                        $"VISIBLE_COFFER_OPENED spot={DescribeActiveSpot()} source={FormatValue(lastMatchSource)} baseId={LastMatchedCoffer.DataId} objectId={LastMatchedCoffer.GameObjectId:X} pos=<{LastMatchedCoffer.Position.X:0.000}, {LastMatchedCoffer.Position.Y:0.000}, {LastMatchedCoffer.Position.Z:0.000}> name='{LastMatchedCoffer.Name}'.");
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

        var matchedCoffer = TryMatchVisibleCoffer(ActiveResolvedPosition, VisibleCofferScanRadius, out var matchDistance);
        if (matchedCoffer == null)
        {
            return false;
        }

        lock (gate)
        {
            lastMatchedCoffer = matchedCoffer;
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
            Coffer = matchedCoffer,
            MatchDistance = matchDistance,
            IsTrustworthy = matchDistance <= MatchConfidenceRadius,
            AttributionReason = $"Matched visible coffer during {acquisitionSource} scan for {spot.Area}:{spot.Label}. routeDistance={matchDistance:0.0}y playerDistance={matchedCoffer.DistanceToPlayer:0.0}y remainingToSpot={remainingDistanceToSpot:0.0}y scanRadius={VisibleCofferScanRadius:0.0}y.",
        };

        logger.Info(
            $"VISIBLE_COFFER_MATCH spot={spot.Area}:{spot.Label} source={acquisitionSource} baseId={matchedCoffer.DataId} objectId={matchedCoffer.GameObjectId:X} routeDistance={matchDistance:0.0}y playerDistance={matchedCoffer.DistanceToPlayer:0.0}y pos=<{matchedCoffer.Position.X:0.000}, {matchedCoffer.Position.Y:0.000}, {matchedCoffer.Position.Z:0.000}> name='{matchedCoffer.Name}'.");

        if (!cofferInteractionController.Start(interactionMatch))
        {
            if (cofferInteractionController.LastResult == CofferInteractionResult.LostCoffer)
            {
                logger.Warning($"Matched visible coffer for {spot.Area}:{spot.Label} vanished before interaction started.");
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
        if (requireApproachThreshold && remainingDistanceToSpot > ApproachScanTriggerDistance)
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

    private VisibleCoffer? TryMatchVisibleCoffer(Vector3 resolvedPosition, float scanRadius, out float matchDistance)
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
            matchDistance = float.MaxValue;
            return null;
        }

        matchDistance = best.Distance;
        return best.Coffer;
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
            logger.Warning($"Failed to persist visible coffer override for {spot.Area}:{spot.Label}.");
        }
    }

    private bool RequiresDangerousTravel(VisibleCofferFarmSpotData spot)
        => spot.AggroLevel > configuration.VisibleTreasureCofferMaximumAggroLevel
            || (spot.HideThresholdDistance ?? 0) > 0;

    private bool ShouldSkipSpot(VisibleCofferFarmSpotData spot)
        => RequiresDangerousTravel(spot);

    private void SetFailure(string reason)
    {
        logger.Warning($"Visible coffer farm failure: {reason}");
        TransitionTo(TreasureCofferFarmState.Failed, reason, error: reason);
    }

    private void TransitionTo(TreasureCofferFarmState nextState, string reason, string? error = null)
    {
        lock (gate)
        {
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

        logger.Info($"Visible coffer farm state -> {nextState}: {reason}");
    }

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
            .Where(coffer => coffer.DistanceToPlayer <= VisibleCofferScanRadius)
            .OrderBy(coffer => coffer.DistanceToPlayer)
            .FirstOrDefault();
        var nearestRoute = visibleCoffers
            .Select(coffer => new
            {
                Coffer = coffer,
                Distance = CalculateFlatDistance(coffer.Position, resolvedPosition),
            })
            .Where(entry => entry.Coffer.DistanceToPlayer <= VisibleCofferScanRadius)
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
