using System;
using System.Globalization;
using System.Numerics;
using AOCCH.Data;
using AOCCH.IPC;
using AOCCH.Logging;
using AOCCH.Scanning;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AOCCH.Movement;

public sealed class MovementController : IDisposable
{
    private static readonly TimeSpan RouteTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AethernetTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TransitionStableTime = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(5);
    private const float TransitionCompletionDistance = 25f;
    private const float AethernetInnerEdgeBias = 0.15f;
    private const float AethernetBandWidth = 0.25f;
    private const float AethernetApproachTolerance = 0.25f;
    private const float ProgressThreshold = 2f;

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly OccultCrescentScanner scanner;
    private readonly VNavmeshIpc vnavmesh;
    private readonly LifestreamIpc lifestream;
    private readonly RoutePlanner routePlanner;
    private readonly GameActionController gameActionController;
    private readonly OccultCrescentData data;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private PlannedRoute? plannedRoute;
    private int currentStepIndex;
    private DateTimeOffset routeStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset stepStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastProgressAt = DateTimeOffset.MinValue;
    private float lastDistance = float.MaxValue;
    private float progressDistance = float.MaxValue;
    private bool stepStarted;
    private bool mountAttempted;
    private bool dismountAttempted;
    private int stepAttemptCount;
    private bool lifestreamOwned;
    private bool transitionObserved;
    private bool startedAwayFromTransitionDestination;
    private bool returnPromptHandled;
    private DateTimeOffset stableSince = DateTimeOffset.MinValue;
    private string lastError = string.Empty;
    private MovementState state = MovementState.Idle;

