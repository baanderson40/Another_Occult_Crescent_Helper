using System;
using System.Numerics;
using System.Threading;

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
    WaitingForHideReady,
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

public enum DangerousTreasureWalkingPhase
{
    None,
    ClearingPreviousThreshold,
    FinalApproach,
}

public sealed class DangerousTreasureTravelController : IDisposable
{
    private static int nextRunSequence;
    private static readonly TimeSpan GearsetEquipTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GearsetRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GearsetPostElixirDelay = TimeSpan.FromSeconds(2);
    private const int MaximumGearsetEquipAttempts = 2;
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HideReadyTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan HideVerifyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HideStateSettleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(5);
    private const float ThresholdArrivalTolerance = 3.5f;
    private const float PreviousThresholdExtraDistance = 6f;

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
    private string currentRunId = string.Empty;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private string activeCandidateLabel = string.Empty;
    private string previousCandidateLabel = string.Empty;
    private Vector3 finalDestination;
    private float arrivalTolerance;
    private DateTimeOffset stateEnteredAt = DateTimeOffset.MinValue;
    private bool ninjaGearsetEquippedByController;
    private bool gearsetAttemptInFlight;
    private int gearsetAttemptCount;
    private int activeGearsetNumber;
    private uint activeGearsetTargetClassJobId;
    private string activeGearsetName = string.Empty;
    private DateTimeOffset gearsetAttemptAvailableAt = DateTimeOffset.MinValue;
    private bool hideRetryUsed;
    private DateTimeOffset lastHideActivatedAt = DateTimeOffset.MinValue;
    private TreasureCofferCandidateData? previousCandidate;
    private TreasureCofferCandidateData? currentCandidate;
    private DangerousTreasureWalkingPhase pendingHiddenMovePhase;
    private DangerousTreasureWalkingPhase activeWalkingPhase;
    private Vector3 pendingHiddenMoveDestination;
    private float pendingHiddenMoveArrivalTolerance;
    private bool restorePending;
    private bool restoreAttemptInFlight;
    private int restoreAttemptCount;
    private int restoreTargetGearsetNumber;
    private uint restoreTargetClassJobId;
    private string restoreGearsetName = string.Empty;
    private string lastRestoreReason = string.Empty;
    private string lastRestoreError = string.Empty;
    private DateTimeOffset restoreRequestedAt = DateTimeOffset.MinValue;
    private DateTimeOffset restoreAttemptAvailableAt = DateTimeOffset.MinValue;
    private DateTimeOffset restoreAttemptStartedAt = DateTimeOffset.MinValue;

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

    public string ActiveCandidateLabel
    {
        get
        {
            lock (gate)
            {
                return activeCandidateLabel;
            }
        }
    }

    public string PreviousCandidateLabel
    {
        get
        {
            lock (gate)
            {
                return previousCandidateLabel;
            }
        }
    }

    public DangerousTreasureWalkingPhase ActiveWalkingPhase
    {
        get
        {
            lock (gate)
            {
                return activeWalkingPhase;
            }
        }
    }

    public DangerousTreasureWalkingPhase PendingHiddenMovePhase
    {
        get
        {
            lock (gate)
            {
                return pendingHiddenMovePhase;
            }
        }
    }

    public Vector3 PendingHiddenMoveDestination
    {
        get
        {
            lock (gate)
            {
                return pendingHiddenMoveDestination;
            }
        }
    }

    public float PendingHiddenMoveArrivalTolerance
    {
        get
        {
            lock (gate)
            {
                return pendingHiddenMoveArrivalTolerance;
            }
        }
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = DangerousTreasureTravelState.Idle;
            lastResult = DangerousTreasureTravelResult.None;
            currentRunId = string.Empty;
            lastTransition = "Idle";
            lastError = string.Empty;
            activeCandidateLabel = string.Empty;
            previousCandidateLabel = string.Empty;
            finalDestination = Vector3.Zero;
            arrivalTolerance = 0f;
            stateEnteredAt = DateTimeOffset.MinValue;
            ninjaGearsetEquippedByController = false;
            gearsetAttemptInFlight = false;
            gearsetAttemptCount = 0;
            activeGearsetNumber = 0;
            activeGearsetTargetClassJobId = 0;
            activeGearsetName = string.Empty;
            gearsetAttemptAvailableAt = DateTimeOffset.MinValue;
            hideRetryUsed = false;
            lastHideActivatedAt = DateTimeOffset.MinValue;
            previousCandidate = null;
            currentCandidate = null;
            pendingHiddenMovePhase = DangerousTreasureWalkingPhase.None;
            activeWalkingPhase = DangerousTreasureWalkingPhase.None;
            pendingHiddenMoveDestination = Vector3.Zero;
            pendingHiddenMoveArrivalTolerance = 0f;
            restorePending = false;
            restoreAttemptInFlight = false;
            restoreAttemptCount = 0;
            restoreTargetGearsetNumber = 0;
            restoreTargetClassJobId = 0;
            restoreGearsetName = string.Empty;
            lastRestoreReason = string.Empty;
            lastRestoreError = string.Empty;
            restoreRequestedAt = DateTimeOffset.MinValue;
            restoreAttemptAvailableAt = DateTimeOffset.MinValue;
            restoreAttemptStartedAt = DateTimeOffset.MinValue;
        }

