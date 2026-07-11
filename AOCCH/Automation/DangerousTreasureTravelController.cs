using System;
using System.Numerics;

using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Movement;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public enum DangerousTreasureTravelState
{
    Idle,
    EquippingNinjaGearset,
    TravelingToHideThreshold,
    Dismounting,
    UsingHide,
    VerifyingHide,
    WalkingToCandidate,
    Arrived,
    CandidateSkipped,
    Stopped,
    Failed,
}

public enum DangerousTreasureTravelResult
{
    None,
    Arrived,
    CandidateSkipped,
    Stopped,
    Failed,
}

public sealed class DangerousTreasureTravelController : IDisposable
{
    private static readonly TimeSpan GearsetEquipTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GearsetRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GearsetPostElixirDelay = TimeSpan.FromSeconds(2);
    private const int MaximumGearsetEquipAttempts = 2;
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HideVerifyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(5);

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly MovementController movementController;
    private readonly GameActionController gameActionController;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private DangerousTreasureTravelState state = DangerousTreasureTravelState.Idle;
    private DangerousTreasureTravelResult lastResult;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private string activeCandidateLabel = string.Empty;
    private Vector3 finalDestination;
    private Vector3 hideThresholdPoint;
    private float arrivalTolerance;
    private DateTimeOffset stateEnteredAt = DateTimeOffset.MinValue;
    private bool hideThresholdTravelRequired;
    private bool ninjaGearsetEquippedByController;
    private bool gearsetAttemptInFlight;
    private int gearsetAttemptCount;
    private int activeGearsetNumber;
    private uint activeGearsetTargetClassJobId;
    private string activeGearsetName = string.Empty;
    private DateTimeOffset gearsetAttemptAvailableAt = DateTimeOffset.MinValue;

