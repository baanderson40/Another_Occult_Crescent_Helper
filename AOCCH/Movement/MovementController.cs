using System;
using System.Globalization;
using System.Numerics;
using System.Threading;
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
    private static int nextMovementOperationSequence;
    private static readonly TimeSpan RouteTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ActivePathStallTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReturnReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReturnReadyPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReturnStartTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReturnRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReturnPathStopTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReturnPathStopStableTime = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan StopVerificationTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AethernetAttemptTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TransitionStableTime = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PathStartGrace = TimeSpan.FromSeconds(1);
    private const int MaxIdlePathResets = 5;
    private const float TransitionCompletionDistance = 25f;
    private const float AethernetInnerEdgeBias = 0.15f;
    private const float AethernetBandWidth = 0.25f;
    private const float AethernetApproachTolerance = 0.25f;
    private const float BaseCampSideArcDegrees = 50f;
    private const float ProgressThreshold = 2f;
    private const int MaxAethernetAttempts = 3;
    private const int MaxReturnAttempts = 3;

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly OccultCrescentScanner scanner;
    private readonly VNavmeshIpc vnavmesh;
    private readonly LifestreamIpc lifestream;
    private readonly RoutePlanner routePlanner;
    private readonly GameActionController gameActionController;
    private readonly Configuration configuration;
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
    private DateTimeOffset dismountAttemptedAt = DateTimeOffset.MinValue;
    private int stepAttemptCount;
    private int idlePathResetCount;
    private bool lifestreamOwned;
    private bool transitionObserved;
    private bool startedAwayFromTransitionDestination;
    private bool returnPromptHandled;
    private int returnAttemptCount;
    private DateTimeOffset returnReadyWaitStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastReturnReadyPollAt = DateTimeOffset.MinValue;
    private DateTimeOffset returnAttemptStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset returnTransitionStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset returnPathStopWaitStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset returnPathStopStableSince = DateTimeOffset.MinValue;
    private DateTimeOffset returnRetryNotBeforeAt = DateTimeOffset.MinValue;
    private DateTimeOffset stopVerificationStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset stableSince = DateTimeOffset.MinValue;
    private string lastError = string.Empty;
    private string logOwner = string.Empty;
    private string currentMovementOperationId = string.Empty;
    private MovementState state = MovementState.Idle;
    private bool stopVerificationPending;

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
        Configuration configuration,
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
        this.configuration = configuration;
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

    public bool IsPathBusy
        => vnavmesh.IsPathRunning() || vnavmesh.IsPathfindInProgress();

    public bool CanUseReturnAction
        => gameActionController.CanUseGeneralAction(GameActionController.ReturnActionId);

    public void SetLogOwner(string? owner)
    {
        lock (gate)
        {
            logOwner = owner ?? string.Empty;
        }
    }

    public void ResetInstanceState(string reason)
    {
        vnavmesh.Stop();
        BeginStopVerification();
        if (lifestreamOwned && lifestream.IsBusy())
        {
            lifestream.Abort();
        }

        lock (gate)
        {
            plannedRoute = null;
            currentStepIndex = 0;
            routeStartedAt = DateTimeOffset.MinValue;
            stepStartedAt = DateTimeOffset.MinValue;
            lastProgressAt = DateTimeOffset.MinValue;
            lastDistance = float.MaxValue;
            progressDistance = float.MaxValue;
            stepStarted = false;
            mountAttempted = false;
            dismountAttempted = false;
            dismountAttemptedAt = DateTimeOffset.MinValue;
            stepAttemptCount = 0;
            idlePathResetCount = 0;
            lifestreamOwned = false;
            ResetTransitionTracking();
            ResetReturnTracking(clearAttemptCount: true);
            lastError = string.Empty;
            currentMovementOperationId = string.Empty;
            state = MovementState.Idle;
        }

        logger.Info($"{BuildLogTag()} op=reset reason={reason}");
    }

    public bool PlanRouteToSelectedTarget()
        => PlanRoute(scanner.Snapshot.EffectiveTarget);

    public bool PlanRoute(FateRunTarget target, bool allowReturn = true, Vector3? finalDestinationOverride = null, float? finalArrivalToleranceOverride = null, float? earlyDismountDistance = null)
    {
        var playerPosition = GetPlayerPosition();
        if (playerPosition == null)
        {
            SetFailure(MovementState.Failed, "Player position is unavailable.");
            return false;
        }

        SetState(MovementState.Planning);
        var territory = scanner.ActiveTerritoryData;
        if (territory == null)
        {
            SetFailure(MovementState.Failed, "A supported Occult Crescent territory is required for route planning.");
            return false;
        }

        if (!routePlanner.TryPlan(territory, target, playerPosition.Value, out var route, out var failureReason, allowReturn, finalDestinationOverride, finalArrivalToleranceOverride, earlyDismountDistance))
        {
            SetFailure(MovementState.Failed, failureReason);
            return false;
        }

        InitializePlannedRoute(route);
        logger.Info($"{BuildLogTag()} op=planned movementOperation={DescribeMovementOperation()} routeType={route.RouteType} target=\"{route.TargetDescription}\" steps={route.Steps.Count}");
        return true;
    }

    public bool PlanRouteToLocation(
        string description,
        string preferredAethernet,
        Vector3 destination,
        float arrivalTolerance,
        bool allowReturn = true,
        float? earlyDismountDistance = null,
        Vector3? earlyDismountTarget = null)
    {
        var playerPosition = GetPlayerPosition();
        if (playerPosition == null)
        {
            SetFailure(MovementState.Failed, "Player position is unavailable.");
            return false;
        }

        SetState(MovementState.Planning);
        var territory = scanner.ActiveTerritoryData;
        if (territory == null)
        {
            SetFailure(MovementState.Failed, "A supported Occult Crescent territory is required for route planning.");
            return false;
        }

        if (!routePlanner.TryPlanToLocation(territory, description, preferredAethernet, destination, playerPosition.Value, out var route, out var failureReason, allowReturn, arrivalTolerance, earlyDismountDistance, earlyDismountTarget))
        {
            SetFailure(MovementState.Failed, failureReason);
            return false;
        }

        InitializePlannedRoute(route);
        logger.Info($"{BuildLogTag()} op=planned movementOperation={DescribeMovementOperation()} routeType={route.RouteType} target=\"{route.TargetDescription}\" steps={route.Steps.Count}");
        return true;
    }

    public bool PlanRoute(
        TargetSelection selection,
        bool allowReturn = true,
        Vector3? finalDestinationOverride = null,
        float? finalArrivalToleranceOverride = null)
    {
        var playerPosition = GetPlayerPosition();
        if (playerPosition == null)
        {
            SetFailure(MovementState.Failed, "Player position is unavailable.");
            return false;
        }

        SetState(MovementState.Planning);
        var territory = scanner.ActiveTerritoryData;
        if (territory == null)
        {
            SetFailure(MovementState.Failed, "A supported Occult Crescent territory is required for route planning.");
            return false;
        }

        if (!routePlanner.TryPlan(territory, selection, playerPosition.Value, out var route, out var failureReason, allowReturn, finalDestinationOverride, finalArrivalToleranceOverride))
        {
            SetFailure(MovementState.Failed, failureReason);
            return false;
        }

        InitializePlannedRoute(route);
        logger.Info($"{BuildLogTag()} op=planned movementOperation={DescribeMovementOperation()} routeType={route.RouteType} target=\"{route.TargetDescription}\" steps={route.Steps.Count}");
        return true;
    }

    public bool StartPlannedRoute()
    {
        vnavmesh.Stop();
        BeginStopVerification();

        lock (gate)
        {
            if (plannedRoute == null)
            {
                lastError = "No route is planned.";
                state = MovementState.Failed;
                return false;
            }

            currentStepIndex = 0;
            currentMovementOperationId = NextMovementOperationId();
            routeStartedAt = DateTimeOffset.UtcNow;
            stepStartedAt = DateTimeOffset.MinValue;
            lastProgressAt = DateTimeOffset.UtcNow;
            lastDistance = float.MaxValue;
            progressDistance = float.MaxValue;
            stepStarted = false;
            mountAttempted = false;
            dismountAttempted = false;
            dismountAttemptedAt = DateTimeOffset.MinValue;
            stepAttemptCount = 0;
            idlePathResetCount = 0;
            lifestreamOwned = false;
            ResetTransitionTracking();
            ResetReturnTracking(clearAttemptCount: true);
            lastError = string.Empty;
            state = plannedRoute.Steps[0].Kind == RouteStepKind.Return
                ? MovementState.UsingReturn
                : MovementState.Pathfinding;
        }

        logger.Info($"{BuildLogTag()} op=start movementOperation={DescribeMovementOperation()} routeType={plannedRoute!.RouteType} target=\"{plannedRoute.TargetDescription}\" steps={plannedRoute.Steps.Count}");
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
        BeginStopVerification();

        var territory = scanner.ActiveTerritoryData;
        if (territory == null)
        {
            SetFailure(MovementState.Failed, "A supported Occult Crescent territory is required for base camp recovery.");
            return false;
        }

        if (!routePlanner.TryPlanBaseCampRecovery(territory, playerPosition.Value, out var route, out var failureReason, allowReturn))
        {
            SetFailure(MovementState.Failed, failureReason);
            return false;
        }

        lock (gate)
        {
            plannedRoute = route;
            currentStepIndex = 0;
            currentMovementOperationId = NextMovementOperationId();
            routeStartedAt = DateTimeOffset.UtcNow;
            stepStartedAt = DateTimeOffset.MinValue;
            lastProgressAt = DateTimeOffset.UtcNow;
            lastDistance = float.MaxValue;
            progressDistance = float.MaxValue;
            stepStarted = false;
            mountAttempted = false;
            dismountAttempted = false;
            dismountAttemptedAt = DateTimeOffset.MinValue;
            stepAttemptCount = 0;
            idlePathResetCount = 0;
            lifestreamOwned = false;
            ResetTransitionTracking();
            ResetReturnTracking(clearAttemptCount: true);
            lastError = string.Empty;
            state = route.Steps.Count > 0 && route.Steps[0].Kind == RouteStepKind.Return
                ? MovementState.UsingReturn
                : MovementState.Pathfinding;
        }

        logger.Info($"{BuildLogTag()} op=start movementOperation={DescribeMovementOperation()} routeType={route.RouteType} target=\"{route.TargetDescription}\" steps={route.Steps.Count} reason=RecoverToBaseCamp");
        return true;
    }

    public bool StartDirectMove(string description, Vector3 destination, float arrivalTolerance = 1f, bool shouldMountBeforeStep = true, bool destinationAlreadyResolved = false)
    {
        var resolvedDestination = destinationAlreadyResolved
            ? new Vector3?(destination)
            : ResolveNavigablePoint(destination, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!resolvedDestination.HasValue)
        {
            SetFailure(MovementState.Failed, $"No reliable vnavmesh point is available for direct movement: {description}. target={FormatVector(destination)}.");
            return false;
        }

        vnavmesh.Stop();
        BeginStopVerification();

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
                        Destination = resolvedDestination.Value,
                        ArrivalTolerance = arrivalTolerance,
                        ShouldMountBeforeStep = shouldMountBeforeStep,
                    },
                ],
            };
            currentStepIndex = 0;
            currentMovementOperationId = NextMovementOperationId();
            routeStartedAt = DateTimeOffset.UtcNow;
            stepStartedAt = DateTimeOffset.MinValue;
            lastProgressAt = DateTimeOffset.UtcNow;
            lastDistance = float.MaxValue;
            progressDistance = float.MaxValue;
            stepStarted = false;
            mountAttempted = false;
            dismountAttempted = false;
            dismountAttemptedAt = DateTimeOffset.MinValue;
            stepAttemptCount = 0;
            idlePathResetCount = 0;
            lifestreamOwned = false;
            ResetTransitionTracking();
            ResetReturnTracking(clearAttemptCount: true);
            lastError = string.Empty;
            state = MovementState.Pathfinding;
        }

        logger.Info($"{BuildLogTag()} op=start-direct movementOperation={DescribeMovementOperation()} target=\"{description}\" requested={FormatVector(destination)} resolved={FormatVector(resolvedDestination.Value)} arrivalTolerance={arrivalTolerance:0.0}");
        return true;
    }

    public Vector3? FindNearestNavigablePoint(Vector3 position, float halfExtentXZ = 5f, float halfExtentY = 5f)
        => ResolveNavigablePoint(position, halfExtentXZ, halfExtentY);

    public Vector3? FindPointOnFloor(Vector3 position, float halfExtentXZ = 2f, bool allowUnlandable = false)
        => vnavmesh.FindPointOnFloor(position, allowUnlandable, halfExtentXZ);

    public void Stop(string reason)
    {
        vnavmesh.Stop();
        BeginStopVerification();
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
            dismountAttemptedAt = DateTimeOffset.MinValue;
            stepAttemptCount = 0;
            idlePathResetCount = 0;
            lifestreamOwned = false;
            ResetTransitionTracking();
            ResetReturnTracking(clearAttemptCount: true);
            lastError = reason;
            currentMovementOperationId = string.IsNullOrWhiteSpace(currentMovementOperationId)
                ? NextMovementOperationId()
                : currentMovementOperationId;
        }

        logger.Info($"{BuildLogTag()} op=stop movementOperation={DescribeMovementOperation()} state={State} route={GetRouteSummary()} step={GetStepSummary()} reason={reason}");
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

        if (!scanner.Snapshot.IsInSupportedTerritory)
        {
            Stop("Left a supported Occult Crescent territory while moving.");
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
            if (objectTable.LocalPlayer?.CurrentHp == 0)
            {
                SetFailure(MovementState.Failed, "Cannot use Return while dead.");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (returnRetryNotBeforeAt != DateTimeOffset.MinValue && now < returnRetryNotBeforeAt)
            {
                logger.DebugThrottled(
                    BuildStepLogKey("return-retry-delay"),
                    TimeSpan.FromMilliseconds(250),
                    $"Waiting to retry Return step '{step.Description}'. remaining={(returnRetryNotBeforeAt - now).TotalMilliseconds:0}ms delay={ReturnRetryDelay.TotalMilliseconds:0}ms.");
                return;
            }

            if (returnReadyWaitStartedAt == DateTimeOffset.MinValue)
            {
                lock (gate)
                {
                    if (returnReadyWaitStartedAt == DateTimeOffset.MinValue)
                    {
                        returnAttemptCount++;
                        returnReadyWaitStartedAt = now;
                        lastReturnReadyPollAt = DateTimeOffset.MinValue;
                        returnPathStopWaitStartedAt = now;
                        returnPathStopStableSince = DateTimeOffset.MinValue;
                    }
                }
            }

            var pathBusy = vnavmesh.IsPathRunning() || vnavmesh.IsPathfindInProgress();
            vnavmesh.Stop();
            if (pathBusy)
            {
                lock (gate)
                {
                    returnPathStopStableSince = DateTimeOffset.MinValue;
                }

                if (now - returnPathStopWaitStartedAt <= ReturnPathStopTimeout)
                {
                    logger.DebugThrottled(
                        BuildStepLogKey("return-stop"),
                        TimeSpan.FromMilliseconds(250),
                        $"Waiting for vnavmesh to stop before Return step '{step.Description}'. elapsed={(now - returnPathStopWaitStartedAt).TotalMilliseconds:0}ms timeout={ReturnPathStopTimeout.TotalMilliseconds:0}ms pathRunning={vnavmesh.IsPathRunning()} pathfinding={vnavmesh.IsPathfindInProgress()}.");
                    return;
                }

                RetryOrFailReturnStep(step, playerPosition, distance, "vnavmesh did not stop before Return.");
                return;
            }

            if (returnPathStopStableSince == DateTimeOffset.MinValue)
            {
                lock (gate)
                {
                    returnPathStopStableSince = now;
                }

                logger.DebugThrottled(
                    BuildStepLogKey("return-stop-stable"),
                    TimeSpan.FromMilliseconds(250),
                    $"vnavmesh reported idle before Return step '{step.Description}'; waiting for a stable stop window of {ReturnPathStopStableTime.TotalMilliseconds:0}ms.");
                return;
            }

            if (now - returnPathStopStableSince < ReturnPathStopStableTime)
            {
                return;
            }

            var returnWaitElapsed = now - returnReadyWaitStartedAt;
            if (!IsReadyForReturn())
            {
                logger.DebugThrottled(
                    BuildStepLogKey("return-ready"),
                    ReturnReadyPollInterval,
                    $"Waiting for Return readiness for step '{step.Description}'. elapsed={returnWaitElapsed.TotalMilliseconds:0}ms timeout={ReturnReadyTimeout.TotalMilliseconds:0}ms conditions={DescribeReturnConditions()}.");

                if (returnWaitElapsed < ReturnReadyTimeout)
                {
                    return;
                }

                RetryOrFailReturnStep(step, playerPosition, distance, $"player not ready for Return. conditions={DescribeReturnConditions()}");
                return;
            }

            if (lastReturnReadyPollAt != DateTimeOffset.MinValue && now - lastReturnReadyPollAt < ReturnReadyPollInterval)
            {
                return;
            }

            lock (gate)
            {
                lastReturnReadyPollAt = now;
            }

            if (!gameActionController.CanUseGeneralAction(step.GeneralActionId))
            {
                if (returnWaitElapsed < ReturnReadyTimeout)
                {
                    logger.DebugThrottled(
                        BuildStepLogKey("return-action-ready"),
                        ReturnReadyPollInterval,
                        $"Waiting for Return action availability for step '{step.Description}'. elapsed={returnWaitElapsed.TotalMilliseconds:0}ms timeout={ReturnReadyTimeout.TotalMilliseconds:0}ms conditions={DescribeReturnConditions()}.");
                    return;
                }

                RetryOrFailReturnStep(step, playerPosition, distance, $"Return action stayed unavailable. conditions={DescribeReturnConditions()}");
                return;
            }

            if (!gameActionController.TryExecuteGeneralAction(step.GeneralActionId, step.Description))
            {
                RetryOrFailReturnStep(step, playerPosition, distance, "failed to execute Return action.");
                return;
            }

            lock (gate)
            {
                state = MovementState.UsingReturn;
                stepStarted = true;
                mountAttempted = false;
                dismountAttempted = false;
                dismountAttemptedAt = DateTimeOffset.MinValue;
                stepAttemptCount = 0;
                idlePathResetCount = 0;
                stepStartedAt = DateTimeOffset.UtcNow;
                returnAttemptStartedAt = DateTimeOffset.UtcNow;
                lastProgressAt = DateTimeOffset.UtcNow;
                lastDistance = distance;
                progressDistance = distance;
                startedAwayFromTransitionDestination = distance > TransitionCompletionDistance;
                transitionObserved = false;
                returnTransitionStartedAt = DateTimeOffset.MinValue;
                returnPromptHandled = false;
                returnReadyWaitStartedAt = DateTimeOffset.MinValue;
                lastReturnReadyPollAt = DateTimeOffset.MinValue;
                returnPathStopWaitStartedAt = DateTimeOffset.MinValue;
                returnPathStopStableSince = DateTimeOffset.MinValue;
                stableSince = DateTimeOffset.MinValue;
            }

            logger.Info($"{BuildLogTag()} op=step-start step=\"{step.Description}\" kind={step.Kind} attempt={returnAttemptCount}/{MaxReturnAttempts}");
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

                logger.Info($"{BuildLogTag()} op=return-confirm step=\"{step.Description}\" prompt=SelectYesno");
            }
        }

        WaitForReturnCompletion(step, playerPosition, distance);
    }

    private void ProcessPathStep(RouteStep step, Vector3 playerPosition)
    {
        var distance = CalculateFlatDistance(playerPosition, step.Destination);
        lock (gate)
        {
            lastDistance = distance;
        }

        var isWithinEarlyDismountRange = IsWithinEarlyDismountRange(step, playerPosition, out _);
        if (isWithinEarlyDismountRange && condition[ConditionFlag.Mounted])
        {
            ProcessEarlyDismount(step, playerPosition);
            return;
        }

        if (IsPathStepComplete(step, playerPosition, distance))
        {
            if (!CompletePathStepArrival(step))
            {
                return;
            }

            logger.Info($"{BuildLogTag()} op=step-complete step=\"{step.Description}\" kind={step.Kind}");
            AdvanceStep();
            return;
        }

        if (!stepStarted)
        {
            if (!WaitForStopVerification(step))
            {
                return;
            }

            if (ShouldMountForStep(step, distance) && !EnsureMounted(step))
            {
                return;
            }

            var targetPoint = GetPathStepTarget(step, playerPosition);
            var destination = ResolveNavigablePoint(targetPoint, halfExtentXZ: 5f, halfExtentY: 5f);
            if (!destination.HasValue)
            {
                SetFailure(MovementState.Failed, $"No reliable vnavmesh point is available for step: {step.Description}. target={FormatVector(targetPoint)}.", stopMovement: true);
                return;
            }

            var started = vnavmesh.PathfindAndMoveCloseTo(destination.Value, fly: false, GetPathStepTolerance(step));
            logger.Debug(
                $"Path step start attempt for '{step.Description}'. distance={distance:0.0} target={FormatVector(targetPoint)} navTarget={FormatVector(destination.Value)} tolerance={GetPathStepTolerance(step):0.0} shouldMount={step.ShouldMountBeforeStep} mounted={condition[ConditionFlag.Mounted]} pathRunning={vnavmesh.IsPathRunning()} pathfinding={vnavmesh.IsPathfindInProgress()} conditions={DescribeMovementConditions()}.");
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
                dismountAttemptedAt = DateTimeOffset.MinValue;
                stepAttemptCount++;
                idlePathResetCount = 0;
                stepStartedAt = DateTimeOffset.UtcNow;
                lastProgressAt = DateTimeOffset.UtcNow;
                lastDistance = distance;
                progressDistance = distance;
            }

            logger.Info($"{BuildLogTag()} op=step-start step=\"{step.Description}\" kind={step.Kind}");
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
        else if (distance < progressDistance)
        {
            lock (gate)
            {
                lastProgressAt = DateTimeOffset.UtcNow;
                state = MovementState.WaitingForArrival;
            }
        }

        var stallTimeout = pathBusy ? ActivePathStallTimeout : StallTimeout;
        if (DateTimeOffset.UtcNow - lastProgressAt > stallTimeout)
        {
            SetFailure(MovementState.TimedOut, $"Movement stalled during step: {step.Description}.", stopMovement: true);
            return;
        }

        ProcessEarlyDismount(step, playerPosition);

        if (pathBusy)
        {
            logger.DebugThrottled(BuildStepLogKey("path"), WaitLogInterval, $"Movement is still traveling step '{step.Description}'. distance={distance:0.0} state={State}.");
        }

        if (!pathBusy && distance > step.ArrivalTolerance)
        {
            if (DateTimeOffset.UtcNow - stepStartedAt <= PathStartGrace)
            {
                logger.DebugThrottled(
                    BuildStepLogKey("path-start-grace"),
                    TimeSpan.FromSeconds(1),
                    $"Path step '{step.Description}' has no active vnavmesh path yet but is still within startup grace. distance={distance:0.0} sinceStart={(DateTimeOffset.UtcNow - stepStartedAt).TotalMilliseconds:0}ms pathRunning={vnavmesh.IsPathRunning()} pathfinding={vnavmesh.IsPathfindInProgress()} conditions={DescribeMovementConditions()}.");
                return;
            }

            var resetAttempt = idlePathResetCount + 1;
            if (resetAttempt > MaxIdlePathResets)
            {
                SetFailure(
                    MovementState.Failed,
                    $"vnavmesh remained idle before arrival for step '{step.Description}' after {MaxIdlePathResets} reset attempt(s). distance={distance:0.0} tolerance={step.ArrivalTolerance:0.0} sinceStart={(DateTimeOffset.UtcNow - stepStartedAt).TotalSeconds:0.0}s lastProgressAgo={(DateTimeOffset.UtcNow - lastProgressAt).TotalSeconds:0.0}s mountAttempted={mountAttempted} mounted={condition[ConditionFlag.Mounted]} pathRunning={vnavmesh.IsPathRunning()} pathfinding={vnavmesh.IsPathfindInProgress()} conditions={DescribeMovementConditions()} expected={FormatVector(step.Destination)} actual={FormatVector(playerPosition)}.",
                    stopMovement: true);
                return;
            }

            logger.Warning(
                $"{BuildLogTag()} op=path-reset step=\"{step.Description}\" resetAttempt={resetAttempt}/{MaxIdlePathResets} distance={distance:0.0} tolerance={step.ArrivalTolerance:0.0} sinceStart={(DateTimeOffset.UtcNow - stepStartedAt).TotalSeconds:0.0}s lastProgressAgo={(DateTimeOffset.UtcNow - lastProgressAt).TotalSeconds:0.0}s mountAttempted={mountAttempted} mounted={condition[ConditionFlag.Mounted]} pathRunning={vnavmesh.IsPathRunning()} pathfinding={vnavmesh.IsPathfindInProgress()} conditions=\"{DescribeMovementConditions()}\" expected={FormatVector(step.Destination)} actual={FormatVector(playerPosition)}");
            lock (gate)
            {
                idlePathResetCount = resetAttempt;
                stepStarted = false;
                mountAttempted = false;
                dismountAttempted = false;
                dismountAttemptedAt = DateTimeOffset.MinValue;
            }
        }
    }

    private void ProcessAethernetStep(RouteStep step, Vector3 playerPosition)
    {
        if (!stepStarted)
        {
            StartAethernetAttempt(step, playerPosition);
            return;
        }

        var distance = CalculateFlatDistance(playerPosition, step.Destination);
        lock (gate)
        {
            lastDistance = distance;
        }

        WaitForAethernetCompletion(step, playerPosition, distance);
    }

    private void StartAethernetAttempt(RouteStep step, Vector3 playerPosition)
    {
        if (stepAttemptCount >= MaxAethernetAttempts)
        {
            SetFailure(MovementState.TimedOut, $"aethernet {step.AethernetName} failed after {stepAttemptCount} attempt(s).", stopMovement: true);
            return;
        }

        if (!EnsureDismountedForAethernet(step))
        {
            return;
        }

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

        int attemptCount;
        lock (gate)
        {
            stepAttemptCount++;
            attemptCount = stepAttemptCount;
        }

        if (!lifestream.TryAethernetTeleportByPlaceNameId(step.AethernetPlaceNameId))
        {
            RetryOrFailAethernetStep(
                step,
                playerPosition,
                CalculateFlatDistance(playerPosition, step.Destination),
                observedTransition: false,
                "Lifestream could not start the teleport request.");
            return;
        }

        var distance = CalculateFlatDistance(playerPosition, step.Destination);
        lock (gate)
        {
            state = MovementState.UsingAethernet;
            stepStarted = true;
            mountAttempted = false;
            dismountAttempted = false;
            dismountAttemptedAt = DateTimeOffset.MinValue;
            stepStartedAt = DateTimeOffset.UtcNow;
            lastProgressAt = DateTimeOffset.UtcNow;
            lastDistance = distance;
            progressDistance = distance;
            startedAwayFromTransitionDestination = distance > TransitionCompletionDistance;
            transitionObserved = false;
            stableSince = DateTimeOffset.MinValue;
            lifestreamOwned = true;
        }

        logger.Info($"{BuildLogTag()} op=step-start step=\"{step.Description}\" kind={step.Kind} attempt={attemptCount}/{MaxAethernetAttempts}");
    }

    private bool EnsureDismountedForAethernet(RouteStep step)
    {
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

                logger.Warning($"{BuildLogTag()} op=aethernet-delay step=\"{step.Description}\" reason=dismount-unavailable");
            }

            return false;
        }

        if (!dismountAttempted)
        {
            if (!gameActionController.TryExecuteGeneralAction(GameActionController.DismountActionId, step.Description))
            {
                logger.Warning($"{BuildLogTag()} op=aethernet-dismount-failed step=\"{step.Description}\" action=retry");
                return false;
            }

            lock (gate)
            {
                dismountAttempted = true;
                stepStartedAt = DateTimeOffset.UtcNow;
            }

            logger.Info($"{BuildLogTag()} op=aethernet-dismount-wait step=\"{step.Description}\"");
            return false;
        }

        if (DateTimeOffset.UtcNow - stepStartedAt > MountTimeout)
        {
            lock (gate)
            {
                dismountAttempted = false;
                stepStartedAt = DateTimeOffset.UtcNow;
            }

            logger.Warning($"{BuildLogTag()} op=aethernet-dismount-timeout step=\"{step.Description}\" action=retry");
        }

        return false;
    }

    private void WaitForAethernetCompletion(RouteStep step, Vector3 playerPosition, float distance)
    {
        var casting = condition[ConditionFlag.Casting];
        var betweenAreas = condition[ConditionFlag.BetweenAreas];
        var occupied = condition[ConditionFlag.OccupiedInQuestEvent];
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
        int attemptCount;
        lock (gate)
        {
            observed = transitionObserved;
            startedAway = startedAwayFromTransitionDestination;
            stableStart = stableSince;
            attemptCount = stepAttemptCount;
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
                logger.Info($"{BuildLogTag()} op=step-complete step=\"{step.Description}\" kind={step.Kind}");
                AdvanceStep();
            }

            return;
        }

        lock (gate)
        {
            stableSince = DateTimeOffset.MinValue;
        }

        if (now - stepStartedAt <= AethernetAttemptTimeout)
        {
            logger.DebugThrottled(BuildStepLogKey("aethernet"), WaitLogInterval, $"Movement is still waiting for aethernet step '{step.Description}'. distance={distance:0.0} observedTransition={observed} conditions={DescribeTransitionConditions(includeOccupiedCondition: true)}.");
            return;
        }

        RetryOrFailAethernetStep(
            step,
            playerPosition,
            distance,
            observed,
            $"conditions={DescribeTransitionConditions(includeOccupiedCondition: true)} attempt={attemptCount}/{MaxAethernetAttempts}");
    }

    private void RetryOrFailAethernetStep(RouteStep step, Vector3 playerPosition, float distance, bool observedTransition, string detail)
    {
        int attemptCount;
        lock (gate)
        {
            attemptCount = stepAttemptCount;
        }

        if (lifestreamOwned && lifestream.IsBusy())
        {
            lifestream.Abort();
        }

        if (attemptCount < MaxAethernetAttempts)
        {
            lock (gate)
            {
                stepStarted = false;
                stepStartedAt = DateTimeOffset.MinValue;
                lastProgressAt = DateTimeOffset.UtcNow;
                transitionObserved = false;
                startedAwayFromTransitionDestination = false;
                stableSince = DateTimeOffset.MinValue;
                lifestreamOwned = false;
            }

            logger.Warning(
                $"{BuildLogTag()} op=aethernet-retry step=\"{step.Description}\" aethernet=\"{step.AethernetName}\" attempt={attemptCount}/{MaxAethernetAttempts} expected={FormatVector(step.Destination)} actual={FormatVector(playerPosition)} distance={distance:0.0} observedTransition={observedTransition} detail=\"{detail}\"");
            return;
        }

        SetFailure(
            MovementState.TimedOut,
            $"aethernet {step.AethernetName} timed out after {attemptCount}/{MaxAethernetAttempts} attempt(s) during step: {step.Description}. expected={FormatVector(step.Destination)} actual={FormatVector(playerPosition)} distance={distance:0.0} observedTransition={observedTransition} {detail}",
            stopMovement: true);
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
            dismountAttemptedAt = DateTimeOffset.MinValue;
            stepAttemptCount = 0;
            idlePathResetCount = 0;
            ResetTransitionTracking();
            ResetReturnTracking(clearAttemptCount: true);
            if (currentStepIndex >= (plannedRoute?.Steps.Count ?? 0))
            {
                state = MovementState.Arrived;
                lifestreamOwned = false;
                logger.Info($"{BuildLogTag()} op=complete movementOperation={DescribeMovementOperation()} state={MovementState.Arrived} route={GetRouteSummary()} step={GetStepSummary()}");
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
        BeginStopVerification();
        lock (gate)
        {
            state = MovementState.Arrived;
            stepStarted = false;
            mountAttempted = false;
            dismountAttempted = false;
            dismountAttemptedAt = DateTimeOffset.MinValue;
            stepAttemptCount = 0;
            idlePathResetCount = 0;
            lifestreamOwned = false;
            ResetTransitionTracking();
            ResetReturnTracking(clearAttemptCount: true);
            lastError = string.Empty;
        }

        logger.Info($"{BuildLogTag()} op=complete movementOperation={DescribeMovementOperation()} state={MovementState.Arrived} route={GetRouteSummary()} step={GetStepSummary()}");
    }

    private void SetFailure(MovementState failureState, string reason, bool stopMovement = false)
    {
        if (stopMovement)
        {
            vnavmesh.Stop();
            BeginStopVerification();
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
            dismountAttemptedAt = DateTimeOffset.MinValue;
            stepAttemptCount = 0;
            idlePathResetCount = 0;
            lifestreamOwned = false;
            ResetTransitionTracking();
            ResetReturnTracking(clearAttemptCount: true);
            lastError = reason;
            progressDistance = float.MaxValue;
        }

        logger.Warning($"{BuildLogTag()} op=failure movementOperation={DescribeMovementOperation()} state={failureState} route={GetRouteSummary()} step={GetStepSummary()} reason={reason}");
    }

    private void SetState(MovementState nextState)
    {
        lock (gate)
        {
            state = nextState;
        }
    }

    private void InitializePlannedRoute(PlannedRoute route)
    {
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
            dismountAttemptedAt = DateTimeOffset.MinValue;
            stepAttemptCount = 0;
            idlePathResetCount = 0;
            lifestreamOwned = false;
            ResetTransitionTracking();
            ResetReturnTracking(clearAttemptCount: true);
            lastError = string.Empty;
            currentMovementOperationId = string.Empty;
            state = MovementState.Idle;
        }
    }

    private string BuildLogTag()
        => logOwner.Length == 0 ? "[Movement]" : $"[Movement owner={logOwner}]";

    private static string NextMovementOperationId()
        => $"move-{Interlocked.Increment(ref nextMovementOperationSequence)}";

    private string DescribeMovementOperation()
        => string.IsNullOrWhiteSpace(currentMovementOperationId) ? "none" : currentMovementOperationId;

    private string GetRouteSummary()
        => plannedRoute == null ? "none" : $"{plannedRoute.RouteType}:\"{plannedRoute.TargetDescription}\"";

    private string GetStepSummary()
    {
        if (plannedRoute == null || currentStepIndex < 0 || currentStepIndex >= plannedRoute.Steps.Count)
        {
            return "none";
        }

        var step = plannedRoute.Steps[currentStepIndex];
        return $"{currentStepIndex + 1}/{plannedRoute.Steps.Count}:{step.Kind}:\"{step.Description}\"";
    }

    private void WaitForReturnCompletion(RouteStep step, Vector3 playerPosition, float distance)
    {
        var now = DateTimeOffset.UtcNow;
        var transitionActive = condition[ConditionFlag.Casting]
            || condition[ConditionFlag.BetweenAreas]
            || condition[ConditionFlag.OccupiedInQuestEvent]
            || lifestream.IsBusy();

        if (transitionActive)
        {
            lock (gate)
            {
                transitionObserved = true;
                returnTransitionStartedAt = returnTransitionStartedAt == DateTimeOffset.MinValue ? now : returnTransitionStartedAt;
                lastProgressAt = now;
                stableSince = DateTimeOffset.MinValue;
            }

            return;
        }

        bool observed;
        bool startedAway;
        DateTimeOffset stableStart;
        DateTimeOffset attemptStart;
        DateTimeOffset transitionStart;
        lock (gate)
        {
            observed = transitionObserved;
            startedAway = startedAwayFromTransitionDestination;
            stableStart = stableSince;
            attemptStart = returnAttemptStartedAt;
            transitionStart = returnTransitionStartedAt;
        }

        if (!observed)
        {
            if (now - attemptStart <= ReturnStartTimeout)
            {
                logger.DebugThrottled(
                    BuildStepLogKey("return-start"),
                    TimeSpan.FromMilliseconds(250),
                    $"Movement is still waiting for Return to start during step '{step.Description}'. elapsed={(now - attemptStart).TotalMilliseconds:0}ms timeout={ReturnStartTimeout.TotalMilliseconds:0}ms promptHandled={returnPromptHandled} conditions={DescribeTransitionConditions(includeOccupiedCondition: true)}.");
                return;
            }

            RetryOrFailReturnStep(step, playerPosition, distance, $"Return did not start. promptHandled={returnPromptHandled} conditions={DescribeTransitionConditions(includeOccupiedCondition: true)}");
            return;
        }

        var playerAvailable = objectTable.LocalPlayer?.CurrentHp > 0;
        if (playerAvailable && distance <= TransitionCompletionDistance && (observed || startedAway))
        {
            var nextStableStart = stableStart == DateTimeOffset.MinValue ? now : stableStart;
            lock (gate)
            {
                stableSince = nextStableStart;
            }

            if (now - nextStableStart >= TransitionStableTime)
            {
                logger.Info($"{BuildLogTag()} op=step-complete step=\"{step.Description}\" kind={step.Kind}");
                AdvanceStep();
            }

            return;
        }

        lock (gate)
        {
            stableSince = DateTimeOffset.MinValue;
        }

        var timeoutStart = transitionStart == DateTimeOffset.MinValue ? attemptStart : transitionStart;
        if (now - timeoutStart <= ReturnTimeout)
        {
            logger.DebugThrottled(
                BuildStepLogKey("transition-Return"),
                WaitLogInterval,
                $"Movement is still waiting for Return completion during step '{step.Description}'. distance={distance:0.0} observedTransition={observed} conditions={DescribeTransitionConditions(includeOccupiedCondition: true)}.");
            return;
        }

        RetryOrFailReturnStep(
            step,
            playerPosition,
            distance,
            $"Return transition timed out. observedTransition={observed} conditions={DescribeTransitionConditions(includeOccupiedCondition: true)}");
    }

    private void RetryOrFailReturnStep(RouteStep step, Vector3 playerPosition, float distance, string detail)
    {
        int attemptCount;
        lock (gate)
        {
            attemptCount = returnAttemptCount;
        }

        if (attemptCount < MaxReturnAttempts)
        {
            lock (gate)
            {
                stepStarted = false;
                stepStartedAt = DateTimeOffset.MinValue;
                returnAttemptStartedAt = DateTimeOffset.MinValue;
                returnTransitionStartedAt = DateTimeOffset.MinValue;
                lastProgressAt = DateTimeOffset.UtcNow;
                returnReadyWaitStartedAt = DateTimeOffset.MinValue;
                lastReturnReadyPollAt = DateTimeOffset.MinValue;
                returnPathStopWaitStartedAt = DateTimeOffset.MinValue;
                returnPathStopStableSince = DateTimeOffset.MinValue;
                returnRetryNotBeforeAt = DateTimeOffset.UtcNow + ReturnRetryDelay;
                returnPromptHandled = false;
                transitionObserved = false;
                startedAwayFromTransitionDestination = false;
                stableSince = DateTimeOffset.MinValue;
            }

            logger.Warning(
                $"{BuildLogTag()} op=return-retry step=\"{step.Description}\" attempt={attemptCount}/{MaxReturnAttempts} expected={FormatVector(step.Destination)} actual={FormatVector(playerPosition)} distance={distance:0.0} detail=\"{detail}\"");
            return;
        }

        SetFailure(
            MovementState.TimedOut,
            $"Return failed after {attemptCount}/{MaxReturnAttempts} attempt(s) during step: {step.Description}. expected={FormatVector(step.Destination)} actual={FormatVector(playerPosition)} distance={distance:0.0} {detail}",
            stopMovement: true);
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
                logger.Info($"{BuildLogTag()} op=step-complete step=\"{step.Description}\" kind={step.Kind}");
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
            logger.DebugThrottled(BuildStepLogKey($"transition-{transitionName}"), WaitLogInterval, $"Movement is still waiting for {transitionName} completion during step '{step.Description}'. distance={distance:0.0} observedTransition={observed} conditions={DescribeTransitionConditions(includeOccupiedCondition)}.");
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
            logger.Debug($"Mount check for '{step.Description}' succeeded immediately because the player is already mounted. conditions={DescribeMovementConditions()}.");
            return true;
        }

        if (!mountAttempted
            && (condition[ConditionFlag.InCombat]
            || condition[ConditionFlag.BetweenAreas]
            || objectTable.LocalPlayer?.CurrentHp == 0))
        {
            lock (gate)
            {
                mountAttempted = true;
            }

            logger.Debug($"Mount attempt suppressed for '{step.Description}' because mounting is currently unavailable. conditions={DescribeMovementConditions()}.");
            logger.Warning($"{BuildLogTag()} op=mount-skip step=\"{step.Description}\" reason=mount-unavailable action=proceed-on-foot");
            return true;
        }

        if (!mountAttempted)
        {
            if (condition[ConditionFlag.Mounted])
            {
                logger.Debug($"Mount check for '{step.Description}' succeeded on the final sanity check before dispatch. conditions={DescribeMovementConditions()}.");
                return true;
            }

            logger.Debug($"Attempting mount action for '{step.Description}'. conditions={DescribeMovementConditions()}.");
            if (!gameActionController.TryExecuteGeneralAction(GameActionController.MountActionId, step.Description))
            {
                lock (gate)
                {
                    mountAttempted = true;
                }

                logger.Debug($"Mount action dispatch failed for '{step.Description}'. conditions={DescribeMovementConditions()}.");
                logger.Warning($"{BuildLogTag()} op=mount-skip step=\"{step.Description}\" reason=mount-dispatch-unavailable action=proceed-on-foot");
                return true;
            }

            lock (gate)
            {
                mountAttempted = true;
                stepStartedAt = DateTimeOffset.UtcNow;
                lastProgressAt = DateTimeOffset.UtcNow;
                state = MovementState.Pathfinding;
            }

            logger.Info($"{BuildLogTag()} op=mount-wait step=\"{step.Description}\"");
            return false;
        }

        if (DateTimeOffset.UtcNow - stepStartedAt > MountTimeout)
        {
            logger.Debug($"Mount confirmation timed out for '{step.Description}'. conditions={DescribeMovementConditions()}.");
            logger.Warning($"{BuildLogTag()} op=mount-timeout step=\"{step.Description}\" action=proceed-on-foot");
            return true;
        }

        logger.DebugThrottled(
            BuildStepLogKey("mount-wait"),
            TimeSpan.FromSeconds(1),
            $"Still waiting for mount confirmation on '{step.Description}'. elapsed={(DateTimeOffset.UtcNow - stepStartedAt).TotalMilliseconds:0}ms conditions={DescribeMovementConditions()}.");
        return false;
    }

    private bool ShouldMountForStep(RouteStep step, float distance)
    {
        if (!step.ShouldMountBeforeStep)
        {
            return false;
        }

        return distance > Math.Max(0, configuration.MinimumMountingRange);
    }

    private bool IsPathStepComplete(RouteStep step, Vector3 playerPosition, float distance)
    {
        if (!IsAethernetBandStep(step))
        {
            return distance <= step.ArrivalTolerance;
        }

        return IsWithinAethernetInteractRange(playerPosition, step, out _);
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

                logger.Warning($"{BuildLogTag()} op=dismount-delay step=\"{step.Description}\" reason=dismount-unavailable");
            }

            return false;
        }

        if (!dismountAttempted)
        {
            if (!gameActionController.TryExecuteGeneralAction(GameActionController.DismountActionId, step.Description))
            {
                logger.Warning($"{BuildLogTag()} op=dismount-failed step=\"{step.Description}\" action=retry");
                return false;
            }

            lock (gate)
            {
                dismountAttempted = true;
                stepStartedAt = DateTimeOffset.UtcNow;
            }

            logger.Info($"{BuildLogTag()} op=dismount-wait step=\"{step.Description}\"");
            return false;
        }

        if (DateTimeOffset.UtcNow - stepStartedAt > MountTimeout)
        {
            lock (gate)
            {
                dismountAttempted = false;
                stepStartedAt = DateTimeOffset.UtcNow;
            }

            logger.Warning($"{BuildLogTag()} op=dismount-timeout step=\"{step.Description}\" action=retry");
            return false;
        }

        return false;
    }

    private static bool IsWithinEarlyDismountRange(RouteStep step, Vector3 playerPosition, out float distanceToDismountTarget)
    {
        distanceToDismountTarget = float.MaxValue;
        if (step.EarlyDismountDistance <= 0f || IsZeroVector(step.EarlyDismountTarget))
        {
            return false;
        }

        distanceToDismountTarget = CalculateFlatDistance(playerPosition, step.EarlyDismountTarget);
        return distanceToDismountTarget <= step.EarlyDismountDistance;
    }

    private void ProcessEarlyDismount(RouteStep step, Vector3 playerPosition)
    {
        if (!IsWithinEarlyDismountRange(step, playerPosition, out var distanceToDismountTarget))
        {
            return;
        }

        if (!condition[ConditionFlag.Mounted])
        {
            return;
        }

        if (condition[ConditionFlag.InCombat]
            || condition[ConditionFlag.Casting]
            || condition[ConditionFlag.BetweenAreas]
            || objectTable.LocalPlayer?.CurrentHp == 0)
        {
            logger.DebugThrottled(
                BuildStepLogKey("early-dismount-blocked"),
                TimeSpan.FromSeconds(1),
                $"Early dismount is waiting during step '{step.Description}'. distanceToTarget={distanceToDismountTarget:0.0} threshold={step.EarlyDismountDistance:0.0} conditions={DescribeMovementConditions()}.");
            return;
        }

        if (!dismountAttempted)
        {
            if (!gameActionController.TryExecuteGeneralAction(GameActionController.DismountActionId, step.Description))
            {
                logger.DebugThrottled(
                    BuildStepLogKey("early-dismount-failed"),
                    TimeSpan.FromSeconds(1),
                    $"Early dismount action dispatch failed for step '{step.Description}'. distanceToTarget={distanceToDismountTarget:0.0} threshold={step.EarlyDismountDistance:0.0} conditions={DescribeMovementConditions()}.");
                return;
            }

            lock (gate)
            {
                dismountAttempted = true;
                dismountAttemptedAt = DateTimeOffset.UtcNow;
            }

            logger.Info($"{BuildLogTag()} op=early-dismount-request step=\"{step.Description}\" distance={distanceToDismountTarget:0.0} threshold={step.EarlyDismountDistance:0.0} target={FormatVector(step.EarlyDismountTarget)}");
            return;
        }

        logger.DebugThrottled(
            BuildStepLogKey("early-dismount-wait"),
            TimeSpan.FromSeconds(1),
            $"Still waiting for early dismount during step '{step.Description}'. distanceToTarget={distanceToDismountTarget:0.0} threshold={step.EarlyDismountDistance:0.0} elapsed={(DateTimeOffset.UtcNow - dismountAttemptedAt).TotalMilliseconds:0}ms conditions={DescribeMovementConditions()}.");

        if (DateTimeOffset.UtcNow - dismountAttemptedAt <= MountTimeout)
        {
            return;
        }

        lock (gate)
        {
            dismountAttempted = false;
            dismountAttemptedAt = DateTimeOffset.MinValue;
        }

        logger.Warning($"{BuildLogTag()} op=early-dismount-timeout step=\"{step.Description}\" action=retry distance={distanceToDismountTarget:0.0} threshold={step.EarlyDismountDistance:0.0}");
    }

    private Vector3 GetPathStepTarget(RouteStep step, Vector3 playerPosition)
    {
        if (!IsAethernetBandStep(step))
        {
            return step.Destination;
        }

        if (step.Kind == RouteStepKind.RecoverToBaseCamp && stepAttemptCount == 0)
        {
            return GetSideBiasedAethernetApproachPoint(playerPosition, step);
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

    private static bool IsZeroVector(Vector3 value)
        => value.X == 0f && value.Y == 0f && value.Z == 0f;

    private static bool IsWithinAethernetInteractRange(Vector3 playerPosition, RouteStep step, out float distance)
    {
        distance = CalculateFlatDistance(playerPosition, step.InteractionCenter);
        return distance <= step.InteractDistanceMax;
    }

    private string BuildStepLogKey(string category)
    {
        var routeDescription = PlannedRoute?.TargetDescription ?? "none";
        var stepDescription = GetActiveStepSummary();
        return $"movement-{category}-{routeDescription}-{stepDescription}";
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

    private static Vector3 GetSideBiasedAethernetApproachPoint(Vector3 playerPosition, RouteStep step)
    {
        var delta = new Vector2(playerPosition.X - step.InteractionCenter.X, playerPosition.Z - step.InteractionCenter.Z);
        var length = delta.Length();
        if (length <= float.Epsilon)
        {
            return GetRandomAethernetBandPoint(step);
        }

        var outward = delta / length;
        var side = Random.Shared.Next(2) == 0
            ? new Vector2(-outward.Y, outward.X)
            : new Vector2(outward.Y, -outward.X);
        var baseAngle = MathF.Atan2(side.Y, side.X);
        var halfArcRadians = (BaseCampSideArcDegrees * (MathF.PI / 180f)) * 0.5f;
        var angle = baseAngle + (((float)Random.Shared.NextDouble() * 2f - 1f) * halfArcRadians);
        var radius = GetRandomAethernetBandRadius(step);
        return new Vector3(
            step.InteractionCenter.X + (MathF.Cos(angle) * radius),
            step.InteractionCenter.Y,
            step.InteractionCenter.Z + (MathF.Sin(angle) * radius));
    }

    private static Vector3 GetRandomAethernetBandPoint(RouteStep step)
    {
        var angle = (float)(Random.Shared.NextDouble() * Math.PI * 2d);
        var radius = GetRandomAethernetBandRadius(step);
        return new Vector3(
            step.InteractionCenter.X + (MathF.Cos(angle) * radius),
            step.InteractionCenter.Y,
            step.InteractionCenter.Z + (MathF.Sin(angle) * radius));
    }

    private static float GetRandomAethernetBandRadius(RouteStep step)
    {
        var innerMin = GetAethernetInnerBandTarget(step);
        var innerMax = MathF.Min(innerMin + AethernetBandWidth, step.InteractDistanceMax);
        var radiusSquared = (float)Random.Shared.NextDouble();
        return MathF.Sqrt((radiusSquared * ((innerMax * innerMax) - (innerMin * innerMin))) + (innerMin * innerMin));
    }

    private static float GetAethernetInnerBandTarget(RouteStep step)
        => MathF.Min(step.InteractDistanceMin + AethernetInnerEdgeBias, step.InteractDistanceMax);

    private Vector3? ResolveNavigablePoint(Vector3 position, float halfExtentXZ, float halfExtentY, bool allowUnlandable = false)
    {
        var floorPoint = vnavmesh.FindPointOnFloor(position, allowUnlandable, halfExtentXZ);
        var nearestFromFloor = floorPoint.HasValue
            ? vnavmesh.FindNearestPoint(floorPoint.Value, halfExtentXZ, halfExtentY) ?? floorPoint
            : null;
        var nearestPoint = vnavmesh.FindNearestPoint(position, halfExtentXZ, halfExtentY);

        return SelectCloserNavigablePoint(position, nearestFromFloor, nearestPoint);
    }

    private static Vector3? SelectCloserNavigablePoint(Vector3 origin, Vector3? primary, Vector3? secondary)
    {
        if (!primary.HasValue)
        {
            return secondary;
        }

        if (!secondary.HasValue)
        {
            return primary;
        }

        return CalculateFlatDistance(origin, primary.Value) <= CalculateFlatDistance(origin, secondary.Value)
            ? primary
            : secondary;
    }

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

        foreach (var aethernet in scanner.ActiveTerritoryData?.Aethernets ?? [])
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

    private bool IsReadyForReturn()
        => objectTable.LocalPlayer?.CurrentHp > 0
            && !condition[ConditionFlag.InCombat]
            && !condition[ConditionFlag.Casting]
            && !condition[ConditionFlag.BetweenAreas]
            && !condition[ConditionFlag.OccupiedInQuestEvent]
            && !lifestream.IsBusy();

    private string DescribeReturnConditions()
        => $"available={objectTable.LocalPlayer?.CurrentHp > 0} dead={objectTable.LocalPlayer?.CurrentHp == 0} combat={condition[ConditionFlag.InCombat]} mounted={condition[ConditionFlag.Mounted]} casting={condition[ConditionFlag.Casting]} betweenAreas={condition[ConditionFlag.BetweenAreas]} occupiedInQuestEvent={condition[ConditionFlag.OccupiedInQuestEvent]} lifestreamBusy={lifestream.IsBusy()} pathRunning={vnavmesh.IsPathRunning()} pathfinding={vnavmesh.IsPathfindInProgress()}";

    private void ResetTransitionTracking()
    {
        transitionObserved = false;
        startedAwayFromTransitionDestination = false;
        stableSince = DateTimeOffset.MinValue;
        returnTransitionStartedAt = DateTimeOffset.MinValue;
    }

    private void ResetReturnTracking(bool clearAttemptCount)
    {
        returnPromptHandled = false;
        returnReadyWaitStartedAt = DateTimeOffset.MinValue;
        lastReturnReadyPollAt = DateTimeOffset.MinValue;
        returnAttemptStartedAt = DateTimeOffset.MinValue;
        returnPathStopWaitStartedAt = DateTimeOffset.MinValue;
        returnPathStopStableSince = DateTimeOffset.MinValue;
        returnRetryNotBeforeAt = DateTimeOffset.MinValue;
        if (clearAttemptCount)
        {
            returnAttemptCount = 0;
        }
    }

    private void BeginStopVerification()
    {
        lock (gate)
        {
            stopVerificationPending = true;
            stopVerificationStartedAt = DateTimeOffset.UtcNow;
        }
    }

    private bool WaitForStopVerification(RouteStep step)
    {
        DateTimeOffset startedAt;
        lock (gate)
        {
            if (!stopVerificationPending)
            {
                return true;
            }

            startedAt = stopVerificationStartedAt;
        }

        if (IsVnavIdle())
        {
            lock (gate)
            {
                stopVerificationPending = false;
                stopVerificationStartedAt = DateTimeOffset.MinValue;
            }

            return true;
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        if (elapsed < StopVerificationTimeout)
        {
            logger.DebugThrottled(
                BuildStepLogKey("stop-settle"),
                TimeSpan.FromMilliseconds(250),
                $"Waiting for vnavmesh to settle before starting step '{step.Description}'. elapsed={elapsed.TotalMilliseconds:0}ms timeout={StopVerificationTimeout.TotalMilliseconds:0}ms pathRunning={vnavmesh.IsPathRunning()} pathfinding={vnavmesh.IsPathfindInProgress()}.");
            return false;
        }

        logger.Warning(
            $"{BuildLogTag()} op=stop-settle-timeout step=\"{step.Description}\" elapsed={elapsed.TotalMilliseconds:0}ms timeout={StopVerificationTimeout.TotalMilliseconds:0}ms pathRunning={vnavmesh.IsPathRunning()} pathfinding={vnavmesh.IsPathfindInProgress()} reason=continuing-after-timeout");
        lock (gate)
        {
            stopVerificationPending = false;
            stopVerificationStartedAt = DateTimeOffset.MinValue;
        }

        return true;
    }

    private bool IsVnavIdle()
        => !vnavmesh.IsPathRunning() && !vnavmesh.IsPathfindInProgress();

    private string DescribeMovementConditions()
        => $"mounted={condition[ConditionFlag.Mounted]} inCombat={condition[ConditionFlag.InCombat]} casting={condition[ConditionFlag.Casting]} betweenAreas={condition[ConditionFlag.BetweenAreas]} occupied={condition[ConditionFlag.Occupied]} occupiedInQuestEvent={condition[ConditionFlag.OccupiedInQuestEvent]} dead={objectTable.LocalPlayer?.CurrentHp == 0}";

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
