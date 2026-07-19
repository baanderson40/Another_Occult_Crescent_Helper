using System;
using System.Numerics;
using System.Threading;

using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
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
    TravelingDirectlyAfterThreatClear,
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

public readonly record struct DangerousTreasureTravelOptions(int GearsetNumber, int HideThresholdDistance, int MaximumAggroLevel);

public sealed class DangerousTreasureTravelController : IDisposable
{
    private static int nextRunSequence;
    private static readonly TimeSpan GearsetEquipTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GearsetRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GearsetPostElixirDelay = TimeSpan.FromSeconds(2);
    private const int MaximumGearsetEquipAttempts = 2;
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DismountRequestInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HideReadyTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan HideVerifyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HideStateSettleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HideDispatchRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GearsetDismountRequestInterval = TimeSpan.FromSeconds(5);
    private const float ThresholdArrivalTolerance = 3.5f;
    private const float PreviousThresholdExtraDistance = 6f;

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly MovementController movementController;
    private readonly OccultCrescentScanner scanner;
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
    private DateTimeOffset runStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset stateEnteredAt = DateTimeOffset.MinValue;
    private bool ninjaGearsetEquippedByController;
    private bool gearsetAttemptInFlight;
    private int gearsetAttemptCount;
    private int activeGearsetNumber;
    private uint activeGearsetTargetClassJobId;
    private string activeGearsetName = string.Empty;
    private DateTimeOffset gearsetAttemptAvailableAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastGearsetDismountRequestAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastHideDismountRequestAt = DateTimeOffset.MinValue;
    private int activeHideThresholdDistance;
    private int activeMaximumAggroLevel;
    private bool hideRetryUsed;
    private bool hiddenFinalApproachRetryUsed;
    private DateTimeOffset lastHideActivatedAt = DateTimeOffset.MinValue;
    private DateTimeOffset hideReadyDeadline = DateTimeOffset.MinValue;
    private DateTimeOffset hideDispatchRetryAvailableAt = DateTimeOffset.MinValue;
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
    private string callerName = string.Empty;
    private KnowledgeThreatPolicy? activeKnowledgeThreatPolicy;

    public DangerousTreasureTravelController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        OccultCrescentScanner scanner,
        MovementController movementController,
        GameActionController gameActionController,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.scanner = scanner;
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
            runStartedAt = DateTimeOffset.MinValue;
            stateEnteredAt = DateTimeOffset.MinValue;
            ninjaGearsetEquippedByController = false;
            gearsetAttemptInFlight = false;
            gearsetAttemptCount = 0;
            activeGearsetNumber = 0;
            activeGearsetTargetClassJobId = 0;
            activeGearsetName = string.Empty;
            gearsetAttemptAvailableAt = DateTimeOffset.MinValue;
            lastGearsetDismountRequestAt = DateTimeOffset.MinValue;
            lastHideDismountRequestAt = DateTimeOffset.MinValue;
            activeHideThresholdDistance = 0;
            activeMaximumAggroLevel = 0;
            hideRetryUsed = false;
            hiddenFinalApproachRetryUsed = false;
            lastHideActivatedAt = DateTimeOffset.MinValue;
            hideReadyDeadline = DateTimeOffset.MinValue;
            hideDispatchRetryAvailableAt = DateTimeOffset.MinValue;
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
            callerName = string.Empty;
            activeKnowledgeThreatPolicy = null;
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

    public bool Start(TreasureCofferCandidateData? previousCandidate, TreasureCofferCandidateData candidate, Vector3 destination, float finalArrivalTolerance, DangerousTreasureTravelOptions options)
        => Start("unspecified", previousCandidate, candidate, destination, finalArrivalTolerance, options);

