using System;
using System.Collections.Generic;
using System.Linq;

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

    private readonly IFramework framework;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly FateAutomationController fateAutomationController;
    private readonly PotCycleTracker potCycleTracker;
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
    private DateTimeOffset waitDeadlineAt = DateTimeOffset.MinValue;
    private DateTimeOffset stateEnteredAt = DateTimeOffset.MinValue;
    private bool pendingStop;
    private bool resumeBootstrapAfterRecovery;
    private PotFarmRunResult completionResultAfterRecovery;

    public PotFarmController(
        IFramework framework,
        OccultCrescentScanner scanner,
        MovementController movementController,
        FateAutomationController fateAutomationController,
        PotCycleTracker potCycleTracker,
        OccultCrescentData data,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.scanner = scanner;
        this.movementController = movementController;
        this.fateAutomationController = fateAutomationController;
        this.potCycleTracker = potCycleTracker;
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

        if (scannerSnapshot.HasTreasureBuff)
        {
            controlReason = PotControlReason.TreasurePending;
            reason = $"Treasure phase is pending with {scannerSnapshot.TreasureBuffRemainingSeconds:0}s remaining on Cache Me If You Can.";
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
            waitDeadlineAt = DateTimeOffset.MinValue;
            stateEnteredAt = DateTimeOffset.MinValue;
            lastError = string.Empty;
            lastResult = PotFarmRunResult.None;
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

        movementController.Stop(reason);
        TransitionTo(PotFarmState.Stopped, reason, error: reason, result: PotFarmRunResult.Stopped);
        logger.Info($"Pot farm stopped: {reason}");
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

        if (currentState != PotFarmState.TreasurePending && scannerSnapshot.HasTreasureBuff)
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
            case PotFarmState.TreasurePending:
                TickTreasurePending();
                break;
            case PotFarmState.RecoveringToBase:
                TickRecoveringToBase();
                break;
        }
    }

    private void TickBootstrapping()
    {
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
                var treasurePending = scanner.Snapshot.HasTreasureBuff;
                if (treasurePending)
                {
                    TransitionTo(PotFarmState.TreasurePending, $"Treasure hunt is pending after {CurrentPotName}, but treasure execution is not implemented yet.");
                    return;
                }

                BeginRecoveryToBase(
                    $"Pot FATE {CurrentPotName} completed; returning to Base Camp.",
                    resumeBootstrapAfterRecovery: false,
                    completionResult: PotFarmRunResult.Completed);
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
            BeginRecoveryToBase("Treasure buff expired before treasure execution was implemented; returning to Base Camp.", resumeBootstrapAfterRecovery: false, completionResult: PotFarmRunResult.TreasurePending);
            return;
        }

        logger.DebugThrottled("pot-treasure-pending", WaitLogInterval, $"Treasure phase is holding farm-session fallback. Cache Me If You Can remains active for {scannerSnapshot.TreasureBuffRemainingSeconds:0}s.");
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
        var destination = potFate.StagingPosition?.ToVector3() ?? potFate.CenterPosition.ToVector3();
        var arrivalTolerance = Math.Max(1, configuration.SpawnArrivalRadius);
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
            waitDeadlineAt = waitUntil;
        }

        TransitionTo(PotFarmState.TravelingToSpawn, $"Traveling to {context} staging for {potFate.Name}.");
        return true;
    }

    private void StartActivePotFate(ActivePotFate activePotFate)
    {
        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop($"Active pot FATE detected: {activePotFate.Name}.");
        }

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
            waitDeadlineAt = DateTimeOffset.MinValue;
        }

        logger.ResetThrottle("pot-bootstrap-wait");
        logger.ResetThrottle("pot-traveling-spawn");
        logger.ResetThrottle("pot-wait-spawn");
        TransitionTo(PotFarmState.RunningPotFate, $"Running pot FATE {activePotFate.Name} ({activePotFate.Id}).");
    }

    private void BeginRecoveryToBase(string reason, bool resumeBootstrapAfterRecovery, PotFarmRunResult completionResult)
    {
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
        movementController.Stop(reason);
        TransitionTo(PotFarmState.Failed, reason, error: reason, result: PotFarmRunResult.Failed);
        logger.Warning(reason);
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