    public MovementController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        IGameGui gameGui,
        OccultCrescentScanner scanner,
        VNavmeshIpc vnavmesh,
        LifestreamIpc lifestream,
        RoutePlanner routePlanner,
        GameActionController gameActionController,
        OccultCrescentData data,
        AocchLogger logger)
    {
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.scanner = scanner;
        this.vnavmesh = vnavmesh;
        this.lifestream = lifestream;
        this.routePlanner = routePlanner;
        this.gameActionController = gameActionController;
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

    public bool IsVNavmeshReady
        => vnavmesh.IsReady();

    public bool IsLifestreamAvailable
        => lifestream.IsAvailable();

    public bool CanUseReturnAction
        => gameActionController.CanUseGeneralAction(GameActionController.ReturnActionId);

    public bool PlanRouteToSelectedTarget()
        => PlanRoute(scanner.Snapshot.EffectiveTarget);

    public bool PlanRoute(TargetSelection selection, bool allowReturn = true)
    {
        var playerPosition = GetPlayerPosition();
        if (playerPosition == null)
        {
            SetFailure(MovementState.Failed, "Player position is unavailable.");
            return false;
        }

        SetState(MovementState.Planning);
        if (!routePlanner.TryPlan(selection, playerPosition.Value, out var route, out var failureReason, allowReturn))
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
            progressDistance = float.MaxValue;
            stepStarted = false;
            mountAttempted = false;
            dismountAttempted = false;
            stepAttemptCount = 0;
            lifestreamOwned = false;
            transitionObserved = false;
            startedAwayFromTransitionDestination = false;
            returnPromptHandled = false;
            stableSince = DateTimeOffset.MinValue;
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
            progressDistance = float.MaxValue;
            stepStarted = false;
            mountAttempted = false;
            dismountAttempted = false;
            stepAttemptCount = 0;
            lifestreamOwned = false;
            transitionObserved = false;
            startedAwayFromTransitionDestination = false;
            returnPromptHandled = false;
            stableSince = DateTimeOffset.MinValue;
            lastError = string.Empty;
            state = plannedRoute.Steps[0].Kind == RouteStepKind.Return
                ? MovementState.UsingReturn
                : MovementState.Pathfinding;
        }

        logger.Info($"Starting route to {plannedRoute!.TargetDescription}.");
        return true;
    }

    public bool RecoverToBaseCamp(bool allowReturn = true)
    {
        var playerPosition = GetPlayerPosition();
        if (playerPosition == null)
        {
            SetFailure(MovementState.Failed, "Player position is unavailable.");
            return false;
        }

        vnavmesh.Stop();

        if (!routePlanner.TryPlanBaseCampRecovery(playerPosition.Value, out var route, out var failureReason, allowReturn))
        {
            SetFailure(MovementState.Failed, failureReason);
            return false;
        }

        lock (gate)
        {
            plannedRoute = route;
            currentStepIndex = 0;
            routeStartedAt = DateTimeOffset.UtcNow;
            stepStartedAt = DateTimeOffset.MinValue;
            lastProgressAt = DateTimeOffset.UtcNow;
            lastDistance = float.MaxValue;
            progressDistance = float.MaxValue;
            stepStarted = false;
            mountAttempted = false;
            dismountAttempted = false;
            stepAttemptCount = 0;
            lifestreamOwned = false;
            transitionObserved = false;
            startedAwayFromTransitionDestination = false;
            returnPromptHandled = false;
            stableSince = DateTimeOffset.MinValue;
            lastError = string.Empty;
            state = route.Steps.Count > 0 && route.Steps[0].Kind == RouteStepKind.Return
                ? MovementState.UsingReturn
                : MovementState.Pathfinding;
        }

        logger.Info("Starting Base Camp recovery route.");
        return true;
    }

    public bool StartDirectMove(string description, Vector3 destination, float arrivalTolerance = 1f)
    {
        vnavmesh.Stop();

        lock (gate)
        {
            plannedRoute = new PlannedRoute
            {
                TargetDescription = description,
                RouteType = "Direct",
                FinalDestination = destination,
                EstimatedDistance = float.MaxValue,
                Steps =
                [
                    new RouteStep
                    {
                        Kind = RouteStepKind.PathToPoint,
                        Description = description,
                        Destination = destination,
                        ArrivalTolerance = arrivalTolerance,
                    },
                ],
            };
            currentStepIndex = 0;
            routeStartedAt = DateTimeOffset.UtcNow;
            stepStartedAt = DateTimeOffset.MinValue;
            lastProgressAt = DateTimeOffset.UtcNow;
            lastDistance = float.MaxValue;
            progressDistance = float.MaxValue;
            stepStarted = false;
            mountAttempted = false;
            dismountAttempted = false;
            stepAttemptCount = 0;
            lifestreamOwned = false;
            transitionObserved = false;
            startedAwayFromTransitionDestination = false;
            returnPromptHandled = false;
            stableSince = DateTimeOffset.MinValue;
            lastError = string.Empty;
            state = MovementState.Pathfinding;
        }

        logger.Info($"Starting direct movement: {description}.");
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
            mountAttempted = false;
            dismountAttempted = false;
            stepAttemptCount = 0;
            lifestreamOwned = false;
            transitionObserved = false;
            startedAwayFromTransitionDestination = false;
            returnPromptHandled = false;
            stableSince = DateTimeOffset.MinValue;
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
            case RouteStepKind.Return:
                ProcessReturnStep(step, playerPosition.Value);
                break;
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

    private void ProcessReturnStep(RouteStep step, Vector3 playerPosition)
    {
        var distance = CalculateFlatDistance(playerPosition, step.Destination);
        lock (gate)
        {
            lastDistance = distance;
        }

        if (!stepStarted)
        {
            if (condition[ConditionFlag.InCombat])
            {
                SetFailure(MovementState.Failed, "Cannot use Return while in combat.");
                return;
            }

            if (objectTable.LocalPlayer?.CurrentHp == 0)
            {
                SetFailure(MovementState.Failed, "Cannot use Return while dead.");
                return;
            }

            vnavmesh.Stop();
            if (!gameActionController.TryExecuteGeneralAction(step.GeneralActionId, step.Description))
            {
                SetFailure(MovementState.Failed, $"Failed to execute Return for step: {step.Description}.");
                return;
            }

            lock (gate)
            {
                state = MovementState.UsingReturn;
                stepStarted = true;
                mountAttempted = false;
                dismountAttempted = false;
                stepAttemptCount = 0;
                stepStartedAt = DateTimeOffset.UtcNow;
                lastProgressAt = DateTimeOffset.UtcNow;
                lastDistance = distance;
                progressDistance = distance;
                startedAwayFromTransitionDestination = distance > TransitionCompletionDistance;
                transitionObserved = false;
                returnPromptHandled = false;
                stableSince = DateTimeOffset.MinValue;
            }

            logger.Info($"Started route step: {step.Description}.");
            return;
        }

        if (!returnPromptHandled && IsSelectYesnoReady())
        {
            if (TryConfirmSelectYesno())
            {
                lock (gate)
                {
                    returnPromptHandled = true;
                }

                logger.Info("Confirmed SelectYesno prompt during Return.");
            }
        }

        WaitForTransitionCompletion(step, playerPosition, distance, ReturnTimeout, includeOccupiedCondition: false, "Return");
    }

    private void ProcessPathStep(RouteStep step, Vector3 playerPosition)
    {
        var distance = CalculateFlatDistance(playerPosition, step.Destination);
        lock (gate)
        {
            lastDistance = distance;
        }

        if (IsPathStepComplete(step, playerPosition, distance))
        {
            if (!CompletePathStepArrival(step))
            {
                return;
            }

            logger.Info($"Completed route step: {step.Description}.");
            AdvanceStep();
            return;
        }

        if (!stepStarted)
        {
            if (step.ShouldMountBeforeStep && !EnsureMounted(step))
            {
                return;
            }

            var targetPoint = GetPathStepTarget(step, playerPosition);
            var destination = vnavmesh.FindNearestPoint(targetPoint, 5f, 5f) ?? targetPoint;
            var started = vnavmesh.PathfindAndMoveCloseTo(destination, fly: false, GetPathStepTolerance(step));
            if (!started)
            {
                SetFailure(MovementState.Failed, $"Failed to start pathing for step: {step.Description}.");
                return;
            }

            lock (gate)
            {
                state = MovementState.Pathfinding;
                stepStarted = true;
                mountAttempted = false;
                dismountAttempted = false;
                stepAttemptCount++;
                stepStartedAt = DateTimeOffset.UtcNow;
                lastProgressAt = DateTimeOffset.UtcNow;
                lastDistance = distance;
                progressDistance = distance;
            }

            logger.Info($"Started route step: {step.Description}.");
            return;
        }

        var pathBusy = vnavmesh.IsPathRunning() || vnavmesh.IsPathfindInProgress();
        if (progressDistance - distance >= ProgressThreshold)
        {
            lock (gate)
            {
                lastProgressAt = DateTimeOffset.UtcNow;
                progressDistance = distance;
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
                mountAttempted = false;
                dismountAttempted = false;
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
                if (source == null)
                {
                    SetFailure(MovementState.Failed, "No aethernet data is available for Lifestream teleport.");
                    return;
                }

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
                mountAttempted = false;
                dismountAttempted = false;
                stepAttemptCount = 0;
                stepStartedAt = DateTimeOffset.UtcNow;
                lastProgressAt = DateTimeOffset.UtcNow;
                lastDistance = CalculateFlatDistance(playerPosition, step.Destination);
                progressDistance = lastDistance;
                startedAwayFromTransitionDestination = lastDistance > TransitionCompletionDistance;
                transitionObserved = false;
                stableSince = DateTimeOffset.MinValue;
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

        WaitForTransitionCompletion(step, playerPosition, distance, AethernetTimeout, includeOccupiedCondition: true, $"aethernet {step.AethernetName}");
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
            progressDistance = float.MaxValue;
            mountAttempted = false;
            dismountAttempted = false;
            stepAttemptCount = 0;
            transitionObserved = false;
            startedAwayFromTransitionDestination = false;
            returnPromptHandled = false;
            stableSince = DateTimeOffset.MinValue;
            if (currentStepIndex >= (plannedRoute?.Steps.Count ?? 0))
            {
                state = MovementState.Arrived;
                lifestreamOwned = false;
                logger.Info("Movement route completed.");
                return;
            }

            state = plannedRoute!.Steps[currentStepIndex].Kind == RouteStepKind.Return
                ? MovementState.UsingReturn
                : MovementState.Pathfinding;
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
            mountAttempted = false;
            dismountAttempted = false;
            stepAttemptCount = 0;
            lifestreamOwned = false;
            transitionObserved = false;
            startedAwayFromTransitionDestination = false;
            returnPromptHandled = false;
            stableSince = DateTimeOffset.MinValue;
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
            mountAttempted = false;
            dismountAttempted = false;
            stepAttemptCount = 0;
            lifestreamOwned = false;
            transitionObserved = false;
            startedAwayFromTransitionDestination = false;
            returnPromptHandled = false;
            stableSince = DateTimeOffset.MinValue;
            lastError = reason;
            progressDistance = float.MaxValue;
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

    private void WaitForTransitionCompletion(
        RouteStep step,
        Vector3 playerPosition,
        float distance,
        TimeSpan timeout,
        bool includeOccupiedCondition,
        string transitionName)
    {
        var casting = condition[ConditionFlag.Casting];
        var betweenAreas = condition[ConditionFlag.BetweenAreas];
        var occupied = includeOccupiedCondition && condition[ConditionFlag.OccupiedInQuestEvent];
        var busy = lifestream.IsBusy();

        if (casting || betweenAreas || occupied || busy)
        {
            lock (gate)
            {
                transitionObserved = true;
                lastProgressAt = DateTimeOffset.UtcNow;
                stableSince = DateTimeOffset.MinValue;
            }

            return;
        }

        bool observed;
        bool startedAway;
        DateTimeOffset stableStart;
        lock (gate)
        {
            observed = transitionObserved;
            startedAway = startedAwayFromTransitionDestination;
            stableStart = stableSince;
        }

        var playerAvailable = objectTable.LocalPlayer?.CurrentHp > 0;
        var now = DateTimeOffset.UtcNow;
        if (playerAvailable && distance <= TransitionCompletionDistance && (observed || startedAway))
        {
            var nextStableStart = stableStart == DateTimeOffset.MinValue ? now : stableStart;
            lock (gate)
            {
                stableSince = nextStableStart;
            }

            if (now - nextStableStart >= TransitionStableTime)
            {
                logger.Info($"Completed route step: {step.Description}.");
                AdvanceStep();
            }

            return;
        }

        lock (gate)
        {
            stableSince = DateTimeOffset.MinValue;
        }

        if (now - stepStartedAt <= timeout)
        {
            return;
        }

        SetFailure(
            MovementState.TimedOut,
            $"{transitionName} timed out during step: {step.Description}. expected={FormatVector(step.Destination)} actual={FormatVector(playerPosition)} distance={distance:0.0} observedTransition={observed} conditions={DescribeTransitionConditions(includeOccupiedCondition)}",
            stopMovement: true);
    }

    private bool EnsureMounted(RouteStep step)
    {
        if (condition[ConditionFlag.Mounted])
        {
            return true;
        }

        if (condition[ConditionFlag.InCombat]
            || condition[ConditionFlag.Casting]
            || condition[ConditionFlag.BetweenAreas]
            || objectTable.LocalPlayer?.CurrentHp == 0)
        {
            if (!mountAttempted)
            {
                lock (gate)
                {
                    mountAttempted = true;
                }

                logger.Warning($"Proceeding on foot for step {step.Description} because mounting is currently unavailable.");
            }

            return true;
        }

        if (!mountAttempted)
        {
            if (!gameActionController.TryExecuteGeneralAction(GameActionController.MountActionId, step.Description))
            {
                lock (gate)
                {
                    mountAttempted = true;
                }

                logger.Warning($"Mount action unavailable; proceeding on foot for step {step.Description}.");
                return true;
            }

            lock (gate)
            {
                mountAttempted = true;
                stepStartedAt = DateTimeOffset.UtcNow;
                lastProgressAt = DateTimeOffset.UtcNow;
                state = MovementState.Pathfinding;
            }

            logger.Info($"Waiting for mount before route step: {step.Description}.");
            return false;
        }

        if (DateTimeOffset.UtcNow - stepStartedAt > MountTimeout)
        {
            logger.Warning($"Mount confirmation timed out; proceeding on foot for step {step.Description}.");
            return true;
        }

        return false;
    }

    private bool IsPathStepComplete(RouteStep step, Vector3 playerPosition, float distance)
    {
        if (!IsAethernetBandStep(step))
        {
            return distance <= step.ArrivalTolerance;
        }

        return IsWithinAethernetBand(playerPosition, step, out _);
    }

    private bool CompletePathStepArrival(RouteStep step)
    {
        if (!step.ShouldDismountOnArrival)
        {
            return true;
        }

        if (!condition[ConditionFlag.Mounted])
        {
            return true;
        }

        if (condition[ConditionFlag.InCombat]
            || condition[ConditionFlag.Casting]
            || condition[ConditionFlag.BetweenAreas]
            || objectTable.LocalPlayer?.CurrentHp == 0)
        {
            if (!dismountAttempted)
            {
                lock (gate)
                {
                    dismountAttempted = true;
                }

                logger.Warning($"Delaying dismount for step {step.Description} because dismount is currently unavailable.");
            }

            return false;
        }

        if (!dismountAttempted)
        {
            if (!gameActionController.TryExecuteGeneralAction(GameActionController.DismountActionId, step.Description))
            {
                logger.Warning($"Failed to dismount on arrival for step {step.Description}; retrying.");
                return false;
            }

            lock (gate)
            {
                dismountAttempted = true;
                stepStartedAt = DateTimeOffset.UtcNow;
            }

            logger.Info($"Waiting to dismount after route step: {step.Description}.");
            return false;
        }

        if (DateTimeOffset.UtcNow - stepStartedAt > MountTimeout)
        {
            lock (gate)
            {
                dismountAttempted = false;
                stepStartedAt = DateTimeOffset.UtcNow;
            }

            logger.Warning($"Dismount confirmation timed out for step {step.Description}; retrying.");
            return false;
        }

        return false;
    }

    private Vector3 GetPathStepTarget(RouteStep step, Vector3 playerPosition)
    {
        if (!IsAethernetBandStep(step))
        {
            return step.Destination;
        }

        return stepAttemptCount == 0
            ? GetDirectionalAethernetApproachPoint(playerPosition, step)
            : GetRandomAethernetBandPoint(step);
    }

    private float GetPathStepTolerance(RouteStep step)
        => IsAethernetBandStep(step) ? AethernetApproachTolerance : step.ArrivalTolerance;

    private static bool IsAethernetBandStep(RouteStep step)
        => step.Kind is RouteStepKind.PathToAethernet or RouteStepKind.RecoverToBaseCamp;

    private static bool IsWithinAethernetBand(Vector3 playerPosition, RouteStep step, out float distance)
    {
        distance = CalculateFlatDistance(playerPosition, step.InteractionCenter);
        return distance >= step.InteractDistanceMin && distance <= step.InteractDistanceMax;
    }

    private static Vector3 GetDirectionalAethernetApproachPoint(Vector3 playerPosition, RouteStep step)
    {
        var targetRadius = GetAethernetInnerBandTarget(step);
        var delta = new Vector2(playerPosition.X - step.InteractionCenter.X, playerPosition.Z - step.InteractionCenter.Z);
        var length = delta.Length();
        if (length <= float.Epsilon)
        {
            return GetRandomAethernetBandPoint(step);
        }

        var normalized = delta / length;
        return new Vector3(
            step.InteractionCenter.X + (normalized.X * targetRadius),
            step.InteractionCenter.Y,
            step.InteractionCenter.Z + (normalized.Y * targetRadius));
    }

    private static Vector3 GetRandomAethernetBandPoint(RouteStep step)
    {
        var innerMin = GetAethernetInnerBandTarget(step);
        var innerMax = MathF.Min(innerMin + AethernetBandWidth, step.InteractDistanceMax);
        var angle = (float)(Random.Shared.NextDouble() * Math.PI * 2d);
        var radiusSquared = (float)Random.Shared.NextDouble();
        var radius = MathF.Sqrt((radiusSquared * ((innerMax * innerMax) - (innerMin * innerMin))) + (innerMin * innerMin));
        return new Vector3(
            step.InteractionCenter.X + (MathF.Cos(angle) * radius),
            step.InteractionCenter.Y,
            step.InteractionCenter.Z + (MathF.Sin(angle) * radius));
    }

    private static float GetAethernetInnerBandTarget(RouteStep step)
        => MathF.Min(step.InteractDistanceMin + AethernetInnerEdgeBias, step.InteractDistanceMax);

    private unsafe bool IsSelectYesnoReady()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("SelectYesno", 1).Address;
        return addon != null && addon->IsReady;
    }

    private unsafe bool TryConfirmSelectYesno()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("SelectYesno", 1).Address;
        if (addon == null || !addon->IsReady)
        {
            return false;
        }

        addon->FireCallbackInt(0);
        return true;
    }

    private AethernetData? GetClosestAethernet(Vector3 playerPosition)
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

        return closest;
    }

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private string DescribeTransitionConditions(bool includeOccupiedCondition)
    {
        var occupied = includeOccupiedCondition && condition[ConditionFlag.OccupiedInQuestEvent];
        return $"casting={condition[ConditionFlag.Casting]} betweenAreas={condition[ConditionFlag.BetweenAreas]} occupiedInQuestEvent={occupied} lifestreamBusy={lifestream.IsBusy()}";
    }

    private static string FormatVector(Vector3 position)
        => $"<{position.X:0.000}, {position.Y:0.000}, {position.Z:0.000}>";

    public string GetStatusSummary()
    {
        var route = PlannedRoute;
        if (route == null)
        {
            return "No route planned";
        }

        return string.IsNullOrEmpty(route.SelectionReason)
            ? $"{route.RouteType} to {route.TargetDescription}"
            : $"{route.RouteType} to {route.TargetDescription} ({route.SelectionReason})";
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