        logger.Info($"[DangerousTravel] op=reset reason={reason}");
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

    public bool IsFateGearsetRestorePending
    {
        get
        {
            lock (gate)
            {
                return restorePending;
            }
        }
    }

    public bool IsFateGearsetRestoreInProgress
    {
        get
        {
            lock (gate)
            {
                return restoreAttemptInFlight;
            }
        }
    }

    public int FateGearsetRestoreAttemptCount
    {
        get
        {
            lock (gate)
            {
                return restoreAttemptCount;
            }
        }
    }

    public int PendingFateGearsetNumber
    {
        get
        {
            lock (gate)
            {
                return restoreTargetGearsetNumber;
            }
        }
    }

    public uint PendingFateGearsetTargetClassJobId
    {
        get
        {
            lock (gate)
            {
                return restoreTargetClassJobId;
            }
        }
    }

    public string PendingFateGearsetName
    {
        get
        {
            lock (gate)
            {
                return restoreGearsetName;
            }
        }
    }

    public string LastFateGearsetRestoreReason
    {
        get
        {
            lock (gate)
            {
                return lastRestoreReason;
            }
        }
    }

    public string LastFateGearsetRestoreError
    {
        get
        {
            lock (gate)
            {
                return lastRestoreError;
            }
        }
    }

    public DateTimeOffset FateGearsetRestoreRequestedAt
    {
        get
        {
            lock (gate)
            {
                return restoreRequestedAt;
            }
        }
    }

    public DateTimeOffset FateGearsetRestoreAttemptAvailableAt
    {
        get
        {
            lock (gate)
            {
                return restoreAttemptAvailableAt;
            }
        }
    }

    public bool Start(TreasureCofferCandidateData? previousCandidate, TreasureCofferCandidateData candidate, Vector3 destination, float finalArrivalTolerance)
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

        lock (gate)
        {
            currentRunId = $"DangerousTravel#{Interlocked.Increment(ref nextRunSequence)}";
            activeCandidateLabel = candidate.Label;
            previousCandidateLabel = previousCandidate?.Label ?? string.Empty;
            finalDestination = destination;
            arrivalTolerance = finalArrivalTolerance;
            lastError = string.Empty;
            lastResult = DangerousTreasureTravelResult.None;
            gearsetAttemptInFlight = false;
            gearsetAttemptCount = 0;
            activeGearsetNumber = configuration.NinjaGearsetNumber;
            activeGearsetTargetClassJobId = 0;
            activeGearsetName = string.Empty;
            gearsetAttemptAvailableAt = DateTimeOffset.UtcNow + GearsetPostElixirDelay;
            hideRetryUsed = false;
            lastHideActivatedAt = DateTimeOffset.MinValue;
            this.previousCandidate = previousCandidate;
            currentCandidate = candidate;
            pendingHiddenMovePhase = DangerousTreasureWalkingPhase.None;
            activeWalkingPhase = DangerousTreasureWalkingPhase.None;
            pendingHiddenMoveDestination = Vector3.Zero;
            pendingHiddenMoveArrivalTolerance = 0f;
        }

