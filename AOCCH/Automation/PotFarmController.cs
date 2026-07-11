using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class PotFarmController : IDisposable
{
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PotSpawnGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TreasureBuffWaitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TreasureHintWaitTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TreasureElixirRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LeaveTransitionTimeout = TimeSpan.FromSeconds(15);
    private const int MaximumTreasureElixirAttempts = 3;
    private const int PotWaitPointCandidateCount = 10;
    private const float PotWaitPointStopDistance = 4f;
    private const float TreasureCenterArrivalTolerance = 5f;

    private readonly IFramework framework;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly GameActionController gameActionController;
    private readonly FateAutomationController fateAutomationController;
    private readonly InstancedContentController instancedContentController;
    private readonly PotCycleTracker potCycleTracker;
    private readonly TreasureHintTracker treasureHintTracker;
    private readonly TreasureSearchController treasureSearchController;
    private readonly CofferInteractionController cofferInteractionController;
    private readonly DangerousTreasureTravelController dangerousTreasureTravelController;
    private readonly PotInstanceTimeEvaluator potInstanceTimeEvaluator;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly Dictionary<uint, PotFateData> potFatesById;
    private readonly object gate = new();

    private PotFarmState state = PotFarmState.Idle;
    private PotFarmRunResult lastResult;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private string currentPotName = string.Empty;
    private uint currentPotId;
    private Vector3 currentPotCenter;
    private bool hasCurrentPotCenter;
    private string treasurePotName = string.Empty;
    private uint treasurePotId;
    private Vector3 treasurePotCenter;
    private bool hasTreasurePotContext;
    private DateTimeOffset waitDeadlineAt = DateTimeOffset.MinValue;
    private DateTimeOffset treasureBuffWaitDeadlineAt = DateTimeOffset.MinValue;
    private DateTimeOffset treasureHintDeadlineAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastTreasureElixirAttemptAt = DateTimeOffset.MinValue;
    private DateTimeOffset leaveRequestedAt = DateTimeOffset.MinValue;
    private DateTimeOffset stateEnteredAt = DateTimeOffset.MinValue;
    private int treasureElixirAttemptCount;
    private bool leavePending;
    private bool pendingStop;
    private bool resumeBootstrapAfterRecovery;
    private PotFarmRunResult completionResultAfterRecovery;
    private PotInstanceTimeDecision lastInstanceTimeDecision = new();

    public PotFarmController(
        IFramework framework,
        OccultCrescentScanner scanner,
        MovementController movementController,
        GameActionController gameActionController,
        FateAutomationController fateAutomationController,
        InstancedContentController instancedContentController,
        PotCycleTracker potCycleTracker,
        TreasureHintTracker treasureHintTracker,
        TreasureSearchController treasureSearchController,
        CofferInteractionController cofferInteractionController,
        DangerousTreasureTravelController dangerousTreasureTravelController,
        PotInstanceTimeEvaluator potInstanceTimeEvaluator,
        OccultCrescentData data,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.scanner = scanner;
        this.movementController = movementController;
        this.gameActionController = gameActionController;
        this.fateAutomationController = fateAutomationController;
        this.instancedContentController = instancedContentController;
        this.potCycleTracker = potCycleTracker;
        this.treasureHintTracker = treasureHintTracker;
        this.treasureSearchController = treasureSearchController;
        this.cofferInteractionController = cofferInteractionController;
        this.dangerousTreasureTravelController = dangerousTreasureTravelController;
        this.potInstanceTimeEvaluator = potInstanceTimeEvaluator;
        this.configuration = configuration;
        this.logger = logger;
        potFatesById = data.PotFates.ToDictionary(potFate => potFate.FateId);

        framework.Update += OnFrameworkUpdate;
    }

    public PotFarmState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public PotFarmRunResult LastResult
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

    public string CurrentPotName
    {
        get
        {
            lock (gate)
            {
                return currentPotName;
            }
        }
    }

    public DateTimeOffset WaitDeadlineAt
    {
        get
        {
            lock (gate)
            {
                return waitDeadlineAt;
            }
        }
    }

    public bool IsRunning
        => State is not PotFarmState.Idle
            and not PotFarmState.Stopped
            and not PotFarmState.Completed
            and not PotFarmState.Failed;

    public PotInstanceTimeDecision LastInstanceTimeDecision
    {
        get
        {
            lock (gate)
            {
                return lastInstanceTimeDecision;
            }
        }
    }

    public bool IsLeavePending
    {
        get
        {
            lock (gate)
            {
                return leavePending;
            }
        }
    }

    public DateTimeOffset LeaveRequestedAt
    {
        get
        {
            lock (gate)
            {
                return leaveRequestedAt;
            }
        }
    }

    public bool NeedsControlNow(DateTimeOffset now, out string reason)
        => NeedsControlNow(now, out _, out reason);

    public bool NeedsControlNow(DateTimeOffset now, out PotControlReason controlReason, out string reason)
    {
        if (!configuration.EnablePotFarming)
        {
            controlReason = PotControlReason.None;
            reason = "Pot farming is disabled.";
            return false;
        }

        if (IsRunning)
        {
            controlReason = State == PotFarmState.TreasurePending ? PotControlReason.TreasurePending : PotControlReason.ActiveRun;
            reason = LastTransition;
            return true;
        }

        var scannerSnapshot = scanner.Snapshot;
        if (!scannerSnapshot.IsInSouthHorn)
        {
            controlReason = PotControlReason.None;
            reason = "Pot farming requires South Horn.";
            return false;
        }

        if (scannerSnapshot.ActivePotFate != null)
        {
            controlReason = PotControlReason.ActivePotFate;
            reason = $"Active pot FATE detected: {scannerSnapshot.ActivePotFate.Name} ({scannerSnapshot.ActivePotFate.Id}).";
            return true;
        }

        if (scannerSnapshot.HasTreasureBuff && hasTreasurePotContext)
        {
            controlReason = PotControlReason.TreasurePending;
            reason = $"Treasure phase is pending with {scannerSnapshot.TreasureBuffRemainingSeconds:0}s remaining on Cache Me If You Can.";
            return true;
        }

        if (leavePending)
        {
            controlReason = PotControlReason.InstanceTimeManagement;
            reason = lastInstanceTimeDecision.Reason.Length == 0
                ? "Waiting for instanced content leave to complete."
                : lastInstanceTimeDecision.Reason;
            return true;
        }

        var instanceTimeDecision = EvaluateInstanceTimeDecision(now);
        if (instanceTimeDecision.ManageInstanceTimeEnabled
            && instanceTimeDecision.IsContentTimerAvailable
            && !instanceTimeDecision.AllowNextPotCycle)
        {
            controlReason = PotControlReason.InstanceTimeManagement;
            reason = instanceTimeDecision.Reason;
            return true;
        }

        var potCycleSnapshot = potCycleTracker.Snapshot;
        if (potCycleSnapshot.HasPredictedNextPot)
        {
            var departureAt = GetDepartureAt(potCycleSnapshot);
            if (departureAt != DateTimeOffset.MinValue && now >= departureAt)
            {
                controlReason = PotControlReason.PredictedDepartureWindow;
                reason = $"Predicted pot departure window opened for {potCycleSnapshot.PredictedNextPotFateName}.";
                return true;
            }
        }

        if (!potCycleSnapshot.HasKnownAnchor && configuration.StartingPotFate != StartingPotFateMode.Auto)
        {
            controlReason = PotControlReason.BootstrapStaging;
            reason = "No pot anchor is known yet; bootstrap staging is required.";
            return true;
        }

        controlReason = PotControlReason.None;
        reason = "No pot work is needed right now.";
        return false;
    }

    public bool Start()
    {
        if (!configuration.EnablePotFarming)
        {
            SetFailure("Pot farming start blocked because pot farming is disabled.");
            return false;
        }

        if (IsRunning)
        {
            return true;
        }

        if (!scanner.Snapshot.IsInSouthHorn)
        {
            SetFailure("Pot farming requires South Horn.");
            return false;
        }

        lock (gate)
        {
            pendingStop = false;
            resumeBootstrapAfterRecovery = false;
            completionResultAfterRecovery = PotFarmRunResult.None;
            currentPotId = 0;
            currentPotName = string.Empty;
            currentPotCenter = Vector3.Zero;
            hasCurrentPotCenter = false;
            treasurePotId = 0;
            treasurePotName = string.Empty;
            treasurePotCenter = Vector3.Zero;
            hasTreasurePotContext = false;
            waitDeadlineAt = DateTimeOffset.MinValue;
            treasureBuffWaitDeadlineAt = DateTimeOffset.MinValue;
            treasureHintDeadlineAt = DateTimeOffset.MinValue;
            lastTreasureElixirAttemptAt = DateTimeOffset.MinValue;
            leaveRequestedAt = DateTimeOffset.MinValue;
            stateEnteredAt = DateTimeOffset.MinValue;
            treasureElixirAttemptCount = 0;
            leavePending = false;
            lastError = string.Empty;
            lastResult = PotFarmRunResult.None;
            lastInstanceTimeDecision = new();
        }

        TransitionTo(PotFarmState.Bootstrapping, "Starting pot farm control.");
        return true;
    }

    public void Stop(string reason)
    {
        lock (gate)
        {
            pendingStop = true;
        }

        if (fateAutomationController.IsRunning && currentPotId != 0)
        {
            fateAutomationController.Stop(reason);
        }

        treasureHintTracker.CompleteCurrentTreasureSession($"Pot farm stopped: {reason}", TreasureSessionState.Abandoned);
        if (treasureSearchController.State != TreasureSearchState.Idle)
        {
            treasureSearchController.Stop(reason);
        }

        if (cofferInteractionController.IsRunning)
        {
            cofferInteractionController.Stop(reason);
        }

        dangerousTreasureTravelController.RestoreFateGearset($"pot farm stop: {reason}");
        ClearLeavePending();
        ClearTreasurePotContext();
        movementController.Stop(reason);
        TransitionTo(PotFarmState.Stopped, reason, error: reason, result: PotFarmRunResult.Stopped);
        logger.Info($"Pot farm stopped: {reason}");
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = PotFarmState.Idle;
            lastResult = PotFarmRunResult.None;
            lastTransition = "Idle";
            lastError = string.Empty;
            currentPotName = string.Empty;
            currentPotId = 0;
            currentPotCenter = Vector3.Zero;
            hasCurrentPotCenter = false;
            treasurePotName = string.Empty;
            treasurePotId = 0;
            treasurePotCenter = Vector3.Zero;
            hasTreasurePotContext = false;
            waitDeadlineAt = DateTimeOffset.MinValue;
            treasureBuffWaitDeadlineAt = DateTimeOffset.MinValue;
            treasureHintDeadlineAt = DateTimeOffset.MinValue;
            lastTreasureElixirAttemptAt = DateTimeOffset.MinValue;
            leaveRequestedAt = DateTimeOffset.MinValue;
            stateEnteredAt = DateTimeOffset.MinValue;
            treasureElixirAttemptCount = 0;
            leavePending = false;
            pendingStop = false;
            resumeBootstrapAfterRecovery = false;
            completionResultAfterRecovery = PotFarmRunResult.None;
            lastInstanceTimeDecision = new();
        }

        logger.Info($"Pot farm reset: {reason}");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        if (IsRunning)
        {
            Stop("Pot farm disposal");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var currentState = State;
        if (currentState is PotFarmState.Idle or PotFarmState.Stopped or PotFarmState.Completed or PotFarmState.Failed)
        {
            return;
        }

        var scannerSnapshot = scanner.Snapshot;
        if (leavePending)
        {
            if (!scannerSnapshot.IsInSouthHorn)
            {
                ClearLeavePending();
                TransitionTo(PotFarmState.Completed, "Left South Horn after an instance-time leave request.", result: PotFarmRunResult.LeftContent);
                return;
            }

            if (LeaveRequestedAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - LeaveRequestedAt >= LeaveTransitionTimeout)
            {
                ClearLeavePending();
                TransitionTo(PotFarmState.WaitingForPredictedWindow, "Instanced-content leave request did not produce a territory transition yet; will retry while holding pot control.");
                return;
            }

            logger.DebugThrottled("pot-instance-leave", WaitLogInterval, $"Waiting for instanced-content leave transition. requestedAt={LeaveRequestedAt:O}.");
            return;
        }

        if (!scannerSnapshot.IsInSouthHorn)
        {
            SetFailure("Left South Horn while pot farm control was active.");
            return;
        }

        if (currentState != PotFarmState.RunningPotFate && scannerSnapshot.ActivePotFate != null)
        {
            StartActivePotFate(scannerSnapshot.ActivePotFate);
            return;
        }

        if (currentState is not PotFarmState.RunningPotFate
            and not PotFarmState.WaitingForTreasureBuff
            and not PotFarmState.MovingNearTreasureCenter
            and not PotFarmState.TreasurePending
            and not PotFarmState.RunningTreasureSearch
            && scannerSnapshot.HasTreasureBuff
            && hasTreasurePotContext)
        {
            TransitionTo(PotFarmState.TreasurePending, $"Treasure phase is pending with {scannerSnapshot.TreasureBuffRemainingSeconds:0}s remaining on Cache Me If You Can.");
            return;
        }

        switch (currentState)
        {
            case PotFarmState.Bootstrapping:
                TickBootstrapping();
                break;
            case PotFarmState.WaitingForPredictedWindow:
                TickWaitingForPredictedWindow();
                break;
            case PotFarmState.TravelingToSpawn:
                TickTravelingToSpawn();
                break;
            case PotFarmState.WaitingAtSpawn:
                TickWaitingAtSpawn();
                break;
            case PotFarmState.RunningPotFate:
                TickRunningPotFate();
                break;
            case PotFarmState.WaitingForTreasureBuff:
                TickWaitingForTreasureBuff();
                break;
            case PotFarmState.MovingNearTreasureCenter:
                TickMovingNearTreasureCenter();
                break;
            case PotFarmState.TreasurePending:
                TickTreasurePending();
                break;
            case PotFarmState.RunningTreasureSearch:
                TickRunningTreasureSearch();
                break;
            case PotFarmState.RunningCofferInteraction:
                TickRunningCofferInteraction();
                break;
            case PotFarmState.RecoveringToBase:
                TickRecoveringToBase();
                break;
        }
    }

    private void TickBootstrapping()
    {
        if (TryHandleInstanceTimeManagement("before starting the next pot cycle", PotFarmState.WaitingForPredictedWindow))
        {
            return;
        }

        var snapshot = potCycleTracker.Snapshot;
        if (snapshot.HasPredictedNextPot)
        {
            var departureAt = GetDepartureAt(snapshot);
            if (departureAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= departureAt)
            {
                BeginPredictedStaging(snapshot);
                return;
            }

            TransitionTo(PotFarmState.WaitingForPredictedWindow, snapshot.HasPredictedNextPot
                ? $"Waiting for predicted pot departure window for {snapshot.PredictedNextPotFateName}."
                : "Waiting for predicted pot departure window.");
            return;
        }

        if (TryBeginConfiguredBootstrapStaging())
        {
            return;
        }

        TransitionTo(PotFarmState.WaitingForPredictedWindow, "Waiting for the first observed pot anchor.");
    }

    private void TickWaitingForPredictedWindow()
    {
        if (TryHandleInstanceTimeManagement("while waiting for the next pot cycle", PotFarmState.WaitingForPredictedWindow))
        {
            return;
        }

        var snapshot = potCycleTracker.Snapshot;
        if (!snapshot.HasPredictedNextPot)
        {
            logger.DebugThrottled("pot-bootstrap-wait", WaitLogInterval, "Pot farm is waiting for the first observed pot anchor.");
            return;
        }

        var departureAt = GetDepartureAt(snapshot);
        if (departureAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= departureAt)
        {
            logger.ResetThrottle("pot-bootstrap-wait");
            BeginPredictedStaging(snapshot);
            return;
        }

        logger.DebugThrottled("pot-bootstrap-wait", WaitLogInterval, $"Pot farm is waiting for predicted departure to {snapshot.PredictedNextPotFateName} at {departureAt:O}.");
    }

    private void TickTravelingToSpawn()
    {
        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached pot staging position.");
                TransitionTo(PotFarmState.WaitingAtSpawn, $"Waiting at pot spawn for {CurrentPotName}.");
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                SetFailure(movementController.LastError.Length == 0
                    ? "Pot staging movement failed."
                    : movementController.LastError);
                return;
        }

        logger.DebugThrottled("pot-traveling-spawn", WaitLogInterval, $"Pot farm is still traveling to spawn. MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
    }

    private void TickWaitingAtSpawn()
    {
        var potCycleSnapshot = potCycleTracker.Snapshot;
        if (WaitDeadlineAt == DateTimeOffset.MinValue
            && potCycleSnapshot.HasPredictedNextPot
            && potCycleSnapshot.PredictedNextPotFateId != 0
            && potCycleSnapshot.PredictedNextPotFateId != currentPotId)
        {
            var departureAt = GetDepartureAt(potCycleSnapshot);
            if (departureAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= departureAt)
            {
                logger.ResetThrottle("pot-wait-spawn");
                BeginPredictedStaging(potCycleSnapshot);
                return;
            }
        }

        if (WaitDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= WaitDeadlineAt)
        {
            logger.ResetThrottle("pot-wait-spawn");
            potCycleTracker.Reset($"Predicted pot {CurrentPotName} did not appear before the wait window expired.");
            BeginRecoveryToBase("Predicted pot did not appear; returning to Base Camp.", resumeBootstrapAfterRecovery: true, completionResult: PotFarmRunResult.None);
            return;
        }

        logger.DebugThrottled("pot-wait-spawn", WaitLogInterval, $"Pot farm is waiting at spawn for {CurrentPotName}. deadline={WaitDeadlineAt:O}.");
    }

    private void TickRunningPotFate()
    {
        if (fateAutomationController.IsRunning)
        {
            logger.DebugThrottled("pot-running-fate", WaitLogInterval, $"Pot farm is still running {CurrentPotName}. FateState={fateAutomationController.State}.");
            return;
        }

        logger.ResetThrottle("pot-running-fate");
        switch (fateAutomationController.LastResult)
        {
            case AutomationRunResult.Completed:
                BeginTreasureBuffWait();
                return;
            case AutomationRunResult.Stopped when pendingStop:
                TransitionTo(PotFarmState.Stopped, "Pot farm stop completed.", error: LastError, result: PotFarmRunResult.Stopped);
                return;
            default:
                SetFailure(fateAutomationController.LastError.Length == 0
                    ? fateAutomationController.LastTransition
                    : fateAutomationController.LastError);
                return;
        }
    }

    private void TickWaitingForTreasureBuff()
    {
        var scannerSnapshot = scanner.Snapshot;
        if (scannerSnapshot.HasTreasureBuff)
        {
            logger.ResetThrottle("pot-waiting-treasure-buff");
            if (!BeginTreasureCenterApproach())
            {
                return;
            }

            return;
        }

        if (treasureBuffWaitDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= treasureBuffWaitDeadlineAt)
        {
            logger.ResetThrottle("pot-waiting-treasure-buff");
            BeginRecoveryToBase(
                $"Treasure buff did not appear within {TreasureBuffWaitTimeout.TotalSeconds:0}s after {CurrentPotName} completed; returning to Base Camp.",
                resumeBootstrapAfterRecovery: false,
                completionResult: PotFarmRunResult.Completed);
            return;
        }

        logger.DebugThrottled(
            "pot-waiting-treasure-buff",
            WaitLogInterval,
            $"Pot FATE {CurrentPotName} completed; waiting briefly for treasure buff until {treasureBuffWaitDeadlineAt:O}.");
    }

    private void TickRecoveringToBase()
    {
        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Pot recovery completed.");
                if (resumeBootstrapAfterRecovery)
                {
                    TransitionTo(PotFarmState.Bootstrapping, "Pot recovery completed; resuming bootstrap.");
                    return;
                }

                TransitionTo(PotFarmState.Completed, "Pot recovery completed.", result: completionResultAfterRecovery);
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                SetFailure(movementController.LastError.Length == 0
                    ? "Pot recovery failed."
                    : movementController.LastError);
                return;
        }

        logger.DebugThrottled("pot-recovering", WaitLogInterval, $"Pot farm is still recovering to Base Camp. MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
    }

    private void TickMovingNearTreasureCenter()
    {
        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached completed pot FATE center.");
                if (!TryUseMagicalElixir($"Moved near completed FATE center for {treasurePotName} ({treasurePotId}) at <{treasurePotCenter.X:0.0}, {treasurePotCenter.Y:0.0}, {treasurePotCenter.Z:0.0}>."))
                {
                    return;
                }

                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                SetFailure(movementController.LastError.Length == 0
                    ? $"Failed to move near the completed FATE center for {treasurePotName}."
                    : movementController.LastError);
                return;
        }

        logger.DebugThrottled("pot-moving-treasure-center", WaitLogInterval, $"Pot farm is still moving near the completed FATE center for {treasurePotName} ({treasurePotId}) at <{treasurePotCenter.X:0.0}, {treasurePotCenter.Y:0.0}, {treasurePotCenter.Z:0.0}>. MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
    }

    private void BeginTreasureBuffWait()
    {
        lock (gate)
        {
            treasureBuffWaitDeadlineAt = DateTimeOffset.UtcNow + TreasureBuffWaitTimeout;
        }

        TransitionTo(PotFarmState.WaitingForTreasureBuff, $"Pot FATE {CurrentPotName} completed; waiting briefly for treasure buff.");
    }

    private void TickTreasurePending()
    {
        var scannerSnapshot = scanner.Snapshot;
        if (scannerSnapshot.ActivePotFate != null)
        {
            StartActivePotFate(scannerSnapshot.ActivePotFate);
            return;
        }

        if (!scannerSnapshot.HasTreasureBuff)
        {
            logger.ResetThrottle("pot-treasure-pending");
            if (treasureSearchController.State != TreasureSearchState.Idle)
            {
                treasureSearchController.Stop("Treasure buff expired before treasure search completed.");
            }

            if (cofferInteractionController.IsRunning)
            {
                cofferInteractionController.Stop("Treasure buff expired before coffer interaction completed.");
            }

            ClearTreasurePotContext();
            BeginRecoveryToBase("Treasure buff expired before treasure execution completed; returning to Base Camp.", resumeBootstrapAfterRecovery: false, completionResult: PotFarmRunResult.TreasurePending);
            return;
        }

        var treasureSnapshot = treasureHintTracker.Snapshot;
        if (!treasureSearchController.IsRunning && treasureSnapshot.HasInitialHint)
        {
            if (!treasureSearchController.Start(treasurePotId, treasurePotName))
            {
                if (treasureSearchController.LastResult == TreasureSearchRunResult.CandidatesExhausted)
                {
                    ClearTreasurePotContext();
                    BeginRecoveryToBase(
                        "Treasure traversal exhausted all mapped candidates while starting the selected treasure group; returning to Base Camp.",
                        resumeBootstrapAfterRecovery: false,
                        completionResult: PotFarmRunResult.Completed);
                    return;
                }

                SetFailure(treasureSearchController.LastError.Length == 0
                    ? "Failed to start treasure candidate traversal."
                    : treasureSearchController.LastError);
                return;
            }

            logger.ResetThrottle("pot-treasure-pending");
            TransitionTo(PotFarmState.RunningTreasureSearch, treasureSearchController.LastTransition);
            return;
        }

        if (treasureElixirAttemptCount == 0)
        {
            if (!TryUseMagicalElixir($"Treasure phase is active for {treasurePotName} ({treasurePotId}) but no Magical Elixir attempt has been recorded yet."))
            {
                return;
            }

            return;
        }

        if (treasureHintDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= treasureHintDeadlineAt)
        {
            logger.ResetThrottle("pot-treasure-pending");
            if (treasureElixirAttemptCount >= MaximumTreasureElixirAttempts)
            {
                treasureHintTracker.CompleteCurrentTreasureSession(
                    $"No initial treasure hint arrived after {treasureElixirAttemptCount} Magical Elixir attempt(s).",
                    TreasureSessionState.Abandoned);
                ClearTreasurePotContext();
                BeginRecoveryToBase(
                    $"No initial treasure hint arrived after {treasureElixirAttemptCount} Magical Elixir attempt(s); returning to Base Camp.",
                    resumeBootstrapAfterRecovery: false,
                    completionResult: PotFarmRunResult.TreasurePending);
                return;
            }

            if (!TryUseMagicalElixir($"No treasure hint arrived after Magical Elixir attempt {treasureElixirAttemptCount}; retrying."))
            {
                return;
            }

            return;
        }

        logger.DebugThrottled(
            "pot-treasure-pending",
            WaitLogInterval,
            $"Treasure phase is holding farm-session fallback. Cache Me If You Can remains active for {scannerSnapshot.TreasureBuffRemainingSeconds:0}s. elixirAttempts={treasureElixirAttemptCount}/{MaximumTreasureElixirAttempts} hintDeadline={treasureHintDeadlineAt:O} session={treasureSnapshot.SessionState} sessionId={treasureSnapshot.SessionId} revision={treasureSnapshot.Revision} hint={treasureSnapshot.GetHintSummary()}.");
    }

    private void TickRunningTreasureSearch()
    {
        var scannerSnapshot = scanner.Snapshot;
        if (!scannerSnapshot.HasTreasureBuff)
        {
            treasureSearchController.Stop("Treasure buff expired during treasure traversal.");
            ClearTreasurePotContext();
            BeginRecoveryToBase("Treasure buff expired during treasure traversal; returning to Base Camp.", resumeBootstrapAfterRecovery: false, completionResult: PotFarmRunResult.TreasurePending);
            return;
        }

        if (treasureSearchController.IsRunning)
        {
            logger.DebugThrottled(
                "pot-treasure-search",
                WaitLogInterval,
                $"Treasure traversal is active. group={treasureSearchController.ActiveGroupKey} candidate={treasureSearchController.ActiveCandidateKey?.Label ?? "none"} index={treasureSearchController.CurrentCandidateIndex} handoff={treasureSearchController.LastHandoffReason}.");
            return;
        }

        logger.ResetThrottle("pot-treasure-search");
        switch (treasureSearchController.LastResult)
        {
            case TreasureSearchRunResult.ReadyForInteraction:
                var activeMatch = treasureSearchController.ActiveVisibleCofferMatch;
                if (activeMatch == null)
                {
                    SetFailure("Treasure search reported a ready coffer interaction without an active coffer match.");
                    return;
                }

                if (!cofferInteractionController.IsRunning && !cofferInteractionController.Start(activeMatch))
                {
                    switch (cofferInteractionController.LastResult)
                    {
                        case CofferInteractionResult.LostCoffer:
                            logger.Warning($"Matched coffer for candidate {treasureSearchController.ActiveCandidateKey?.Label ?? "unknown"} vanished before interaction started; resuming candidate traversal.");
                            if (!treasureSearchController.StartNextCandidateAfterInteractionLoss(cofferInteractionController.LastTransition))
                            {
                                if (treasureSearchController.LastResult == TreasureSearchRunResult.CandidatesExhausted)
                                {
                                    ClearTreasurePotContext();
                                    BeginRecoveryToBase(
                                        "Matched coffer vanished before interaction and no more mapped candidates remained; returning to Base Camp.",
                                        resumeBootstrapAfterRecovery: false,
                                        completionResult: PotFarmRunResult.Completed);
                                    return;
                                }

                                SetFailure(treasureSearchController.LastError.Length == 0
                                    ? "Failed to continue treasure traversal after losing the matched coffer."
                                    : treasureSearchController.LastError);
                                return;
                            }

                            TransitionTo(PotFarmState.RunningTreasureSearch, treasureSearchController.LastTransition);
                            return;
                        default:
                            SetFailure(cofferInteractionController.LastError.Length == 0
                                ? "Failed to start coffer interaction."
                                : cofferInteractionController.LastError);
                            return;
                    }
                }

                TransitionTo(PotFarmState.RunningCofferInteraction, cofferInteractionController.LastTransition);
                return;
            case TreasureSearchRunResult.CandidatesExhausted:
                ClearTreasurePotContext();
                BeginRecoveryToBase(
                    "Treasure traversal exhausted all mapped candidates; returning to Base Camp.",
                    resumeBootstrapAfterRecovery: false,
                    completionResult: PotFarmRunResult.Completed);
                return;
            case TreasureSearchRunResult.Stopped when pendingStop:
                TransitionTo(PotFarmState.Stopped, "Pot farm stop completed.", error: LastError, result: PotFarmRunResult.Stopped);
                return;
            default:
                SetFailure(treasureSearchController.LastError.Length == 0
                    ? treasureSearchController.LastTransition
                    : treasureSearchController.LastError);
                return;
        }
    }

    private void TickRunningCofferInteraction()
    {
        var scannerSnapshot = scanner.Snapshot;
        if (!scannerSnapshot.HasTreasureBuff)
        {
            cofferInteractionController.Stop("Treasure buff expired during coffer interaction.");
            ClearTreasurePotContext();
            BeginRecoveryToBase("Treasure buff expired during coffer interaction; returning to Base Camp.", resumeBootstrapAfterRecovery: false, completionResult: PotFarmRunResult.TreasurePending);
            return;
        }

        if (cofferInteractionController.IsRunning)
        {
            logger.DebugThrottled(
                "pot-coffer-interaction",
                WaitLogInterval,
                $"Coffer interaction is active. state={cofferInteractionController.State} attempts={cofferInteractionController.InteractionAttemptCount} deadline={cofferInteractionController.ConfirmationDeadlineAt:O} candidate={treasureSearchController.ActiveCandidateKey?.Label ?? "none"}.");
            return;
        }

        logger.ResetThrottle("pot-coffer-interaction");
        switch (cofferInteractionController.LastResult)
        {
            case CofferInteractionResult.Opened:
                treasureHintTracker.CompleteCurrentTreasureSession("Treasure coffer opened successfully.", TreasureSessionState.Completed);
                ClearTreasurePotContext();
                BeginRecoveryToBase(
                    $"Opened treasure coffer for candidate {treasureSearchController.ActiveCandidateKey?.Label ?? "unknown"}; returning to Base Camp.",
                    resumeBootstrapAfterRecovery: false,
                    completionResult: PotFarmRunResult.Completed);
                return;
            case CofferInteractionResult.LostCoffer:
            case CofferInteractionResult.TimedOut:
                if (!treasureSearchController.StartNextCandidateAfterInteractionLoss(cofferInteractionController.LastTransition))
                {
                    if (treasureSearchController.LastResult != TreasureSearchRunResult.CandidatesExhausted)
                    {
                        SetFailure(treasureSearchController.LastError.Length == 0
                            ? cofferInteractionController.LastTransition
                            : treasureSearchController.LastError);
                        return;
                    }

                    ClearTreasurePotContext();
                    BeginRecoveryToBase(
                        "Treasure coffer interaction failed and no more mapped candidates remained; returning to Base Camp.",
                        resumeBootstrapAfterRecovery: false,
                        completionResult: PotFarmRunResult.Completed);
                    return;
                }

                TransitionTo(PotFarmState.RunningTreasureSearch, treasureSearchController.LastTransition);
                return;
            case CofferInteractionResult.Stopped when pendingStop:
                TransitionTo(PotFarmState.Stopped, "Pot farm stop completed.", error: LastError, result: PotFarmRunResult.Stopped);
                return;
            default:
                SetFailure(cofferInteractionController.LastError.Length == 0
                    ? cofferInteractionController.LastTransition
                    : cofferInteractionController.LastError);
                return;
        }
    }

    private bool TryBeginConfiguredBootstrapStaging()
    {
        var configuredPot = configuration.StartingPotFate switch
        {
            StartingPotFateMode.PersistentPots => potFatesById.Values.FirstOrDefault(pot => string.Equals(pot.Name, "Persistent Pots", StringComparison.OrdinalIgnoreCase)),
            StartingPotFateMode.PleadingPots => potFatesById.Values.FirstOrDefault(pot => string.Equals(pot.Name, "Pleading Pots", StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };

        if (configuredPot == null)
        {
            return false;
        }

        return BeginTravelToPotLocation(configuredPot, "configured starting pot", waitUntil: DateTimeOffset.MinValue);
    }

    private void BeginPredictedStaging(PotCycleSnapshot snapshot)
    {
        if (!potFatesById.TryGetValue(snapshot.PredictedNextPotFateId, out var potFate))
        {
            SetFailure($"Predicted pot FATE {snapshot.PredictedNextPotFateId} is missing from data.");
            return;
        }

        BeginTravelToPotLocation(potFate, "predicted pot", snapshot.PredictedNextSpawnAt + PotSpawnGrace);
    }

    private bool BeginTravelToPotLocation(PotFateData potFate, string context, DateTimeOffset waitUntil)
    {
        var stagingCenter = potFate.StagingPosition?.ToVector3() ?? potFate.CenterPosition.ToVector3();
        var destination = TrySelectPotWaitPoint(stagingCenter, out var randomWaitPoint)
            ? randomWaitPoint
            : stagingCenter;
        var arrivalTolerance = destination == stagingCenter
            ? Math.Max(1, configuration.SpawnArrivalRadius)
            : PotWaitPointStopDistance;
        var description = $"Stage for {potFate.Name} ({context})";
        if (!movementController.PlanRouteToLocation(description, potFate.PreferredAethernet, destination, arrivalTolerance))
        {
            SetFailure(movementController.LastError.Length == 0
                ? $"Failed to plan pot staging route for {potFate.Name}."
                : movementController.LastError);
            return false;
        }

        if (!movementController.StartPlannedRoute())
        {
            SetFailure(movementController.LastError.Length == 0
                ? $"Failed to start pot staging route for {potFate.Name}."
                : movementController.LastError);
            return false;
        }

        lock (gate)
        {
            currentPotId = potFate.FateId;
            currentPotName = potFate.Name;
            currentPotCenter = potFate.CenterPosition.ToVector3();
            hasCurrentPotCenter = true;
            waitDeadlineAt = waitUntil;
        }

        TransitionTo(PotFarmState.TravelingToSpawn, $"Traveling to {context} staging for {potFate.Name}.");
        if (destination != stagingCenter)
        {
            logger.Info($"Selected randomized pot wait point for {potFate.Name} at <{destination.X:0.000}, {destination.Y:0.000}, {destination.Z:0.000}> around <{stagingCenter.X:0.000}, {stagingCenter.Y:0.000}, {stagingCenter.Z:0.000}>.");
        }

        return true;
    }

    private void StartActivePotFate(ActivePotFate activePotFate)
    {
        ClearTreasurePotContext();

        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop($"Active pot FATE detected: {activePotFate.Name}.");
        }

        dangerousTreasureTravelController.RestoreFateGearset($"starting pot FATE {activePotFate.Name}");

        if (!fateAutomationController.Start(activePotFate, FateRunCompletionBehavior.CompleteInPlace))
        {
            SetFailure(fateAutomationController.LastError.Length == 0
                ? $"Failed to start pot FATE execution for {activePotFate.Name}."
                : fateAutomationController.LastError);
            return;
        }

        lock (gate)
        {
            currentPotId = activePotFate.Id;
            currentPotName = activePotFate.Name;
            currentPotCenter = activePotFate.CenterPosition;
            hasCurrentPotCenter = true;
            waitDeadlineAt = DateTimeOffset.MinValue;
        }

        logger.ResetThrottle("pot-bootstrap-wait");
        logger.ResetThrottle("pot-traveling-spawn");
        logger.ResetThrottle("pot-wait-spawn");
        TransitionTo(PotFarmState.RunningPotFate, $"Running pot FATE {activePotFate.Name} ({activePotFate.Id}).");
    }

    private bool BeginTreasureCenterApproach()
    {
        if (!hasCurrentPotCenter)
        {
            SetFailure($"Treasure hunt could not start after {CurrentPotName} because the completed pot center was not captured at FATE start.");
            return false;
        }

        var destination = movementController.FindNearestNavigablePoint(currentPotCenter, halfExtentXZ: 5f, halfExtentY: 5f) ?? currentPotCenter;
        if (!movementController.StartDirectMove($"Move near completed FATE center for {CurrentPotName}", destination, TreasureCenterArrivalTolerance))
        {
            SetFailure(movementController.LastError.Length == 0
                ? $"Failed to start movement near the completed FATE center for {CurrentPotName}."
                : movementController.LastError);
            return false;
        }

        lock (gate)
        {
            treasurePotId = currentPotId;
            treasurePotName = currentPotName;
            treasurePotCenter = currentPotCenter;
            hasTreasurePotContext = true;
            treasureBuffWaitDeadlineAt = DateTimeOffset.MinValue;
            treasureHintDeadlineAt = DateTimeOffset.MinValue;
            lastTreasureElixirAttemptAt = DateTimeOffset.MinValue;
            treasureElixirAttemptCount = 0;
        }

        logger.ResetThrottle("pot-running-fate");
        TransitionTo(PotFarmState.MovingNearTreasureCenter, $"Moving near completed FATE center for {CurrentPotName} before using Magical Elixir.");
        return true;
    }

    private void BeginRecoveryToBase(string reason, bool resumeBootstrapAfterRecovery, PotFarmRunResult completionResult)
    {
        dangerousTreasureTravelController.RestoreFateGearset($"pot recovery: {reason}");

        if (!movementController.RecoverToBaseCamp())
        {
            if (configuration.UseReturn && movementController.RecoverToBaseCamp(allowReturn: false))
            {
                lock (gate)
                {
                    this.resumeBootstrapAfterRecovery = resumeBootstrapAfterRecovery;
                    completionResultAfterRecovery = completionResult;
                }

                TransitionTo(PotFarmState.RecoveringToBase, reason);
                return;
            }

            SetFailure(movementController.LastError.Length == 0
                ? "Failed to start pot recovery to Base Camp."
                : movementController.LastError);
            return;
        }

        lock (gate)
        {
            this.resumeBootstrapAfterRecovery = resumeBootstrapAfterRecovery;
            completionResultAfterRecovery = completionResult;
        }

        TransitionTo(PotFarmState.RecoveringToBase, reason);
    }

    private DateTimeOffset GetDepartureAt(PotCycleSnapshot snapshot)
    {
        if (!snapshot.HasPredictedNextPot)
        {
            return DateTimeOffset.MinValue;
        }

        return snapshot.PredictedNextSpawnAt - TimeSpan.FromMinutes(Math.Max(0, configuration.SpawnLeadMinutes));
    }

    private void SetFailure(string reason)
    {
        treasureHintTracker.CompleteCurrentTreasureSession($"Pot farm failed: {reason}", TreasureSessionState.Abandoned);
        if (treasureSearchController.State != TreasureSearchState.Idle)
        {
            treasureSearchController.Stop(reason);
        }

        if (cofferInteractionController.IsRunning)
        {
            cofferInteractionController.Stop(reason);
        }

        dangerousTreasureTravelController.RestoreFateGearset($"pot farm failure: {reason}");
        ClearLeavePending();
        ClearTreasurePotContext();
        movementController.Stop(reason);
        TransitionTo(PotFarmState.Failed, reason, error: reason, result: PotFarmRunResult.Failed);
        logger.Warning(reason);
    }

    private void ClearTreasurePotContext()
    {
        lock (gate)
        {
            treasurePotId = 0;
            treasurePotName = string.Empty;
            treasurePotCenter = Vector3.Zero;
            hasTreasurePotContext = false;
            treasureBuffWaitDeadlineAt = DateTimeOffset.MinValue;
            treasureHintDeadlineAt = DateTimeOffset.MinValue;
            lastTreasureElixirAttemptAt = DateTimeOffset.MinValue;
            treasureElixirAttemptCount = 0;
        }
    }

    private bool TryUseMagicalElixir(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (lastTreasureElixirAttemptAt != DateTimeOffset.MinValue && now - lastTreasureElixirAttemptAt < TreasureElixirRetryDelay)
        {
            logger.DebugThrottled(
                "pot-treasure-elixir-retry",
                TimeSpan.FromMilliseconds(250),
                $"Treasure elixir retry is waiting for {TreasureElixirRetryDelay.TotalSeconds:0.0}s between attempts. attempts={treasureElixirAttemptCount}/{MaximumTreasureElixirAttempts}.");
            return true;
        }

        if (!gameActionController.TryUseMagicalElixir("pot treasure startup"))
        {
            SetFailure($"Failed to use Magical Elixir during pot treasure startup.");
            return false;
        }

        lock (gate)
        {
            treasureElixirAttemptCount++;
            lastTreasureElixirAttemptAt = now;
            treasureHintDeadlineAt = now + TreasureHintWaitTimeout;
        }

        logger.ResetThrottle("pot-treasure-pending");
        logger.ResetThrottle("pot-treasure-elixir-retry");
        TransitionTo(
            PotFarmState.TreasurePending,
            $"{reason} Used Magical Elixir; waiting for an initial treasure hint (attempt {treasureElixirAttemptCount}/{MaximumTreasureElixirAttempts}).");
        return true;
    }

    private PotInstanceTimeDecision EvaluateInstanceTimeDecision(DateTimeOffset now)
    {
        var hasContentTimer = instancedContentController.TryGetContentTimeLeftSeconds(out var remainingSeconds);
        var decision = potInstanceTimeEvaluator.Evaluate(scanner.Snapshot, potCycleTracker.Snapshot, now, remainingSeconds, hasContentTimer);
        decision.CanLeaveCurrentContent = decision.ShouldAttemptLeave && instancedContentController.CanLeaveCurrentContent();

        lock (gate)
        {
            lastInstanceTimeDecision = decision;
        }

        return decision;
    }

    private bool TryHandleInstanceTimeManagement(string context, PotFarmState holdState)
    {
        var decision = EvaluateInstanceTimeDecision(DateTimeOffset.UtcNow);
        if (!decision.ManageInstanceTimeEnabled || !decision.IsContentTimerAvailable || decision.AllowNextPotCycle)
        {
            logger.ResetThrottle("pot-instance-time");
            return false;
        }

        if (!decision.CanLeaveCurrentContent)
        {
            var holdReason = $"{decision.Reason} The content cannot be left yet, so pot control is holding.";
            if (State != holdState || !string.Equals(LastTransition, holdReason, StringComparison.Ordinal))
            {
                TransitionTo(holdState, holdReason);
            }

            logger.DebugThrottled("pot-instance-time", WaitLogInterval, decision.Reason);
            return true;
        }

        movementController.Stop($"Instance-time management triggered while {context}.");
        if (!instancedContentController.TryLeaveCurrentContent(context))
        {
            var retryReason = $"{decision.Reason} Leave request failed and will be retried.";
            if (State != holdState || !string.Equals(LastTransition, retryReason, StringComparison.Ordinal))
            {
                TransitionTo(holdState, retryReason);
            }

            logger.DebugThrottled("pot-instance-time", WaitLogInterval, decision.Reason);
            return true;
        }

        lock (gate)
        {
            leavePending = true;
            leaveRequestedAt = DateTimeOffset.UtcNow;
            lastInstanceTimeDecision = decision;
            lastInstanceTimeDecision.Reason = $"{decision.Reason} Leave request issued; waiting for a territory transition.";
        }

        logger.ResetThrottle("pot-instance-time");
        logger.ResetThrottle("pot-instance-leave");
        TransitionTo(holdState, LastInstanceTimeDecision.Reason);
        return true;
    }

    private void ClearLeavePending()
    {
        lock (gate)
        {
            leavePending = false;
            leaveRequestedAt = DateTimeOffset.MinValue;
        }
    }

    private bool TrySelectPotWaitPoint(Vector3 center, out Vector3 waitPoint)
    {
        var maxRadius = MathF.Max(10f, configuration.SpawnArrivalRadius);
        var minRadius = MathF.Max(3f, MathF.Min(maxRadius * 0.35f, maxRadius - 1f));
        var candidates = new Vector3[PotWaitPointCandidateCount];
        var validCount = 0;

        for (var index = 0; index < PotWaitPointCandidateCount; index++)
        {
            var candidate = CreateRandomRingPoint(center, minRadius, maxRadius);
            var snappedCandidate = movementController.FindNearestNavigablePoint(candidate, 5f, 5f);
            if (!snappedCandidate.HasValue)
            {
                continue;
            }

            var snappedDistance = CalculateFlatDistance(snappedCandidate.Value, center);
            if (snappedDistance < minRadius || snappedDistance > maxRadius)
            {
                continue;
            }

            candidates[validCount++] = snappedCandidate.Value;
        }

        if (validCount == 0)
        {
            waitPoint = default;
            return false;
        }

        waitPoint = candidates[Random.Shared.Next(validCount)];
        return true;
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

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private void TransitionTo(PotFarmState nextState, string reason, string? error = null, PotFarmRunResult? result = null)
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
            else if (nextState is not PotFarmState.Failed and not PotFarmState.Stopped)
            {
                lastError = string.Empty;
            }

            if (result.HasValue)
            {
                lastResult = result.Value;
            }
        }

        logger.Info($"Pot farm state -> {nextState}: {reason}");
    }
}
