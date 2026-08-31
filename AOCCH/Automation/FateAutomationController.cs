using System;
using System.Numerics;
using System.Threading;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class FateAutomationController : IDisposable
{
    private static int nextRunSequence;
    private static readonly TimeSpan CombatExitGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MonitorLogInterval = TimeSpan.FromSeconds(10);
    private const float FateParticipationPadding = 5f;
    private const float MinimumFateArrivalTolerance = 5f;
    private const float PotFateArrivalTolerance = 10f;
    private const int MinimumFateDismountDistance = 5;
    private const int MaximumFateDismountDistance = 50;
    private const float LiveTargetReplanDistance = 10f;
    private const int AutorotationDismountCycles = 3;
    private const int AutorotationDismountDispatchesPerCycle = 2;
    private static readonly TimeSpan AutorotationDismountPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly GameActionController gameActionController;
    private readonly AutorotationController autorotationController;
    private readonly CombatTargetController combatTargetController;
    private readonly PotCycleTracker potCycleTracker;
    private readonly PotFallbackWindowEvaluator potFallbackWindowEvaluator;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private FateAutomationState state = FateAutomationState.Idle;
    private uint targetFateId;
    private string targetFateName = string.Empty;
    private uint lastTargetFateId;
    private string lastTargetFateName = string.Empty;
    private string currentRunId = string.Empty;
    private bool targetIsPot;
    private bool lastTargetIsPot;
    private FateRunCompletionBehavior completionBehavior = FateRunCompletionBehavior.RecoverToBase;
    private FateRunCompletionBehavior lastCompletionBehavior = FateRunCompletionBehavior.RecoverToBase;
    private string lastError = string.Empty;
    private string lastTransition = "Idle";
    private DateTimeOffset lastCombatSeenAt = DateTimeOffset.MinValue;
    private DateTimeOffset stateEnteredAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastMonitorLogAt = DateTimeOffset.MinValue;
    private int lastLoggedProgress = -1;
    private int lastLoggedStateCode = -1;
    private bool returnTravelFallbackAttempted;
    private bool returnRecoveryFallbackAttempted;
    private Vector3? initialDestinationOverride;
    private float? initialArrivalToleranceOverride;
    private int lastObservedProgress = -1;
    private int lastObservedStateCode = -1;
    private string lastObservedState = string.Empty;
    private DateTimeOffset monitorStartedAt = DateTimeOffset.MinValue;
    private bool autorotationApplied;
    private bool pausedForRevival;
    private int autorotationDismountCycle;
    private int autorotationDismountDispatches;
    private DateTimeOffset autorotationDismountNextPollAt = DateTimeOffset.MinValue;
    private bool autorotationDismountPending;
    private AutomationRunResult lastResult;
    private string pendingCompletionReason = string.Empty;
    private bool plannedRouteUsesLiveTarget;
    private bool liveTargetRoutingActivated;
    private ulong plannedLiveTargetObjectId;
    private Vector3 plannedFateDestination;

    public FateAutomationController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        OccultCrescentScanner scanner,
        MovementController movementController,
        GameActionController gameActionController,
        AutorotationController autorotationController,
        CombatTargetController combatTargetController,
        PotCycleTracker potCycleTracker,
        PotFallbackWindowEvaluator potFallbackWindowEvaluator,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.scanner = scanner;
        this.movementController = movementController;
        this.gameActionController = gameActionController;
        this.autorotationController = autorotationController;
        this.combatTargetController = combatTargetController;
        this.potCycleTracker = potCycleTracker;
        this.potFallbackWindowEvaluator = potFallbackWindowEvaluator;
        this.configuration = configuration;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
    }

    public FateAutomationState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public uint TargetFateId
    {
        get
        {
            lock (gate)
            {
                return targetFateId;
            }
        }
    }

    public string TargetFateName
    {
        get
        {
            lock (gate)
            {
                return targetFateName;
            }
        }
    }

    public bool TargetIsPot
    {
        get
        {
            lock (gate)
            {
                return targetIsPot;
            }
        }
    }

    public uint LastTargetFateId
    {
        get
        {
            lock (gate)
            {
                return lastTargetFateId;
            }
        }
    }

    public string LastTargetFateName
    {
        get
        {
            lock (gate)
            {
                return lastTargetFateName;
            }
        }
    }

    public bool LastTargetIsPot
    {
        get
        {
            lock (gate)
            {
                return lastTargetIsPot;
            }
        }
    }

    public FateRunCompletionBehavior LastCompletionBehavior
    {
        get
        {
            lock (gate)
            {
                return lastCompletionBehavior;
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

    public bool IsRunning
        => State is not FateAutomationState.Idle
            and not FateAutomationState.Stopped
            and not FateAutomationState.Completed
            and not FateAutomationState.Failed;

    public bool IsPausedForRevival => pausedForRevival;

    public bool PauseForRevival(string reason)
    {
        if (State != FateAutomationState.Participating)
        {
            return false;
        }

        movementController.Stop(reason);
        autorotationController.ReleaseOwnership(reason);
        combatTargetController.ReleaseOwnedTarget(reason);
        lock (gate)
        {
            autorotationApplied = false;
        }
        pausedForRevival = true;
        logger.Info($"{BuildLogTag()} op=pause-for-revival target=\"{TargetFateName}\" ({TargetFateId}) reason={reason}");
        return true;
    }

    public bool CanPauseForRevival
        => State == FateAutomationState.Participating
            && condition[ConditionFlag.InCombat];

    public bool ResumeAfterRevival(string reason)
    {
        if (!pausedForRevival)
        {
            return false;
        }

        var target = scanner.Snapshot.FindFateRunTarget(TargetFateId, targetIsPot);
        if (target == null || !IsFateActive(target))
        {
            pausedForRevival = false;
            return false;
        }

        pausedForRevival = false;
        lastCombatSeenAt = DateTimeOffset.UtcNow;
        combatTargetController.MaintainFateTarget(target);
        EnsureAutorotationApplied(target);
        logger.Info($"{BuildLogTag()} op=resume-after-revival target=\"{TargetFateName}\" ({TargetFateId}) reason={reason}");
        return true;
    }

    public bool ResumeAfterRaise(FateRunTarget target, FateRunCompletionBehavior completionBehavior, string reason)
    {
        if (!IsRunning)
        {
            return Start(target, completionBehavior, resumeAfterRaise: true);
        }

        if (State != FateAutomationState.Participating
            || TargetFateId != target.Id
            || TargetIsPot != target.IsPotTarget
            || !IsFateActive(target))
        {
            logger.Warning(
                $"{BuildLogTag()} op=resume-after-raise-rejected state={State} "
                + $"currentTarget=\"{TargetFateName}\" ({TargetFateId}) pot={TargetIsPot} "
                + $"requestedTarget=\"{target.Name}\" ({target.Id}) pot={target.IsPotTarget} reason={reason}");
            return false;
        }

        pausedForRevival = false;
        lock (gate)
        {
            this.completionBehavior = completionBehavior;
            lastCombatSeenAt = DateTimeOffset.UtcNow;
        }

        movementController.Stop("Resuming active FATE after raise; participation is already in progress.");
        combatTargetController.MaintainFateTarget(target);
        EnsureAutorotationApplied(target);
        TransitionTo(FateAutomationState.Participating, reason);
        logger.Info(
            $"{BuildLogTag()} op=resume-after-raise target=\"{target.Name}\" ({target.Id}) "
            + $"pot={target.IsPotTarget} reason={reason}");
        return true;
    }

    public AutomationRunResult LastResult
    {
        get
        {
            lock (gate)
            {
                return lastResult;
            }
        }
    }

    public bool Start()
        => Start(scanner.Snapshot.EffectiveTarget.Fate);

    public bool Start(ActiveFate? target)
        => Start(target?.ToFateRunTarget(), FateRunCompletionBehavior.RecoverToBase);

    public bool Start(ActiveFate? target, FateRunCompletionBehavior completionBehavior)
        => Start(target?.ToFateRunTarget(), completionBehavior);

    public bool Start(ActivePotFate? target, FateRunCompletionBehavior completionBehavior = FateRunCompletionBehavior.CompleteInPlace)
        => Start(target?.ToFateRunTarget(), completionBehavior);

    public bool Start(ActivePotFate? target, Vector3? initialDestinationOverride, FateRunCompletionBehavior completionBehavior = FateRunCompletionBehavior.CompleteInPlace)
        => Start(target?.ToFateRunTarget(), completionBehavior, initialDestinationOverride);

    public bool Start(ActivePotFate? target, Vector3? initialDestinationOverride, float? initialArrivalToleranceOverride, FateRunCompletionBehavior completionBehavior = FateRunCompletionBehavior.CompleteInPlace)
        => Start(target?.ToFateRunTarget(), completionBehavior, initialDestinationOverride, initialArrivalToleranceOverride);

    public bool Start(FateRunTarget? target, FateRunCompletionBehavior completionBehavior = FateRunCompletionBehavior.RecoverToBase, Vector3? initialDestinationOverride = null, float? initialArrivalToleranceOverride = null, bool resumeAfterRaise = false)
    {
        if (resumeAfterRaise && IsRunning)
        {
            return target != null
                && ResumeAfterRaise(target, completionBehavior, $"Resuming active FATE {target.Name} ({target.Id}) after raise.");
        }

        if (IsRunning)
        {
            logger.Warning(
                $"{BuildLogTag()} op=start-rejected reason=already-running state={State} "
                + $"currentTarget=\"{TargetFateName}\" ({TargetFateId}) pot={TargetIsPot} "
                + $"requestedTarget=\"{target?.Name ?? string.Empty}\" ({target?.Id ?? 0}) pot={target?.IsPotTarget ?? false}");
            return false;
        }

        if (configuration.ScannerOnlyMode)
        {
            SetFailure("FATE automation start blocked because scanner-only mode is enabled.");
            return false;
        }

        var snapshot = scanner.Snapshot;
        if (target == null)
        {
            SetFailure("No FATE target is currently selected.");
            return false;
        }

        if (!snapshot.IsInSupportedTerritory || !snapshot.CanFarmFates)
        {
            SetFailure(snapshot.IsInSupportedTerritory
                ? $"FATE automation is unavailable in {snapshot.TerritoryDisplayName}."
                : "FATE automation requires a supported Occult Crescent territory.");
            return false;
        }

        var dependencyReport = Plugin.Current?.GetNormalAutomationDependencyReport();
        if (dependencyReport is { IsReady: false })
        {
            Plugin.Current?.TryOpenDependencyWindow();
            SetFailure(dependencyReport.FailureSummary);
            return false;
        }

        lock (gate)
        {
            currentRunId = $"FATE#{Interlocked.Increment(ref nextRunSequence)}";
            targetFateId = target.Id;
            targetFateName = target.Name;
            lastTargetFateId = target.Id;
            lastTargetFateName = target.Name;
            targetIsPot = target.IsPotTarget;
            lastTargetIsPot = target.IsPotTarget;
            this.completionBehavior = completionBehavior;
            lastCompletionBehavior = completionBehavior;
            lastError = string.Empty;
            lastCombatSeenAt = DateTimeOffset.MinValue;
            stateEnteredAt = DateTimeOffset.MinValue;
            lastMonitorLogAt = DateTimeOffset.MinValue;
            lastLoggedProgress = -1;
            lastLoggedStateCode = -1;
            returnTravelFallbackAttempted = false;
            returnRecoveryFallbackAttempted = false;
            this.initialDestinationOverride = initialDestinationOverride;
            this.initialArrivalToleranceOverride = initialArrivalToleranceOverride;
            lastObservedProgress = -1;
            lastObservedStateCode = -1;
            lastObservedState = string.Empty;
            monitorStartedAt = DateTimeOffset.MinValue;
            autorotationApplied = false;
            autorotationDismountCycle = 0;
            autorotationDismountDispatches = 0;
            autorotationDismountNextPollAt = DateTimeOffset.MinValue;
            autorotationDismountPending = false;
            lastResult = AutomationRunResult.None;
            pendingCompletionReason = string.Empty;
            plannedRouteUsesLiveTarget = false;
            liveTargetRoutingActivated = false;
            plannedLiveTargetObjectId = 0;
            plannedFateDestination = Vector3.Zero;
            pausedForRevival = false;
        }

        logger.Info($"{BuildLogTag()} op=start target=\"{target.Name}\" ({target.Id}) pot={target.IsPotTarget} completionBehavior={completionBehavior} initialDestinationOverride={(initialDestinationOverride.HasValue ? FormatVector(initialDestinationOverride.Value) : "none")} initialArrivalToleranceOverride={(initialArrivalToleranceOverride.HasValue ? $"{initialArrivalToleranceOverride.Value:0.0}" : "none")}");
        movementController.SetLogOwner(currentRunId);
        autorotationController.ValidateConfiguredPreset();

        if (resumeAfterRaise && IsFateActive(target) && (target.IsInFate || condition[ConditionFlag.InCombat]))
        {
            movementController.Stop("Resuming active FATE after raise; participation is already in progress.");
            monitorStartedAt = DateTimeOffset.UtcNow;
            lastCombatSeenAt = DateTimeOffset.UtcNow;
            combatTargetController.MaintainFateTarget(target);
            EnsureAutorotationApplied(target);
            TransitionTo(FateAutomationState.Participating, $"Resuming active FATE {target.Name} ({target.Id}) after raise.");
            logger.Info($"{BuildLogTag()} op=resume-in-combat target=\"{target.Name}\" ({target.Id}) state={target.State}({target.StateCode}) inFate={target.IsInFate} inCombat={condition[ConditionFlag.InCombat]} reason=active-after-raise");
            return true;
        }

        return BeginPlanning(target, initialDestinationOverride, initialArrivalToleranceOverride);
    }

    public void Stop(string reason)
    {
        var targetId = TargetFateId;
        var targetName = TargetFateName;
        var isPot = TargetIsPot;
        autorotationController.ReleaseOwnership(reason);
        combatTargetController.ReleaseOwnedTarget(reason);
        movementController.Stop(reason);
        TransitionTo(FateAutomationState.Stopped, reason, clearTarget: true, error: reason, clearAutorotationState: true, result: AutomationRunResult.Stopped);
        logger.Info($"{BuildLogTag()} op=stop state={State} target=\"{targetName}\" ({targetId}) pot={isPot} reason={reason}");
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = FateAutomationState.Idle;
            targetFateId = 0;
            targetFateName = string.Empty;
            lastTargetFateId = 0;
            lastTargetFateName = string.Empty;
            currentRunId = string.Empty;
            targetIsPot = false;
            lastTargetIsPot = false;
            completionBehavior = FateRunCompletionBehavior.RecoverToBase;
            lastCompletionBehavior = FateRunCompletionBehavior.RecoverToBase;
            lastError = string.Empty;
            lastTransition = "Idle";
            lastCombatSeenAt = DateTimeOffset.MinValue;
            stateEnteredAt = DateTimeOffset.MinValue;
            lastMonitorLogAt = DateTimeOffset.MinValue;
            lastLoggedProgress = -1;
            lastLoggedStateCode = -1;
            returnTravelFallbackAttempted = false;
            returnRecoveryFallbackAttempted = false;
            initialDestinationOverride = null;
            initialArrivalToleranceOverride = null;
            lastObservedProgress = -1;
            lastObservedStateCode = -1;
            lastObservedState = string.Empty;
            monitorStartedAt = DateTimeOffset.MinValue;
            autorotationApplied = false;
            autorotationDismountCycle = 0;
            autorotationDismountDispatches = 0;
            autorotationDismountNextPollAt = DateTimeOffset.MinValue;
            autorotationDismountPending = false;
            lastResult = AutomationRunResult.None;
            pendingCompletionReason = string.Empty;
            plannedRouteUsesLiveTarget = false;
            liveTargetRoutingActivated = false;
            plannedLiveTargetObjectId = 0;
            plannedFateDestination = Vector3.Zero;
        }

        logger.Info($"[FATE] op=reset reason={reason}");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        autorotationController.ReleaseOwnership("FATE automation disposal");
        combatTargetController.ReleaseOwnedTarget("FATE automation disposal");
        if (State != FateAutomationState.Idle)
        {
            movementController.Stop("FATE automation disposal");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var currentState = State;
        if (currentState is FateAutomationState.Idle or FateAutomationState.Stopped or FateAutomationState.Completed or FateAutomationState.Failed)
        {
            return;
        }

        var snapshot = scanner.Snapshot;
        if (!snapshot.IsInSupportedTerritory || !snapshot.CanFarmFates)
        {
            Stop("FATE automation stopped because its territory feature became unavailable.");
            return;
        }

        if (IsCePreempting(snapshot))
        {
            HandleCePreemption(snapshot);
            return;
        }

        if (pausedForRevival)
        {
            return;
        }

        var target = snapshot.FindFateRunTarget(TargetFateId, targetIsPot);
        switch (currentState)
        {
            case FateAutomationState.PlanningRoute:
                if (target == null)
                {
                    SetFailureAndStopMovement("Target FATE is no longer available while planning.");
                    return;
                }

                break;
            case FateAutomationState.TravelingToFate:
                TickTraveling(target);
                break;
            case FateAutomationState.Participating:
                TickParticipating(target);
                break;
            case FateAutomationState.AwaitingCombatExit:
                TickAwaitingCombatExit();
                break;
            case FateAutomationState.Recovering:
                TickRecovering();
                break;
        }
    }

    private bool BeginPlanning(FateRunTarget target, Vector3? initialDestinationOverride = null, float? initialArrivalToleranceOverride = null)
    {
        if (initialDestinationOverride.HasValue && initialArrivalToleranceOverride.HasValue && IsWithinDestination(initialDestinationOverride.Value, initialArrivalToleranceOverride.Value))
        {
            monitorStartedAt = DateTimeOffset.UtcNow;
            logger.Info($"{BuildLogTag()} op=travel-skip target=\"{target.Name}\" ({target.Id}) destination={FormatVector(initialDestinationOverride.Value)} tolerance={initialArrivalToleranceOverride.Value:0.0} reason=already-within-arrival-tolerance");
            TransitionTo(FateAutomationState.Participating, $"Monitoring FATE {target.Name} ({target.Id}) from the current wait point.");
            return true;
        }

        TransitionTo(FateAutomationState.PlanningRoute, $"Planning route to FATE {target.Name} ({target.Id}).");
        var routeTarget = !target.IsPotTarget && !liveTargetRoutingActivated && !initialDestinationOverride.HasValue
            ? CreateCenterRouteTarget(target)
            : target;
        var arrivalTolerance = GetFateArrivalTolerance(target, initialArrivalToleranceOverride);
        logger.Info($"{BuildLogTag()} op=fate-arrival-tolerance target=\"{target.Name}\" ({target.Id}) pot={target.IsPotTarget} tolerance={arrivalTolerance:0.0} earlyDismountDistance={(GetEarlyDismountDistance(target)?.ToString("0.0") ?? "none")} override={(initialArrivalToleranceOverride.HasValue ? "true" : "false")}");
        if (!movementController.PlanRoute(routeTarget, finalDestinationOverride: initialDestinationOverride, finalArrivalToleranceOverride: arrivalTolerance, earlyDismountDistance: GetEarlyDismountDistance(target), enableStuckJumpMonitor: true))
        {
            SetFailure($"Failed to plan route to FATE: {movementController.LastError}");
            return false;
        }

        if (!movementController.StartPlannedRoute())
        {
            SetFailure($"Failed to start route to FATE: {movementController.LastError}");
            return false;
        }

        RememberPlannedFateDestination(routeTarget, initialDestinationOverride);
        TransitionTo(FateAutomationState.TravelingToFate, $"Traveling to FATE {target.Name} ({target.Id}).");
        return true;
    }

    private void TickTraveling(FateRunTarget? target)
    {
        if (target == null)
        {
            FinishFate("Target FATE disappeared before arrival.");
            return;
        }

        if (movementController.StuckJumpAttemptsExhausted)
        {
            StartStuckTravelRecovery(target);
            return;
        }

        if (TryReplanForLiveTarget(target))
        {
            return;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                logger.ResetThrottle("fate-traveling");
                movementController.Stop("Reached FATE destination.");
                monitorStartedAt = DateTimeOffset.UtcNow;
                TransitionTo(FateAutomationState.Participating, $"Monitoring FATE {target.Name} ({target.Id}).");
                break;
            case MovementState.Failed:
            case MovementState.TimedOut:
                logger.ResetThrottle("fate-traveling");
                if (TryHandleReturnTravelFallback(target))
                {
                    return;
                }

                SetFailure($"Movement failed while traveling to FATE: {movementController.LastError}");
                break;
            default:
                logger.DebugThrottled("fate-traveling", MonitorLogInterval, $"FATE automation is still traveling to {target.Name} ({target.Id}). MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
                break;
        }
    }

    private void TickParticipating(FateRunTarget? target)
    {
        if (target == null)
        {
            LogFateCompletionAudit("disappeared from the FATE table");
            FinishFate($"FATE {TargetFateName} completed or despawned.");
            return;
        }

        LogFateMonitor(target);

        if (IsAutorotationParticipationActive(target))
        {
            lastCombatSeenAt = DateTimeOffset.UtcNow;
            combatTargetController.MaintainFateTarget(target);
            EnsureAutorotationApplied(target);
            return;
        }

        if (lastCombatSeenAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - lastCombatSeenAt < CombatExitGrace)
        {
            return;
        }

        if (!IsFateActive(target))
        {
            LogFateCompletionAudit("left the active FATE state");
            FinishFate($"FATE {target.Name} ({target.Id}) is no longer active.");
            return;
        }

        if (HasArrivedWithinFateRadius(target))
        {
            return;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        var distance = playerPosition == null ? float.MaxValue : CalculateFlatDistance(playerPosition.Value, target.Position);
        var elapsed = monitorStartedAt == DateTimeOffset.MinValue ? TimeSpan.Zero : DateTimeOffset.UtcNow - monitorStartedAt;
        logger.Info($"{BuildLogTag()} op=no-participation-complete target=\"{target.Name}\" ({target.Id}) state={target.State}({target.StateCode}) progress={target.Progress}% distance={(playerPosition == null ? "unavailable" : $"{distance:0.0}")} radius={target.Radius:0.0} participationRadius={MathF.Max(target.Radius, 1f) + FateParticipationPadding:0.0} inFate={target.IsInFate} inCombat={condition[ConditionFlag.InCombat]} elapsed={elapsed:mm\\:ss}");
        FinishFate($"No active FATE participation detected for {target.Name} ({target.Id}).");
    }

    private void TickRecovering()
    {
        if (movementController.State is MovementState.Pathfinding or MovementState.WaitingForArrival or MovementState.UsingReturn or MovementState.UsingAethernet)
        {
            logger.DebugThrottled("fate-recovering", MonitorLogInterval, $"FATE automation is still recovering to Base Camp. MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                logger.ResetThrottle("fate-recovering");
                TransitionTo(FateAutomationState.Completed, "FATE recovery completed.", clearTarget: true, clearAutorotationState: true, result: AutomationRunResult.Completed);
                break;
            case MovementState.Failed:
            case MovementState.TimedOut:
                logger.ResetThrottle("fate-recovering");
                if (TryHandleReturnRecoveryFallback())
                {
                    return;
                }

                SetFailure($"FATE recovery failed: {movementController.LastError}");
                break;
        }
    }

    private void TickAwaitingCombatExit()
    {
        if (condition[ConditionFlag.InCombat])
        {
            var elapsed = stateEnteredAt == DateTimeOffset.MinValue ? TimeSpan.Zero : DateTimeOffset.UtcNow - stateEnteredAt;
            logger.DebugThrottled("fate-awaiting-combat-exit", MonitorLogInterval, $"FATE completion is waiting for combat to end. target=\"{TargetFateName}\" ({TargetFateId}) elapsed={elapsed:mm\\:ss}.");
            return;
        }

        logger.ResetThrottle("fate-awaiting-combat-exit");
        var reason = string.IsNullOrEmpty(pendingCompletionReason)
            ? "FATE completion resumed after combat ended."
            : pendingCompletionReason;
        logger.Info($"{BuildLogTag()} op=combat-exit target=\"{TargetFateName}\" ({TargetFateId}) completion-resumed reason={reason}");
        CompleteFate(reason);
    }

    private void EnsureAutorotationApplied(FateRunTarget target)
    {
        lock (gate)
        {
            if (autorotationApplied)
            {
                return;
            }
        }

        if (condition[ConditionFlag.Mounting])
        {
            logger.DebugThrottled(
                "fate-autorotation-mounting",
                MonitorLogInterval,
                $"{BuildLogTag()} op=autorotation-wait reason=mounting target=\"{target.Name}\" ({target.Id}).");
            return;
        }

        if (!condition[ConditionFlag.Mounted])
        {
            ResetAutorotationDismountState();
        }
        else if (!ProcessAutorotationDismount(target))
        {
            return;
        }

        var reason = target.IsInFate
            ? "joined FATE"
            : "entered combat";
        logger.Info($"{BuildLogTag()} op=autorotation-apply target=\"{target.Name}\" ({target.Id}) reason={reason}");
        autorotationController.ApplyForCombat($"FATE {target.Name} ({target.Id}) combat");
        lock (gate)
        {
            autorotationApplied = true;
        }
    }

    private bool ProcessAutorotationDismount(FateRunTarget target)
    {
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (!autorotationDismountPending)
            {
                autorotationDismountPending = true;
                autorotationDismountCycle = 1;
                autorotationDismountDispatches = 0;
                autorotationDismountNextPollAt = now;
            }

            if (now < autorotationDismountNextPollAt)
            {
                return false;
            }

            if (autorotationDismountCycle > AutorotationDismountCycles)
            {
                // The final 500 ms poll has elapsed with the player still mounted.
                autorotationDismountNextPollAt = DateTimeOffset.MinValue;
            }
        }

        if (!condition[ConditionFlag.Mounted])
        {
            ResetAutorotationDismountState();
            return true;
        }

        int cycle;
        int dispatch;
        lock (gate)
        {
            cycle = autorotationDismountCycle;
            if (cycle > AutorotationDismountCycles)
            {
                // Keep the failure outside the lock so recovery can transition state safely.
                dispatch = 0;
            }
            else
            {
                dispatch = ++autorotationDismountDispatches;
                autorotationDismountNextPollAt = now + AutorotationDismountPollInterval;
            }
        }

        if (cycle > AutorotationDismountCycles)
        {
            logger.Warning($"{BuildLogTag()} op=autorotation-dismount-failed target=\"{target.Name}\" ({target.Id}) cycles={AutorotationDismountCycles} dispatches={AutorotationDismountCycles * AutorotationDismountDispatchesPerCycle} action=abandon-fate");
            AbandonFateForDismountFailure(target);
            return false;
        }

        var actionDescription = $"FATE autorotation dismount cycle {cycle}/{AutorotationDismountCycles} dispatch {dispatch}/{AutorotationDismountDispatchesPerCycle}";
        if (!gameActionController.TryExecuteGeneralAction(GameActionController.DismountActionId, actionDescription))
        {
            logger.Warning($"{BuildLogTag()} op=autorotation-dismount-dispatch-failed target=\"{target.Name}\" ({target.Id}) cycle={cycle}/{AutorotationDismountCycles} dispatch={dispatch}/{AutorotationDismountDispatchesPerCycle}");
        }
        else
        {
            logger.Info($"{BuildLogTag()} op=autorotation-dismount-dispatch target=\"{target.Name}\" ({target.Id}) cycle={cycle}/{AutorotationDismountCycles} dispatch={dispatch}/{AutorotationDismountDispatchesPerCycle}");
        }

        if (dispatch == AutorotationDismountDispatchesPerCycle)
        {
            lock (gate)
            {
                autorotationDismountCycle++;
                autorotationDismountDispatches = 0;
            }
        }

        return false;
    }

    private void AbandonFateForDismountFailure(FateRunTarget target)
    {
        const string reason = "FATE autorotation could not dismount the player after three cycles; returning to Base Camp.";
        autorotationController.ReleaseOwnership(reason);
        combatTargetController.ReleaseOwnedTarget(reason);

        if (movementController.RecoverToBaseCamp())
        {
            ResetAutorotationDismountState();
            TransitionTo(FateAutomationState.Recovering, reason, clearTarget: true, clearAutorotationState: true);
            return;
        }

        if (!returnTravelFallbackAttempted && movementController.RecoverToBaseCamp(allowReturn: false))
        {
            returnTravelFallbackAttempted = true;
            logger.Warning($"{BuildLogTag()} op=autorotation-dismount-recovery-fallback target=\"{target.Name}\" ({target.Id}) reason=return-setup-failed fallback=direct-base-camp");
            ResetAutorotationDismountState();
            TransitionTo(FateAutomationState.Recovering, $"{reason} Return fallback disabled.", clearTarget: true, clearAutorotationState: true);
            return;
        }

        ResetAutorotationDismountState();
        SetFailure($"{reason} Failed to start Base Camp recovery: {movementController.LastError}");
    }

    private void ResetAutorotationDismountState()
    {
        lock (gate)
        {
            autorotationDismountCycle = 0;
            autorotationDismountDispatches = 0;
            autorotationDismountNextPollAt = DateTimeOffset.MinValue;
            autorotationDismountPending = false;
        }
    }

    private void FinishFate(string reason)
    {
        if (condition[ConditionFlag.InCombat])
        {
            pendingCompletionReason = reason;
            TransitionTo(FateAutomationState.AwaitingCombatExit, $"FATE completion detected; waiting for combat to end before completing: {reason}");
            return;
        }

        CompleteFate(reason);
    }

    private void CompleteFate(string reason)
    {
        autorotationController.ReleaseOwnership(reason);
        combatTargetController.ReleaseOwnedTarget(reason);
        if (completionBehavior == FateRunCompletionBehavior.CompleteInPlace)
        {
            TransitionTo(FateAutomationState.Completed, reason, clearTarget: true, clearAutorotationState: true, result: AutomationRunResult.Completed);
            return;
        }

        if (configuration.UseReturn)
        {
            if (!movementController.RecoverToBaseCamp())
            {
                if (!returnRecoveryFallbackAttempted && movementController.RecoverToBaseCamp(allowReturn: false))
                {
                    returnRecoveryFallbackAttempted = true;
                    logger.Warning($"{BuildLogTag()} op=recovery-fallback target=\"{TargetFateName}\" ({TargetFateId}) reason=return-setup-failed fallback=direct-base-camp");
                    TransitionTo(FateAutomationState.Recovering, "Recovering to Base Camp after FATE with Return fallback.", clearAutorotationState: true);
                    return;
                }

                SetFailure($"Failed to start FATE recovery: {movementController.LastError}");
                return;
            }

            TransitionTo(FateAutomationState.Recovering, "Recovering to Base Camp after FATE.", clearAutorotationState: true);
            return;
        }

        TransitionTo(FateAutomationState.Completed, reason, clearTarget: true, clearAutorotationState: true, result: AutomationRunResult.Completed);
    }

    private void HandleCePreemption(ScannerSnapshot snapshot)
    {
        var criticalEncounter = snapshot.EffectiveTarget.CriticalEncounter;
        var reason = criticalEncounter == null
            ? "CE preempted the active FATE target."
            : $"CE {criticalEncounter.Name} ({criticalEncounter.Id}) preempted the active FATE target.";

        autorotationController.ReleaseOwnership(reason);
        combatTargetController.ReleaseOwnedTarget(reason);
        movementController.Stop(reason);
        TransitionTo(FateAutomationState.Stopped, reason, clearTarget: true, error: reason, clearAutorotationState: true, result: AutomationRunResult.Preempted);
    }

    private bool IsCePreempting(ScannerSnapshot snapshot)
    {
        if (State is not (FateAutomationState.PlanningRoute or FateAutomationState.TravelingToFate)
            || targetIsPot
            || snapshot.EffectiveTarget.Kind != SelectedTargetKind.CriticalEncounter
            || !snapshot.EffectiveTarget.WouldPreemptFate)
        {
            return false;
        }

        var ceStartDecision = potFallbackWindowEvaluator.EvaluateCeStart(
            potCycleTracker.Snapshot,
            DateTimeOffset.UtcNow,
            snapshot.CanRunPotTreasure,
            snapshot.TerritoryKey);
        if (ceStartDecision.AllowStart)
        {
            return true;
        }

        var criticalEncounter = snapshot.EffectiveTarget.CriticalEncounter;
        logger.DebugThrottled(
            "fate-ce-preemption-cutoff",
            MonitorLogInterval,
            $"{BuildLogTag()} op=ce-preemption-blocked activeFate=\"{TargetFateName}\" ({TargetFateId}) candidateCe=\"{criticalEncounter?.Name ?? "unknown"}\" ({criticalEncounter?.Id ?? 0}) reason={ceStartDecision.Reason}");
        return false;
    }

    private bool IsAutorotationParticipationActive(FateRunTarget target)
    {
        return condition[ConditionFlag.InCombat] || target.IsInFate;
    }

    private bool HasArrivedWithinFateRadius(FateRunTarget target)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition == null)
        {
            return false;
        }

        var participationRadius = MathF.Max(target.Radius, 1f) + FateParticipationPadding;
        return CalculateFlatDistance(playerPosition.Value, target.Position) <= participationRadius;
    }

    private static bool IsFateActive(FateRunTarget target)
        => !string.Equals(target.State, "Ended", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(target.State, "Failed", StringComparison.OrdinalIgnoreCase);

    private void SetFailureAndStopMovement(string reason)
    {
        movementController.Stop(reason);
        SetFailure(reason);
    }

    private void StartStuckTravelRecovery(FateRunTarget target)
    {
        var reason = $"FATE travel remained stuck after three jump attempts for {target.Name} ({target.Id}); returning to Base Camp.";
        autorotationController.ReleaseOwnership(reason);
        combatTargetController.ReleaseOwnedTarget(reason);

        if (movementController.RecoverToBaseCamp())
        {
            TransitionTo(FateAutomationState.Recovering, reason, clearTarget: true, clearAutorotationState: true);
            return;
        }

        if (!returnRecoveryFallbackAttempted && movementController.RecoverToBaseCamp(allowReturn: false))
        {
            returnRecoveryFallbackAttempted = true;
            TransitionTo(FateAutomationState.Recovering, $"{reason} Return fallback disabled.", clearTarget: true, clearAutorotationState: true);
            return;
        }

        SetFailure($"{reason} Failed to start Base Camp recovery: {movementController.LastError}");
    }

    private void SetFailure(string reason)
    {
        autorotationController.ReleaseOwnership(reason);
        TransitionTo(FateAutomationState.Failed, reason, clearTarget: false, error: reason, clearAutorotationState: true, result: AutomationRunResult.Failed);
        logger.Warning($"{BuildLogTag()} op=failure state={FateAutomationState.Failed} target=\"{TargetFateName}\" ({TargetFateId}) pot={TargetIsPot} reason={reason}");
    }

    private void TransitionTo(FateAutomationState nextState, string reason, bool clearTarget = false, string? error = null, bool clearAutorotationState = false, AutomationRunResult? result = null)
    {
        FateAutomationState previousState;
        uint fateId;
        string fateName;
        bool isPot;
        lock (gate)
        {
            previousState = state;
            fateId = targetFateId;
            fateName = targetFateName;
            isPot = targetIsPot;
            state = nextState;
            lastTransition = reason;
            stateEnteredAt = DateTimeOffset.UtcNow;
            if (result.HasValue)
            {
                lastResult = result.Value;
            }
            if (error != null)
            {
                lastError = error;
            }

            if (clearTarget)
            {
                targetFateId = 0;
                targetFateName = string.Empty;
                targetIsPot = false;
                completionBehavior = FateRunCompletionBehavior.RecoverToBase;
                initialDestinationOverride = null;
                initialArrivalToleranceOverride = null;
                pendingCompletionReason = string.Empty;
            }

            if (clearAutorotationState)
            {
                autorotationApplied = false;
                autorotationDismountCycle = 0;
                autorotationDismountDispatches = 0;
                autorotationDismountNextPollAt = DateTimeOffset.MinValue;
                autorotationDismountPending = false;
                lastCombatSeenAt = DateTimeOffset.MinValue;
            }

            if (nextState != FateAutomationState.Participating)
            {
                lastMonitorLogAt = DateTimeOffset.MinValue;
                lastLoggedProgress = -1;
                lastLoggedStateCode = -1;
            }
        }

        logger.Info($"{BuildLogTag()} op=transition from={previousState} to={nextState} target=\"{fateName}\" ({fateId}) pot={isPot} reason={reason}");
    }

    private string BuildLogTag()
        => currentRunId.Length == 0 ? "[FATE]" : $"[FATE run={currentRunId}]";

    private void LogFateMonitor(FateRunTarget target)
    {
        lastObservedProgress = target.Progress;
        lastObservedStateCode = target.StateCode;
        lastObservedState = target.State;

        var now = DateTimeOffset.UtcNow;
        var playerPosition = objectTable.LocalPlayer?.Position;
        var distance = playerPosition == null ? float.MaxValue : CalculateFlatDistance(playerPosition.Value, target.Position);
        var shouldLog = now - lastMonitorLogAt >= MonitorLogInterval
            || lastLoggedProgress != target.Progress
            || lastLoggedStateCode != target.StateCode;

        if (!shouldLog)
        {
            return;
        }

        lastMonitorLogAt = now;
        lastLoggedProgress = target.Progress;
        lastLoggedStateCode = target.StateCode;
        var elapsed = stateEnteredAt == DateTimeOffset.MinValue ? TimeSpan.Zero : now - stateEnteredAt;
        logger.Verbose(
            $"FATE monitor {target.Name} ({target.Id}): state={target.State}({target.StateCode}) progress={target.Progress}% inFate={target.IsInFate} inCombat={condition[ConditionFlag.InCombat]} insideRadius={HasArrivedWithinFateRadius(target)} distance={distance:0.0} elapsed={elapsed:mm\\:ss}.");
    }

    private bool TryHandleReturnTravelFallback(FateRunTarget target)
    {
        if (returnTravelFallbackAttempted || movementController.PlannedRoute?.RouteType != "Return")
        {
            return false;
        }

        returnTravelFallbackAttempted = true;
        logger.Warning($"{BuildLogTag()} op=travel-fallback target=\"{target.Name}\" ({target.Id}) routeType=Return reason=route-failed fallback=without-return");
        return BeginPlanningWithoutReturn(target);
    }

    private bool BeginPlanningWithoutReturn(FateRunTarget target)
    {
        TransitionTo(FateAutomationState.PlanningRoute, $"Retrying route to FATE {target.Name} ({target.Id}) without Return.");
        var arrivalTolerance = GetFateArrivalTolerance(target, initialArrivalToleranceOverride);
        logger.Info($"{BuildLogTag()} op=fate-arrival-tolerance target=\"{target.Name}\" ({target.Id}) pot={target.IsPotTarget} tolerance={arrivalTolerance:0.0} earlyDismountDistance={(GetEarlyDismountDistance(target)?.ToString("0.0") ?? "none")} override={(initialArrivalToleranceOverride.HasValue ? "true" : "false")} fallback=without-return");
        if (!movementController.PlanRoute(target, allowReturn: false, finalDestinationOverride: initialDestinationOverride, finalArrivalToleranceOverride: arrivalTolerance, earlyDismountDistance: GetEarlyDismountDistance(target), enableStuckJumpMonitor: true))
        {
            logger.Warning($"{BuildLogTag()} op=fallback-plan-failed target=\"{target.Name}\" ({target.Id}) reason={movementController.LastError}");
            return false;
        }

        if (!movementController.StartPlannedRoute())
        {
            logger.Warning($"{BuildLogTag()} op=fallback-start-failed target=\"{target.Name}\" ({target.Id}) reason={movementController.LastError}");
            return false;
        }

        RememberPlannedFateDestination(target, initialDestinationOverride);
        TransitionTo(FateAutomationState.TravelingToFate, $"Traveling to FATE {target.Name} ({target.Id}) with Return fallback disabled.");
        return true;
    }

    private bool TryHandleReturnRecoveryFallback()
    {
        if (returnRecoveryFallbackAttempted || movementController.PlannedRoute?.RouteType != "Return")
        {
            return false;
        }

        if (!movementController.RecoverToBaseCamp(allowReturn: false))
        {
            return false;
        }

        returnRecoveryFallbackAttempted = true;
        logger.Warning($"{BuildLogTag()} op=recovery-fallback target=\"{TargetFateName}\" ({TargetFateId}) reason=return-recovery-failed fallback=without-return");
        TransitionTo(FateAutomationState.Recovering, "Recovering to Base Camp after FATE with direct fallback.", clearAutorotationState: true);
        return true;
    }

    private void LogFateCompletionAudit(string completionKind)
    {
        var elapsed = monitorStartedAt == DateTimeOffset.MinValue ? TimeSpan.Zero : DateTimeOffset.UtcNow - monitorStartedAt;
        logger.Info($"{BuildLogTag()} op=completion-audit target=\"{TargetFateName}\" ({TargetFateId}) completion={completionKind} lastState={lastObservedState}({lastObservedStateCode}) lastProgress={lastObservedProgress}% monitorElapsed={elapsed:mm\\:ss}");
    }

    private float? GetEarlyDismountDistance(FateRunTarget target)
        => target.IsPotTarget
            ? null
            : Math.Clamp(configuration.FateDismountDistance, MinimumFateDismountDistance, MaximumFateDismountDistance);

    private float GetFateArrivalTolerance(FateRunTarget target, float? overrideTolerance)
    {
        if (overrideTolerance.HasValue)
        {
            return overrideTolerance.Value;
        }

        if (target.IsPotTarget)
        {
            return PotFateArrivalTolerance;
        }

        return MathF.Max(
            MinimumFateArrivalTolerance,
            GetEarlyDismountDistance(target).GetValueOrDefault() / 2f);
    }

    private bool TryReplanForLiveTarget(FateRunTarget target)
    {
        if (target.IsPotTarget || initialDestinationOverride.HasValue)
        {
            return false;
        }

        if (movementController.IsEarlyDismountPending)
        {
            logger.DebugThrottled(
                "fate-live-target-dismount-pending",
                MonitorLogInterval,
                $"{BuildLogTag()} op=live-target-replan-suppressed reason=early-dismount-pending target=\"{target.Name}\" ({target.Id}) movement={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
            return false;
        }

        if (!liveTargetRoutingActivated)
        {
            if (!target.HasLiveTarget)
            {
                return false;
            }

            var playerPosition = objectTable.LocalPlayer?.Position;
            var earlyDismountDistance = GetEarlyDismountDistance(target);
            var activationDistance = earlyDismountDistance.GetValueOrDefault() * 1.5f;
            if (playerPosition is not { } position)
            {
                return false;
            }

            var playerDistance = CalculateFlatDistance(position, target.Position);
            if (playerDistance > activationDistance)
            {
                return false;
            }

            liveTargetRoutingActivated = true;
            logger.Info($"{BuildLogTag()} op=live-target-activation target=\"{target.Name}\" ({target.Id}) playerDistance={playerDistance:0.0} activationDistance={activationDistance:0.0} earlyDismountDistance={earlyDismountDistance:0.0}");
        }

        var usesLiveTarget = target.HasLiveTarget;
        var destinationChanged = CalculateFlatDistance(plannedFateDestination, target.Destination) >= LiveTargetReplanDistance;
        if (plannedRouteUsesLiveTarget == usesLiveTarget
            && (!usesLiveTarget || (plannedLiveTargetObjectId == target.LiveTargetObjectId && !destinationChanged)))
        {
            return false;
        }

        var previousSource = plannedRouteUsesLiveTarget ? $"live-target:{plannedLiveTargetObjectId:X}" : "fate-center";
        var nextSource = usesLiveTarget ? $"live-target:{target.LiveTargetName}({target.LiveTargetObjectId:X})" : "fate-center";
        logger.Info($"{BuildLogTag()} op=live-target-replan target=\"{target.Name}\" ({target.Id}) previous={previousSource} next={nextSource} destination={FormatVector(target.Destination)}");
        movementController.Stop("FATE live target changed; replanning route.");
        return BeginPlanning(target);
    }

    private static FateRunTarget CreateCenterRouteTarget(FateRunTarget target)
        => new()
        {
            Id = target.Id,
            Name = target.Name,
            State = target.State,
            StateCode = target.StateCode,
            IsInFate = target.IsInFate,
            Progress = target.Progress,
            Radius = target.Radius,
            Position = target.Position,
            PreferredAethernet = target.PreferredAethernet,
            IsPotTarget = target.IsPotTarget,
        };

    private void RememberPlannedFateDestination(FateRunTarget target, Vector3? destinationOverride)
    {
        if (target.IsPotTarget || destinationOverride.HasValue)
        {
            plannedRouteUsesLiveTarget = false;
            plannedLiveTargetObjectId = 0;
            plannedFateDestination = destinationOverride ?? target.Position;
            return;
        }

        plannedRouteUsesLiveTarget = target.HasLiveTarget;
        plannedLiveTargetObjectId = target.LiveTargetObjectId;
        plannedFateDestination = target.Destination;
    }

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private bool IsWithinDestination(Vector3 destination, float arrivalTolerance)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition == null)
        {
            return false;
        }

        return CalculateFlatDistance(playerPosition.Value, destination) <= MathF.Max(arrivalTolerance, 0.5f);
    }

    private static string FormatVector(Vector3 value)
        => $"<{value.X:0.000}, {value.Y:0.000}, {value.Z:0.000}>";
}
