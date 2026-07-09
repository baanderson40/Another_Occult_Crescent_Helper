using System;
using System.Numerics;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class CriticalEngagementAutomationController : IDisposable
{
    private static readonly TimeSpan CombatExitGrace = TimeSpan.FromSeconds(3);
    private const float RepositionBuffer = 2f;
    private const float MinimumHoldRadius = 3f;

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly AutorotationController autorotationController;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private CriticalEngagementAutomationState state = CriticalEngagementAutomationState.Idle;
    private uint targetCeId;
    private string targetCeName = string.Empty;
    private string lastError = string.Empty;
    private string lastTransition = "Idle";
    private AutomationRunResult lastResult;
    private DateTimeOffset lastCombatSeenAt = DateTimeOffset.MinValue;
    private bool returnTravelFallbackAttempted;
    private bool returnRecoveryFallbackAttempted;

    public CriticalEngagementAutomationController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        OccultCrescentScanner scanner,
        MovementController movementController,
        AutorotationController autorotationController,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.scanner = scanner;
        this.movementController = movementController;
        this.autorotationController = autorotationController;
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

    public bool Start(ActiveCriticalEncounter? target)
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

        if (!snapshot.IsInSouthHorn)
        {
            SetFailure("Critical Engagement automation requires South Horn.");
            return false;
        }

        lock (gate)
        {
            targetCeId = target.Id;
            targetCeName = target.Name;
            lastError = string.Empty;
            lastResult = AutomationRunResult.None;
            lastCombatSeenAt = DateTimeOffset.MinValue;
            returnTravelFallbackAttempted = false;
            returnRecoveryFallbackAttempted = false;
        }

        logger.Info($"CE automation starting for {target.Name} ({target.Id}).");
        autorotationController.ValidateConfiguredPreset();
        return BeginPlanning(target);
    }

    public void Stop(string reason)
    {
        autorotationController.ReleaseOwnership(reason);
        movementController.Stop(reason);
        TransitionTo(CriticalEngagementAutomationState.Stopped, reason, clearTarget: true, error: reason, result: AutomationRunResult.Stopped);
        logger.Info($"CE automation stopped: {reason}");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        autorotationController.ReleaseOwnership("CE automation disposal");
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
        if (!snapshot.IsInSouthHorn)
        {
            Stop("Left South Horn while CE automation was active.");
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
            case CriticalEngagementAutomationState.Recovering:
                TickRecovering();
                break;
        }
    }

    private bool BeginPlanning(ActiveCriticalEncounter target)
    {
        TransitionTo(CriticalEngagementAutomationState.PlanningRoute, $"Planning route to CE {target.Name} ({target.Id}).");
        var selection = new TargetSelection
        {
            Kind = SelectedTargetKind.CriticalEncounter,
            CriticalEncounter = target,
            Reason = "CE automation lock",
        };

        if (!movementController.PlanRoute(selection))
        {
            SetFailure($"Failed to plan route to CE: {movementController.LastError}");
            return false;
        }

        if (!movementController.StartPlannedRoute())
        {
            SetFailure($"Failed to start route to CE: {movementController.LastError}");
            return false;
        }

        TransitionTo(CriticalEngagementAutomationState.TravelingToStaging, $"Traveling to CE staging point for {target.Name} ({target.Id}).");
        return true;
    }

    private void TickTraveling(ScannerSnapshot snapshot, ActiveCriticalEncounter? target)
    {
        if (target == null)
        {
            StartRecovery("Target CE disappeared before arrival.");
            return;
        }

        if (snapshot.CurrentCriticalEncounterId == target.Id || IsInBattleState(target))
        {
            movementController.Stop("CE entered battle before arrival.");
            StartRecovery($"CE {target.Name} entered battle before arrival.");
            return;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached CE staging point.");
                TransitionTo(CriticalEngagementAutomationState.WaitingForEngage, $"Waiting inside engage radius for {target.Name} ({target.Id}).");
                break;
            case MovementState.Failed:
            case MovementState.TimedOut:
                if (TryHandleReturnTravelFallback(target))
                {
                    return;
                }

                SetFailure($"Movement failed while traveling to CE: {movementController.LastError}");
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

        if (snapshot.CurrentCriticalEncounterId == target.Id)
        {
            lastCombatSeenAt = DateTimeOffset.UtcNow;
            autorotationController.ApplyForCombat($"CE {target.Name} ({target.Id}) combat");
            TransitionTo(CriticalEngagementAutomationState.InBattle, $"Entered CE battle for {target.Name} ({target.Id}).");
            return;
        }

        if (IsInBattleState(target))
        {
            StartRecovery($"CE {target.Name} entered battle before engagement.");
            return;
        }

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
        if (snapshot.CurrentCriticalEncounterId == TargetCeId || condition[ConditionFlag.InCombat])
        {
            lastCombatSeenAt = DateTimeOffset.UtcNow;
            return;
        }

        if (lastCombatSeenAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - lastCombatSeenAt < CombatExitGrace)
        {
            return;
        }

        var reason = target == null
            ? $"CE {TargetCeName} completed or despawned."
            : $"CE {target.Name} no longer has the player engaged.";

        logger.Info(reason);
        autorotationController.ReleaseOwnership(reason);
        if (configuration.UseReturn)
        {
            StartRecovery(reason);
            return;
        }

        TransitionTo(CriticalEngagementAutomationState.Completed, reason, clearTarget: true, result: AutomationRunResult.Completed);
    }

    private void TickRecovering()
    {
        switch (movementController.State)
        {
            case MovementState.Arrived:
                TransitionTo(CriticalEngagementAutomationState.Completed, "CE recovery completed.", clearTarget: true);
                break;
            case MovementState.Failed:
            case MovementState.TimedOut:
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

        logger.Info($"Player drifted outside CE engage radius for {target.Name} ({target.Id}); repositioning.");
        return BeginPlanning(target);
    }

    private void StartRecovery(string reason)
    {
        logger.Info(reason);
        if (!movementController.RecoverToBaseCamp())
        {
            if (!returnRecoveryFallbackAttempted && movementController.RecoverToBaseCamp(allowReturn: false))
            {
                returnRecoveryFallbackAttempted = true;
                logger.Warning("CE recovery Return setup failed; falling back to direct Base Camp recovery.");
                TransitionTo(CriticalEngagementAutomationState.Recovering, "Recovering to Base Camp after CE with Return fallback.");
                return;
            }

            SetFailure($"Failed to start CE recovery: {movementController.LastError}");
            return;
        }

        TransitionTo(CriticalEngagementAutomationState.Recovering, "Recovering to Base Camp after CE.");
    }

    private bool IsInBattleState(ActiveCriticalEncounter target)
        => target.StateCode >= 3;

    private bool TryHandleReturnTravelFallback(ActiveCriticalEncounter target)
    {
        if (returnTravelFallbackAttempted || movementController.PlannedRoute?.RouteType != "Return")
        {
            return false;
        }

        returnTravelFallbackAttempted = true;
        logger.Warning($"Return route failed while traveling to CE {target.Name} ({target.Id}); retrying without Return.");
        return BeginPlanningWithoutReturn(target);
    }

    private bool BeginPlanningWithoutReturn(ActiveCriticalEncounter target)
    {
        TransitionTo(CriticalEngagementAutomationState.PlanningRoute, $"Retrying route to CE {target.Name} ({target.Id}) without Return.");
        var selection = new TargetSelection
        {
            Kind = SelectedTargetKind.CriticalEncounter,
            CriticalEncounter = target,
            Reason = "CE automation lock fallback",
        };

        if (!movementController.PlanRoute(selection, allowReturn: false))
        {
            logger.Warning($"CE fallback route planning failed: {movementController.LastError}");
            return false;
        }

        if (!movementController.StartPlannedRoute())
        {
            logger.Warning($"CE fallback route start failed: {movementController.LastError}");
            return false;
        }

        TransitionTo(CriticalEngagementAutomationState.TravelingToStaging, $"Traveling to CE staging point for {target.Name} ({target.Id}) with Return fallback disabled.");
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
        logger.Warning("Return recovery failed after CE; retrying Base Camp recovery without Return.");
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
        logger.Warning(reason);
    }

    private void TransitionTo(CriticalEngagementAutomationState nextState, string reason, bool clearTarget = false, string? error = null, AutomationRunResult? result = null)
    {
        lock (gate)
        {
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
            }
        }

        logger.Info($"CE automation state -> {nextState}: {reason}");
    }

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }
}
