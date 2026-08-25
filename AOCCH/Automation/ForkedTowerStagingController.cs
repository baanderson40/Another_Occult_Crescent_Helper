using System;
using System.Numerics;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class ForkedTowerStagingController : IDisposable
{
    private const uint ForkedTowerCeId = 64;
    private const int WaitPointCandidateCount = 10;
    private const float WaitRingMinimumRadius = 7f;
    private const float RepositionBuffer = 2f;
    private const float WaitPointArrivalTolerance = 0.75f;
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);

    private readonly IFramework framework;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private ForkedTowerStagingState state = ForkedTowerStagingState.Idle;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private AutomationRunResult lastResult;
    private Vector3 waitPoint;

    public ForkedTowerStagingController(
        IFramework framework,
        OccultCrescentScanner scanner,
        MovementController movementController,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.scanner = scanner;
        this.movementController = movementController;
        this.configuration = configuration;
        this.logger = logger;
        framework.Update += OnFrameworkUpdate;
    }

    public ForkedTowerStagingState State
    {
        get { lock (gate) return state; }
    }

    public bool IsRunning => State is ForkedTowerStagingState.TravelingToStaging or ForkedTowerStagingState.WaitingForSelection;

    public string LastTransition
    {
        get { lock (gate) return lastTransition; }
    }

    public string LastError
    {
        get { lock (gate) return lastError; }
    }

    public AutomationRunResult LastResult
    {
        get { lock (gate) return lastResult; }
    }

    public Vector3 WaitPoint
    {
        get { lock (gate) return waitPoint; }
    }

    public bool Start(ActiveCriticalEncounter target)
    {
        if (IsRunning)
        {
            return false;
        }

        if (!configuration.EnableForkedTowerAutomation)
        {
            SetFailure("Forked Tower automation is disabled.");
            return false;
        }

        if (target.Id != ForkedTowerCeId || !string.Equals(target.AutomationKind, "ForkedTower", StringComparison.OrdinalIgnoreCase))
        {
            SetFailure($"Target {target.Name} ({target.Id}) is not configured as Forked Tower automation.");
            return false;
        }

        if (!scanner.Snapshot.IsInSupportedTerritory || scanner.ActiveTerritoryData == null)
        {
            SetFailure("Forked Tower staging requires a supported Occult Crescent territory.");
            return false;
        }

        var playerPosition = Plugin.ObjectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue)
        {
            SetFailure("Player position is unavailable while starting Forked Tower staging.");
            return false;
        }

        if (!TrySelectWaitPoint(target, playerPosition.Value, out var selectedWaitPoint))
        {
            SetFailure("Could not find a navigable randomized Forked Tower staging point.");
            return false;
        }

        lock (gate)
        {
            waitPoint = selectedWaitPoint;
            lastError = string.Empty;
            lastResult = AutomationRunResult.None;
        }

        movementController.SetLogOwner("ForkedTowerStaging");
        if (!movementController.PlanRouteToLocation(
                $"Forked Tower staging for {target.Name} ({target.Id})",
                target.PreferredAethernet,
                selectedWaitPoint,
                WaitPointArrivalTolerance,
                allowReturn: true,
                shouldMountBeforeStep: true))
        {
            SetFailure($"Failed to plan Forked Tower staging route: {movementController.LastError}");
            return false;
        }

        if (!movementController.StartPlannedRoute())
        {
            SetFailure($"Failed to start Forked Tower staging route: {movementController.LastError}");
            return false;
        }

        TransitionTo(ForkedTowerStagingState.TravelingToStaging, $"Traveling to randomized Forked Tower staging point <{selectedWaitPoint.X:0.000},{selectedWaitPoint.Y:0.000},{selectedWaitPoint.Z:0.000}>.");
        return true;
    }

    public void Stop(string reason)
    {
        movementController.Stop(reason);
        TransitionTo(ForkedTowerStagingState.Stopped, reason, error: reason, result: AutomationRunResult.Stopped);
    }

    public void Preempt(string reason)
    {
        movementController.Stop(reason);
        TransitionTo(ForkedTowerStagingState.Stopped, reason, error: reason, result: AutomationRunResult.Preempted);
    }

    public void ResetInstanceState(string reason)
    {
        movementController.Stop(reason);
        lock (gate)
        {
            state = ForkedTowerStagingState.Idle;
            lastTransition = "Idle";
            lastError = string.Empty;
            lastResult = AutomationRunResult.None;
            waitPoint = default;
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        if (IsRunning)
        {
            Stop("Forked Tower staging disposal");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        switch (State)
        {
            case ForkedTowerStagingState.TravelingToStaging:
                if (HandleBattleStateBeforeSelection())
                {
                    return;
                }

                if (IsHigherPriorityActivityAvailable())
                {
                    Preempt("A higher-priority activity became available before Forked Tower entry.");
                    return;
                }

                switch (movementController.State)
                {
                    case MovementState.Arrived:
                        movementController.Stop("Reached Forked Tower staging point.");
                        TransitionTo(ForkedTowerStagingState.WaitingForSelection, "Reached Forked Tower staging point; waiting for selection/instance entry.");
                        break;
                    case MovementState.Failed:
                    case MovementState.TimedOut:
                        SetFailure($"Forked Tower staging movement failed: {movementController.LastError}");
                        break;
                    default:
                        logger.DebugThrottled("forked-tower-staging", WaitLogInterval, $"Forked Tower staging movement is active. movementState={movementController.State} distance={movementController.DistanceRemaining:0.0}y waitPoint=<{WaitPoint.X:0.0},{WaitPoint.Y:0.0},{WaitPoint.Z:0.0}>.");
                        break;
                }

                break;
            case ForkedTowerStagingState.WaitingForSelection:
                if (HandleBattleStateBeforeSelection())
                {
                    return;
                }

                if (IsHigherPriorityActivityAvailable())
                {
                    Preempt("A higher-priority activity became available while waiting for Forked Tower selection.");
                    return;
                }

                logger.DebugThrottled("forked-tower-selection", WaitLogInterval, $"Waiting for Forked Tower selection/instance entry. currentCeId={scanner.Snapshot.CurrentCriticalEncounterId} state={scanner.Snapshot.CurrentCriticalEncounter?.State ?? "none"}.");
                break;
        }
    }

    private bool HandleBattleStateBeforeSelection()
    {
        var snapshot = scanner.Snapshot;
        var tower = snapshot.FindCriticalEncounter(ForkedTowerCeId);
        if (tower == null || !tower.IsBattle)
        {
            return false;
        }

        if (snapshot.CurrentCriticalEncounterId != ForkedTowerCeId)
        {
            Preempt(
                $"Forked Tower entered battle before the player joined. " +
                $"currentCeId={snapshot.CurrentCriticalEncounterId} towerState={tower.State}({tower.StateCode}).");
            return true;
        }

        movementController.Stop("Forked Tower entered battle after the player joined; stopping staging movement.");
        TransitionTo(
            ForkedTowerStagingState.WaitingForSelection,
            $"Forked Tower battle detected for the joined player; staging movement stopped. currentCeId={snapshot.CurrentCriticalEncounterId}.");
        return true;
    }

    private bool TrySelectWaitPoint(ActiveCriticalEncounter target, Vector3 playerPosition, out Vector3 selectedPoint)
    {
        selectedPoint = default;
        var outerRadius = MathF.Max(0.5f, target.EngageRadius - RepositionBuffer);
        var minimumRadius = MathF.Min(WaitRingMinimumRadius, outerRadius);
        var direction = new Vector2(playerPosition.X - target.StagingPoint.X, playerPosition.Z - target.StagingPoint.Z);
        var hasDirection = direction.LengthSquared() > float.Epsilon;
        if (hasDirection)
        {
            direction = Vector2.Normalize(direction);
        }

        for (var index = 0; index < WaitPointCandidateCount; index++)
        {
            var angle = Random.Shared.NextSingle() * MathF.Tau;
            var radius = minimumRadius + (Random.Shared.NextSingle() * (outerRadius - minimumRadius));
            if (hasDirection)
            {
                angle += MathF.Atan2(direction.Y, direction.X);
            }

            var candidate = new Vector3(
                target.StagingPoint.X + (MathF.Cos(angle) * radius),
                target.StagingPoint.Y,
                target.StagingPoint.Z + (MathF.Sin(angle) * radius));
            var navigable = movementController.FindNearestNavigablePoint(candidate, 5f, 5f);
            if (!navigable.HasValue)
            {
                continue;
            }

            var snappedDistance = CalculateFlatDistance(navigable.Value, target.StagingPoint);
            if (snappedDistance >= minimumRadius && snappedDistance <= outerRadius)
            {
                selectedPoint = navigable.Value;
                return true;
            }
        }

        return false;
    }

    private void SetFailure(string reason)
    {
        movementController.Stop(reason);
        TransitionTo(ForkedTowerStagingState.Failed, reason, error: reason);
    }

    private bool IsHigherPriorityActivityAvailable()
    {
        var target = scanner.Snapshot.EffectiveTarget;
        if (target.Kind == SelectedTargetKind.None
            || (target.Kind == SelectedTargetKind.CriticalEncounter
                && string.Equals(target.CriticalEncounter?.AutomationKind, "ForkedTower", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var targetPriority = target.Kind == SelectedTargetKind.Fate
            ? configuration.GetAutomationPriority(FarmActivityKind.Fates)
            : configuration.GetAutomationPriority(FarmActivityKind.CriticalEngagements);
        return targetPriority < configuration.GetAutomationPriority(FarmActivityKind.ForkedTower);
    }

    private void TransitionTo(ForkedTowerStagingState nextState, string reason, string? error = null, AutomationRunResult? result = null)
    {
        lock (gate)
        {
            state = nextState;
            lastTransition = reason;
            if (error != null)
            {
                lastError = error;
            }

            if (result.HasValue)
            {
                lastResult = result.Value;
            }
        }

        logger.Info($"[ForkedTower] op=staging-transition state={nextState} reason={reason}");
    }

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }
}
