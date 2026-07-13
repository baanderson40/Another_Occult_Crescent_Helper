using System;
using AOCCH.IPC;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Plugin.Services;
using System.Threading;

namespace AOCCH.Automation;

public sealed class FarmSessionController : IDisposable
{
    private static int nextRunSequence;
    private enum InterruptedActivityKind
    {
        None,
        Ce,
        Fate,
        PotFate,
    }

    private static readonly TimeSpan IdleRescanInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);

    private readonly IFramework framework;
    private readonly OccultCrescentScanner scanner;
    private readonly VNavmeshIpc vnavmesh;
    private readonly LifestreamIpc lifestream;
    private readonly MovementController movementController;
    private readonly AutorotationController autorotationController;
    private readonly BuffRotationController buffRotationController;
    private readonly CriticalEngagementAutomationController criticalEngagementAutomationController;
    private readonly FateAutomationController fateAutomationController;
    private readonly DeathRecoveryController deathRecoveryController;
    private readonly PotCycleTracker potCycleTracker;
    private readonly PotFallbackWindowEvaluator potFallbackWindowEvaluator;
    private readonly PotFarmController potFarmController;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private FarmSessionState state = FarmSessionState.Idle;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private string currentActivity = "None";
    private string currentRunId = string.Empty;
    private DateTimeOffset lastIdleScanAt = DateTimeOffset.MinValue;
    private DateTimeOffset stateEnteredAt = DateTimeOffset.MinValue;
    private bool pendingStop;
    private bool recoverAfterBuffRotation;
    private bool runBuffRotationAfterRecovery;
    private InterruptedActivityKind interruptedActivity;
    private uint interruptedTargetId;
    private string interruptedTargetName = string.Empty;
    private FateRunCompletionBehavior interruptedFateCompletionBehavior = FateRunCompletionBehavior.RecoverToBase;

    public FarmSessionController(
        IFramework framework,
        OccultCrescentScanner scanner,
        VNavmeshIpc vnavmesh,
        LifestreamIpc lifestream,
        MovementController movementController,
        AutorotationController autorotationController,
        BuffRotationController buffRotationController,
        CriticalEngagementAutomationController criticalEngagementAutomationController,
        FateAutomationController fateAutomationController,
        DeathRecoveryController deathRecoveryController,
        PotCycleTracker potCycleTracker,
        PotFallbackWindowEvaluator potFallbackWindowEvaluator,
        PotFarmController potFarmController,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.scanner = scanner;
        this.vnavmesh = vnavmesh;
        this.lifestream = lifestream;
        this.movementController = movementController;
        this.autorotationController = autorotationController;
        this.buffRotationController = buffRotationController;
        this.criticalEngagementAutomationController = criticalEngagementAutomationController;
        this.fateAutomationController = fateAutomationController;
        this.deathRecoveryController = deathRecoveryController;
        this.potCycleTracker = potCycleTracker;
        this.potFallbackWindowEvaluator = potFallbackWindowEvaluator;
        this.potFarmController = potFarmController;
        this.configuration = configuration;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
    }

    public FarmSessionState State
    {
        get
        {
            lock (gate)
            {
                return state;
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

    public string CurrentActivity
    {
        get
        {
            lock (gate)
            {
                return currentActivity;
            }
        }
    }

    public bool IsRunning
        => State is not FarmSessionState.Idle
            and not FarmSessionState.Stopped
            and not FarmSessionState.Failed;

    public bool Start()
    {
        if (IsRunning)
        {
            logger.Warning("[Farm] op=start-ignored reason=already-running");
            return false;
        }

        if (configuration.ScannerOnlyMode)
        {
            SetFailure("Farm session start blocked because scanner-only mode is enabled.");
            return false;
        }

        if (criticalEngagementAutomationController.IsRunning || fateAutomationController.IsRunning || buffRotationController.IsRunning || potFarmController.IsRunning)
        {
            SetFailure("Stop CE/FATE automation, pot control, and buff rotation before starting the farm session.");
            return false;
        }

        lock (gate)
        {
            pendingStop = false;
            currentRunId = $"Farm#{Interlocked.Increment(ref nextRunSequence)}";
            lastError = string.Empty;
            lastIdleScanAt = DateTimeOffset.MinValue;
            recoverAfterBuffRotation = false;
            runBuffRotationAfterRecovery = false;
            interruptedActivity = InterruptedActivityKind.None;
            interruptedTargetId = 0;
            interruptedTargetName = string.Empty;
            interruptedFateCompletionBehavior = FateRunCompletionBehavior.RecoverToBase;
        }

        logger.Info($"{BuildLogTag()} op=start ceFarming={configuration.EnableCriticalEngagementFarming} fateFarming={configuration.EnableFateFarming} prioritizeCe={configuration.PrioritizeCe} fatePriority={configuration.FatePriority} useReturn={configuration.UseReturn} enableBuffRotation={configuration.EnableBuffRotation} scannerOnlyMode={configuration.ScannerOnlyMode} minimumMountingRange={configuration.MinimumMountingRange}.");
        TransitionTo(FarmSessionState.Starting, "Starting unified CE/FATE farm session.", "Startup");
        return true;
    }

    public void Stop(string reason)
    {
        lock (gate)
        {
            pendingStop = true;
            interruptedActivity = InterruptedActivityKind.None;
            interruptedTargetId = 0;
            interruptedTargetName = string.Empty;
            interruptedFateCompletionBehavior = FateRunCompletionBehavior.RecoverToBase;
        }

        if (criticalEngagementAutomationController.IsRunning)
        {
            criticalEngagementAutomationController.Stop(reason);
        }

        if (fateAutomationController.IsRunning)
        {
            fateAutomationController.Stop(reason);
        }

        if (potFarmController.IsRunning)
        {
            potFarmController.Stop(reason);
        }

        if (buffRotationController.IsRunning)
        {
            buffRotationController.Stop(reason);
        }

        movementController.Stop(reason);
        autorotationController.ReleaseOwnership(reason);
        TransitionTo(FarmSessionState.Stopped, reason, "Stopped", clearError: false);
        logger.Info($"{BuildLogTag()} op=stop state={State} reason={reason}");
    }

    public void PanicStop()
        => Stop("Farm panic stop requested.");

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = FarmSessionState.Idle;
            lastTransition = "Idle";
            lastError = string.Empty;
            currentActivity = "None";
            currentRunId = string.Empty;
            lastIdleScanAt = DateTimeOffset.MinValue;
            stateEnteredAt = DateTimeOffset.MinValue;
            pendingStop = false;
            recoverAfterBuffRotation = false;
            runBuffRotationAfterRecovery = false;
            interruptedActivity = InterruptedActivityKind.None;
            interruptedTargetId = 0;
            interruptedTargetName = string.Empty;
            interruptedFateCompletionBehavior = FateRunCompletionBehavior.RecoverToBase;
        }

        logger.Info($"[Farm] op=reset reason={reason}");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        if (IsRunning)
        {
            Stop("Farm session disposal");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var currentState = State;
        if (currentState is FarmSessionState.Idle or FarmSessionState.Stopped or FarmSessionState.Failed)
        {
            return;
        }

        if (deathRecoveryController.State == DeathRecoveryState.Failed)
        {
            SetFailure(deathRecoveryController.LastError.Length == 0
                ? "Death recovery failed during farm session."
                : deathRecoveryController.LastError);
            return;
        }

        if (potFarmController.State == PotFarmState.Failed)
        {
            SetFailure(potFarmController.LastError.Length == 0
                ? "Pot farm control failed during farm session."
                : potFarmController.LastError);
            return;
        }

        if (deathRecoveryController.State is not DeathRecoveryState.Idle and not DeathRecoveryState.Recovered)
        {
            if (currentState != FarmSessionState.WaitingForDeathRecovery)
            {
                CaptureInterruptedActivity(currentState);
                TransitionTo(FarmSessionState.WaitingForDeathRecovery, deathRecoveryController.LastTransition, "Death recovery");
            }

            return;
        }

        if (currentState == FarmSessionState.WaitingForDeathRecovery)
        {
            if (!scanner.Snapshot.IsInSouthHorn)
            {
                ClearInterruptedActivity();
                TransitionTo(FarmSessionState.WaitingForSouthHorn, "Death recovery completed outside South Horn.", "Waiting for South Horn");
                return;
            }

            if (deathRecoveryController.LastRecoveryMethod == DeathRecoveryMethod.Raised)
            {
                if (BeginInterruptedActivityResumeAfterRaise())
                {
                    return;
                }

                ClearInterruptedActivity();
                TransitionTo(FarmSessionState.SelectingTarget, "Death recovery completed without an interrupted CE/FATE to resume.", "Selecting target");
                return;
            }

            if (IsInterruptedPotFate() && potFarmController.IsRunning)
            {
                potFarmController.Stop("Death recovery completed without resuming interrupted pot FATE.");
            }

            ClearInterruptedActivity();
            StartRecoveryToBase("Death recovery completed; returning to Base Camp.");
            return;
        }

        switch (currentState)
        {
            case FarmSessionState.Starting:
            case FarmSessionState.ValidatingDependencies:
                TickStartup();
                break;
            case FarmSessionState.WaitingForSouthHorn:
                TickWaitingForSouthHorn();
                break;
            case FarmSessionState.RunningBuffRotation:
                TickBuffRotation();
                break;
            case FarmSessionState.RecoveringToBase:
                TickRecoveryToBase();
                break;
            case FarmSessionState.SelectingTarget:
                TickSelectingTarget();
                break;
            case FarmSessionState.WaitingForPredictedPotWindow:
            case FarmSessionState.WaitingAtPotSpawn:
            case FarmSessionState.RunningPots:
            case FarmSessionState.RunningTreasureHunt:
                TickPotRun();
                break;
            case FarmSessionState.RunningCe:
                TickCeRun();
                break;
            case FarmSessionState.RunningFate:
                TickFateRun();
                break;
            case FarmSessionState.ResumingInterruptedCe:
            case FarmSessionState.ResumingInterruptedFate:
            case FarmSessionState.ResumingInterruptedPotFate:
                TickInterruptedActivityResume();
                break;
            case FarmSessionState.IdleWaiting:
                TickIdleWaiting();
                break;
            case FarmSessionState.Stopping:
                Stop("Farm session stop completed.");
                break;
        }
    }

    private void TickStartup()
    {
        if (State == FarmSessionState.Starting)
        {
            TransitionTo(FarmSessionState.ValidatingDependencies, "Validating farm session dependencies.", "Validating dependencies");
            return;
        }

        if (!vnavmesh.IsReady())
        {
            SetFailure("vnavmesh IPC is unavailable.");
            return;
        }

        if (!lifestream.IsAvailable())
        {
            SetFailure("Lifestream IPC is unavailable.");
            return;
        }

        if (configuration.UseReturn && !movementController.CanUseReturnAction)
        {
            SetFailure("Return general action is unavailable while Use Return is enabled.");
            return;
        }

        if (autorotationController.ConfiguredPreset.Length > 0 && !autorotationController.ValidateConfiguredPreset())
        {
            SetFailure(autorotationController.LastError.Length == 0
                ? "BossMod preset validation failed."
                : autorotationController.LastError);
            return;
        }

        if (!scanner.Snapshot.IsInSouthHorn)
        {
            TransitionTo(FarmSessionState.WaitingForSouthHorn, "Waiting for South Horn.", "Waiting for South Horn");
            return;
        }

        StartStartupBuffRotation();
    }

    private void TickWaitingForSouthHorn()
    {
        if (!scanner.Snapshot.IsInSouthHorn)
        {
            logger.DebugThrottled("farm-waiting-south-horn", WaitLogInterval, "Farm session is still waiting for the player to enter South Horn.");
            return;
        }

        logger.ResetThrottle("farm-waiting-south-horn");
        StartStartupBuffRotation();
    }

    private void StartStartupBuffRotation()
    {
        if (!configuration.EnableBuffRotation)
        {
            StartRecoveryToBase("Startup buff rotation disabled; returning to Base Camp.");
            return;
        }

        StartSessionBuffRotation("farm session startup", recoverAfterSuccess: true, skipReason: "Startup buff rotation skipped; returning to Base Camp.");
    }

    private void StartSessionBuffRotation(string context, bool recoverAfterSuccess, string skipReason)
    {
        lock (gate)
        {
            recoverAfterBuffRotation = recoverAfterSuccess;
        }

        if (!buffRotationController.Start(context))
        {
            if (buffRotationController.State == BuffRotationState.Completed)
            {
                HandleBuffRotationSkippedOrCompleted(skipReason);
                return;
            }

            if (buffRotationController.State == BuffRotationState.Failed)
            {
                logger.Warning($"{BuildLogTag()} op=buff-rotation-skip reason={buffRotationController.LastError}");
                HandleBuffRotationSkippedOrCompleted(skipReason);
                return;
            }

            SetFailure(buffRotationController.LastError.Length == 0
                ? "Failed to start buff rotation."
                : buffRotationController.LastError);
            return;
        }

        TransitionTo(FarmSessionState.RunningBuffRotation, $"Running {context}.", "Buff rotation");
    }

    private void TickBuffRotation()
    {
        switch (buffRotationController.State)
        {
            case BuffRotationState.Completed:
                HandleBuffRotationSkippedOrCompleted("Buff rotation complete.");
                break;
            case BuffRotationState.Failed:
                logger.Warning($"{BuildLogTag()} op=buff-rotation-warning reason={buffRotationController.LastError}");
                HandleBuffRotationSkippedOrCompleted("Buff rotation finished with a warning.");
                break;
            case BuffRotationState.CriticalFailed:
                SetFailure(buffRotationController.LastError.Length == 0
                    ? "Buff rotation failed critically during farm session."
                    : buffRotationController.LastError);
                break;
            case BuffRotationState.Stopped:
                SetFailure(buffRotationController.LastError.Length == 0
                    ? "Buff rotation stopped unexpectedly during farm session."
                    : buffRotationController.LastError);
                break;
        }
    }

    private void HandleBuffRotationSkippedOrCompleted(string reason)
    {
        var recover = recoverAfterBuffRotation;
        lock (gate)
        {
            recoverAfterBuffRotation = false;
        }

        if (recover)
        {
            StartRecoveryToBase(reason);
            return;
        }

        TransitionTo(FarmSessionState.SelectingTarget, reason, "Selecting target");
    }

    private void StartRecoveryToBase(string reason)
    {
        if (!scanner.Snapshot.IsInSouthHorn)
        {
            TransitionTo(FarmSessionState.WaitingForSouthHorn, reason, "Waiting for South Horn");
            return;
        }

        movementController.SetLogOwner(currentRunId);
        if (!movementController.RecoverToBaseCamp())
        {
            SetFailure(movementController.LastError.Length == 0
                ? "Failed to begin Base Camp recovery."
                : movementController.LastError);
            return;
        }

        TransitionTo(FarmSessionState.RecoveringToBase, reason, "Recovering to Base Camp");
    }

    private void TickRecoveryToBase()
    {
        if (movementController.State is MovementState.Pathfinding or MovementState.WaitingForArrival or MovementState.UsingReturn or MovementState.UsingAethernet)
        {
            logger.DebugThrottled("farm-recovering-base", WaitLogInterval, $"Farm session is still recovering to Base Camp. MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                logger.ResetThrottle("farm-recovering-base");
                movementController.Stop("Base Camp recovery completed.");
                if (runBuffRotationAfterRecovery)
                {
                    lock (gate)
                    {
                        runBuffRotationAfterRecovery = false;
                    }

                    if (!configuration.EnableBuffRotation)
                    {
                        TransitionTo(FarmSessionState.SelectingTarget, "Base Camp recovery complete.", "Selecting target");
                        return;
                    }

                    StartSessionBuffRotation("farm session recovery", recoverAfterSuccess: false, skipReason: "Recovery buff rotation skipped.");
                    return;
                }

                TransitionTo(FarmSessionState.SelectingTarget, "Ready to select the next target.", "Selecting target");
                break;
            case MovementState.Failed:
            case MovementState.TimedOut:
                logger.ResetThrottle("farm-recovering-base");
                SetFailure(movementController.LastError.Length == 0
                    ? "Base Camp recovery failed."
                    : movementController.LastError);
                break;
        }
    }

    private void TickSelectingTarget()
    {
        var snapshot = scanner.Snapshot;
        var potCycleSnapshot = potCycleTracker.Snapshot;
        var now = DateTimeOffset.UtcNow;
        logger.ResetThrottle("farm-idle-waiting");
        if (!snapshot.IsInSouthHorn)
        {
            TransitionTo(FarmSessionState.WaitingForSouthHorn, "Left South Horn while selecting a target.", "Waiting for South Horn");
            return;
        }

        if (TryStartOrResumePotControl(now))
        {
            return;
        }

        switch (snapshot.EffectiveTarget.Kind)
        {
            case SelectedTargetKind.CriticalEncounter when snapshot.EffectiveTarget.CriticalEncounter != null:
                var ceStartDecision = potFallbackWindowEvaluator.EvaluateCeStart(potCycleSnapshot, now);
                if (!ceStartDecision.AllowStart)
                {
                    TransitionTo(FarmSessionState.IdleWaiting, ceStartDecision.Reason, "Idle waiting");
                    return;
                }

                if (!criticalEngagementAutomationController.Start(snapshot.EffectiveTarget.CriticalEncounter))
                {
                    SetFailure(criticalEngagementAutomationController.LastError.Length == 0
                        ? "Failed to start CE automation."
                        : criticalEngagementAutomationController.LastError);
                    return;
                }

                TransitionTo(FarmSessionState.RunningCe,
                    $"Running CE {criticalEngagementAutomationController.TargetCeName} ({criticalEngagementAutomationController.TargetCeId}).",
                    "Critical Engagement");
                return;
            case SelectedTargetKind.Fate when snapshot.EffectiveTarget.Fate != null:
                var fateStartDecision = potFallbackWindowEvaluator.EvaluateFateStart(potCycleSnapshot, now);
                if (!fateStartDecision.AllowStart)
                {
                    TransitionTo(FarmSessionState.IdleWaiting, fateStartDecision.Reason, "Idle waiting");
                    return;
                }

                if (!fateAutomationController.Start(snapshot.EffectiveTarget.Fate))
                {
                    SetFailure(fateAutomationController.LastError.Length == 0
                        ? "Failed to start FATE automation."
                        : fateAutomationController.LastError);
                    return;
                }

                TransitionTo(FarmSessionState.RunningFate,
                    $"Running FATE {fateAutomationController.TargetFateName} ({fateAutomationController.TargetFateId}).",
                    "FATE");
                return;
        }

        TransitionTo(FarmSessionState.IdleWaiting, "No eligible CE/FATE target selected.", "Idle waiting");
    }

    private void TickCeRun()
    {
        if (criticalEngagementAutomationController.IsRunning)
        {
            logger.DebugThrottled("farm-running-ce", WaitLogInterval, $"Farm session is still running CE automation. State={criticalEngagementAutomationController.State} target={criticalEngagementAutomationController.TargetCeName} ({criticalEngagementAutomationController.TargetCeId}).");
            return;
        }

        logger.ResetThrottle("farm-running-ce");

        switch (criticalEngagementAutomationController.LastResult)
        {
            case AutomationRunResult.Completed:
                StartPostCeFlow();
                break;
            case AutomationRunResult.Stopped when pendingStop:
                TransitionTo(FarmSessionState.Stopped, "Farm session stop completed.", "Stopped", clearError: false);
                break;
            default:
                SetFailure(criticalEngagementAutomationController.LastError.Length == 0
                    ? criticalEngagementAutomationController.LastTransition
                    : criticalEngagementAutomationController.LastError);
                break;
        }
    }

    private void TickFateRun()
    {
        if (fateAutomationController.IsRunning)
        {
            logger.DebugThrottled("farm-running-fate", WaitLogInterval, $"Farm session is still running FATE automation. State={fateAutomationController.State} target={fateAutomationController.TargetFateName} ({fateAutomationController.TargetFateId}).");
            return;
        }

        logger.ResetThrottle("farm-running-fate");

        switch (fateAutomationController.LastResult)
        {
            case AutomationRunResult.Completed:
                StartPostFateFlow();
                break;
            case AutomationRunResult.Preempted:
                TransitionTo(FarmSessionState.SelectingTarget, fateAutomationController.LastTransition, "Selecting target");
                break;
            case AutomationRunResult.Stopped when pendingStop:
                TransitionTo(FarmSessionState.Stopped, "Farm session stop completed.", "Stopped", clearError: false);
                break;
            default:
                SetFailure(fateAutomationController.LastError.Length == 0
                    ? fateAutomationController.LastTransition
                    : fateAutomationController.LastError);
                break;
        }
    }

    private void StartPostCeFlow()
    {
        if (!configuration.UseReturn)
        {
            lock (gate)
            {
                runBuffRotationAfterRecovery = configuration.EnableBuffRotation;
            }

            StartRecoveryToBase("CE complete; returning to Base Camp.");
            return;
        }

        StartPostFateFlow();
    }

    private void StartPostFateFlow()
    {
        if (!configuration.EnableBuffRotation)
        {
            TransitionTo(FarmSessionState.SelectingTarget, "Activity complete.", "Selecting target");
            return;
        }

        StartSessionBuffRotation("farm session recovery", recoverAfterSuccess: false, skipReason: "Recovery buff rotation skipped.");
    }

    private void TickIdleWaiting()
    {
        var snapshot = scanner.Snapshot;
        var potCycleSnapshot = potCycleTracker.Snapshot;
        var now = DateTimeOffset.UtcNow;
        if (!snapshot.IsInSouthHorn)
        {
            logger.ResetThrottle("farm-idle-waiting");
            TransitionTo(FarmSessionState.WaitingForSouthHorn, "Left South Horn while idle waiting.", "Waiting for South Horn");
            return;
        }

        if (TryStartOrResumePotControl(now))
        {
            logger.ResetThrottle("farm-idle-waiting");
            return;
        }

        var startDecision = EvaluateEffectiveTargetStart(snapshot, potCycleSnapshot, now);
        if (startDecision?.AllowStart == true)
        {
            logger.ResetThrottle("farm-idle-waiting");
            TransitionTo(FarmSessionState.SelectingTarget, "Target became available while idle waiting.", "Selecting target");
            return;
        }

        if (now - lastIdleScanAt >= IdleRescanInterval)
        {
            lock (gate)
            {
                lastIdleScanAt = now;
            }

            logger.DebugThrottled("farm-idle-waiting", WaitLogInterval, startDecision?.Reason ?? "Farm session is idle waiting for a new CE or FATE target.");
        }
    }

    private void TickPotRun()
    {
        if (!potFarmController.IsRunning)
        {
            switch (potFarmController.LastResult)
            {
                case PotFarmRunResult.Completed:
                    StartPostFateFlow();
                    return;
                case PotFarmRunResult.LeftContent:
                    TransitionTo(FarmSessionState.Stopped, potFarmController.LastTransition, "Stopped");
                    return;
                case PotFarmRunResult.TreasurePending:
                    TransitionTo(FarmSessionState.SelectingTarget, potFarmController.LastTransition, "Selecting target");
                    return;
                case PotFarmRunResult.Stopped when pendingStop:
                    TransitionTo(FarmSessionState.Stopped, "Farm session stop completed.", "Stopped", clearError: false);
                    return;
                case PotFarmRunResult.None:
                    TransitionTo(FarmSessionState.SelectingTarget, potFarmController.LastTransition, "Selecting target");
                    return;
                default:
                    SetFailure(potFarmController.LastError.Length == 0
                        ? potFarmController.LastTransition
                        : potFarmController.LastError);
                    return;
            }
        }

        var mappedState = MapPotFarmState(potFarmController.State);
        if (mappedState != State)
        {
            TransitionTo(mappedState, potFarmController.LastTransition, MapPotFarmActivity(mappedState));
        }
    }

    private PotFallbackStartDecision? EvaluateEffectiveTargetStart(ScannerSnapshot snapshot, PotCycleSnapshot potCycleSnapshot, DateTimeOffset now)
        => snapshot.EffectiveTarget.Kind switch
        {
            SelectedTargetKind.CriticalEncounter when snapshot.EffectiveTarget.CriticalEncounter != null
                => potFallbackWindowEvaluator.EvaluateCeStart(potCycleSnapshot, now),
            SelectedTargetKind.Fate when snapshot.EffectiveTarget.Fate != null
                => potFallbackWindowEvaluator.EvaluateFateStart(potCycleSnapshot, now),
            _ => null,
        };

    private void CaptureInterruptedActivity(FarmSessionState currentState)
    {
        var activity = InterruptedActivityKind.None;
        var targetId = 0u;
        var targetName = string.Empty;
        var fateCompletionBehavior = FateRunCompletionBehavior.RecoverToBase;

        switch (currentState)
        {
            case FarmSessionState.RunningCe when criticalEngagementAutomationController.TargetCeId != 0 || criticalEngagementAutomationController.LastTargetCeId != 0:
                activity = InterruptedActivityKind.Ce;
                targetId = criticalEngagementAutomationController.TargetCeId != 0
                    ? criticalEngagementAutomationController.TargetCeId
                    : criticalEngagementAutomationController.LastTargetCeId;
                targetName = criticalEngagementAutomationController.TargetCeId != 0
                    ? criticalEngagementAutomationController.TargetCeName
                    : criticalEngagementAutomationController.LastTargetCeName;
                break;
            case FarmSessionState.RunningFate when fateAutomationController.TargetFateId != 0 || fateAutomationController.LastTargetFateId != 0:
                activity = (fateAutomationController.TargetFateId != 0 ? fateAutomationController.TargetIsPot : fateAutomationController.LastTargetIsPot)
                    ? InterruptedActivityKind.PotFate
                    : InterruptedActivityKind.Fate;
                targetId = fateAutomationController.TargetFateId != 0
                    ? fateAutomationController.TargetFateId
                    : fateAutomationController.LastTargetFateId;
                targetName = fateAutomationController.TargetFateId != 0
                    ? fateAutomationController.TargetFateName
                    : fateAutomationController.LastTargetFateName;
                fateCompletionBehavior = fateAutomationController.LastCompletionBehavior;
                break;
            case FarmSessionState.RunningPots when potFarmController.State == PotFarmState.RunningPotFate && (fateAutomationController.TargetFateId != 0 || fateAutomationController.LastTargetFateId != 0):
                activity = InterruptedActivityKind.PotFate;
                targetId = fateAutomationController.TargetFateId != 0
                    ? fateAutomationController.TargetFateId
                    : fateAutomationController.LastTargetFateId;
                targetName = fateAutomationController.TargetFateId != 0
                    ? fateAutomationController.TargetFateName
                    : fateAutomationController.LastTargetFateName;
                fateCompletionBehavior = fateAutomationController.LastCompletionBehavior;
                break;
        }

        lock (gate)
        {
            interruptedActivity = activity;
            interruptedTargetId = targetId;
            interruptedTargetName = targetName;
            interruptedFateCompletionBehavior = fateCompletionBehavior;
        }

        if (activity != InterruptedActivityKind.None)
        {
            logger.Info($"{BuildLogTag()} op=interrupted-capture activity={activity} target=\"{targetName}\" ({targetId}) reason=death-recovery");
        }
    }

    private bool BeginInterruptedActivityResumeAfterRaise()
    {
        InterruptedActivityKind activity;
        uint targetId;
        string targetName;

        lock (gate)
        {
            activity = interruptedActivity;
            targetId = interruptedTargetId;
            targetName = interruptedTargetName;
        }

        if (activity == InterruptedActivityKind.None || targetId == 0)
        {
            return false;
        }

        var nextState = activity switch
        {
            InterruptedActivityKind.Ce => FarmSessionState.ResumingInterruptedCe,
            InterruptedActivityKind.Fate => FarmSessionState.ResumingInterruptedFate,
            InterruptedActivityKind.PotFate => FarmSessionState.ResumingInterruptedPotFate,
            _ => FarmSessionState.WaitingForDeathRecovery,
        };

        var activityLabel = activity switch
        {
            InterruptedActivityKind.Ce => "CE",
            InterruptedActivityKind.Fate => "FATE",
            InterruptedActivityKind.PotFate => "PotFate",
            _ => "Unknown",
        };

        logger.Info($"{BuildLogTag()} op=resume-wait activity={activityLabel} target=\"{targetName}\" ({targetId}) reason=raised-after-death");
        TransitionTo(nextState, $"Death recovery completed after raise; resuming {activityLabel} {targetName} ({targetId}).", $"Resuming {activityLabel}");
        return true;
    }

    private void TickInterruptedActivityResume()
    {
        var snapshot = scanner.Snapshot;
        InterruptedActivityKind activity;
        uint targetId;
        string targetName;
        FateRunCompletionBehavior fateCompletionBehavior;

        lock (gate)
        {
            activity = interruptedActivity;
            targetId = interruptedTargetId;
            targetName = interruptedTargetName;
            fateCompletionBehavior = interruptedFateCompletionBehavior;
        }

        if (activity == InterruptedActivityKind.None || targetId == 0)
        {
            TransitionTo(FarmSessionState.SelectingTarget, "Interrupted activity data was cleared before resume could complete.", "Selecting target");
            return;
        }

        switch (activity)
        {
            case InterruptedActivityKind.Ce:
                TickInterruptedCeResume(snapshot, targetId, targetName);
                break;
            case InterruptedActivityKind.Fate:
                TickInterruptedFateResume(snapshot, targetId, targetName, isPotTarget: false, fateCompletionBehavior);
                break;
            case InterruptedActivityKind.PotFate:
                TickInterruptedFateResume(snapshot, targetId, targetName, isPotTarget: true, fateCompletionBehavior);
                break;
        }
    }

    private void TickInterruptedCeResume(ScannerSnapshot snapshot, uint targetId, string targetName)
    {
        var ceTarget = snapshot.FindCriticalEncounter(targetId);
        if (ceTarget == null)
        {
            logger.ResetThrottle("farm-resume-ce");
            ClearInterruptedActivity();
            logger.Info($"{BuildLogTag()} op=resume-ended activity=CE target=\"{targetName}\" ({targetId}) reason=target-no-longer-active-after-raise");
            StartPostCeFlow();
            return;
        }

        logger.ResetThrottle("farm-resume-ce");
        logger.Info($"{BuildLogTag()} op=resume-attempt activity=CE target=\"{ceTarget.Name}\" ({ceTarget.Id}) reason=after-raise");
        if (!criticalEngagementAutomationController.Start(ceTarget))
        {
            SetFailure(criticalEngagementAutomationController.LastError.Length == 0
                ? $"Failed to resume CE {ceTarget.Name} ({ceTarget.Id}) after raise."
                : criticalEngagementAutomationController.LastError);
            return;
        }

        ClearInterruptedActivity();
        TransitionTo(FarmSessionState.RunningCe, $"Resumed CE {criticalEngagementAutomationController.TargetCeName} ({criticalEngagementAutomationController.TargetCeId}) after raise.", "Critical Engagement");
    }

    private void TickInterruptedFateResume(ScannerSnapshot snapshot, uint targetId, string targetName, bool isPotTarget, FateRunCompletionBehavior completionBehavior)
    {
        if (isPotTarget && (!potFarmController.IsRunning || potFarmController.State != PotFarmState.RunningPotFate))
        {
            logger.ResetThrottle("farm-resume-pot-fate");
            ClearInterruptedActivity();
            logger.Info($"{BuildLogTag()} op=resume-ended activity=PotFate target=\"{targetName}\" ({targetId}) reason=pot-state-{potFarmController.State}");
            TransitionTo(FarmSessionState.SelectingTarget, "Interrupted pot FATE ended while death recovery completed.", "Selecting target");
            return;
        }

        var fateTarget = snapshot.FindFateRunTarget(targetId, isPotTarget);
        var throttleKey = isPotTarget ? "farm-resume-pot-fate" : "farm-resume-fate";
        var activityLabel = isPotTarget ? "PotFate" : "FATE";
        if (fateTarget == null)
        {
            logger.ResetThrottle(throttleKey);
            ClearInterruptedActivity();
            logger.Info($"{BuildLogTag()} op=resume-ended activity={activityLabel} target=\"{targetName}\" ({targetId}) reason=target-no-longer-active-after-raise");
            if (isPotTarget)
            {
                TransitionTo(FarmSessionState.SelectingTarget, "Interrupted pot FATE ended while death recovery completed.", "Selecting target");
            }
            else
            {
                StartPostFateFlow();
            }

            return;
        }

        logger.ResetThrottle(throttleKey);
        logger.Info($"{BuildLogTag()} op=resume-attempt activity={activityLabel} target=\"{fateTarget.Name}\" ({fateTarget.Id}) reason=after-raise completionBehavior={completionBehavior}");
        if (!fateAutomationController.Start(fateTarget, completionBehavior))
        {
            SetFailure(fateAutomationController.LastError.Length == 0
                ? $"Failed to resume {activityLabel} {fateTarget.Name} ({fateTarget.Id}) after raise."
                : fateAutomationController.LastError);
            return;
        }

        ClearInterruptedActivity();
        TransitionTo(
            isPotTarget ? FarmSessionState.RunningPots : FarmSessionState.RunningFate,
            isPotTarget
                ? $"Resumed pot FATE {fateAutomationController.TargetFateName} ({fateAutomationController.TargetFateId}) after raise."
                : $"Resumed FATE {fateAutomationController.TargetFateName} ({fateAutomationController.TargetFateId}) after raise.",
            isPotTarget ? "Running pots" : "FATE");
    }

    private bool TryStartOrResumePotControl(DateTimeOffset now)
    {
        if (!potFarmController.NeedsControlNow(now, out _, out var reason))
        {
            return false;
        }

        if (!potFarmController.IsRunning && !potFarmController.Start())
        {
            SetFailure(potFarmController.LastError.Length == 0
                ? "Failed to start pot farm control."
                : potFarmController.LastError);
            return true;
        }

        var mappedState = MapPotFarmState(potFarmController.State);
        TransitionTo(mappedState, reason, MapPotFarmActivity(mappedState));
        return true;
    }

    private static FarmSessionState MapPotFarmState(PotFarmState state)
        => state switch
        {
            PotFarmState.WaitingForPredictedWindow or PotFarmState.Bootstrapping => FarmSessionState.WaitingForPredictedPotWindow,
            PotFarmState.TravelingToSpawn or PotFarmState.WaitingAtSpawn => FarmSessionState.WaitingAtPotSpawn,
            PotFarmState.RunningPotFate or PotFarmState.RecoveringToBase => FarmSessionState.RunningPots,
            PotFarmState.WaitingForTreasureBuff or PotFarmState.MovingNearTreasureCenter or PotFarmState.TreasurePending or PotFarmState.RunningTreasureSearch or PotFarmState.RunningCofferInteraction => FarmSessionState.RunningTreasureHunt,
            _ => FarmSessionState.RunningPots,
        };

    private static string MapPotFarmActivity(FarmSessionState state)
        => state switch
        {
            FarmSessionState.WaitingForPredictedPotWindow => "Waiting for predicted pot window",
            FarmSessionState.WaitingAtPotSpawn => "Waiting at pot spawn",
            FarmSessionState.RunningPots => "Running pots",
            FarmSessionState.RunningTreasureHunt => "Running treasure hunt",
            _ => "Running pots",
        };

    private void TransitionTo(FarmSessionState nextState, string reason, string activity, bool clearError = true)
    {
        FarmSessionState previousState;
        lock (gate)
        {
            previousState = state;
            state = nextState;
            lastTransition = reason;
            currentActivity = activity;
            stateEnteredAt = DateTimeOffset.UtcNow;
            if (clearError)
            {
                lastError = string.Empty;
            }
        }

        logger.Info($"{BuildLogTag()} op=transition from={previousState} to={nextState} activity={activity} reason={reason}");
    }

    private void SetFailure(string reason)
    {
        if (potFarmController.IsRunning)
        {
            potFarmController.Stop(reason);
        }

        movementController.Stop(reason);
        autorotationController.ReleaseOwnership(reason);

        lock (gate)
        {
            state = FarmSessionState.Failed;
            lastTransition = reason;
            lastError = reason;
            currentActivity = "Failed";
            stateEnteredAt = DateTimeOffset.UtcNow;
            pendingStop = false;
            recoverAfterBuffRotation = false;
            runBuffRotationAfterRecovery = false;
            interruptedActivity = InterruptedActivityKind.None;
            interruptedTargetId = 0;
            interruptedTargetName = string.Empty;
            interruptedFateCompletionBehavior = FateRunCompletionBehavior.RecoverToBase;
        }

        logger.Warning($"{BuildLogTag()} op=failure state={FarmSessionState.Failed} activity=Failed reason={reason}");
    }

    private string BuildLogTag()
        => currentRunId.Length == 0 ? "[Farm]" : $"[Farm run={currentRunId}]";

    private void ClearInterruptedActivity()
    {
        lock (gate)
        {
            interruptedActivity = InterruptedActivityKind.None;
            interruptedTargetId = 0;
            interruptedTargetName = string.Empty;
            interruptedFateCompletionBehavior = FateRunCompletionBehavior.RecoverToBase;
        }
    }

    private bool IsInterruptedPotFate()
    {
        lock (gate)
        {
            return interruptedActivity == InterruptedActivityKind.PotFate;
        }
    }
}