    public bool Start(string caller, TreasureCofferCandidateData? previousCandidate, TreasureCofferCandidateData candidate, Vector3 destination, float finalArrivalTolerance, DangerousTreasureTravelOptions options, KnowledgeThreatPolicy? knowledgeThreatPolicy = null)
    {
        if (IsRunning)
        {
            return true;
        }

        var dependencyReport = Plugin.Current?.GetNormalAutomationDependencyReport();
        if (dependencyReport is { IsReady: false })
        {
            Plugin.Current?.TryOpenDependencyWindow();
            SetFailure(dependencyReport.FailureSummary);
            return false;
        }

        if (options.GearsetNumber <= 0)
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
            runStartedAt = DateTimeOffset.UtcNow;
            lastError = string.Empty;
            lastResult = DangerousTreasureTravelResult.None;
            gearsetAttemptInFlight = false;
            gearsetAttemptCount = 0;
            activeGearsetNumber = options.GearsetNumber;
            activeGearsetTargetClassJobId = 0;
            activeGearsetName = string.Empty;
            gearsetAttemptAvailableAt = DateTimeOffset.UtcNow + GearsetPostElixirDelay;
            lastGearsetDismountRequestAt = DateTimeOffset.MinValue;
            lastHideDismountRequestAt = DateTimeOffset.MinValue;
            activeHideThresholdDistance = options.HideThresholdDistance;
            activeMaximumAggroLevel = options.MaximumAggroLevel;
            hideRetryUsed = false;
            hiddenFinalApproachRetryUsed = false;
            lastHideActivatedAt = DateTimeOffset.MinValue;
            hideReadyDeadline = DateTimeOffset.MinValue;
            hideDispatchRetryAvailableAt = DateTimeOffset.MinValue;
            this.previousCandidate = previousCandidate;
            currentCandidate = candidate;
            pendingHiddenMovePhase = DangerousTreasureWalkingPhase.None;
            activeWalkingPhase = DangerousTreasureWalkingPhase.None;
            pendingHiddenMoveDestination = Vector3.Zero;
            pendingHiddenMoveArrivalTolerance = 0f;
            callerName = caller;
            activeKnowledgeThreatPolicy = knowledgeThreatPolicy;
        }

        var playerForayLevel = scanner.Snapshot.PlayerForayLevel;
        var knowledgeHideAtOrAbove = knowledgeThreatPolicy.HasValue && playerForayLevel.HasValue
            ? knowledgeThreatPolicy.Value.GetHideAtOrAbove(playerForayLevel.Value)
            : 0;
        var knowledgeOffsetText = knowledgeThreatPolicy.HasValue ? knowledgeThreatPolicy.Value.HideOffset.ToString() : "none";
        var knowledgeEnterRangeText = knowledgeThreatPolicy.HasValue ? knowledgeThreatPolicy.Value.EnterDistance.ToString("0.0") : "none";
        var knowledgeExitRangeText = knowledgeThreatPolicy.HasValue ? knowledgeThreatPolicy.Value.ExitDistance.ToString("0.0") : "none";
        logger.Info($"{BuildLogTag()} op=start caller={FormatValue(caller)} candidate={candidate.Label} previousCandidate={(previousCandidate?.Label ?? "none")} playerPos={FormatVector(playerPosition)} candidatePos={FormatVector(candidate.Position.ToVector3())} previousCandidatePos={FormatVector(previousCandidate?.Position.ToVector3())} destination={FormatVector(destination)} arrivalTolerance={finalArrivalTolerance:0.0} gearset={options.GearsetNumber} candidateAggro={candidate.AggroLevel} maxAggro={options.MaximumAggroLevel} candidateHideThreshold={(candidate.HideThresholdDistance?.ToString() ?? "none")} configuredHideThreshold={options.HideThresholdDistance} knowledgePolicy={knowledgeThreatPolicy.HasValue} playerForayLevel={(playerForayLevel?.ToString() ?? "unavailable")} knowledgeOffset={knowledgeOffsetText} knowledgeHideAtOrAbove={(knowledgeHideAtOrAbove == 0 ? "unavailable" : knowledgeHideAtOrAbove)} knowledgeEnterRange={knowledgeEnterRangeText} knowledgeExitRange={knowledgeExitRangeText}");
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
        logger.Info($"{BuildLogTag()} op=terminal-ack caller={FormatValue(callerName)} state={State} result={LastResult} candidate={activeCandidateLabel}");
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
            case DangerousTreasureTravelState.TravelingDirectlyAfterThreatClear:
                TickTravelingDirectlyAfterThreatClear();
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
                WaitLogInterval,
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

            if (now - lastGearsetDismountRequestAt < GearsetDismountRequestInterval)
            {
                return;
            }

            lastGearsetDismountRequestAt = now;
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
                var playerPosition = objectTable.LocalPlayer?.Position;
                movementController.Stop("Reached dangerous treasure hide threshold.");
                logger.ResetThrottle("dangerous-treasure-travel");

                if (!playerPosition.HasValue || currentCandidate == null)
                {
                    SetFailure($"Dangerous treasure candidate {activeCandidateLabel} could not confirm threshold arrival because the player position or candidate context is unavailable.");
                    return;
                }

