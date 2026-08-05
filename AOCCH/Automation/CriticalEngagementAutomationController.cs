using System;
using System.Numerics;
using System.Threading;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class CriticalEngagementAutomationController : IDisposable
{
    private static int nextRunSequence;
    private static readonly TimeSpan CombatExitGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);
    private const int WaitPointCandidateCount = 10;
    private const int WaitPointApproachArcDegrees = 120;
    private const float WaitRingMinRadius = 7f;
    private const float WaitPointStopDistance = 0.75f;
    private const float RepositionBuffer = 2f;
    private const float MinimumHoldRadius = 3f;

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly AutorotationController autorotationController;
    private readonly CombatTargetController combatTargetController;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private CriticalEngagementAutomationState state = CriticalEngagementAutomationState.Idle;
    private uint targetCeId;
    private string targetCeName = string.Empty;
    private uint lastTargetCeId;
    private string lastTargetCeName = string.Empty;
    private string currentRunId = string.Empty;
    private string lastError = string.Empty;
    private string lastTransition = "Idle";
    private AutomationRunResult lastResult;
    private DateTimeOffset lastCombatSeenAt = DateTimeOffset.MinValue;
    private DateTimeOffset awaitingCombatExitAt = DateTimeOffset.MinValue;
    private bool returnTravelFallbackAttempted;
    private bool returnRecoveryFallbackAttempted;
    private bool preemptionRecoveryPending;
    private Vector3 ceWaitPoint;
    private float ceWaitPointArrivalTolerance;

    public CriticalEngagementAutomationController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        OccultCrescentScanner scanner,
        MovementController movementController,
        AutorotationController autorotationController,
        CombatTargetController combatTargetController,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.scanner = scanner;
        this.movementController = movementController;
        this.autorotationController = autorotationController;
        this.combatTargetController = combatTargetController;
        this.configuration = configuration;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
    }

    public CriticalEngagementAutomationState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public uint TargetCeId
    {
        get
        {
            lock (gate)
            {
                return targetCeId;
            }
        }
    }

    public string TargetCeName
    {
        get
        {
            lock (gate)
            {
                return targetCeName;
            }
        }
    }

    public uint LastTargetCeId
    {
        get
        {
            lock (gate)
            {
                return lastTargetCeId;
            }
        }
    }

    public string LastTargetCeName
    {
        get
        {
            lock (gate)
            {
                return lastTargetCeName;
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
        => State is not CriticalEngagementAutomationState.Idle
            and not CriticalEngagementAutomationState.Stopped
            and not CriticalEngagementAutomationState.Completed
            and not CriticalEngagementAutomationState.Failed;

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
        => Start(scanner.Snapshot.EffectiveTarget.CriticalEncounter);

    public bool Start(ActiveCriticalEncounter? target, bool resumeAfterRaise = false)
    {
        if (IsRunning)
        {
            SetFailure("Critical Engagement automation is already running.");
            return false;
        }

        if (configuration.ScannerOnlyMode)
        {
            SetFailure("Critical Engagement automation start blocked because scanner-only mode is enabled.");
            return false;
        }

        var snapshot = scanner.Snapshot;
        if (target == null)
        {
            SetFailure("No Critical Engagement target is currently selected.");
            return false;
        }

        if (!snapshot.IsInSupportedTerritory || !snapshot.CanFarmCriticalEncounters)
        {
            SetFailure(snapshot.IsInSupportedTerritory
                ? $"Critical Engagement automation is unavailable in {snapshot.TerritoryDisplayName}."
                : "Critical Engagement automation requires a supported Occult Crescent territory.");
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
            currentRunId = $"CE#{Interlocked.Increment(ref nextRunSequence)}";
            targetCeId = target.Id;
            targetCeName = target.Name;
            lastTargetCeId = target.Id;
            lastTargetCeName = target.Name;
            lastError = string.Empty;
            lastResult = AutomationRunResult.None;
            lastCombatSeenAt = DateTimeOffset.MinValue;
            awaitingCombatExitAt = DateTimeOffset.MinValue;
            returnTravelFallbackAttempted = false;
            returnRecoveryFallbackAttempted = false;
            preemptionRecoveryPending = false;
            ceWaitPoint = default;
            ceWaitPointArrivalTolerance = 0f;
        }

        logger.Info($"{BuildLogTag()} op=start target=\"{target.Name}\" ({target.Id})");
        movementController.SetLogOwner(currentRunId);
        autorotationController.ValidateConfiguredPreset();

        if (resumeAfterRaise && snapshot.CurrentCriticalEncounterId == target.Id && target.IsBattle)
        {
            movementController.Stop("Resuming active CE after raise; combat is already in progress.");
            lastCombatSeenAt = DateTimeOffset.UtcNow;
            combatTargetController.MaintainCeTarget(target);
            autorotationController.ApplyForCombat($"CE {target.Name} ({target.Id}) resumed after raise");
            TransitionTo(CriticalEngagementAutomationState.InBattle, $"Resuming active CE {target.Name} ({target.Id}) after raise.");
            logger.Info($"{BuildLogTag()} op=resume-in-combat target=\"{target.Name}\" ({target.Id}) state={target.State}({target.StateCode}) reason=active-after-raise");
            return true;
        }

        return BeginPlanning(target);
    }

    public void Stop(string reason)
    {
        var targetId = TargetCeId;
        var targetName = TargetCeName;
        autorotationController.ReleaseOwnership(reason);
        combatTargetController.ReleaseOwnedTarget(reason);
        movementController.Stop(reason);
        TransitionTo(CriticalEngagementAutomationState.Stopped, reason, clearTarget: true, error: reason, result: AutomationRunResult.Stopped);
        logger.Info($"{BuildLogTag()} op=stop state={State} target=\"{targetName}\" ({targetId}) reason={reason}");
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = CriticalEngagementAutomationState.Idle;
            targetCeId = 0;
            targetCeName = string.Empty;
            lastTargetCeId = 0;
            lastTargetCeName = string.Empty;
            currentRunId = string.Empty;
            lastError = string.Empty;
            lastTransition = "Idle";
            lastResult = AutomationRunResult.None;
            lastCombatSeenAt = DateTimeOffset.MinValue;
            awaitingCombatExitAt = DateTimeOffset.MinValue;
            returnTravelFallbackAttempted = false;
            returnRecoveryFallbackAttempted = false;
            preemptionRecoveryPending = false;
            ceWaitPoint = default;
            ceWaitPointArrivalTolerance = 0f;
        }

        logger.Info($"[CE] op=reset reason={reason}");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        autorotationController.ReleaseOwnership("CE automation disposal");
        combatTargetController.ReleaseOwnedTarget("CE automation disposal");
        if (State != CriticalEngagementAutomationState.Idle)
        {
            movementController.Stop("CE automation disposal");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var currentState = State;
        if (currentState is CriticalEngagementAutomationState.Idle or CriticalEngagementAutomationState.Stopped or CriticalEngagementAutomationState.Completed or CriticalEngagementAutomationState.Failed)
        {
            return;
        }

        var snapshot = scanner.Snapshot;
        if (!snapshot.IsInSupportedTerritory || !snapshot.CanFarmCriticalEncounters)
        {
            Stop("CE automation stopped because its territory feature became unavailable.");
            return;
        }

        var target = snapshot.FindCriticalEncounter(TargetCeId);
        switch (currentState)
        {
            case CriticalEngagementAutomationState.PlanningRoute:
                if (target == null)
                {
                    SetFailureAndStopMovement("Target CE is no longer available while planning.");
                    return;
                }

                break;
            case CriticalEngagementAutomationState.TravelingToStaging:
                TickTraveling(snapshot, target);
                break;
            case CriticalEngagementAutomationState.WaitingForEngage:
                TickWaitingForEngage(snapshot, target);
                break;
            case CriticalEngagementAutomationState.InBattle:
                TickInBattle(snapshot, target);
                break;
            case CriticalEngagementAutomationState.AwaitingCombatExit:
                TickAwaitingCombatExit();
                break;
            case CriticalEngagementAutomationState.Recovering:
                TickRecovering();
                break;
        }
    }

    private bool BeginPlanning(ActiveCriticalEncounter target)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition == null)
        {
            SetFailure("Player position is unavailable while planning CE movement.");
            return false;
        }

        var routeDestination = target.StagingPoint;
        var arrivalTolerance = MathF.Max(0.5f, target.EngageRadius - RepositionBuffer);
        if (TrySelectCeWaitPoint(target, playerPosition.Value, out var waitPoint))
        {
            routeDestination = waitPoint;
            arrivalTolerance = WaitPointStopDistance;

            lock (gate)
            {
                ceWaitPoint = waitPoint;
                ceWaitPointArrivalTolerance = arrivalTolerance;
            }
        }
        else
        {
            lock (gate)
            {
                ceWaitPoint = target.StagingPoint;
                ceWaitPointArrivalTolerance = arrivalTolerance;
            }
        }

        TransitionTo(CriticalEngagementAutomationState.PlanningRoute, $"Planning route to CE {target.Name} ({target.Id}).");
        var selection = new TargetSelection
        {
            Kind = SelectedTargetKind.CriticalEncounter,
            CriticalEncounter = target,
            Reason = "CE automation lock",
        };

        if (!movementController.PlanRoute(selection, finalDestinationOverride: routeDestination, finalArrivalToleranceOverride: arrivalTolerance))
        {
            SetFailure($"Failed to plan route to CE: {movementController.LastError}");
            return false;
        }

        if (!movementController.StartPlannedRoute())
        {
            SetFailure($"Failed to start route to CE: {movementController.LastError}");
            return false;
        }

        TransitionTo(CriticalEngagementAutomationState.TravelingToStaging, $"Traveling to CE wait point for {target.Name} ({target.Id}).");
        return true;
    }

    private void TickTraveling(ScannerSnapshot snapshot, ActiveCriticalEncounter? target)
    {
        if (target == null)
        {
            StartRecovery("Target CE disappeared before arrival.");
            return;
        }

        if (target.IsWarmup)
        {
            if (snapshot.CurrentCriticalEncounterId == target.Id)
            {
                movementController.Stop("Entered CE warmup gate.");
                TransitionTo(CriticalEngagementAutomationState.WaitingForEngage, $"Waiting for CE {target.Name} ({target.Id}) to enter battle from warmup.");
            }
            else
            {
                PreemptForUnavailableEntry(target, "CE entered warmup and can no longer be joined.");
            }

            return;
        }

        if (target.IsBattle)
        {
            if (snapshot.CurrentCriticalEncounterId == target.Id)
            {
                movementController.Stop("CE entered battle before arrival.");
                EnterBattle(target, $"Entered CE battle for {target.Name} ({target.Id}) while traveling.");
            }
            else
            {
                PreemptForUnavailableEntry(target, "CE entered battle before the player joined.");
            }

            return;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                logger.ResetThrottle("ce-traveling");
                movementController.Stop("Reached CE wait point.");
                TransitionTo(CriticalEngagementAutomationState.WaitingForEngage, $"Waiting inside engage radius for {target.Name} ({target.Id}).");
                break;
            case MovementState.Failed:
            case MovementState.TimedOut:
                logger.ResetThrottle("ce-traveling");
                if (TryHandleReturnTravelFallback(target))
                {
                    return;
                }

                SetFailure($"Movement failed while traveling to CE: {movementController.LastError}");
                break;
            default:
                logger.DebugThrottled("ce-traveling", WaitLogInterval, $"CE automation is still traveling to {target.Name} ({target.Id}). MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
                break;
        }
    }

    private void TickWaitingForEngage(ScannerSnapshot snapshot, ActiveCriticalEncounter? target)
    {
        if (target == null)
        {
            StartRecovery("Target CE disappeared before engage.");
            return;
        }

        if (target.IsBattle && snapshot.CurrentCriticalEncounterId == target.Id)
        {
            EnterBattle(target, $"Entered CE battle for {target.Name} ({target.Id}).");
            return;
        }

        if (target.IsWarmup)
        {
            if (snapshot.CurrentCriticalEncounterId != target.Id)
            {
                PreemptForUnavailableEntry(target, "CE entered warmup and can no longer be joined.");
                return;
            }

            logger.DebugThrottled("ce-waiting-warmup", WaitLogInterval, $"CE automation is waiting in the warmup gate for {target.Name} ({target.Id}).");
            return;
        }

        if (target.IsBattle)
        {
            PreemptForUnavailableEntry(target, "CE entered battle before the player joined.");
            return;
        }

        logger.DebugThrottled("ce-waiting-engage", WaitLogInterval, $"CE automation is still waiting to engage {target.Name} ({target.Id}). MovementState={movementController.State} currentCe={snapshot.CurrentCriticalEncounterId}.");

        if (!EnsureInsideEngageRadius(target))
        {
            return;
        }

        if (movementController.State is MovementState.Failed or MovementState.TimedOut)
        {
            SetFailure($"Failed while repositioning for CE: {movementController.LastError}");
        }
    }

    private void TickInBattle(ScannerSnapshot snapshot, ActiveCriticalEncounter? target)
    {
        if (snapshot.CurrentCriticalEncounterId == TargetCeId)
        {
            lastCombatSeenAt = DateTimeOffset.UtcNow;
            if (target != null)
            {
                combatTargetController.MaintainCeTarget(target);
            }
            logger.DebugThrottled("ce-in-battle", WaitLogInterval, $"CE automation is still in battle for {TargetCeName} ({TargetCeId}). inCombat={condition[ConditionFlag.InCombat]} currentCe={snapshot.CurrentCriticalEncounterId}.");
            return;
        }

        if (lastCombatSeenAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - lastCombatSeenAt < CombatExitGrace)
        {
            return;
        }

        var reason = target == null
            ? $"CE {TargetCeName} completed or despawned."
            : $"CE {target.Name} no longer has the player engaged.";

        logger.ResetThrottle("ce-in-battle");
        if (condition[ConditionFlag.InCombat])
        {
            lock (gate)
            {
                awaitingCombatExitAt = DateTimeOffset.UtcNow;
            }

            TransitionTo(CriticalEngagementAutomationState.AwaitingCombatExit, $"{reason} Waiting for combat to end before releasing autorotation or recovering.");
            return;
        }

        CompleteBattle(reason);
    }

    private void TickAwaitingCombatExit()
    {
        if (condition[ConditionFlag.InCombat])
        {
            DateTimeOffset waitingSince;
            lock (gate)
            {
                waitingSince = awaitingCombatExitAt;
            }

            var elapsed = waitingSince == DateTimeOffset.MinValue ? TimeSpan.Zero : DateTimeOffset.UtcNow - waitingSince;
            logger.DebugThrottled("ce-awaiting-combat-exit", WaitLogInterval, $"CE automation is waiting for combat to end before completing {TargetCeName} ({TargetCeId}). elapsed={elapsed.TotalSeconds:0}s.");
            return;
        }

        logger.ResetThrottle("ce-awaiting-combat-exit");
        var reason = $"Combat cleared after CE {TargetCeName} ({TargetCeId}) completed.";
        logger.Info($"{BuildLogTag()} op=combat-cleared target=\"{TargetCeName}\" ({TargetCeId}) reason={reason}");
        CompleteBattle(reason);
    }

    private void CompleteBattle(string reason)
    {
        autorotationController.ReleaseOwnership(reason);
        combatTargetController.ReleaseOwnedTarget(reason);
        if (configuration.UseReturn)
        {
            StartRecovery(reason);
            return;
        }

        TransitionTo(CriticalEngagementAutomationState.Completed, reason, clearTarget: true, result: AutomationRunResult.Completed);
    }

    private void TickRecovering()
    {
        if (movementController.State is MovementState.Pathfinding or MovementState.WaitingForArrival or MovementState.UsingReturn or MovementState.UsingAethernet)
        {
            logger.DebugThrottled("ce-recovering", WaitLogInterval, $"CE automation is still recovering to Base Camp. MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                logger.ResetThrottle("ce-recovering");
                if (preemptionRecoveryPending)
                {
                    var targetId = TargetCeId;
                    var targetName = TargetCeName;
                    preemptionRecoveryPending = false;
                    var preemptionReason = $"Returned to Base Camp after CE {targetName} ({targetId}) became unavailable.";
                    TransitionTo(CriticalEngagementAutomationState.Stopped, preemptionReason, clearTarget: true, error: preemptionReason, result: AutomationRunResult.Preempted);
                    logger.Info($"{BuildLogTag()} op=preempt-complete target=\"{targetName}\" ({targetId}) reason={preemptionReason}");
                    break;
                }

                TransitionTo(CriticalEngagementAutomationState.Completed, "CE recovery completed.", clearTarget: true, result: AutomationRunResult.Completed);
                break;
            case MovementState.Failed:
            case MovementState.TimedOut:
                logger.ResetThrottle("ce-recovering");
                if (TryHandleReturnRecoveryFallback())
                {
                    return;
                }

                SetFailure($"CE recovery failed: {movementController.LastError}");
                break;
        }
    }

    private bool EnsureInsideEngageRadius(ActiveCriticalEncounter target)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition == null)
        {
            SetFailure("Player position is unavailable while waiting for CE engage.");
            return false;
        }

        var holdRadius = MathF.Max(MinimumHoldRadius, target.EngageRadius - RepositionBuffer);
        var distance = CalculateFlatDistance(playerPosition.Value, target.StagingPoint);
        if (distance <= holdRadius)
        {
            if (movementController.State is MovementState.Pathfinding or MovementState.WaitingForArrival)
            {
                movementController.Stop("Reached CE engage hold radius.");
            }

            return true;
        }

        if (movementController.State is MovementState.Pathfinding or MovementState.WaitingForArrival or MovementState.UsingAethernet)
        {
            return true;
        }

        logger.Info($"{BuildLogTag()} op=reposition target=\"{target.Name}\" ({target.Id}) reason=outside-engage-radius");
        return BeginWaitPointReposition(target, playerPosition.Value);
    }

    private void StartRecovery(string reason)
    {
        if (!movementController.RecoverToBaseCamp())
        {
            if (!returnRecoveryFallbackAttempted && movementController.RecoverToBaseCamp(allowReturn: false))
            {
                returnRecoveryFallbackAttempted = true;
                logger.Warning($"{BuildLogTag()} op=recovery-fallback target=\"{TargetCeName}\" ({TargetCeId}) reason=return-setup-failed fallback=direct-base-camp");
                TransitionTo(CriticalEngagementAutomationState.Recovering, "Recovering to Base Camp after CE with Return fallback.");
                return;
            }

            SetFailure($"Failed to start CE recovery: {movementController.LastError}");
            return;
        }

        TransitionTo(CriticalEngagementAutomationState.Recovering, "Recovering to Base Camp after CE.");
    }

    private void EnterBattle(ActiveCriticalEncounter target, string reason)
    {
        logger.ResetThrottle("ce-waiting-engage");
        lastCombatSeenAt = DateTimeOffset.UtcNow;
        combatTargetController.MaintainCeTarget(target);
        autorotationController.ApplyForCombat($"CE {target.Name} ({target.Id}) combat");
        TransitionTo(CriticalEngagementAutomationState.InBattle, reason);
    }

    private void PreemptForUnavailableEntry(ActiveCriticalEncounter target, string reason)
    {
        var fullReason = $"{reason} target=\"{target.Name}\" ({target.Id}) state={target.State}({target.StateCode}).";
        autorotationController.ReleaseOwnership(fullReason);
        combatTargetController.ReleaseOwnedTarget(fullReason);
        movementController.Stop(fullReason);
        preemptionRecoveryPending = true;
        logger.Info($"{BuildLogTag()} op=preempt state={target.State}({target.StateCode}) target=\"{target.Name}\" ({target.Id}) reason={reason} recovery=base-camp");
        StartRecovery(fullReason);
    }

    private bool TryHandleReturnTravelFallback(ActiveCriticalEncounter target)
    {
        if (returnTravelFallbackAttempted || movementController.PlannedRoute?.RouteType != "Return")
        {
            return false;
        }

        returnTravelFallbackAttempted = true;
        logger.Warning($"{BuildLogTag()} op=travel-fallback target=\"{target.Name}\" ({target.Id}) routeType=Return reason=route-failed fallback=without-return");
        return BeginPlanningWithoutReturn(target);
    }

    private bool BeginPlanningWithoutReturn(ActiveCriticalEncounter target)
    {
        Vector3 routeDestination;
        float arrivalTolerance;
        lock (gate)
        {
            routeDestination = ceWaitPoint;
            arrivalTolerance = ceWaitPointArrivalTolerance;
        }

        if (arrivalTolerance <= 0f)
        {
            routeDestination = target.StagingPoint;
            arrivalTolerance = MathF.Max(0.5f, target.EngageRadius - RepositionBuffer);
        }

        TransitionTo(CriticalEngagementAutomationState.PlanningRoute, $"Retrying route to CE {target.Name} ({target.Id}) without Return.");
        var selection = new TargetSelection
        {
            Kind = SelectedTargetKind.CriticalEncounter,
            CriticalEncounter = target,
            Reason = "CE automation lock fallback",
        };

        if (!movementController.PlanRoute(selection, allowReturn: false, finalDestinationOverride: routeDestination, finalArrivalToleranceOverride: arrivalTolerance))
        {
            logger.Warning($"{BuildLogTag()} op=fallback-plan-failed target=\"{target.Name}\" ({target.Id}) reason={movementController.LastError}");
            return false;
        }

        if (!movementController.StartPlannedRoute())
        {
            logger.Warning($"{BuildLogTag()} op=fallback-start-failed target=\"{target.Name}\" ({target.Id}) reason={movementController.LastError}");
            return false;
        }

        TransitionTo(CriticalEngagementAutomationState.TravelingToStaging, $"Traveling to CE wait point for {target.Name} ({target.Id}) with Return fallback disabled.");
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
        logger.Warning($"{BuildLogTag()} op=recovery-fallback target=\"{TargetCeName}\" ({TargetCeId}) reason=return-recovery-failed fallback=without-return");
        TransitionTo(CriticalEngagementAutomationState.Recovering, "Recovering to Base Camp after CE with direct fallback.");
        return true;
    }

    private void SetFailureAndStopMovement(string reason)
    {
        movementController.Stop(reason);
        SetFailure(reason);
    }

    private void SetFailure(string reason)
    {
        autorotationController.ReleaseOwnership(reason);
        TransitionTo(CriticalEngagementAutomationState.Failed, reason, clearTarget: false, error: reason, result: AutomationRunResult.Failed);
        logger.Warning($"{BuildLogTag()} op=failure state={CriticalEngagementAutomationState.Failed} target=\"{TargetCeName}\" ({TargetCeId}) reason={reason}");
    }

    private void TransitionTo(CriticalEngagementAutomationState nextState, string reason, bool clearTarget = false, string? error = null, AutomationRunResult? result = null)
    {
        CriticalEngagementAutomationState previousState;
        uint targetId;
        string targetName;
        lock (gate)
        {
            previousState = state;
            targetId = targetCeId;
            targetName = targetCeName;
            state = nextState;
            lastTransition = reason;
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
                targetCeId = 0;
                targetCeName = string.Empty;
                ceWaitPoint = default;
                ceWaitPointArrivalTolerance = 0f;
                awaitingCombatExitAt = DateTimeOffset.MinValue;
            }
        }

        logger.Info($"{BuildLogTag()} op=transition from={previousState} to={nextState} target=\"{targetName}\" ({targetId}) reason={reason}");
    }

    private string BuildLogTag()
        => currentRunId.Length == 0 ? "[CE]" : $"[CE run={currentRunId}]";

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private bool BeginWaitPointReposition(ActiveCriticalEncounter target, Vector3 playerPosition)
    {
        if (TrySelectCeWaitPoint(target, playerPosition, out var waitPoint))
        {
            lock (gate)
            {
                ceWaitPoint = waitPoint;
                ceWaitPointArrivalTolerance = WaitPointStopDistance;
            }

            return movementController.StartDirectMove($"Reposition to CE wait point for {target.Name} ({target.Id})", waitPoint, WaitPointStopDistance);
        }

        var fallbackTolerance = MathF.Max(0.5f, target.EngageRadius - RepositionBuffer);
        lock (gate)
        {
            ceWaitPoint = target.StagingPoint;
            ceWaitPointArrivalTolerance = fallbackTolerance;
        }

        logger.Warning($"{BuildLogTag()} op=wait-point-fallback target=\"{target.Name}\" ({target.Id}) reason=no-valid-wait-point fallback=staging-center");
        return movementController.StartDirectMove($"Reposition inside CE radius for {target.Name} ({target.Id})", target.StagingPoint, fallbackTolerance);
    }

    private bool TrySelectCeWaitPoint(ActiveCriticalEncounter target, Vector3 playerPosition, out Vector3 waitPoint)
    {
        waitPoint = default;

        var safeOuterRadius = MathF.Max(0.5f, target.EngageRadius - RepositionBuffer);
        var minRadius = MathF.Min(WaitRingMinRadius, safeOuterRadius);
        var candidates = new Vector3[WaitPointCandidateCount];
        var validCount = 0;
        var hasDirection = TryGetApproachDirection(target.StagingPoint, playerPosition, out var directionX, out var directionZ);

        for (var index = 0; index < WaitPointCandidateCount; index++)
        {
            var candidate = hasDirection
                ? CreateBiasedRingPoint(target.StagingPoint, minRadius, safeOuterRadius, directionX, directionZ)
                : CreateRandomRingPoint(target.StagingPoint, minRadius, safeOuterRadius);
            var snappedCandidate = movementController.FindNearestNavigablePoint(candidate, 5f, 5f);
            if (!snappedCandidate.HasValue)
            {
                continue;
            }

            var snappedDistance = CalculateFlatDistance(snappedCandidate.Value, target.StagingPoint);
            if (snappedDistance < minRadius || snappedDistance > safeOuterRadius)
            {
                continue;
            }

            candidates[validCount++] = snappedCandidate.Value;
        }

        if (validCount == 0)
        {
            logger.Warning($"{BuildLogTag()} op=wait-point-none target=\"{target.Name}\" ({target.Id}) minRadius={minRadius:0.0} maxRadius={safeOuterRadius:0.0}");
            return false;
        }

        waitPoint = candidates[Random.Shared.Next(validCount)];
        logger.Info($"{BuildLogTag()} op=wait-point-selected target=\"{target.Name}\" ({target.Id}) validCandidates={validCount}/{WaitPointCandidateCount} point={FormatVector(waitPoint)}");
        return true;
    }

    private static bool TryGetApproachDirection(Vector3 center, Vector3 playerPosition, out float directionX, out float directionZ)
    {
        directionX = playerPosition.X - center.X;
        directionZ = playerPosition.Z - center.Z;
        var length = MathF.Sqrt((directionX * directionX) + (directionZ * directionZ));
        if (length <= float.Epsilon)
        {
            directionX = 0f;
            directionZ = 0f;
            return false;
        }

        directionX /= length;
        directionZ /= length;
        return true;
    }

    private static Vector3 CreateBiasedRingPoint(Vector3 center, float minRadius, float maxRadius, float directionX, float directionZ)
    {
        var baseAngle = MathF.Atan2(directionZ, directionX);
        var halfArcRadians = (WaitPointApproachArcDegrees * (MathF.PI / 180f)) * 0.5f;
        var angle = baseAngle + (((float)Random.Shared.NextDouble() * 2f - 1f) * halfArcRadians);
        var radius = GetRandomRingRadius(minRadius, maxRadius);
        return new Vector3(
            center.X + (MathF.Cos(angle) * radius),
            center.Y,
            center.Z + (MathF.Sin(angle) * radius));
    }

    private static Vector3 CreateRandomRingPoint(Vector3 center, float minRadius, float maxRadius)
    {
        var angle = (float)(Random.Shared.NextDouble() * Math.PI * 2d);
        var radius = GetRandomRingRadius(minRadius, maxRadius);
        return new Vector3(
            center.X + (MathF.Cos(angle) * radius),
            center.Y,
            center.Z + (MathF.Sin(angle) * radius));
    }

    private static float GetRandomRingRadius(float minRadius, float maxRadius)
    {
        var radiusSquared = (float)Random.Shared.NextDouble();
        return MathF.Sqrt((radiusSquared * ((maxRadius * maxRadius) - (minRadius * minRadius))) + (minRadius * minRadius));
    }

    private static string FormatVector(Vector3 position)
        => $"<{position.X:0.000}, {position.Y:0.000}, {position.Z:0.000}>";
}