        logger.Info($"{BuildLogTag()} op=start candidate={candidate.Label} previousCandidate={(previousCandidate?.Label ?? "none")} arrivalTolerance={finalArrivalTolerance:0.0}");
        movementController.SetLogOwner(currentRunId);
        TransitionTo(DangerousTreasureTravelState.EquippingNinjaGearset, $"Equipping Ninja gearset for dangerous candidate {candidate.Label}.");
        return true;
    }

    public void Stop(string reason)
    {
        var candidateLabel = activeCandidateLabel;
        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop(reason);
        }

        TransitionTo(DangerousTreasureTravelState.Stopped, reason, error: reason, result: DangerousTreasureTravelResult.Stopped);
        logger.Info($"{BuildLogTag()} op=stop state={State} candidate={candidateLabel} reason={reason}");
    }

    public bool RestoreFateGearset(string reason)
    {
        if (!HasEquippedNinjaGearset)
        {
            if (IsFateGearsetRestorePending)
            {
                ClearPendingFateGearsetRestore();
            }

            return true;
        }

        if (configuration.FateGearsetNumber <= 0)
        {
            lock (gate)
            {
                ninjaGearsetEquippedByController = false;
                restorePending = false;
                restoreAttemptInFlight = false;
                restoreAttemptCount = 0;
                restoreTargetGearsetNumber = 0;
                restoreTargetClassJobId = 0;
                restoreGearsetName = string.Empty;
                lastRestoreError = string.Empty;
                restoreRequestedAt = DateTimeOffset.MinValue;
                restoreAttemptAvailableAt = DateTimeOffset.MinValue;
                restoreAttemptStartedAt = DateTimeOffset.MinValue;
            }

            logger.Info($"{BuildLogTag()} op=restore-gearset-skip reason=\"{reason}\" fateGearsetConfigured=false");
            return true;
        }

        lock (gate)
        {
            lastRestoreReason = reason;
            restorePending = true;
            restoreTargetGearsetNumber = configuration.FateGearsetNumber;
            restoreRequestedAt = restoreRequestedAt == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : restoreRequestedAt;
            if (restoreAttemptAvailableAt == DateTimeOffset.MinValue)
            {
                restoreAttemptAvailableAt = DateTimeOffset.UtcNow;
            }
        }

        logger.Info($"{BuildLogTag()} op=restore-gearset-requested reason=\"{reason}\" gearset={configuration.FateGearsetNumber} currentClassJob={gameActionController.CurrentClassJobId}");
        return true;
    }

    public void TryProcessPendingFateGearsetRestore(string context)
    {
        if (IsRunning || !IsFateGearsetRestorePending)
        {
            return;
        }

        if (!HasEquippedNinjaGearset)
        {
            ClearPendingFateGearsetRestore();
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (restoreAttemptInFlight)
        {
            if (restoreTargetClassJobId != 0 && gameActionController.IsOnClassJob(restoreTargetClassJobId))
            {
                var completedReason = lastRestoreReason;
                var completedGearsetNumber = restoreTargetGearsetNumber;
                var completedGearsetName = restoreGearsetName.Length == 0 ? $"Gearset {restoreTargetGearsetNumber}" : restoreGearsetName;
                var completedTargetClassJobId = restoreTargetClassJobId;
                ClearPendingFateGearsetRestore(clearNinjaOwnership: true);
                logger.ResetThrottle("dangerous-treasure-restore-confirmation");
                logger.Info($"{BuildLogTag()} op=restore-gearset-success context=\"{context}\" reason=\"{completedReason}\" gearset={completedGearsetNumber} gearsetName=\"{completedGearsetName}\" targetClassJob={completedTargetClassJobId} currentClassJob={gameActionController.CurrentClassJobId}");
                return;
            }

            if (now - restoreAttemptStartedAt >= GearsetEquipTimeout)
            {
                var timeoutError = $"FATE gearset restore attempt {restoreAttemptCount} did not activate ClassJob {restoreTargetClassJobId} within {GearsetEquipTimeout.TotalSeconds:0.0}s. currentClassJob={gameActionController.CurrentClassJobId}.";
                SchedulePendingFateGearsetRestoreRetry(timeoutError, now, context, "timeout");
                return;
            }

            logger.DebugThrottled(
                "dangerous-treasure-restore-confirmation",
                WaitLogInterval,
                $"Pending FATE gearset restore is waiting for class/job confirmation. context={context} gearset={restoreTargetGearsetNumber} gearsetName={restoreGearsetName} currentClassJob={gameActionController.CurrentClassJobId} targetClassJob={restoreTargetClassJobId}.");
            return;
        }

        if (now < restoreAttemptAvailableAt)
        {
            logger.DebugThrottled(
                "dangerous-treasure-restore-delay",
                TimeSpan.FromMilliseconds(250),
                $"Pending FATE gearset restore is waiting {(restoreAttemptAvailableAt - now).TotalSeconds:0.0}s before the next throttled retry. context={context} gearset={restoreTargetGearsetNumber} currentClassJob={gameActionController.CurrentClassJobId}.");
            return;
        }

        if (!gameActionController.IsPlayerInChangeableState())
        {
            logger.DebugThrottled(
                "dangerous-treasure-restore-ready",
                WaitLogInterval,
                $"Pending FATE gearset restore is waiting for a changeable state. context={context} gearset={restoreTargetGearsetNumber} {gameActionController.GetChangeableStateSummary()}");
            return;
        }

        restoreAttemptCount++;
        var result = gameActionController.TryEquipGearset(restoreTargetGearsetNumber, $"FATE gearset restore ({context})");
        if (!result.Success)
        {
            SchedulePendingFateGearsetRestoreRetry(result.Error, now, context, "equip-failed");
            return;
        }

        restoreGearsetName = result.Gearset?.Name ?? $"Gearset {restoreTargetGearsetNumber}";
        restoreTargetClassJobId = result.TargetClassJobId ?? 0;

        if (restoreTargetClassJobId != 0 && gameActionController.IsOnClassJob(restoreTargetClassJobId))
        {
            var completedReason = lastRestoreReason;
            var completedGearsetNumber = restoreTargetGearsetNumber;
            var completedGearsetName = restoreGearsetName;
            var completedTargetClassJobId = restoreTargetClassJobId;
            ClearPendingFateGearsetRestore(clearNinjaOwnership: true);
            logger.Info($"{BuildLogTag()} op=restore-gearset-success context=\"{context}\" reason=\"{completedReason}\" gearset={completedGearsetNumber} gearsetName=\"{completedGearsetName}\" targetClassJob={completedTargetClassJobId} currentClassJob={gameActionController.CurrentClassJobId}");
            return;
        }

        restoreAttemptInFlight = true;
        restoreAttemptStartedAt = now;
        lastRestoreError = string.Empty;

        logger.Info($"{BuildLogTag()} op=restore-gearset-attempt context=\"{context}\" attempt={restoreAttemptCount} gearset={restoreTargetGearsetNumber} gearsetName=\"{restoreGearsetName}\" targetClassJob={restoreTargetClassJobId} currentClassJob={gameActionController.CurrentClassJobId}");
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
            case DangerousTreasureTravelState.WaitingForHideReady:
                TickWaitingForHideReady();
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
                activeGearsetTargetClassJobId = GameActionController.NinjaClassJobId;
                activeGearsetName = activeGearsetName.Length == 0 ? $"Gearset {activeGearsetNumber}" : activeGearsetName;
            }

            ContinueDangerousApproach($"Confirmed Ninja gearset for dangerous candidate {activeCandidateLabel}.");
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
                logger.Warning($"{BuildLogTag()} op=gearset-timeout candidate={activeCandidateLabel} attempt={gearsetAttemptCount}/{MaximumGearsetEquipAttempts} retryDelay={GearsetRetryDelay.TotalSeconds:0.0}s");
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

        if (condition[ConditionFlag.Mounted])
        {
            if (now - stateEnteredAt >= DismountTimeout)
            {
                SkipCandidate($"Timed out dismounting before equipping Ninja gearset {activeGearsetNumber} for dangerous treasure candidate {activeCandidateLabel}. {gameActionController.GetChangeableStateSummary()}");
                return;
            }

            if (!gameActionController.TryExecuteGeneralAction(GameActionController.DismountActionId, $"dangerous treasure gearset prep for {activeCandidateLabel}"))
            {
                logger.DebugThrottled(
                    "dangerous-treasure-travel-gearset-ready",
                    WaitLogInterval,
                    $"Dangerous treasure travel is waiting to dismount before equipping Ninja gearset {activeGearsetNumber} on {activeCandidateLabel}. {gameActionController.GetChangeableStateSummary()}");
                return;
            }

            logger.DebugThrottled(
                "dangerous-treasure-travel-gearset-ready",
                WaitLogInterval,
                $"Dangerous treasure travel sent a dismount request before equipping Ninja gearset {activeGearsetNumber} on {activeCandidateLabel}.");
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
            logger.Warning($"{BuildLogTag()} op=gearset-failed candidate={activeCandidateLabel} attempt={gearsetAttemptCount}/{MaximumGearsetEquipAttempts} retryDelay={GearsetRetryDelay.TotalSeconds:0.0}s error={result.Error}");
            stateEnteredAt = now;
            return;
        }

        activeGearsetName = result.Gearset?.Name ?? $"Gearset {activeGearsetNumber}";
        activeGearsetTargetClassJobId = result.TargetClassJobId ?? GameActionController.NinjaClassJobId;

        if (gameActionController.IsOnClassJob(activeGearsetTargetClassJobId))
        {
            logger.Info($"{BuildLogTag()} op=gearset-skip candidate={activeCandidateLabel} gearset={activeGearsetNumber} gearsetName=\"{activeGearsetName}\" targetClassJob={activeGearsetTargetClassJobId} reason=already-active");
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
                ContinueDangerousApproach($"Reached current dangerous threshold for {activeCandidateLabel}.");
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
            ContinueDangerousApproach($"Combat started before Hide for dangerous candidate {activeCandidateLabel}.");
            return;
        }

        if (!condition[ConditionFlag.Mounted])
        {
            logger.ResetThrottle("dangerous-treasure-travel");
            TransitionTo(DangerousTreasureTravelState.WaitingForHideReady, $"Dismounted at dangerous threshold for {activeCandidateLabel}; waiting for Hide.");
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

    private void TickWaitingForHideReady()
    {
        if (condition[ConditionFlag.InCombat])
        {
            ContinueDangerousApproach($"Combat started while waiting for Hide on dangerous candidate {activeCandidateLabel}.");
            return;
        }

        if (gameActionController.IsStealthed)
        {
            TransitionTo(DangerousTreasureTravelState.VerifyingHide, $"Hide already active for dangerous candidate {activeCandidateLabel}; verifying stealth before continuing.");
            return;
        }

        if (condition[ConditionFlag.Mounted])
        {
            TransitionTo(DangerousTreasureTravelState.Dismounting, $"Dangerous treasure candidate {activeCandidateLabel} became mounted again while waiting for Hide; dismounting before retry.");
            return;
        }

        if (DateTimeOffset.UtcNow - stateEnteredAt < HideStateSettleDelay)
        {
            logger.DebugThrottled(
                "dangerous-treasure-travel-hide-ready",
                TimeSpan.FromMilliseconds(250),
                $"Dangerous treasure travel is settling after dismount/job change before checking Hide readiness on {activeCandidateLabel}. elapsed={(DateTimeOffset.UtcNow - stateEnteredAt).TotalSeconds:0.00}s required={HideStateSettleDelay.TotalSeconds:0.00}s.");
            return;
        }

        if (gameActionController.CanUseHide())
        {
            logger.ResetThrottle("dangerous-treasure-travel-hide-ready");
            TransitionTo(DangerousTreasureTravelState.UsingHide, $"Hide ready for dangerous candidate {activeCandidateLabel}; attempting stealth.");
            return;
        }

        if (DateTimeOffset.UtcNow - stateEnteredAt >= HideReadyTimeout)
        {
            SkipCandidate($"Hide did not become ready for dangerous treasure candidate {activeCandidateLabel} within {HideReadyTimeout.TotalSeconds:0.0}s.");
            return;
        }

        logger.DebugThrottled(
            "dangerous-treasure-travel-hide-ready",
            WaitLogInterval,
            $"Dangerous treasure travel is waiting for Hide to become ready on {activeCandidateLabel}. currentClassJob={gameActionController.CurrentClassJobId} canUseHide={gameActionController.CanUseHide()} stealthed={gameActionController.IsStealthed} mounted={condition[ConditionFlag.Mounted]} retryUsed={hideRetryUsed}.");
    }

    private void TickUsingHide()
    {
        if (condition[ConditionFlag.InCombat])
        {
            ContinueDangerousApproach($"Combat started before Hide application on dangerous candidate {activeCandidateLabel}.");
            return;
        }

        if (!gameActionController.TryExecuteAction(GameActionController.HideActionId, $"dangerous treasure travel for {activeCandidateLabel}"))
        {
            if (!hideRetryUsed)
            {
                lock (gate)
                {
                    hideRetryUsed = true;
                }

                TransitionTo(DangerousTreasureTravelState.WaitingForHideReady, $"Hide action was unavailable for dangerous treasure candidate {activeCandidateLabel}; waiting for one final ready-state retry.");
                return;
            }

            SkipCandidate($"Failed to use Hide for dangerous treasure candidate {activeCandidateLabel} after two ready-state attempts.");
            return;
        }

        lock (gate)
        {
            lastHideActivatedAt = DateTimeOffset.UtcNow;
        }

        TransitionTo(DangerousTreasureTravelState.VerifyingHide, $"Used Hide for dangerous treasure candidate {activeCandidateLabel}; verifying stealth.");
    }

    private void TickVerifyingHide()
    {
        if (gameActionController.IsStealthed)
        {
            StartWalkingPhase(
                pendingHiddenMovePhase,
                pendingHiddenMoveDestination,
                pendingHiddenMoveArrivalTolerance,
                allowMount: false,
                $"Hidden movement for {activeCandidateLabel}",
                $"Hide verified for dangerous treasure candidate {activeCandidateLabel}; continuing the hidden approach.");
            return;
        }

        if (condition[ConditionFlag.InCombat])
        {
            ContinueDangerousApproach($"Combat started during Hide verification for dangerous candidate {activeCandidateLabel}.");
            return;
        }

        if (DateTimeOffset.UtcNow - stateEnteredAt >= HideVerifyTimeout)
        {
            if (!hideRetryUsed)
            {
                lock (gate)
                {
                    hideRetryUsed = true;
                }

                TransitionTo(DangerousTreasureTravelState.WaitingForHideReady, $"Hide did not apply in time for dangerous treasure candidate {activeCandidateLabel}; waiting for one final ready-state retry.");
                return;
            }

            SkipCandidate($"Hide failed after two ready-state attempts for dangerous treasure candidate {activeCandidateLabel}.");
            return;
        }

        logger.DebugThrottled(
            "dangerous-treasure-travel",
            WaitLogInterval,
            $"Dangerous treasure travel is waiting for Hide to apply on {activeCandidateLabel}. stealthed={gameActionController.IsStealthed} canUseHide={gameActionController.CanUseHide()} lastHideActivatedAt={(lastHideActivatedAt == DateTimeOffset.MinValue ? "none" : lastHideActivatedAt.ToString("O"))}.");
    }

    private void TickWalkingToCandidate()
    {
        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached dangerous treasure travel waypoint.");
                logger.ResetThrottle("dangerous-treasure-travel");
                if (activeWalkingPhase == DangerousTreasureWalkingPhase.ClearingPreviousThreshold)
                {
                    activeWalkingPhase = DangerousTreasureWalkingPhase.None;
                    ContinueDangerousApproach($"Cleared previous dangerous threshold while routing to {activeCandidateLabel}.");
                    return;
                }

                activeWalkingPhase = DangerousTreasureWalkingPhase.None;
                TransitionTo(DangerousTreasureTravelState.Arrived, $"Reached dangerous treasure candidate {activeCandidateLabel} after Ninja travel.", result: DangerousTreasureTravelResult.Arrived);
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                SkipCandidate(movementController.LastError.Length == 0
                    ? $"Failed movement for dangerous treasure candidate {activeCandidateLabel}."
                    : movementController.LastError);
                return;
        }

        logger.DebugThrottled(
            "dangerous-treasure-travel",
            WaitLogInterval,
            $"Dangerous treasure travel is moving for {activeCandidateLabel}. phase={activeWalkingPhase} MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()} stealthed={gameActionController.IsStealthed} inCombat={condition[ConditionFlag.InCombat]}.");
    }

    private void SkipCandidate(string reason)
    {
        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop(reason);
        }

        logger.ResetThrottle("dangerous-treasure-travel");
        TransitionTo(DangerousTreasureTravelState.CandidateSkipped, reason, error: reason, result: DangerousTreasureTravelResult.CandidateSkipped);
        logger.Warning($"{BuildLogTag()} op=skip state={DangerousTreasureTravelState.CandidateSkipped} candidate={activeCandidateLabel} reason={reason}");
    }

    private void SchedulePendingFateGearsetRestoreRetry(string error, DateTimeOffset now, string context, string outcome)
    {
        lock (gate)
        {
            restoreAttemptInFlight = false;
            restoreAttemptStartedAt = DateTimeOffset.MinValue;
            restoreAttemptAvailableAt = now + GearsetRetryDelay;
            lastRestoreError = error;
        }

        logger.Warning($"{BuildLogTag()} op=restore-gearset-retry context=\"{context}\" outcome={outcome} reason=\"{LastFateGearsetRestoreReason}\" gearset={PendingFateGearsetNumber} attempt={FateGearsetRestoreAttemptCount} retryDelay={GearsetRetryDelay.TotalSeconds:0.0}s error={error}");
    }

    private void ClearPendingFateGearsetRestore(bool clearNinjaOwnership = false)
    {
        lock (gate)
        {
            if (clearNinjaOwnership)
            {
                ninjaGearsetEquippedByController = false;
            }

            restorePending = false;
            restoreAttemptInFlight = false;
            restoreAttemptCount = 0;
            restoreTargetGearsetNumber = 0;
            restoreTargetClassJobId = 0;
            restoreGearsetName = string.Empty;
            lastRestoreError = string.Empty;
            restoreRequestedAt = DateTimeOffset.MinValue;
            restoreAttemptAvailableAt = DateTimeOffset.MinValue;
            restoreAttemptStartedAt = DateTimeOffset.MinValue;
        }
    }

    private void SetFailure(string reason)
    {
        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop(reason);
        }

        logger.ResetThrottle("dangerous-treasure-travel");
        TransitionTo(DangerousTreasureTravelState.Failed, reason, error: reason, result: DangerousTreasureTravelResult.Failed);
        logger.Warning($"{BuildLogTag()} op=failure state={DangerousTreasureTravelState.Failed} candidate={activeCandidateLabel} gearset={activeGearsetNumber} reason={reason}");
    }

    private void TransitionTo(DangerousTreasureTravelState nextState, string reason, string? error = null, DangerousTreasureTravelResult? result = null)
    {
        DangerousTreasureTravelState previousState;
        lock (gate)
        {
            previousState = state;
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

        logger.Info($"{BuildLogTag()} op=transition from={previousState} to={nextState} candidate={activeCandidateLabel} previousCandidate={(previousCandidateLabel.Length == 0 ? "none" : previousCandidateLabel)} gearset={activeGearsetNumber} walkingPhase={activeWalkingPhase} pendingHiddenMove={pendingHiddenMovePhase} result={LastResult} reason={reason}");
    }

    private string BuildLogTag()
        => currentRunId.Length == 0 ? "[DangerousTravel]" : $"[DangerousTravel run={currentRunId}]";

    private bool ContinueDangerousApproach(string reason)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue || currentCandidate == null)
        {
            SetFailure($"Dangerous treasure candidate {activeCandidateLabel} could not continue because the player position or candidate context is unavailable.");
            return false;
        }

        activeWalkingPhase = DangerousTreasureWalkingPhase.None;
        if (TryBeginPreviousThresholdClear(playerPosition.Value, reason))
        {
            return true;
        }

        playerPosition = objectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue)
        {
            SetFailure($"Dangerous treasure candidate {activeCandidateLabel} lost player position while continuing the dangerous route.");
            return false;
        }

        if (TryBeginCurrentThresholdTravel(playerPosition.Value, reason))
        {
            return true;
        }

        if (condition[ConditionFlag.InCombat])
        {
            return StartWalkingPhase(
                DangerousTreasureWalkingPhase.FinalApproach,
                finalDestination,
                arrivalTolerance,
                allowMount: true,
                $"Dangerous treasure final approach for {activeCandidateLabel}",
                $"{reason} Continuing dangerous approach without Hide because combat is active.");
        }

        if (gameActionController.IsStealthed)
        {
            return StartWalkingPhase(
                DangerousTreasureWalkingPhase.FinalApproach,
                finalDestination,
                arrivalTolerance,
                allowMount: false,
                $"Hidden final approach for {activeCandidateLabel}",
                $"{reason} Reusing active Hide for the final on-foot approach to dangerous candidate {activeCandidateLabel}.");
        }

        pendingHiddenMovePhase = DangerousTreasureWalkingPhase.FinalApproach;
        pendingHiddenMoveDestination = finalDestination;
        pendingHiddenMoveArrivalTolerance = arrivalTolerance;
        TransitionTo(DangerousTreasureTravelState.Dismounting, $"{reason} Preparing Hide before the final approach to dangerous candidate {activeCandidateLabel}.");
        return true;
    }

    private bool TryBeginPreviousThresholdClear(Vector3 playerPosition, string reason)
    {
        if (!IsWithinHideThreshold(previousCandidate, playerPosition))
        {
            return false;
        }

        if (condition[ConditionFlag.InCombat])
        {
            logger.Info($"{BuildLogTag()} op=previous-threshold-combat previousCandidate={(previousCandidateLabel.Length == 0 ? "none" : previousCandidateLabel)} candidate={activeCandidateLabel} reason={reason}");
            return false;
        }

        var clearPoint = GetThresholdApproachPoint(previousCandidate, finalDestination, PreviousThresholdExtraDistance);
        if (!clearPoint.HasValue)
        {
            return false;
        }

        var resolvedClearPoint = movementController.FindNearestNavigablePoint(clearPoint.Value, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!resolvedClearPoint.HasValue)
        {
            SkipCandidate($"Dangerous treasure candidate {activeCandidateLabel} could not resolve a reliable vnavmesh clear point while leaving previous threshold {previousCandidateLabel}.");
            return true;
        }

        if (gameActionController.IsStealthed)
        {
            return StartWalkingPhase(
                DangerousTreasureWalkingPhase.ClearingPreviousThreshold,
                resolvedClearPoint.Value,
                ThresholdArrivalTolerance,
                allowMount: false,
                $"Dangerous treasure previous threshold clear for {activeCandidateLabel}",
                $"{reason} Reusing active Hide to clear previous dangerous threshold {previousCandidateLabel} before continuing.");
        }

        pendingHiddenMovePhase = DangerousTreasureWalkingPhase.ClearingPreviousThreshold;
        pendingHiddenMoveDestination = resolvedClearPoint.Value;
        pendingHiddenMoveArrivalTolerance = ThresholdArrivalTolerance;
        TransitionTo(DangerousTreasureTravelState.Dismounting, $"{reason} Inside previous dangerous threshold {previousCandidateLabel}; preparing Hide to clear it.");
        return true;
    }

    private bool TryBeginCurrentThresholdTravel(Vector3 playerPosition, string reason)
    {
        if (currentCandidate == null || IsWithinHideThreshold(currentCandidate, playerPosition))
        {
            return false;
        }

        var thresholdPoint = GetThresholdApproachPoint(currentCandidate, playerPosition, 0f);
        if (!thresholdPoint.HasValue)
        {
            return false;
        }

        var resolvedThresholdPoint = movementController.FindNearestNavigablePoint(thresholdPoint.Value, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!resolvedThresholdPoint.HasValue)
        {
            SkipCandidate($"Dangerous treasure candidate {activeCandidateLabel} has no reliable vnavmesh hide-threshold point near <{thresholdPoint.Value.X:0.000}, {thresholdPoint.Value.Y:0.000}, {thresholdPoint.Value.Z:0.000}>.");
            return true;
        }

        movementController.SetLogOwner(currentRunId);
        if (!movementController.StartDirectMove($"Dangerous treasure threshold for {activeCandidateLabel}", resolvedThresholdPoint.Value, ThresholdArrivalTolerance, shouldMountBeforeStep: true))
        {
            SkipCandidate(movementController.LastError.Length == 0
                ? $"Failed to start mounted travel to the hide threshold for dangerous treasure candidate {activeCandidateLabel}."
                : movementController.LastError);
            return true;
        }

        TransitionTo(DangerousTreasureTravelState.TravelingToHideThreshold, $"{reason} Moving to current dangerous threshold for {activeCandidateLabel}.");
        return true;
    }

    private bool StartWalkingPhase(
        DangerousTreasureWalkingPhase phase,
        Vector3 destination,
        float destinationArrivalTolerance,
        bool allowMount,
        string description,
        string reason)
    {
        movementController.SetLogOwner(currentRunId);
        if (!movementController.StartDirectMove(description, destination, destinationArrivalTolerance, shouldMountBeforeStep: allowMount))
        {
            SkipCandidate(movementController.LastError.Length == 0
                ? $"Failed to start movement for dangerous treasure candidate {activeCandidateLabel}."
                : movementController.LastError);
            return false;
        }

        activeWalkingPhase = phase;
        pendingHiddenMovePhase = DangerousTreasureWalkingPhase.None;
        pendingHiddenMoveDestination = Vector3.Zero;
        pendingHiddenMoveArrivalTolerance = 0f;
        TransitionTo(DangerousTreasureTravelState.WalkingToCandidate, reason);
        return true;
    }

    private int GetHideThresholdDistance(TreasureCofferCandidateData? candidate)
        => Math.Max(10, candidate?.HideThresholdDistance ?? configuration.HideThresholdDistance);

    private bool IsWithinHideThreshold(TreasureCofferCandidateData? candidate, Vector3 position)
    {
        if (candidate == null || !IsDangerousCandidate(candidate))
        {
            return false;
        }

        return CalculateFlatDistance(position, candidate.Position.ToVector3()) <= GetHideThresholdDistance(candidate);
    }

    private bool IsDangerousCandidate(TreasureCofferCandidateData candidate)
        => candidate.AggroLevel > configuration.MaximumAggroLevel;

    private Vector3? GetThresholdApproachPoint(TreasureCofferCandidateData? candidate, Vector3 fromPosition, float extraDistance)
    {
        if (candidate == null)
        {
            return null;
        }

        var reference = candidate.Position.ToVector3();
        var delta = new Vector2(fromPosition.X - reference.X, fromPosition.Z - reference.Z);
        var distance = delta.Length();
        if (distance <= float.Epsilon)
        {
            return null;
        }

        var direction = Vector2.Normalize(delta);
        var radius = GetHideThresholdDistance(candidate) + MathF.Max(0f, extraDistance);
        return new Vector3(
            reference.X + (direction.X * radius),
            reference.Y,
            reference.Z + (direction.Y * radius));
    }

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }
}