                if (!IsWithinHideThreshold(currentCandidate, playerPosition.Value))
                {
                    logger.Info(
                        $"{BuildLogTag()} op=threshold-arrival-outside-boundary candidate={activeCandidateLabel} player={FormatVector(playerPosition)} candidatePosition={FormatVector(currentCandidate.Position.ToVector3())} distance={CalculateFlatDistance(playerPosition.Value, currentCandidate.Position.ToVector3()):0.0} threshold={GetHideThresholdDistance(currentCandidate):0.0}");
                    PrepareHideFinalApproach($"Reached threshold approach point for {activeCandidateLabel} but remained just outside the strict hide boundary.");
                    return;
                }

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
            $"Dangerous treasure travel is moving to the hide threshold for {activeCandidateLabel}. playerPos={FormatVector(objectTable.LocalPlayer?.Position)} destination={FormatVector(finalDestination)} remainingDistance={CalculateFlatDistance(objectTable.LocalPlayer?.Position ?? finalDestination, finalDestination):0.0}y movementState={movementController.State} pathBusy={movementController.IsPathBusy} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()} mounted={condition[ConditionFlag.Mounted]} stealthed={gameActionController.IsStealthed} inCombat={condition[ConditionFlag.InCombat]}.");
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
            hideReadyDeadline = DateTimeOffset.UtcNow + HideReadyTimeout;
            hideDispatchRetryAvailableAt = DateTimeOffset.MinValue;
            TransitionTo(DangerousTreasureTravelState.WaitingForHideReady, $"Dismounted at dangerous threshold for {activeCandidateLabel}; waiting for Hide.");
            return;
        }

        if (DateTimeOffset.UtcNow - stateEnteredAt >= DismountTimeout)
        {
            SkipCandidate($"Timed out dismounting inside the hide threshold for dangerous treasure candidate {activeCandidateLabel}.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - lastHideDismountRequestAt < DismountRequestInterval)
        {
            return;
        }

        lastHideDismountRequestAt = now;
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
        var now = DateTimeOffset.UtcNow;
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

        if (now - stateEnteredAt < HideStateSettleDelay)
        {
            logger.DebugThrottled(
                "dangerous-treasure-travel-hide-ready",
                TimeSpan.FromMilliseconds(250),
                $"Dangerous treasure travel is settling after dismount/job change before checking Hide readiness on {activeCandidateLabel}. elapsed={(now - stateEnteredAt).TotalSeconds:0.00}s required={HideStateSettleDelay.TotalSeconds:0.00}s.");
            return;
        }

        if (now < hideDispatchRetryAvailableAt)
        {
            logger.DebugThrottled(
                "dangerous-treasure-travel-hide-retry-delay",
                WaitLogInterval,
                $"Dangerous treasure travel is waiting for the next Hide dispatch retry on {activeCandidateLabel}. retryAvailableIn={Math.Max(0, (hideDispatchRetryAvailableAt - now).TotalSeconds):0.00}s deadlineIn={Math.Max(0, (hideReadyDeadline - now).TotalSeconds):0.0}s changeableState=\"{gameActionController.GetChangeableStateSummary()}\"");
            return;
        }

        if (gameActionController.IsPlayerInChangeableState() && gameActionController.CanUseHide())
        {
            logger.ResetThrottle("dangerous-treasure-travel-hide-ready");
            TransitionTo(DangerousTreasureTravelState.UsingHide, $"Hide ready for dangerous candidate {activeCandidateLabel}; attempting stealth.");
            return;
        }

        if (hideReadyDeadline != DateTimeOffset.MinValue && now >= hideReadyDeadline)
        {
            SkipCandidate($"Hide did not become ready for dangerous treasure candidate {activeCandidateLabel} within {HideReadyTimeout.TotalSeconds:0.0}s.");
            return;
        }

        logger.DebugThrottled(
            "dangerous-treasure-travel-hide-ready",
            WaitLogInterval,
            $"Dangerous treasure travel is waiting for Hide to become ready on {activeCandidateLabel}. deadlineIn={Math.Max(0, (hideReadyDeadline - now).TotalSeconds):0.0}s currentClassJob={gameActionController.CurrentClassJobId} canUseHide={gameActionController.CanUseHide()} changeable={gameActionController.IsPlayerInChangeableState()} changeableState=\"{gameActionController.GetChangeableStateSummary()}\" stealthed={gameActionController.IsStealthed} mounted={condition[ConditionFlag.Mounted]} retryUsed={hideRetryUsed} pendingPhase={pendingHiddenMovePhase} pendingDestination={FormatVector(pendingHiddenMoveDestination)}.");
    }

    private void TickUsingHide()
    {
        if (condition[ConditionFlag.InCombat])
        {
            ContinueDangerousApproach($"Combat started before Hide application on dangerous candidate {activeCandidateLabel}.");
            return;
        }

        if (!gameActionController.IsPlayerInChangeableState() || !gameActionController.CanUseHide())
        {
            hideDispatchRetryAvailableAt = DateTimeOffset.UtcNow + HideDispatchRetryDelay;
            logger.DebugThrottled(
                "dangerous-treasure-travel-hide-dispatch-rejected",
                TimeSpan.FromSeconds(1),
                $"Dangerous treasure travel is deferring Hide dispatch because readiness changed on {activeCandidateLabel}. canUseHide={gameActionController.CanUseHide()} changeableState=\"{gameActionController.GetChangeableStateSummary()}\" retryAt={hideDispatchRetryAvailableAt:O}");
            TransitionTo(DangerousTreasureTravelState.WaitingForHideReady, $"Hide was no longer ready for dangerous treasure candidate {activeCandidateLabel}; retrying after a short delay.");
            return;
        }

        if (!gameActionController.TryExecuteAction(GameActionController.HideActionId, $"dangerous treasure travel for {activeCandidateLabel}"))
        {
            hideDispatchRetryAvailableAt = DateTimeOffset.UtcNow + HideDispatchRetryDelay;
            logger.DebugThrottled(
                "dangerous-treasure-travel-hide-dispatch-rejected",
                TimeSpan.FromSeconds(1),
                $"Dangerous treasure travel received an ambiguous Hide dispatch result on {activeCandidateLabel}. actionId={GameActionController.HideActionId} canUseHide={gameActionController.CanUseHide()} changeableState=\"{gameActionController.GetChangeableStateSummary()}\" retryAt={hideDispatchRetryAvailableAt:O}");
            TransitionTo(DangerousTreasureTravelState.WaitingForHideReady, $"Hide action dispatch was rejected for dangerous treasure candidate {activeCandidateLabel}; retrying after a short delay.");
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
            $"Dangerous treasure travel is waiting for Hide to apply on {activeCandidateLabel}. elapsed={(DateTimeOffset.UtcNow - stateEnteredAt).TotalSeconds:0.0}s timeout={HideVerifyTimeout.TotalSeconds:0.0}s stealthed={gameActionController.IsStealthed} canUseHide={gameActionController.CanUseHide()} mounted={condition[ConditionFlag.Mounted]} inCombat={condition[ConditionFlag.InCombat]} pendingPhase={pendingHiddenMovePhase} pendingDestination={FormatVector(pendingHiddenMoveDestination)} lastHideActivatedAt={(lastHideActivatedAt == DateTimeOffset.MinValue ? "none" : lastHideActivatedAt.ToString("O"))}.");
    }

    private void TickWalkingToCandidate()
    {
        LogKnowledgeThreatStatus("hidden");
        if (activeWalkingPhase == DangerousTreasureWalkingPhase.FinalApproach
            && gameActionController.IsStealthed
            && (!activeKnowledgeThreatPolicy.HasValue
                || !IsKnowledgeThreatActive(activeKnowledgeThreatPolicy.Value.ExitDistance, out _, out _))
            && KnowledgeThreatEvaluator.TryFindHideException(
                scanner.Snapshot,
                KnowledgeThreatEvaluator.OccultIsleblazerUnhideDistance,
                out var hideException))
        {
            movementController.Stop("Occult Isleblazer is unsafe while hidden; resuming unhidden travel.");
            logger.Info($"{BuildLogTag()} op=hide-exception-unhide candidate={activeCandidateLabel} entity='{hideException?.Name}' objectId={hideException?.ObjectId:X} baseId={hideException?.BaseId} distance={hideException?.DistanceToPlayer:0.0}y");
            StartDirectTravelAfterThreatClear("Occult Isleblazer entered the unhide range.");
            return;
        }

        if (activeWalkingPhase == DangerousTreasureWalkingPhase.FinalApproach
            && activeKnowledgeThreatPolicy.HasValue
            && scanner.Snapshot.PlayerForayLevel.HasValue
            && !IsKnowledgeThreatActive(activeKnowledgeThreatPolicy.Value.ExitDistance, out _, out _))
        {
            movementController.Stop("Knowledge threat cleared; resuming mounted treasure travel.");
            StartDirectTravelAfterThreatClear("Knowledge threat cleared.");
            return;
        }

        if (activeWalkingPhase == DangerousTreasureWalkingPhase.FinalApproach
            && activeKnowledgeThreatPolicy.HasValue
            && !gameActionController.IsStealthed
            && !condition[ConditionFlag.InCombat])
        {
            movementController.Stop("Hide was lost while a knowledge threat remained nearby.");
            ContinueDangerousApproach($"Hide was lost while a live knowledge threat remained near {activeCandidateLabel}.");
            return;
        }

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
                SkipCandidate(movementController.LastError.Length == 0
                    ? $"Failed movement for dangerous treasure candidate {activeCandidateLabel}."
                    : movementController.LastError);
                return;
            case MovementState.TimedOut:
                if (TryRetryTimedOutHiddenFinalApproach())
                {
                    return;
                }

                SkipCandidate(movementController.LastError.Length == 0
                    ? $"Failed movement for dangerous treasure candidate {activeCandidateLabel}."
                    : movementController.LastError);
                return;
        }

        logger.DebugThrottled(
            "dangerous-treasure-travel",
            WaitLogInterval,
            $"Dangerous treasure travel is moving for {activeCandidateLabel}. phase={activeWalkingPhase} playerPos={FormatVector(objectTable.LocalPlayer?.Position)} destination={FormatVector(finalDestination)} remainingDistance={CalculateFlatDistance(objectTable.LocalPlayer?.Position ?? finalDestination, finalDestination):0.0}y tolerance={arrivalTolerance:0.0} MovementState={movementController.State} pathBusy={movementController.IsPathBusy} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()} stealthed={gameActionController.IsStealthed} mounted={condition[ConditionFlag.Mounted]} inCombat={condition[ConditionFlag.InCombat]}.");
    }

    private void TickTravelingDirectlyAfterThreatClear()
    {
        LogKnowledgeThreatStatus("direct-after-clear");
        if (activeKnowledgeThreatPolicy.HasValue && !scanner.Snapshot.PlayerForayLevel.HasValue)
        {
            movementController.Stop("Live knowledge data became unavailable during treasure travel.");
            ContinueDangerousApproach($"Live knowledge data became unavailable while traveling to {activeCandidateLabel}.");
            return;
        }

        if (activeKnowledgeThreatPolicy.HasValue
            && IsKnowledgeThreatActive(activeKnowledgeThreatPolicy.Value.EnterDistance, out var threat, out var hideAtOrAbove))
        {
            movementController.Stop("Knowledge threat entered the Hide range.");
            logger.Info($"{BuildLogTag()} op=knowledge-threat-enter candidate={activeCandidateLabel} entity='{threat?.Name}' objectId={threat?.ObjectId:X} playerForayLevel={scanner.Snapshot.PlayerForayLevel?.ToString() ?? "unavailable"} entityLevel={threat?.KnowledgeLevel} hideAtOrAbove={hideAtOrAbove} enterRange={activeKnowledgeThreatPolicy.Value.EnterDistance:0.0} distance={threat?.DistanceToPlayer:0.0}");
            ContinueDangerousApproach($"A live knowledge threat entered the Hide range for {activeCandidateLabel}.");
            return;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached treasure candidate after knowledge threat cleared.");
                activeWalkingPhase = DangerousTreasureWalkingPhase.None;
                TransitionTo(DangerousTreasureTravelState.Arrived, $"Reached dangerous treasure candidate {activeCandidateLabel} after the knowledge threat cleared.", result: DangerousTreasureTravelResult.Arrived);
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                SkipCandidate(movementController.LastError.Length == 0
                    ? $"Failed direct movement after the knowledge threat cleared for {activeCandidateLabel}."
                    : movementController.LastError);
                return;
        }
    }

    private void StartDirectTravelAfterThreatClear(string reason)
    {
        logger.Info($"{BuildLogTag()} op=direct-travel-resume candidate={activeCandidateLabel} reason={reason} playerForayLevel={scanner.Snapshot.PlayerForayLevel?.ToString() ?? "unavailable"} destination={FormatVector(finalDestination)} exitRange={activeKnowledgeThreatPolicy?.ExitDistance:0.0}");
        movementController.SetLogOwner(currentRunId);
        if (!movementController.StartDirectMove($"Treasure travel after knowledge threat clear for {activeCandidateLabel}", finalDestination, arrivalTolerance, shouldMountBeforeStep: true))
        {
            SkipCandidate(movementController.LastError.Length == 0
                ? $"Failed to resume direct travel after the knowledge threat cleared for {activeCandidateLabel}."
                : movementController.LastError);
            return;
        }

        activeWalkingPhase = DangerousTreasureWalkingPhase.None;
        TransitionTo(DangerousTreasureTravelState.TravelingDirectlyAfterThreatClear, $"{reason} Resuming direct travel to {activeCandidateLabel}.");
    }

    private bool IsKnowledgeThreatActive(float radius, out ForayThreatEntity? threat, out int hideAtOrAbove)
    {
        threat = null;
        hideAtOrAbove = 0;
        return activeKnowledgeThreatPolicy.HasValue
            && KnowledgeThreatEvaluator.TryFindThreat(scanner.Snapshot, activeKnowledgeThreatPolicy.Value, radius, out threat, out hideAtOrAbove);
    }

    private void LogKnowledgeThreatStatus(string travelMode)
    {
        if (!activeKnowledgeThreatPolicy.HasValue)
        {
            return;
        }

        var snapshot = scanner.Snapshot;
        var hasExitThreat = KnowledgeThreatEvaluator.TryFindThreat(
            snapshot,
            activeKnowledgeThreatPolicy.Value,
            activeKnowledgeThreatPolicy.Value.ExitDistance,
            out var threat,
            out var hideAtOrAbove);
        logger.InfoThrottled(
            $"dangerous-knowledge-threat-status-{currentRunId}",
            WaitLogInterval,
            $"{BuildLogTag()} op=knowledge-threat-status mode={travelMode} caller={FormatValue(callerName)} playerForayLevel={snapshot.PlayerForayLevel?.ToString() ?? "unavailable"} offset={activeKnowledgeThreatPolicy.Value.HideOffset} hideAtOrAbove={(hideAtOrAbove == 0 ? "unavailable" : hideAtOrAbove)} enterRange={activeKnowledgeThreatPolicy.Value.EnterDistance:0.0} exitRange={activeKnowledgeThreatPolicy.Value.ExitDistance:0.0} exitThreat={hasExitThreat} entity='{threat?.Name ?? "none"}' objectId={threat?.ObjectId:X} entityLevel={threat?.KnowledgeLevel ?? 0} distance={threat?.DistanceToPlayer:0.0} stealthed={gameActionController.IsStealthed} mounted={condition[ConditionFlag.Mounted]}");
    }

    private bool TryRetryTimedOutHiddenFinalApproach()
    {
        if (activeWalkingPhase != DangerousTreasureWalkingPhase.FinalApproach
            || hiddenFinalApproachRetryUsed
            || !gameActionController.IsStealthed
            || condition[ConditionFlag.Mounted]
            || condition[ConditionFlag.InCombat])
        {
            return false;
        }

        hiddenFinalApproachRetryUsed = true;
        logger.Warning($"{BuildLogTag()} op=hidden-final-approach-retry candidate={activeCandidateLabel} destination={FormatVector(finalDestination)} arrivalTolerance={arrivalTolerance:0.0} reason={movementController.LastError}");
        return StartWalkingPhase(
            DangerousTreasureWalkingPhase.FinalApproach,
            finalDestination,
            arrivalTolerance,
            allowMount: false,
            $"Hidden final approach for {activeCandidateLabel}",
            $"Retrying the timed-out hidden final approach for dangerous candidate {activeCandidateLabel}.");
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
            if (nextState == DangerousTreasureTravelState.EquippingNinjaGearset && previousState != nextState)
            {
                lastGearsetDismountRequestAt = DateTimeOffset.MinValue;
            }

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

        logger.Info($"{BuildLogTag()} op=transition caller={FormatValue(callerName)} from={previousState} to={nextState} candidate={activeCandidateLabel} previousCandidate={(previousCandidateLabel.Length == 0 ? "none" : previousCandidateLabel)} gearset={activeGearsetNumber} walkingPhase={activeWalkingPhase} pendingHiddenMove={pendingHiddenMovePhase} result={LastResult} reason={reason}");
        if (result.HasValue)
        {
            var now = DateTimeOffset.UtcNow;
            logger.Info($"{BuildLogTag()} op=terminal caller={FormatValue(callerName)} state={nextState} result={result.Value} candidate={activeCandidateLabel} playerPos={FormatVector(objectTable.LocalPlayer?.Position)} candidatePos={FormatVector(currentCandidate?.Position.ToVector3())} destination={FormatVector(finalDestination)} remainingDistance={CalculateFlatDistance(objectTable.LocalPlayer?.Position ?? finalDestination, finalDestination):0.0}y runElapsed={(runStartedAt == DateTimeOffset.MinValue ? 0 : (now - runStartedAt).TotalSeconds):0.0}s walkingPhase={activeWalkingPhase} pendingPhase={pendingHiddenMovePhase} movementState={movementController.State} pathBusy={movementController.IsPathBusy} movementError={FormatValue(movementController.LastError)} currentClassJob={gameActionController.CurrentClassJobId} stealthed={gameActionController.IsStealthed} mounted={condition[ConditionFlag.Mounted]} inCombat={condition[ConditionFlag.InCombat]} gearsetAttempts={gearsetAttemptCount} hideRetryUsed={hideRetryUsed} reason={reason}");
        }
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
        logger.Info($"{BuildLogTag()} op=approach-evaluate candidate={activeCandidateLabel} previousCandidate={FormatValue(previousCandidateLabel)} playerPos={FormatVector(playerPosition)} destination={FormatVector(finalDestination)} previousThresholdActive={IsWithinHideThreshold(previousCandidate, playerPosition.Value)} currentThresholdActive={IsWithinHideThreshold(currentCandidate, playerPosition.Value)} mounted={condition[ConditionFlag.Mounted]} stealthed={gameActionController.IsStealthed} inCombat={condition[ConditionFlag.InCombat]} reason={reason}");
        if (activeKnowledgeThreatPolicy.HasValue)
        {
            return ContinueKnowledgeThreatApproach(reason);
        }

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
            logger.Info($"{BuildLogTag()} op=approach-decision candidate={activeCandidateLabel} branch=combat-bypass phase=FinalApproach destination={FormatVector(finalDestination)} arrivalTolerance={arrivalTolerance:0.0}");
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
            logger.Info($"{BuildLogTag()} op=approach-decision candidate={activeCandidateLabel} branch=reuse-active-hide phase=FinalApproach destination={FormatVector(finalDestination)} arrivalTolerance={arrivalTolerance:0.0}");
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
        logger.Info($"{BuildLogTag()} op=approach-decision candidate={activeCandidateLabel} branch=prepare-hide phase=FinalApproach destination={FormatVector(finalDestination)} arrivalTolerance={arrivalTolerance:0.0}");
        TransitionTo(DangerousTreasureTravelState.Dismounting, $"{reason} Preparing Hide before the final approach to dangerous candidate {activeCandidateLabel}.");
        return true;
    }

    private bool ContinueKnowledgeThreatApproach(string reason)
    {
        if (condition[ConditionFlag.InCombat])
        {
            return StartWalkingPhase(
                DangerousTreasureWalkingPhase.FinalApproach,
                finalDestination,
                arrivalTolerance,
                allowMount: true,
                $"Knowledge threat travel for {activeCandidateLabel}",
                $"{reason} Continuing without Hide because combat is active.");
        }

        if (gameActionController.IsStealthed)
        {
            return StartWalkingPhase(
                DangerousTreasureWalkingPhase.FinalApproach,
                finalDestination,
                arrivalTolerance,
                allowMount: false,
                $"Hidden knowledge threat travel for {activeCandidateLabel}",
                $"{reason} Reusing active Hide while a live knowledge threat remains nearby.");
        }

        pendingHiddenMovePhase = DangerousTreasureWalkingPhase.FinalApproach;
        pendingHiddenMoveDestination = finalDestination;
        pendingHiddenMoveArrivalTolerance = arrivalTolerance;
        TransitionTo(DangerousTreasureTravelState.Dismounting, $"{reason} Preparing Hide for a live knowledge threat near {activeCandidateLabel}.");
        return true;
    }

    private bool TryBeginPreviousThresholdClear(Vector3 playerPosition, string reason)
    {
        if (!IsWithinHideThreshold(previousCandidate, playerPosition))
        {
            return false;
        }

        logger.Info($"{BuildLogTag()} op=threshold-decision kind=previous-clear candidate={activeCandidateLabel} previousCandidate={FormatValue(previousCandidateLabel)} playerPos={FormatVector(playerPosition)} previousPos={FormatVector(previousCandidate?.Position.ToVector3())} previousThreshold={GetHideThresholdDistance(previousCandidate):0.0} destination={FormatVector(finalDestination)} stealthed={gameActionController.IsStealthed} mounted={condition[ConditionFlag.Mounted]} inCombat={condition[ConditionFlag.InCombat]} reason={reason}");

        if (condition[ConditionFlag.InCombat])
        {
            logger.Info($"{BuildLogTag()} op=previous-threshold-combat previousCandidate={(previousCandidateLabel.Length == 0 ? "none" : previousCandidateLabel)} candidate={activeCandidateLabel} reason={reason}");
            return false;
        }

        var clearPoint = GetThresholdApproachPoint(previousCandidate, finalDestination, PreviousThresholdExtraDistance);
        var resolvedClearPoint = clearPoint.HasValue
            ? movementController.FindNearestNavigablePoint(clearPoint.Value, halfExtentXZ: 5f, halfExtentY: 5f)
            : null;
        if (!resolvedClearPoint.HasValue)
        {
            var fallbackClearPoint = GetThresholdApproachPoint(previousCandidate, playerPosition, PreviousThresholdExtraDistance);
            resolvedClearPoint = fallbackClearPoint.HasValue
                ? movementController.FindNearestNavigablePoint(fallbackClearPoint.Value, halfExtentXZ: 5f, halfExtentY: 5f)
                : null;

            if (fallbackClearPoint.HasValue && resolvedClearPoint.HasValue)
            {
                logger.Info(
                    $"{BuildLogTag()} op=previous-threshold-clear-fallback previousCandidate={previousCandidateLabel} candidate={activeCandidateLabel} primaryTarget={FormatVector(clearPoint)} fallbackTarget={FormatVector(fallbackClearPoint)} resolved={FormatVector(resolvedClearPoint)} reason={reason}");
            }
            else
            {
                logger.Warning(
                    $"{BuildLogTag()} op=previous-threshold-clear-skip previousCandidate={previousCandidateLabel} candidate={activeCandidateLabel} primaryTarget={FormatVector(clearPoint)} fallbackTarget={FormatVector(fallbackClearPoint)} reason=Could not resolve a reliable vnavmesh clear point; continuing without previous-threshold clear.");

                if (gameActionController.IsStealthed)
                {
                    return StartWalkingPhase(
                        DangerousTreasureWalkingPhase.FinalApproach,
                        finalDestination,
                        arrivalTolerance,
                        allowMount: false,
                        $"Hidden final approach for {activeCandidateLabel}",
                        $"{reason} Could not resolve previous dangerous threshold clear point for {previousCandidateLabel}; continuing the hidden final approach directly.");
                }

                pendingHiddenMovePhase = DangerousTreasureWalkingPhase.FinalApproach;
                pendingHiddenMoveDestination = finalDestination;
                pendingHiddenMoveArrivalTolerance = arrivalTolerance;
                TransitionTo(DangerousTreasureTravelState.Dismounting, $"{reason} Could not resolve previous dangerous threshold clear point for {previousCandidateLabel}; preparing Hide before continuing the final approach.");
                return true;
            }
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
            logger.Info($"{BuildLogTag()} op=threshold-decision kind=direct-hide candidate={activeCandidateLabel} reason=no-threshold-point playerPos={FormatVector(playerPosition)} candidatePos={FormatVector(currentCandidate.Position.ToVector3())} destination={FormatVector(finalDestination)}");
            return false;
        }

        var resolvedThresholdPoint = movementController.FindNearestNavigablePoint(thresholdPoint.Value, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!resolvedThresholdPoint.HasValue)
        {
            SkipCandidate($"Dangerous treasure candidate {activeCandidateLabel} has no reliable vnavmesh hide-threshold point near <{thresholdPoint.Value.X:0.000}, {thresholdPoint.Value.Y:0.000}, {thresholdPoint.Value.Z:0.000}>.");
            return true;
        }

        logger.Info($"{BuildLogTag()} op=threshold-decision kind=current-approach candidate={activeCandidateLabel} playerPos={FormatVector(playerPosition)} candidatePos={FormatVector(currentCandidate.Position.ToVector3())} threshold={GetHideThresholdDistance(currentCandidate):0.0} rawThreshold={FormatVector(thresholdPoint)} resolvedThreshold={FormatVector(resolvedThresholdPoint)} destination={FormatVector(finalDestination)}");

        if (CalculateFlatDistance(playerPosition, resolvedThresholdPoint.Value) <= ThresholdArrivalTolerance)
        {
            logger.Info(
                $"{BuildLogTag()} op=threshold-near-enough candidate={activeCandidateLabel} player={FormatVector(playerPosition)} resolvedThreshold={FormatVector(resolvedThresholdPoint)} tolerance={ThresholdArrivalTolerance:0.0} reason={reason}");
            PrepareHideFinalApproach($"{reason} Already near the current dangerous threshold for {activeCandidateLabel}.");
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
        logger.Info($"{BuildLogTag()} op=hidden-move-start candidate={activeCandidateLabel} phase={phase} destination={FormatVector(destination)} arrivalTolerance={destinationArrivalTolerance:0.0} allowMount={allowMount} playerPos={FormatVector(objectTable.LocalPlayer?.Position)} stealthed={gameActionController.IsStealthed} mounted={condition[ConditionFlag.Mounted]} reason={reason}");
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

    private void PrepareHideFinalApproach(string reason)
    {
        pendingHiddenMovePhase = DangerousTreasureWalkingPhase.FinalApproach;
        pendingHiddenMoveDestination = finalDestination;
        pendingHiddenMoveArrivalTolerance = arrivalTolerance;
        TransitionTo(DangerousTreasureTravelState.Dismounting, $"{reason} Preparing Hide before the final approach to dangerous candidate {activeCandidateLabel}.");
    }

    private int GetHideThresholdDistance(TreasureCofferCandidateData? candidate)
        => Math.Max(10, candidate?.HideThresholdDistance ?? activeHideThresholdDistance);

    private bool IsWithinHideThreshold(TreasureCofferCandidateData? candidate, Vector3 position)
    {
        if (candidate == null || !IsDangerousCandidate(candidate))
        {
            return false;
        }

        return CalculateFlatDistance(position, candidate.Position.ToVector3()) <= GetHideThresholdDistance(candidate);
    }

    private bool IsDangerousCandidate(TreasureCofferCandidateData candidate)
        => candidate.AggroLevel > activeMaximumAggroLevel
            || (candidate.HideThresholdDistance ?? 0) > 0;

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

    private static string FormatVector(Vector3? value)
        => value.HasValue
            ? $"<{value.Value.X:0.000}, {value.Value.Y:0.000}, {value.Value.Z:0.000}>"
            : "none";

    private static string FormatValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value;
}
