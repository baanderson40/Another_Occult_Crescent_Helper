using System;
using AOCCH.IPC;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class FarmSessionController : IDisposable
{
    private static readonly TimeSpan IdleRescanInterval = TimeSpan.FromSeconds(1);

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
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private FarmSessionState state = FarmSessionState.Idle;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private string currentActivity = "None";
    private DateTimeOffset lastIdleScanAt = DateTimeOffset.MinValue;
    private DateTimeOffset stateEnteredAt = DateTimeOffset.MinValue;
    private bool pendingStop;
    private bool recoverAfterBuffRotation;
    private bool runBuffRotationAfterRecovery;

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
            logger.Warning("Farm session start ignored because it is already running.");
            return false;
        }

        if (criticalEngagementAutomationController.IsRunning || fateAutomationController.IsRunning || buffRotationController.IsRunning)
        {
            SetFailure("Stop CE/FATE automation and buff rotation before starting the farm session.");
            return false;
        }

        lock (gate)
        {
            pendingStop = false;
            lastError = string.Empty;
            lastIdleScanAt = DateTimeOffset.MinValue;
            recoverAfterBuffRotation = false;
            runBuffRotationAfterRecovery = false;
        }

        TransitionTo(FarmSessionState.Starting, "Starting unified CE/FATE farm session.", "Startup");
        return true;
    }

    public void Stop(string reason)
    {
        lock (gate)
        {
            pendingStop = true;
        }

        if (criticalEngagementAutomationController.IsRunning)
        {
            criticalEngagementAutomationController.Stop(reason);
        }

        if (fateAutomationController.IsRunning)
        {
            fateAutomationController.Stop(reason);
        }

        if (buffRotationController.IsRunning)
        {
            buffRotationController.Stop(reason);
        }

        movementController.Stop(reason);
        autorotationController.ReleaseOwnership(reason);
        TransitionTo(FarmSessionState.Stopped, reason, "Stopped", clearError: false);
        logger.Info($"Farm session stopped: {reason}");
    }

    public void PanicStop()
        => Stop("Farm panic stop requested.");

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

        if (deathRecoveryController.State is not DeathRecoveryState.Idle and not DeathRecoveryState.Recovered)
        {
            if (currentState != FarmSessionState.WaitingForDeathRecovery)
            {
                TransitionTo(FarmSessionState.WaitingForDeathRecovery, deathRecoveryController.LastTransition, "Death recovery");
            }

            return;
        }

        if (currentState == FarmSessionState.WaitingForDeathRecovery)
        {
            if (!scanner.Snapshot.IsInSouthHorn)
            {
                TransitionTo(FarmSessionState.WaitingForSouthHorn, "Death recovery completed outside South Horn.", "Waiting for South Horn");
                return;
            }

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
            case FarmSessionState.RunningCe:
                TickCeRun();
                break;
            case FarmSessionState.RunningFate:
                TickFateRun();
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
            return;
        }

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
                logger.Warning($"Farm buff rotation was skipped: {buffRotationController.LastError}");
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
                logger.Warning($"Farm buff rotation finished with non-critical failure: {buffRotationController.LastError}");
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
        switch (movementController.State)
        {
            case MovementState.Arrived:
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
                SetFailure(movementController.LastError.Length == 0
                    ? "Base Camp recovery failed."
                    : movementController.LastError);
                break;
        }
    }

    private void TickSelectingTarget()
    {
        var snapshot = scanner.Snapshot;
        if (!snapshot.IsInSouthHorn)
        {
            TransitionTo(FarmSessionState.WaitingForSouthHorn, "Left South Horn while selecting a target.", "Waiting for South Horn");
            return;
        }

        if (configuration.FarmingMode != FarmingMode.FateOnly && snapshot.SelectedCriticalEncounter != null)
        {
            if (!criticalEngagementAutomationController.Start(snapshot.SelectedCriticalEncounter))
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
        }

        if (configuration.FarmingMode != FarmingMode.CeOnly && snapshot.SelectedFate != null)
        {
            if (!fateAutomationController.Start(snapshot.SelectedFate))
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
            return;
        }

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
            return;
        }

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
        var now = DateTimeOffset.UtcNow;
        if (now - lastIdleScanAt < IdleRescanInterval)
        {
            return;
        }

        lock (gate)
        {
            lastIdleScanAt = now;
        }

        TransitionTo(FarmSessionState.SelectingTarget, "Retrying target selection.", "Selecting target");
    }

    private void TransitionTo(FarmSessionState nextState, string reason, string activity, bool clearError = true)
    {
        lock (gate)
        {
            state = nextState;
            lastTransition = reason;
            currentActivity = activity;
            stateEnteredAt = DateTimeOffset.UtcNow;
            if (clearError)
            {
                lastError = string.Empty;
            }
        }

        logger.Info($"Farm session state -> {nextState}: {reason}");
    }

    private void SetFailure(string reason)
    {
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
        }

        logger.Warning(reason);
    }
}