    public DangerousTreasureTravelController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        MovementController movementController,
        GameActionController gameActionController,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.movementController = movementController;
        this.gameActionController = gameActionController;
        this.configuration = configuration;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
    }

    public DangerousTreasureTravelState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public DangerousTreasureTravelResult LastResult
    {
        get
        {
            lock (gate)
            {
                return lastResult;
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

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = DangerousTreasureTravelState.Idle;
            lastResult = DangerousTreasureTravelResult.None;
            lastTransition = "Idle";
            lastError = string.Empty;
            activeCandidateLabel = string.Empty;
            finalDestination = Vector3.Zero;
            hideThresholdPoint = Vector3.Zero;
            arrivalTolerance = 0f;
            stateEnteredAt = DateTimeOffset.MinValue;
            hideThresholdTravelRequired = false;
            ninjaGearsetEquippedByController = false;
            gearsetAttemptInFlight = false;
            gearsetAttemptCount = 0;
            activeGearsetNumber = 0;
            activeGearsetTargetClassJobId = 0;
            activeGearsetName = string.Empty;
            gearsetAttemptAvailableAt = DateTimeOffset.MinValue;
        }

        logger.Info($"Dangerous treasure travel reset: {reason}");
    }

    public bool IsRunning
        => State is not DangerousTreasureTravelState.Idle
            and not DangerousTreasureTravelState.Arrived
            and not DangerousTreasureTravelState.CandidateSkipped
            and not DangerousTreasureTravelState.Stopped
            and not DangerousTreasureTravelState.Failed;

    public bool HasEquippedNinjaGearset
    {
        get
        {
            lock (gate)
            {
                return ninjaGearsetEquippedByController;
            }
        }
    }

    public bool Start(TreasureCofferCandidateData candidate, Vector3 destination, float finalArrivalTolerance)
    {
        if (IsRunning)
        {
            return true;
        }

        if (!configuration.UseNinjaForDangerousArea)
        {
            SkipCandidate($"Dangerous treasure candidate {candidate.Label} requires Ninja travel, but Use Ninja For Dangerous Area is disabled.");
            return false;
        }

        if (configuration.NinjaGearsetNumber <= 0)
        {
            SkipCandidate($"Dangerous treasure candidate {candidate.Label} requires a configured Ninja gearset number.");
            return false;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue)
        {
            SetFailure($"Dangerous treasure candidate {candidate.Label} could not start because the player position is unavailable.");
            return false;
        }

        var hideThresholdDistance = Math.Max(
            finalArrivalTolerance + 1f,
            candidate.HideThresholdDistance ?? configuration.HideThresholdDistance);
        var thresholdPoint = CalculateHideThresholdPoint(playerPosition.Value, destination, hideThresholdDistance);

        lock (gate)
        {
            activeCandidateLabel = candidate.Label;
            finalDestination = destination;
            hideThresholdPoint = thresholdPoint;
            arrivalTolerance = finalArrivalTolerance;
            hideThresholdTravelRequired = CalculateFlatDistance(playerPosition.Value, thresholdPoint) > arrivalTolerance;
            lastError = string.Empty;
            lastResult = DangerousTreasureTravelResult.None;
            gearsetAttemptInFlight = false;
            gearsetAttemptCount = 0;
            activeGearsetNumber = configuration.NinjaGearsetNumber;
            activeGearsetTargetClassJobId = 0;
            activeGearsetName = string.Empty;
            gearsetAttemptAvailableAt = DateTimeOffset.UtcNow + GearsetPostElixirDelay;
        }

        TransitionTo(DangerousTreasureTravelState.EquippingNinjaGearset, $"Equipping Ninja gearset for dangerous treasure candidate {candidate.Label}.");
        return true;
    }

    public void Stop(string reason)
    {
        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop(reason);
        }

        TransitionTo(DangerousTreasureTravelState.Stopped, reason, error: reason, result: DangerousTreasureTravelResult.Stopped);
    }

    public bool RestoreFateGearset(string reason)
    {
        if (!HasEquippedNinjaGearset)
        {
            return true;
        }

        if (configuration.FateGearsetNumber <= 0)
        {
            lock (gate)
            {
                ninjaGearsetEquippedByController = false;
            }

            logger.Info($"Leaving current gearset unchanged after {reason} because no FATE gearset number is configured.");
            return true;
        }

        var result = gameActionController.TryEquipGearsetReliably(
            configuration.FateGearsetNumber,
            reason,
            GearsetEquipTimeout,
            MaximumGearsetEquipAttempts,
            GearsetRetryDelay);
        if (!result.Success)
        {
            logger.Warning($"Failed to restore FATE gearset {configuration.FateGearsetNumber} after {reason}: {result.Error}");
            return false;
        }

        lock (gate)
        {
            ninjaGearsetEquippedByController = false;
        }

        logger.Info($"Restored FATE gearset {configuration.FateGearsetNumber} after {reason}. gearset={(result.Gearset?.Name ?? "unknown")} targetClassJob={(result.TargetClassJobId?.ToString() ?? "unknown")} currentClassJob={gameActionController.CurrentClassJobId}.");
        return true;
    }

    public void AcknowledgeTerminalState()
    {
        lock (gate)
        {
            if (state is DangerousTreasureTravelState.Arrived
                or DangerousTreasureTravelState.CandidateSkipped
                or DangerousTreasureTravelState.Stopped
                or DangerousTreasureTravelState.Failed)
            {
                state = DangerousTreasureTravelState.Idle;
            }
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        if (IsRunning)
        {
            Stop("Dangerous treasure travel disposal");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsRunning)
        {
            return;
        }

        switch (State)
        {
            case DangerousTreasureTravelState.EquippingNinjaGearset:
                TickGearsetEquip();
                break;
            case DangerousTreasureTravelState.TravelingToHideThreshold:
                TickTravelToHideThreshold();
                break;
            case DangerousTreasureTravelState.Dismounting:
                TickDismounting();
                break;
            case DangerousTreasureTravelState.UsingHide:
                TickUsingHide();
                break;
            case DangerousTreasureTravelState.VerifyingHide:
                TickVerifyingHide();
                break;
            case DangerousTreasureTravelState.WalkingToCandidate:
                TickWalkingToCandidate();
                break;
        }
    }

    private void TickGearsetEquip()
    {
        var now = DateTimeOffset.UtcNow;
        if (gameActionController.IsOnClassJob(GameActionController.NinjaClassJobId))
        {
            lock (gate)
            {
                ninjaGearsetEquippedByController = true;
                gearsetAttemptInFlight = false;
            }

            if (!gameActionController.CanUseHide())
            {
                SkipCandidate($"Ninja gearset {activeGearsetNumber} ({activeGearsetName}) was equipped for dangerous treasure candidate {activeCandidateLabel}, but Hide is still unavailable. currentClassJob={gameActionController.CurrentClassJobId} targetClassJob={activeGearsetTargetClassJobId}.");
                return;
            }

            if (!hideThresholdTravelRequired)
            {
                TransitionTo(DangerousTreasureTravelState.Dismounting, $"Confirmed Ninja gearset for dangerous treasure candidate {activeCandidateLabel}; already inside the hide threshold and preparing to Hide.");
                return;
            }

            TransitionTo(DangerousTreasureTravelState.TravelingToHideThreshold, $"Confirmed Ninja gearset for dangerous treasure candidate {activeCandidateLabel}; continuing to the hide threshold.");
            if (!movementController.StartDirectMove($"Dangerous treasure threshold for {activeCandidateLabel}", hideThresholdPoint, arrivalTolerance, shouldMountBeforeStep: true))
            {
                SkipCandidate(movementController.LastError.Length == 0
                    ? $"Failed to start mounted travel to the hide threshold for dangerous treasure candidate {activeCandidateLabel}."
                    : movementController.LastError);
            }
            return;
        }

        if (gearsetAttemptInFlight)
        {
            if (now - stateEnteredAt >= GearsetEquipTimeout)
            {
                gearsetAttemptInFlight = false;
                if (gearsetAttemptCount >= MaximumGearsetEquipAttempts)
                {
                    SkipCandidate($"Timed out waiting for Ninja gearset {activeGearsetNumber} ({activeGearsetName}) equip confirmation for dangerous treasure candidate {activeCandidateLabel}. currentClassJob={gameActionController.CurrentClassJobId} targetClassJob={activeGearsetTargetClassJobId}.");
                    return;
                }

                gearsetAttemptAvailableAt = now + GearsetRetryDelay;
                logger.Warning($"Ninja gearset equip attempt {gearsetAttemptCount}/{MaximumGearsetEquipAttempts} timed out for dangerous treasure candidate {activeCandidateLabel}; retrying in {GearsetRetryDelay.TotalSeconds:0.0}s.");
                stateEnteredAt = now;
            }

            logger.DebugThrottled(
                "dangerous-treasure-travel-gearset",
                WaitLogInterval,
                $"Dangerous treasure travel is waiting for Ninja gearset confirmation on {activeCandidateLabel}. requestedGearset={activeGearsetNumber} gearsetName={activeGearsetName} currentClassJob={gameActionController.CurrentClassJobId} targetClassJob={activeGearsetTargetClassJobId} canUseHide={gameActionController.CanUseHide()}.");
            return;
        }

        if (gearsetAttemptCount == 0 && now < gearsetAttemptAvailableAt)
        {
            logger.DebugThrottled(
                "dangerous-treasure-travel-gearset-delay",
                TimeSpan.FromMilliseconds(250),
                $"Dangerous treasure travel is waiting {Math.Max(0, (gearsetAttemptAvailableAt - now).TotalSeconds):0.0}s before the first Ninja gearset attempt on {activeCandidateLabel} to respect the post-elixir lock window.");
            return;
        }

        if (now - stateEnteredAt >= GearsetEquipTimeout)
        {
            SkipCandidate($"Timed out waiting for a changeable state before equipping Ninja gearset {activeGearsetNumber} for dangerous treasure candidate {activeCandidateLabel}. {gameActionController.GetChangeableStateSummary()}");
            return;
        }

        if (!gameActionController.IsPlayerInChangeableState())
        {
            logger.DebugThrottled(
                "dangerous-treasure-travel-gearset-ready",
                WaitLogInterval,
                $"Dangerous treasure travel is waiting for a changeable state before equipping Ninja gearset {activeGearsetNumber} on {activeCandidateLabel}. {gameActionController.GetChangeableStateSummary()}");
            return;
        }

        if (now < gearsetAttemptAvailableAt)
        {
            return;
        }

        gearsetAttemptCount++;
        var result = gameActionController.TryEquipGearset(activeGearsetNumber, $"dangerous treasure travel for {activeCandidateLabel}");
        if (!result.Success)
        {
            if (gearsetAttemptCount >= MaximumGearsetEquipAttempts)
            {
                SkipCandidate($"Failed to equip Ninja gearset {activeGearsetNumber} for dangerous treasure candidate {activeCandidateLabel}: {result.Error}");
                return;
            }

            gearsetAttemptAvailableAt = now + GearsetRetryDelay;
            logger.Warning($"Ninja gearset equip attempt {gearsetAttemptCount}/{MaximumGearsetEquipAttempts} failed for dangerous treasure candidate {activeCandidateLabel}; retrying in {GearsetRetryDelay.TotalSeconds:0.0}s. {result.Error}");
            stateEnteredAt = now;
            return;
        }

        activeGearsetName = result.Gearset?.Name ?? $"Gearset {activeGearsetNumber}";
        activeGearsetTargetClassJobId = result.TargetClassJobId ?? GameActionController.NinjaClassJobId;

        if (gameActionController.IsOnClassJob(activeGearsetTargetClassJobId))
        {
            logger.Info($"Ninja gearset {activeGearsetNumber} ({activeGearsetName}) was already active for dangerous treasure candidate {activeCandidateLabel}. targetClassJob={activeGearsetTargetClassJobId}.");
            return;
        }

        gearsetAttemptInFlight = true;
        stateEnteredAt = now;

        logger.DebugThrottled(
            "dangerous-treasure-travel-gearset",
            WaitLogInterval,
            $"Dangerous treasure travel sent Ninja gearset equip attempt {gearsetAttemptCount}/{MaximumGearsetEquipAttempts} on {activeCandidateLabel}. requestedGearset={activeGearsetNumber} gearsetName={activeGearsetName} currentClassJob={gameActionController.CurrentClassJobId} targetClassJob={activeGearsetTargetClassJobId}.");
    }

    private void TickTravelToHideThreshold()
    {
        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached dangerous treasure hide threshold.");
                logger.ResetThrottle("dangerous-treasure-travel");
                TransitionTo(DangerousTreasureTravelState.Dismounting, $"Reached the hide threshold for dangerous treasure candidate {activeCandidateLabel}.");
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                SkipCandidate(movementController.LastError.Length == 0
                    ? $"Failed to reach the hide threshold for dangerous treasure candidate {activeCandidateLabel}."
                    : movementController.LastError);
                return;
        }

        logger.DebugThrottled(
            "dangerous-treasure-travel",
            WaitLogInterval,
            $"Dangerous treasure travel is moving to the hide threshold for {activeCandidateLabel}. MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
    }

    private void TickDismounting()
    {
        if (condition[ConditionFlag.InCombat])
        {
            SkipCandidate($"Dangerous treasure candidate {activeCandidateLabel} entered combat inside the hide threshold.");
            return;
        }

        if (!condition[ConditionFlag.Mounted])
        {
            logger.ResetThrottle("dangerous-treasure-travel");
            TransitionTo(DangerousTreasureTravelState.UsingHide, $"Dismounted inside the hide threshold for dangerous treasure candidate {activeCandidateLabel}; using Hide.");
            return;
        }

        if (DateTimeOffset.UtcNow - stateEnteredAt >= DismountTimeout)
        {
            SkipCandidate($"Timed out dismounting inside the hide threshold for dangerous treasure candidate {activeCandidateLabel}.");
            return;
        }

        if (!gameActionController.TryExecuteGeneralAction(GameActionController.DismountActionId, $"dangerous treasure travel for {activeCandidateLabel}"))
        {
            logger.DebugThrottled(
                "dangerous-treasure-travel",
                WaitLogInterval,
                $"Dangerous treasure travel is waiting to dismount inside the hide threshold for {activeCandidateLabel}.");
            return;
        }

        logger.DebugThrottled(
            "dangerous-treasure-travel",
            WaitLogInterval,
            $"Dangerous treasure travel sent a dismount request inside the hide threshold for {activeCandidateLabel}.");
    }

    private void TickUsingHide()
    {
        if (condition[ConditionFlag.InCombat])
        {
            SkipCandidate($"Dangerous treasure candidate {activeCandidateLabel} entered combat before Hide could be applied.");
            return;
        }

        if (!gameActionController.TryExecuteAction(GameActionController.HideActionId, $"dangerous treasure travel for {activeCandidateLabel}"))
        {
            SkipCandidate($"Failed to use Hide for dangerous treasure candidate {activeCandidateLabel}.");
            return;
        }

        TransitionTo(DangerousTreasureTravelState.VerifyingHide, $"Used Hide for dangerous treasure candidate {activeCandidateLabel}; verifying stealth.");
    }

    private void TickVerifyingHide()
    {
        if (gameActionController.IsStealthed)
        {
            if (!movementController.StartDirectMove($"Hidden final approach for {activeCandidateLabel}", finalDestination, arrivalTolerance, shouldMountBeforeStep: false))
            {
                SkipCandidate(movementController.LastError.Length == 0
                    ? $"Failed to start the hidden final approach for dangerous treasure candidate {activeCandidateLabel}."
                    : movementController.LastError);
                return;
            }

            logger.ResetThrottle("dangerous-treasure-travel");
            TransitionTo(DangerousTreasureTravelState.WalkingToCandidate, $"Hide verified for dangerous treasure candidate {activeCandidateLabel}; starting the final on-foot approach.");
            return;
        }

        if (DateTimeOffset.UtcNow - stateEnteredAt >= HideVerifyTimeout)
        {
            SkipCandidate($"Hide did not apply in time for dangerous treasure candidate {activeCandidateLabel}.");
            return;
        }

        logger.DebugThrottled(
            "dangerous-treasure-travel",
            WaitLogInterval,
            $"Dangerous treasure travel is waiting for Hide to apply on {activeCandidateLabel}.");
    }

    private void TickWalkingToCandidate()
    {
        if (condition[ConditionFlag.InCombat] || !gameActionController.IsStealthed)
        {
            SkipCandidate($"Dangerous treasure candidate {activeCandidateLabel} lost Hide or entered combat during the final approach.");
            return;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached dangerous treasure candidate after Hide approach.");
                logger.ResetThrottle("dangerous-treasure-travel");
                TransitionTo(DangerousTreasureTravelState.Arrived, $"Reached dangerous treasure candidate {activeCandidateLabel} after Ninja/Hide travel.", result: DangerousTreasureTravelResult.Arrived);
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                SkipCandidate(movementController.LastError.Length == 0
                    ? $"Failed the hidden final approach for dangerous treasure candidate {activeCandidateLabel}."
                    : movementController.LastError);
                return;
        }

        logger.DebugThrottled(
            "dangerous-treasure-travel",
            WaitLogInterval,
            $"Dangerous treasure travel is on the hidden final approach for {activeCandidateLabel}. MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()} stealthed={gameActionController.IsStealthed}.");
    }

    private void SkipCandidate(string reason)
    {
        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop(reason);
        }

        logger.ResetThrottle("dangerous-treasure-travel");
        TransitionTo(DangerousTreasureTravelState.CandidateSkipped, reason, error: reason, result: DangerousTreasureTravelResult.CandidateSkipped);
    }

    private void SetFailure(string reason)
    {
        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop(reason);
        }

        logger.ResetThrottle("dangerous-treasure-travel");
        TransitionTo(DangerousTreasureTravelState.Failed, reason, error: reason, result: DangerousTreasureTravelResult.Failed);
    }

    private void TransitionTo(DangerousTreasureTravelState nextState, string reason, string? error = null, DangerousTreasureTravelResult? result = null)
    {
        lock (gate)
        {
            state = nextState;
            lastTransition = reason;
            stateEnteredAt = DateTimeOffset.UtcNow;
            if (error != null)
            {
                lastError = error;
            }
            else if (nextState is not DangerousTreasureTravelState.CandidateSkipped and not DangerousTreasureTravelState.Stopped and not DangerousTreasureTravelState.Failed)
            {
                lastError = string.Empty;
            }

            if (result.HasValue)
            {
                lastResult = result.Value;
            }
        }

        logger.Info($"Dangerous treasure travel state -> {nextState}: {reason}");
    }

    private static Vector3 CalculateHideThresholdPoint(Vector3 playerPosition, Vector3 destination, float hideThresholdDistance)
    {
        var delta = new Vector2(playerPosition.X - destination.X, playerPosition.Z - destination.Z);
        var distance = delta.Length();
        if (distance <= hideThresholdDistance || distance <= float.Epsilon)
        {
            return playerPosition;
        }

        var direction = Vector2.Normalize(delta);
        return new Vector3(
            destination.X + (direction.X * hideThresholdDistance),
            destination.Y,
            destination.Z + (direction.Y * hideThresholdDistance));
    }

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }
}
