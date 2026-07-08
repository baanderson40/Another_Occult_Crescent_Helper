using System;
using System.Globalization;
using System.Numerics;
using AOCCH.Data;
using AOCCH.IPC;
using AOCCH.Logging;
using AOCCH.Scanning;
using Dalamud.Plugin.Services;

namespace AOCCH.Movement;

public sealed class MovementController : IDisposable
{
    private static readonly TimeSpan RouteTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);
    private const float ProgressThreshold = 2f;

    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly VNavmeshIpc vnavmesh;
    private readonly LifestreamIpc lifestream;
    private readonly RoutePlanner routePlanner;
    private readonly OccultCrescentData data;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private PlannedRoute? plannedRoute;
    private int currentStepIndex;
    private DateTimeOffset routeStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset stepStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastProgressAt = DateTimeOffset.MinValue;
    private float lastDistance = float.MaxValue;
    private bool stepStarted;
    private bool lifestreamOwned;
    private string lastError = string.Empty;
    private MovementState state = MovementState.Idle;

    public MovementController(
        IFramework framework,
        IObjectTable objectTable,
        OccultCrescentScanner scanner,
        VNavmeshIpc vnavmesh,
        LifestreamIpc lifestream,
        RoutePlanner routePlanner,
        OccultCrescentData data,
        AocchLogger logger)
    {
        this.framework = framework;
        this.objectTable = objectTable;
        this.scanner = scanner;
        this.vnavmesh = vnavmesh;
        this.lifestream = lifestream;
        this.routePlanner = routePlanner;
        this.data = data;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
    }

    public MovementState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public PlannedRoute? PlannedRoute
    {
        get
        {
            lock (gate)
            {
                return plannedRoute;
            }
        }
    }

    public int CurrentStepIndex
    {
        get
        {
            lock (gate)
            {
                return currentStepIndex;
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

    public float DistanceRemaining
    {
        get
        {
            lock (gate)
            {
                return lastDistance;
            }
        }
    }

    public string VNavmeshStatusText
        => vnavmesh.IsReady() ? $"Ready ({vnavmesh.GetBuildProgress():P0})" : "Unavailable";

    public string LifestreamStatusText
        => !lifestream.IsAvailable() ? "Unavailable" : (lifestream.IsBusy() ? "Busy" : "Available");

    public bool PlanRouteToSelectedTarget()
        => PlanRoute(scanner.Snapshot.EffectiveTarget);

    public bool PlanRoute(TargetSelection selection)
    {
        var playerPosition = GetPlayerPosition();
        if (playerPosition == null)
        {
            SetFailure(MovementState.Failed, "Player position is unavailable.");
            return false;
        }

        SetState(MovementState.Planning);
        if (!routePlanner.TryPlan(selection, playerPosition.Value, out var route, out var failureReason))
        {
            SetFailure(MovementState.Failed, failureReason);
            return false;
        }

        lock (gate)
        {
            plannedRoute = route;
            currentStepIndex = 0;
            routeStartedAt = DateTimeOffset.MinValue;
            stepStartedAt = DateTimeOffset.MinValue;
            lastProgressAt = DateTimeOffset.MinValue;
            lastDistance = float.MaxValue;
            stepStarted = false;
            lifestreamOwned = false;
            lastError = string.Empty;
            state = MovementState.Idle;
        }

        logger.Info($"Planned {route.RouteType} route to {route.TargetDescription} with {route.Steps.Count} step(s).");
        return true;
    }

    public bool StartPlannedRoute()
    {
        vnavmesh.Stop();

        lock (gate)
        {
            if (plannedRoute == null)
            {
                lastError = "No route is planned.";
                state = MovementState.Failed;
                return false;
            }

            currentStepIndex = 0;
            routeStartedAt = DateTimeOffset.UtcNow;
            stepStartedAt = DateTimeOffset.MinValue;
            lastProgressAt = DateTimeOffset.UtcNow;
            lastDistance = float.MaxValue;
            stepStarted = false;
            lifestreamOwned = false;
            lastError = string.Empty;
            state = MovementState.Pathfinding;
        }

        logger.Info($"Starting route to {plannedRoute!.TargetDescription}.");
        return true;
    }

    public bool RecoverToBaseCamp()
    {
        var playerPosition = GetPlayerPosition();
        if (playerPosition == null)
        {
            SetFailure(MovementState.Failed, "Player position is unavailable.");
            return false;
        }

        vnavmesh.Stop();

        var route = routePlanner.PlanBaseCampRecovery(playerPosition.Value);
        lock (gate)
        {
            plannedRoute = route;
            currentStepIndex = 0;
            routeStartedAt = DateTimeOffset.UtcNow;
            stepStartedAt = DateTimeOffset.MinValue;
            lastProgressAt = DateTimeOffset.UtcNow;
            lastDistance = float.MaxValue;
            stepStarted = false;
            lifestreamOwned = false;
            lastError = string.Empty;
            state = MovementState.Pathfinding;
        }

        logger.Info("Starting Base Camp recovery route.");
        return true;
    }

    public void Stop(string reason)
    {
        vnavmesh.Stop();
        if (lifestreamOwned && lifestream.IsBusy())
        {
            lifestream.Abort();
        }

        lock (gate)
        {
            state = MovementState.Stopped;
            stepStarted = false;
            lifestreamOwned = false;
            lastError = reason;
        }

        logger.Info($"Movement stopped: {reason}");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        Stop("Plugin disposal");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        PlannedRoute? route;
        MovementState currentState;
        int stepIndex;

        lock (gate)
        {
            route = plannedRoute;
            currentState = state;
            stepIndex = currentStepIndex;
        }

        if (currentState is MovementState.Idle or MovementState.Arrived or MovementState.Stopped or MovementState.TimedOut or MovementState.Failed)
        {
            return;
        }

        if (!scanner.Snapshot.IsInSouthHorn)
        {
            Stop("Left South Horn while moving.");
            return;
        }

        if (route == null || stepIndex >= route.Steps.Count)
        {
            CompleteRoute();
            return;
        }

        if (routeStartedAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - routeStartedAt > RouteTimeout)
        {
            SetFailure(MovementState.TimedOut, "Route timed out.", stopMovement: true);
            return;
        }

        ProcessStep(route.Steps[stepIndex]);
    }

    private void ProcessStep(RouteStep step)
    {
        var playerPosition = GetPlayerPosition();
        if (playerPosition == null)
        {
            SetFailure(MovementState.Failed, "Player position is unavailable.", stopMovement: true);
            return;
        }

        switch (step.Kind)
        {
            case RouteStepKind.PathToPoint:
            case RouteStepKind.PathToAethernet:
            case RouteStepKind.RecoverToBaseCamp:
                ProcessPathStep(step, playerPosition.Value);
                break;
            case RouteStepKind.AethernetTeleport:
                ProcessAethernetStep(step, playerPosition.Value);
                break;
        }
    }

    private void ProcessPathStep(RouteStep step, Vector3 playerPosition)
    {
        var distance = CalculateFlatDistance(playerPosition, step.Destination);
        lock (gate)
        {
            lastDistance = distance;
        }

        if (distance <= step.ArrivalTolerance)
        {
            logger.Info($"Completed route step: {step.Description}.");
            AdvanceStep();
            return;
        }

        if (!stepStarted)
        {
            var destination = vnavmesh.FindNearestPoint(step.Destination, 5f, 5f) ?? step.Destination;
            var started = vnavmesh.PathfindAndMoveCloseTo(destination, fly: false, step.ArrivalTolerance);
            if (!started)
            {
                SetFailure(MovementState.Failed, $"Failed to start pathing for step: {step.Description}.");
                return;
            }

            lock (gate)
            {
                state = MovementState.Pathfinding;
                stepStarted = true;
                stepStartedAt = DateTimeOffset.UtcNow;
                lastProgressAt = DateTimeOffset.UtcNow;
                lastDistance = distance;
            }

            logger.Info($"Started route step: {step.Description}.");
            return;
        }

        var pathBusy = vnavmesh.IsPathRunning() || vnavmesh.IsPathfindInProgress();
        if (lastDistance - distance >= ProgressThreshold)
        {
            lock (gate)
            {
                lastProgressAt = DateTimeOffset.UtcNow;
                lastDistance = distance;
                state = MovementState.WaitingForArrival;
            }
        }

        if (DateTimeOffset.UtcNow - lastProgressAt > StallTimeout)
        {
            SetFailure(MovementState.TimedOut, $"Movement stalled during step: {step.Description}.", stopMovement: true);
            return;
        }

        if (!pathBusy && distance > step.ArrivalTolerance)
        {
            lock (gate)
            {
                stepStarted = false;
            }
        }
    }

    private void ProcessAethernetStep(RouteStep step, Vector3 playerPosition)
    {
        if (!stepStarted)
        {
            if (lifestream.IsBusy())
            {
                SetFailure(MovementState.Failed, "Lifestream is busy.");
                return;
            }

            var activeAetheryte = lifestream.GetActiveAetheryte();
            var activeCustomAetheryte = lifestream.GetActiveCustomAetheryte();
            if (activeAetheryte == 0 && activeCustomAetheryte == 0)
            {
                var source = GetClosestAethernet(playerPosition);
                if (CalculateFlatDistance(playerPosition, source.Position.ToVector3()) > source.InteractDistanceMax)
                {
                    SetFailure(MovementState.Failed, "Player is not near an aethernet shard for Lifestream teleport.");
                    return;
                }
            }

            if (!lifestream.TryAethernetTeleportByPlaceNameId(step.AethernetPlaceNameId))
            {
                SetFailure(MovementState.Failed, $"Lifestream could not teleport to {step.AethernetName}.");
                return;
            }

            lock (gate)
            {
                state = MovementState.UsingAethernet;
                stepStarted = true;
                stepStartedAt = DateTimeOffset.UtcNow;
                lastProgressAt = DateTimeOffset.UtcNow;
                lastDistance = CalculateFlatDistance(playerPosition, step.Destination);
                lifestreamOwned = true;
            }

            logger.Info($"Started route step: {step.Description}.");
            return;
        }

        var distance = CalculateFlatDistance(playerPosition, step.Destination);
        lock (gate)
        {
            lastDistance = distance;
        }

        if (lifestream.IsBusy())
        {
            lock (gate)
            {
                lastProgressAt = DateTimeOffset.UtcNow;
            }
            return;
        }

        if (DateTimeOffset.UtcNow - stepStartedAt < TimeSpan.FromSeconds(2))
        {
            return;
        }

        if (distance <= 25f)
        {
            logger.Info($"Completed route step: {step.Description}.");
            AdvanceStep();
            return;
        }

        SetFailure(MovementState.Failed, $"Teleport to {step.AethernetName} did not land near the expected destination.");
    }

    private void AdvanceStep()
    {
        lock (gate)
        {
            currentStepIndex++;
            stepStarted = false;
            stepStartedAt = DateTimeOffset.MinValue;
            lastProgressAt = DateTimeOffset.UtcNow;
            lastDistance = float.MaxValue;
            if (currentStepIndex >= (plannedRoute?.Steps.Count ?? 0))
            {
                state = MovementState.Arrived;
                lifestreamOwned = false;
                logger.Info("Movement route completed.");
                return;
            }

            state = MovementState.Pathfinding;
            lifestreamOwned = false;
        }
    }

    private void CompleteRoute()
    {
        vnavmesh.Stop();
        lock (gate)
        {
            state = MovementState.Arrived;
            stepStarted = false;
            lifestreamOwned = false;
            lastError = string.Empty;
        }

        logger.Info("Movement route completed.");
    }

    private void SetFailure(MovementState failureState, string reason, bool stopMovement = false)
    {
        if (stopMovement)
        {
            vnavmesh.Stop();
            if (lifestreamOwned && lifestream.IsBusy())
            {
                lifestream.Abort();
            }
        }

        lock (gate)
        {
            state = failureState;
            stepStarted = false;
            lifestreamOwned = false;
            lastError = reason;
        }

        logger.Warning(reason);
    }

    private void SetState(MovementState nextState)
    {
        lock (gate)
        {
            state = nextState;
        }
    }

    private Vector3? GetPlayerPosition()
        => objectTable.LocalPlayer?.Position;

    private AethernetData GetClosestAethernet(Vector3 playerPosition)
    {
        AethernetData? closest = null;
        var closestDistance = float.MaxValue;

        foreach (var aethernet in data.Aethernets)
        {
            var distance = CalculateFlatDistance(playerPosition, aethernet.Position.ToVector3());
            if (distance >= closestDistance)
            {
                continue;
            }

            closest = aethernet;
            closestDistance = distance;
        }

        return closest ?? data.Aethernets[0];
    }

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    public string GetStatusSummary()
    {
        var route = PlannedRoute;
        if (route == null)
        {
            return "No route planned";
        }

        return $"{route.RouteType} to {route.TargetDescription}";
    }

    public string GetActiveStepSummary()
    {
        var route = PlannedRoute;
        if (route == null || CurrentStepIndex >= route.Steps.Count)
        {
            return State == MovementState.Arrived ? "Completed" : "No active step";
        }

        return route.Steps[CurrentStepIndex].Description;
    }

    public string GetElapsedSummary()
    {
        if (routeStartedAt == DateTimeOffset.MinValue)
        {
            return "Not started";
        }

        var elapsed = DateTimeOffset.UtcNow - routeStartedAt;
        return elapsed.ToString("mm\\:ss", CultureInfo.InvariantCulture);
    }
}
