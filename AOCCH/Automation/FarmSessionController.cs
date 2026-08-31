using System;
using AOCCH.IPC;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using AOCCH.Shopping;
using Dalamud.Plugin.Services;
using System.Threading;

namespace AOCCH.Automation;

public sealed class FarmSessionController : IDisposable
{
    private static int nextRunSequence;
    private static readonly TimeSpan CofferSurveyWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SupportJobSwitchTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CofferSurveyActionRetryInterval = TimeSpan.FromMilliseconds(250);
    private const int RequiredAutomaticCofferInventoryFreeSlots = 3;
    private enum InterruptedActivityKind
    {
        None,
        Ce,
        Fate,
        PotFate,
    }

    private enum ActiveRevivalActivityKind
    {
        None,
        Ce,
        Fate,
    }

    private static readonly TimeSpan IdleRescanInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);

    private readonly IFramework framework;
    private readonly OccultCrescentScanner scanner;
    private readonly VNavmeshIpc vnavmesh;
    private readonly MovementController movementController;
    private readonly GameActionController gameActionController;
    private readonly AutorotationController autorotationController;
    private readonly BuffRotationController buffRotationController;
    private readonly CriticalEngagementAutomationController criticalEngagementAutomationController;
    private readonly ForkedTowerStagingController forkedTowerStagingController;
    private readonly FateAutomationController fateAutomationController;
    private readonly PostActivityRevivalController postActivityRevivalController;
    private readonly DeathRecoveryController deathRecoveryController;
    private readonly DangerousTreasureTravelController dangerousTreasureTravelController;
    private readonly PotCycleTracker potCycleTracker;
    private readonly PotFallbackWindowEvaluator potFallbackWindowEvaluator;
    private readonly PotFarmController potFarmController;
    private readonly TreasureHintTracker treasureHintTracker;
    private readonly TreasureCofferFarmController treasureCofferFarmController;
    private readonly ManualCurrencyShoppingController manualCurrencyShoppingController;
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
    private bool postActivityDeathRecoveryPending;
    private ActiveRevivalActivityKind activeRevivalActivity;
    private InterruptedActivityKind interruptedActivity;
    private uint interruptedTargetId;
    private string interruptedTargetName = string.Empty;
    private FateRunCompletionBehavior interruptedFateCompletionBehavior = FateRunCompletionBehavior.RecoverToBase;
    private int remainingSilverCompletionsUntilRescan;
    private int remainingBronzeCompletionsUntilRescan;
    private bool automaticTreasureCofferDisabledForRun;
    private bool automaticTreasureCofferRestorePending;
    private bool automaticTreasureCofferStartRouteAfterRestore;
    private bool automaticTreasureCofferResumeAutomaticCheckAfterRestore;
    private bool pendingAutomaticTreasureCofferCheckAfterExternalRecovery;
    private string pendingAutomaticTreasureCofferCheckSource = string.Empty;
    private byte automaticTreasureCofferOriginalSupportJob;
    private int requiredFreshCofferSurveyRevision;
    private DateTimeOffset automaticTreasureCofferSurveyDeadlineAt = DateTimeOffset.MinValue;
    private DateTimeOffset automaticTreasureCofferNextSurveyActionAttemptAt = DateTimeOffset.MinValue;
    private string automaticTreasureCofferStatus = "Idle";

    public FarmSessionController(
        IFramework framework,
        OccultCrescentScanner scanner,
        VNavmeshIpc vnavmesh,
        MovementController movementController,
        GameActionController gameActionController,
        AutorotationController autorotationController,
        BuffRotationController buffRotationController,
        CriticalEngagementAutomationController criticalEngagementAutomationController,
        ForkedTowerStagingController forkedTowerStagingController,
        FateAutomationController fateAutomationController,
        PostActivityRevivalController postActivityRevivalController,
        DeathRecoveryController deathRecoveryController,
        DangerousTreasureTravelController dangerousTreasureTravelController,
        PotCycleTracker potCycleTracker,
        PotFallbackWindowEvaluator potFallbackWindowEvaluator,
        PotFarmController potFarmController,
        TreasureHintTracker treasureHintTracker,
        TreasureCofferFarmController treasureCofferFarmController,
        ManualCurrencyShoppingController manualCurrencyShoppingController,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.scanner = scanner;
        this.vnavmesh = vnavmesh;
        this.movementController = movementController;
        this.gameActionController = gameActionController;
        this.autorotationController = autorotationController;
        this.buffRotationController = buffRotationController;
        this.criticalEngagementAutomationController = criticalEngagementAutomationController;
        this.forkedTowerStagingController = forkedTowerStagingController;
        this.fateAutomationController = fateAutomationController;
        this.postActivityRevivalController = postActivityRevivalController;
        this.deathRecoveryController = deathRecoveryController;
        this.dangerousTreasureTravelController = dangerousTreasureTravelController;
        this.potCycleTracker = potCycleTracker;
        this.potFallbackWindowEvaluator = potFallbackWindowEvaluator;
        this.potFarmController = potFarmController;
        this.treasureHintTracker = treasureHintTracker;
        this.treasureCofferFarmController = treasureCofferFarmController;
        this.manualCurrencyShoppingController = manualCurrencyShoppingController;
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

    public TreasureCofferAutomaticModeStatus AutomaticTreasureCofferStatus
    {
        get
        {
            lock (gate)
            {
                return new TreasureCofferAutomaticModeStatus
                {
                    DisabledForCurrentRun = automaticTreasureCofferDisabledForRun,
                    RestoreRetryPending = automaticTreasureCofferRestorePending,
                    RemainingSilverCompletionsUntilRescan = remainingSilverCompletionsUntilRescan,
                    RemainingBronzeCompletionsUntilRescan = remainingBronzeCompletionsUntilRescan,
                    LastTransition = automaticTreasureCofferStatus,
                };
            }
        }
    }

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

        if (potFarmController.State == PotFarmState.Failed)
        {
            potFarmController.ResetInstanceState("Starting a new farm session after a terminal pot farm failure.");
        }

        var snapshot = scanner.Snapshot;
        if (!snapshot.IsInSupportedTerritory)
        {
            SetFailure("Farm session requires a supported Occult Crescent territory.");
            return false;
        }

        if (!CanRunAnyAutomation(snapshot))
        {
            SetFailure($"No automation features are available in {snapshot.TerritoryDisplayName}.");
            return false;
        }

        if (criticalEngagementAutomationController.IsRunning || fateAutomationController.IsRunning || buffRotationController.IsRunning || potFarmController.IsRunning || treasureCofferFarmController.IsRunning)
        {
            SetFailure("Stop CE/FATE automation, pot control, buff rotation, and overworld coffer routing before starting the farm session.");
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
            pendingStop = false;
            currentRunId = $"Farm#{Interlocked.Increment(ref nextRunSequence)}";
            lastError = string.Empty;
            lastIdleScanAt = DateTimeOffset.MinValue;
            recoverAfterBuffRotation = false;
            runBuffRotationAfterRecovery = false;
            postActivityDeathRecoveryPending = false;
            activeRevivalActivity = ActiveRevivalActivityKind.None;
            interruptedActivity = InterruptedActivityKind.None;
            interruptedTargetId = 0;
            interruptedTargetName = string.Empty;
            interruptedFateCompletionBehavior = FateRunCompletionBehavior.RecoverToBase;
            remainingSilverCompletionsUntilRescan = 0;
            remainingBronzeCompletionsUntilRescan = 0;
            automaticTreasureCofferDisabledForRun = false;
            automaticTreasureCofferRestorePending = false;
            automaticTreasureCofferStartRouteAfterRestore = false;
            automaticTreasureCofferResumeAutomaticCheckAfterRestore = false;
            pendingAutomaticTreasureCofferCheckAfterExternalRecovery = false;
            pendingAutomaticTreasureCofferCheckSource = string.Empty;
            automaticTreasureCofferOriginalSupportJob = 0;
            requiredFreshCofferSurveyRevision = treasureHintTracker.CofferSurveySnapshot.Revision + 1;
            automaticTreasureCofferSurveyDeadlineAt = DateTimeOffset.MinValue;
            automaticTreasureCofferStatus = "Starting automatic coffer tracking.";
            postActivityContinuation = null;
        }

        logger.Info($"{BuildLogTag()} op=start ceFarming={configuration.EnableCriticalEngagementFarming} fateFarming={configuration.EnableFateFarming} automationPriority={string.Join(',', configuration.AutomationPriority)} fatePriority={configuration.FatePriority} useReturn={configuration.UseReturn} enableBuffRotation={configuration.EnableBuffRotation} scannerOnlyMode={configuration.ScannerOnlyMode} minimumMountingRange={configuration.MinimumMountingRange}.");
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
            remainingSilverCompletionsUntilRescan = 0;
            remainingBronzeCompletionsUntilRescan = 0;
            automaticTreasureCofferDisabledForRun = false;
            automaticTreasureCofferRestorePending = false;
            automaticTreasureCofferStartRouteAfterRestore = false;
            automaticTreasureCofferResumeAutomaticCheckAfterRestore = false;
            pendingAutomaticTreasureCofferCheckAfterExternalRecovery = false;
            pendingAutomaticTreasureCofferCheckSource = string.Empty;
            automaticTreasureCofferOriginalSupportJob = 0;
            requiredFreshCofferSurveyRevision = 0;
            automaticTreasureCofferSurveyDeadlineAt = DateTimeOffset.MinValue;
            automaticTreasureCofferStatus = "Idle";
            postActivityDeathRecoveryPending = false;
            activeRevivalActivity = ActiveRevivalActivityKind.None;
            postActivityContinuation = null;
        }

        if (criticalEngagementAutomationController.IsRunning)
        {
            criticalEngagementAutomationController.Stop(reason);
        }

        if (forkedTowerStagingController.IsRunning)
        {
            forkedTowerStagingController.Stop(reason);
        }

        if (fateAutomationController.IsRunning)
        {
            fateAutomationController.Stop(reason);
        }

        if (postActivityRevivalController.IsRunning)
        {
            postActivityRevivalController.Stop(reason);
        }

        if (potFarmController.IsRunning)
        {
            potFarmController.Stop(reason);
        }

        if (treasureCofferFarmController.IsRunning)
        {
            treasureCofferFarmController.Stop(reason);
        }

        if (manualCurrencyShoppingController.IsRunning)
        {
            manualCurrencyShoppingController.Stop(reason);
        }

        if (buffRotationController.IsRunning)
        {
            buffRotationController.Stop(reason);
        }

        movementController.Stop(reason);
        autorotationController.ReleaseOwnership(reason);
        autorotationController.DeleteManagedPreset(reason);
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
            postActivityDeathRecoveryPending = false;
            interruptedActivity = InterruptedActivityKind.None;
            interruptedTargetId = 0;
            interruptedTargetName = string.Empty;
            interruptedFateCompletionBehavior = FateRunCompletionBehavior.RecoverToBase;
            remainingSilverCompletionsUntilRescan = 0;
            remainingBronzeCompletionsUntilRescan = 0;
            automaticTreasureCofferDisabledForRun = false;
            automaticTreasureCofferRestorePending = false;
            automaticTreasureCofferStartRouteAfterRestore = false;
            automaticTreasureCofferResumeAutomaticCheckAfterRestore = false;
            pendingAutomaticTreasureCofferCheckAfterExternalRecovery = false;
            pendingAutomaticTreasureCofferCheckSource = string.Empty;
            automaticTreasureCofferOriginalSupportJob = 0;
            requiredFreshCofferSurveyRevision = 0;
            automaticTreasureCofferSurveyDeadlineAt = DateTimeOffset.MinValue;
            automaticTreasureCofferStatus = reason;
            postActivityContinuation = null;
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
            if (currentState == FarmSessionState.RunningActiveRevival)
            {
                var interruptedActiveActivity = activeRevivalActivity;
                postActivityRevivalController.Stop("Player died during active FATE/CE revival.");
                if (interruptedActiveActivity == ActiveRevivalActivityKind.Ce)
                {
                    CaptureInterruptedActivity(FarmSessionState.RunningCe);
                    ConfigureDeathRecoveryRaiseWait(FarmSessionState.RunningCe);
                }
                else if (interruptedActiveActivity == ActiveRevivalActivityKind.Fate)
                {
                    CaptureInterruptedActivity(FarmSessionState.RunningFate);
                    ConfigureDeathRecoveryRaiseWait(FarmSessionState.RunningFate);
                }

                lock (gate)
                {
                    activeRevivalActivity = ActiveRevivalActivityKind.None;
                }

                TransitionTo(FarmSessionState.WaitingForDeathRecovery, deathRecoveryController.LastTransition, "Death recovery");
                return;
            }

            if (currentState == FarmSessionState.RunningPostActivityRevival)
            {
                postActivityRevivalController.Stop("Player died during post-activity revival.");
                lock (gate)
                {
                    postActivityDeathRecoveryPending = true;
                }
                TransitionTo(FarmSessionState.WaitingForDeathRecovery, deathRecoveryController.LastTransition, "Death recovery");
                return;
            }

            if (currentState == FarmSessionState.RunningPots
                && (potFarmController.State is PotFarmState.RunningPostActivityRevival or PotFarmState.RunningActiveRevival))
            {
                potFarmController.Stop("Player died during post-activity revival after a pot FATE.");
                lock (gate)
                {
                    postActivityDeathRecoveryPending = true;
                }

                TransitionTo(FarmSessionState.WaitingForDeathRecovery, deathRecoveryController.LastTransition, "Death recovery");
                return;
            }

            if (currentState != FarmSessionState.WaitingForDeathRecovery)
            {
                if (currentState == FarmSessionState.RunningVisibleCofferRoute)
                {
                    ResetAutomaticTreasureCofferSurveyAfterDeath();
                    deathRecoveryController.RequestImmediateRelease("Death interrupted the overworld coffer route; abandoning the route and returning to Base Camp.");
                }

                CaptureInterruptedActivity(currentState);
                ConfigureDeathRecoveryRaiseWait(currentState);
                TransitionTo(FarmSessionState.WaitingForDeathRecovery, deathRecoveryController.LastTransition, "Death recovery");
            }

            return;
        }

        if (currentState == FarmSessionState.WaitingForDeathRecovery)
        {
            TryStartDeathRecoveryRaiseTimeoutAfterActivityEnds();
        }

        if (currentState == FarmSessionState.WaitingForDeathRecovery)
        {
            if (!scanner.Snapshot.IsInSupportedTerritory)
            {
                ClearInterruptedActivity();
                TransitionTo(FarmSessionState.WaitingForSupportedTerritory, "Death recovery completed outside a supported territory.", "Waiting for Supported Territory");
                return;
            }

            if (deathRecoveryController.LastRecoveryMethod == DeathRecoveryMethod.Raised)
            {
                var recoverToBaseAfterRevivalDeath = false;
                lock (gate)
                {
                    recoverToBaseAfterRevivalDeath = postActivityDeathRecoveryPending;
                    postActivityDeathRecoveryPending = false;
                }

                if (recoverToBaseAfterRevivalDeath)
                {
                    ClearInterruptedActivity();
                    StartRecoveryToBase("Death recovery completed after post-activity revival was interrupted.");
                    return;
                }

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
            case FarmSessionState.WaitingForSupportedTerritory:
                TickWaitingForSupportedTerritory();
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
            case FarmSessionState.RunningPostActivityRevival:
                TickPostActivityRevival();
                break;
            case FarmSessionState.RunningActiveRevival:
                TickActiveRevival();
                break;
            case FarmSessionState.SwitchingToFreelancerForCofferSurvey:
                TickSwitchingToFreelancerForCofferSurvey();
                break;
            case FarmSessionState.WaitingForCofferSurvey:
                TickWaitingForCofferSurvey();
                break;
            case FarmSessionState.RestoringOriginalJobAfterCofferSurvey:
                TickRestoringOriginalJobAfterCofferSurvey();
                break;
            case FarmSessionState.RunningVisibleCofferRoute:
                TickRunningVisibleCofferRoute();
                break;
            case FarmSessionState.RunningCurrencyShopping:
                TickRunningCurrencyShopping();
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

        if (configuration.UseReturn && !movementController.CanUseReturnAction)
        {
            SetFailure("Return general action is unavailable while Use Return is enabled.");
            return;
        }

        if (!autorotationController.ValidateConfiguredPreset())
        {
            SetFailure(autorotationController.LastError.Length == 0
                ? "BossMod preset validation failed."
                : autorotationController.LastError);
            return;
        }

        if (!scanner.Snapshot.IsInSupportedTerritory)
        {
            TransitionTo(FarmSessionState.WaitingForSupportedTerritory, "Waiting for a supported territory.", "Waiting for Supported Territory");
            return;
        }

        StartStartupBuffRotation();
    }

    private void TickWaitingForSupportedTerritory()
    {
        if (!scanner.Snapshot.IsInSupportedTerritory)
        {
            logger.DebugThrottled("farm-waiting-supported-territory", WaitLogInterval, "Farm session is still waiting for the player to enter a supported territory.");
            return;
        }

        logger.ResetThrottle("farm-waiting-supported-territory");
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

        if (TryHandlePendingAutomaticTreasureCofferCheckAfterExternalRecovery(reason))
        {
            return;
        }

        TransitionTo(FarmSessionState.SelectingTarget, reason, "Selecting target");
    }

    private void StartRecoveryToBase(string reason)
    {
        if (!scanner.Snapshot.IsInSupportedTerritory)
        {
            TransitionTo(FarmSessionState.WaitingForSupportedTerritory, reason, "Waiting for Supported Territory");
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

                if (TryBeginAutomaticTreasureCofferFlow("Base Camp recovery completed."))
                {
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
        if (!snapshot.IsInSupportedTerritory)
        {
            TransitionTo(FarmSessionState.WaitingForSupportedTerritory, "Left a supported territory while selecting a target.", "Waiting for Supported Territory");
            return;
        }

        if (TryHandlePendingReturnFateGearsetRestore("farm target selection", "target selection"))
        {
            return;
        }

        if (TryStartNextPriorityActivity(snapshot, potCycleSnapshot, now))
        {
            return;
        }

        TransitionTo(FarmSessionState.IdleWaiting, "No eligible CE/FATE target selected.", "Idle waiting");
    }

    private bool TryStartNextPriorityActivity(ScannerSnapshot snapshot, PotCycleSnapshot potCycleSnapshot, DateTimeOffset now)
    {
        configuration.NormalizeAutomationPriority();
        foreach (var activity in configuration.AutomationPriority)
        {
            switch (activity)
            {
                case FarmActivityKind.Pots:
                    if (TryStartOrResumePotControl(now))
                    {
                        return true;
                    }

                    break;
                case FarmActivityKind.CriticalEngagements:
                    if (snapshot.SelectedCriticalEncounter is { } selectedCriticalEncounter)
                    {
                        return TryStartCriticalEncounter(selectedCriticalEncounter, potCycleSnapshot, now);
                    }

                    break;
                case FarmActivityKind.ForkedTower:
                    if (snapshot.SelectedForkedTower is { } selectedForkedTower)
                    {
                        return TryStartCriticalEncounter(selectedForkedTower, potCycleSnapshot, now);
                    }

                    break;
                case FarmActivityKind.Fates:
                    if (snapshot.SelectedFate is { } selectedFate)
                    {
                        return TryStartFate(selectedFate, potCycleSnapshot, now);
                    }

                    break;
            }
        }

        return TryStartCurrencyShopping(now);
    }

    private bool TryStartCriticalEncounter(ActiveCriticalEncounter criticalEncounter, PotCycleSnapshot potCycleSnapshot, DateTimeOffset now)
    {
        var forkedTower = string.Equals(criticalEncounter.AutomationKind, "ForkedTower", StringComparison.OrdinalIgnoreCase);
        if (forkedTower)
        {
            logger.Info($"{BuildLogTag()} op=forked-tower-cutoff-bypass reason=Forked Tower has priority over Pots.");
        }
        else
        {
            var startDecision = potFallbackWindowEvaluator.EvaluateCeStart(potCycleSnapshot, now, scanner.Snapshot.CanRunPotTreasure, scanner.Snapshot.TerritoryKey);
            if (!startDecision.AllowStart)
            {
                logger.DebugThrottled("farm-priority-ce-blocked", WaitLogInterval, $"CE activity skipped by pot fallback policy: {startDecision.Reason}");
                return false;
            }
        }

        if (TryHandlePendingReturnFateGearsetRestore($"starting CE {criticalEncounter.Name}", "CE automation"))
        {
            return true;
        }

        if (forkedTower && !forkedTowerStagingController.Start(criticalEncounter))
        {
            SetFailure(forkedTowerStagingController.LastError.Length == 0
                ? "Failed to start Forked Tower staging automation."
                : forkedTowerStagingController.LastError);
            return true;
        }

        if (!forkedTower && !criticalEngagementAutomationController.Start(criticalEncounter, completeInPlace: true))
        {
            SetFailure(criticalEngagementAutomationController.LastError.Length == 0
                ? "Failed to start CE automation."
                : criticalEngagementAutomationController.LastError);
            return true;
        }

        TransitionTo(FarmSessionState.RunningCe,
            forkedTower
                ? $"Staging for Forked Tower {criticalEncounter.Name} ({criticalEncounter.Id})."
                : $"Running CE {criticalEngagementAutomationController.TargetCeName} ({criticalEngagementAutomationController.TargetCeId}).",
            "Critical Engagement");
        return true;
    }

    private bool TryStartFate(ActiveFate fate, PotCycleSnapshot potCycleSnapshot, DateTimeOffset now)
    {
        var startDecision = potFallbackWindowEvaluator.EvaluateFateStart(potCycleSnapshot, now, scanner.Snapshot.CanRunPotTreasure, scanner.Snapshot.TerritoryKey);
        if (!startDecision.AllowStart)
        {
            logger.DebugThrottled("farm-priority-fate-blocked", WaitLogInterval, $"FATE activity skipped by pot fallback policy: {startDecision.Reason}");
            return false;
        }

        if (TryHandlePendingReturnFateGearsetRestore($"starting FATE {fate.Name}", "FATE automation"))
        {
            return true;
        }

        logger.Info(
            $"{BuildLogTag()} op=fate-start-request caller=FarmSession.TryStartNextPriorityActivity "
            + $"target=\"{fate.Name}\" ({fate.Id}) pot=false fateState={fateAutomationController.State} "
            + $"pausedForRevival={fateAutomationController.IsPausedForRevival}");
        if (!fateAutomationController.Start(fate, FateRunCompletionBehavior.CompleteInPlace))
        {
            SetFailure(fateAutomationController.LastError.Length == 0
                ? "Failed to start FATE automation."
                : fateAutomationController.LastError);
            return true;
        }

        TransitionTo(FarmSessionState.RunningFate,
            $"Running FATE {fateAutomationController.TargetFateName} ({fateAutomationController.TargetFateId}).",
            "FATE");
        return true;
    }

    private void TickCeRun()
    {
        if (forkedTowerStagingController.IsRunning)
        {
            logger.DebugThrottled("farm-running-forked-tower-staging", WaitLogInterval, $"Forked Tower staging is active. state={forkedTowerStagingController.State} transition=\"{forkedTowerStagingController.LastTransition}\".");
            return;
        }

        if (forkedTowerStagingController.State == ForkedTowerStagingState.Failed)
        {
            if (movementController.StuckJumpAttemptsExhausted)
            {
                StartRecoveryToBase("Forked Tower staging remained stuck after three jump attempts.");
                return;
            }

            SetFailure(forkedTowerStagingController.LastError.Length == 0
                ? "Forked Tower staging failed."
                : forkedTowerStagingController.LastError);
            return;
        }

        if (forkedTowerStagingController.LastResult == AutomationRunResult.Preempted)
        {
            StartRecoveryToBase("Forked Tower staging was preempted; returning to Base Camp before selecting another activity.");
            return;
        }

        if (criticalEngagementAutomationController.IsRunning)
        {
            if (TryStartActiveRevival(ActiveRevivalActivityKind.Ce, "active CE revival"))
            {
                return;
            }

            return;
        }

        switch (criticalEngagementAutomationController.LastResult)
        {
            case AutomationRunResult.Completed:
                if (configuration.UseReturn)
                {
                    LatchAutomaticTreasureCofferCheckAfterExternalRecovery("CE");
                }

                StartPostActivityRevival("CE completion", StartPostCeFlow);
                break;
            case AutomationRunResult.Preempted:
                TransitionTo(FarmSessionState.SelectingTarget, criticalEngagementAutomationController.LastTransition, "Selecting target");
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
            if (!fateAutomationController.TargetIsPot
                && TryStartActiveRevival(ActiveRevivalActivityKind.Fate, "active FATE revival"))
            {
                return;
            }

            logger.DebugThrottled("farm-running-fate", WaitLogInterval, $"Farm session is still running FATE automation. State={fateAutomationController.State} target={fateAutomationController.TargetFateName} ({fateAutomationController.TargetFateId}).");
            return;
        }

        logger.ResetThrottle("farm-running-fate");

        switch (fateAutomationController.LastResult)
        {
            case AutomationRunResult.Completed:
                if (!fateAutomationController.LastTargetIsPot && fateAutomationController.LastCompletionBehavior == FateRunCompletionBehavior.RecoverToBase)
                {
                    LatchAutomaticTreasureCofferCheckAfterExternalRecovery("FATE");
                }

                StartPostActivityRevival("FATE completion", StartPostFateFlow);
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
        DecrementAutomaticTreasureCofferRescanCounters("CE completion");
        lock (gate)
        {
            runBuffRotationAfterRecovery = configuration.EnableBuffRotation;
        }

        StartRecoveryToBase("CE complete; post-activity revival finished, returning to Base Camp.");
    }

    private void StartPostFateFlow()
    {
        DecrementAutomaticTreasureCofferRescanCounters("FATE completion");
        if (configuration.UseReturn)
        {
            LatchAutomaticTreasureCofferCheckAfterExternalRecovery("FATE");
        }

        lock (gate)
        {
            runBuffRotationAfterRecovery = configuration.EnableBuffRotation;
        }

        StartRecoveryToBase("FATE complete; post-activity revival finished, returning to Base Camp.");
    }

    private void StartPostActivityRevival(string context, Action continuation)
    {
        if (!configuration.EnablePostActivityRevival)
        {
            continuation();
            return;
        }

        if (!postActivityRevivalController.Start(context))
        {
            logger.Warning($"{BuildLogTag()} op=revival-start-failed context=\"{context}\" reason={postActivityRevivalController.LastError}");
            continuation();
            return;
        }

        lock (gate)
        {
            postActivityContinuation = continuation;
        }

        TransitionTo(FarmSessionState.RunningPostActivityRevival, postActivityRevivalController.LastTransition, "Post-activity revival");
    }

    private Action? postActivityContinuation;

    private void TickPostActivityRevival()
    {
        if (postActivityRevivalController.IsRunning)
        {
            return;
        }

        Action? continuation;
        lock (gate)
        {
            continuation = postActivityContinuation;
            postActivityContinuation = null;
        }

        logger.Info($"{BuildLogTag()} op=revival-complete state={postActivityRevivalController.State} transition=\"{postActivityRevivalController.LastTransition}\" error=\"{postActivityRevivalController.LastError}\"");
        continuation?.Invoke();
    }

    private bool TryStartActiveRevival(ActiveRevivalActivityKind activity, string context)
    {
        if (!configuration.EnablePostActivityRevival || postActivityRevivalController.IsRunning)
        {
            return false;
        }

        if ((activity == ActiveRevivalActivityKind.Ce && !criticalEngagementAutomationController.CanPauseForRevival)
            || (activity == ActiveRevivalActivityKind.Fate && !fateAutomationController.CanPauseForRevival))
        {
            return false;
        }

        if (!postActivityRevivalController.StartActive(context))
        {
            return false;
        }

        var paused = activity switch
        {
            ActiveRevivalActivityKind.Ce => criticalEngagementAutomationController.PauseForRevival("Pausing CE combat for active revival."),
            ActiveRevivalActivityKind.Fate => fateAutomationController.PauseForRevival("Pausing FATE combat for active revival."),
            _ => false,
        };
        if (!paused)
        {
            postActivityRevivalController.Stop("Could not pause the active FATE/CE before revival.");
            return false;
        }

        lock (gate)
        {
            activeRevivalActivity = activity;
        }

        TransitionTo(FarmSessionState.RunningActiveRevival, postActivityRevivalController.LastTransition, "Active revival");
        return true;
    }

    private void TickActiveRevival()
    {
        if (postActivityRevivalController.IsRunning)
        {
            return;
        }

        ActiveRevivalActivityKind activity;
        lock (gate)
        {
            activity = activeRevivalActivity;
            activeRevivalActivity = ActiveRevivalActivityKind.None;
        }

        var resumed = activity switch
        {
            ActiveRevivalActivityKind.Ce => criticalEngagementAutomationController.ResumeAfterRevival("Active revival completed; resuming CE combat."),
            ActiveRevivalActivityKind.Fate => fateAutomationController.ResumeAfterRevival("Active revival completed; resuming FATE combat."),
            _ => false,
        };

        logger.Info($"{BuildLogTag()} op=active-revival-complete state={postActivityRevivalController.State} transition=\"{postActivityRevivalController.LastTransition}\" resumed={resumed} error=\"{postActivityRevivalController.LastError}\"");
        TransitionTo(activity switch
        {
            ActiveRevivalActivityKind.Ce => FarmSessionState.RunningCe,
            ActiveRevivalActivityKind.Fate => FarmSessionState.RunningFate,
            _ => FarmSessionState.SelectingTarget,
        }, resumed ? "Active revival complete; activity resumed." : "Active revival complete; activity requires re-evaluation.", resumed ? "FATE/CE" : "Selecting target");
    }

    private void StartPostActivityFlow()
    {
        
        if (!configuration.EnableBuffRotation)
        {
            if (TryHandlePendingAutomaticTreasureCofferCheckAfterExternalRecovery("Activity complete."))
            {
                return;
            }

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
        if (!snapshot.IsInSupportedTerritory)
        {
            logger.ResetThrottle("farm-idle-waiting");
            TransitionTo(FarmSessionState.WaitingForSupportedTerritory, "Left a supported territory while idle waiting.", "Waiting for Supported Territory");
            return;
        }

        if (TryHandlePendingReturnFateGearsetRestore("farm idle waiting", "idle farm loop"))
        {
            return;
        }

        if (TryStartNextPriorityActivity(snapshot, potCycleSnapshot, now))
        {
            logger.ResetThrottle("farm-idle-waiting");
            return;
        }

        if (now - lastIdleScanAt >= IdleRescanInterval)
        {
            lock (gate)
            {
                lastIdleScanAt = now;
            }

            logger.DebugThrottled("farm-idle-waiting", WaitLogInterval, "Farm session is idle waiting for an eligible prioritized activity.");
        }
    }

    private void TickPotRun()
    {
        if (!potFarmController.IsRunning)
        {
            switch (potFarmController.LastResult)
            {
                case PotFarmRunResult.Completed:
                    LatchAutomaticTreasureCofferCheckAfterExternalRecovery("Pot");
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

    private bool TryStartCurrencyShopping(DateTimeOffset now)
    {
        if (!scanner.Snapshot.CanUseShopping)
        {
            return false;
        }

        if (!manualCurrencyShoppingController.NeedsControlNow(now, allowDuringFarmSession: true, out var reason))
        {
            return false;
        }

        if (manualCurrencyShoppingController.IsRunning)
        {
            TransitionTo(FarmSessionState.RunningCurrencyShopping, manualCurrencyShoppingController.Status, "Currency Shopping");
            return true;
        }

        if (!manualCurrencyShoppingController.Start())
        {
            logger.Warning($"{BuildLogTag()} op=shopping-start-blocked reason={reason}");
            return false;
        }

        logger.Info($"{BuildLogTag()} op=shopping-start reason={reason}");
        TransitionTo(FarmSessionState.RunningCurrencyShopping, manualCurrencyShoppingController.Status, "Currency Shopping");
        return true;
    }

    private static bool CanRunAnyAutomation(ScannerSnapshot snapshot)
        => snapshot.CanFarmFates
            || snapshot.CanFarmCriticalEncounters
            || snapshot.CanRunPotTreasure
            || snapshot.CanRunVisibleCofferRoute
            || snapshot.CanUseShopping
            || snapshot.CanRunBuffRotation;

    private void TickRunningCurrencyShopping()
    {
        if (manualCurrencyShoppingController.IsRunning)
        {
            logger.DebugThrottled("farm-running-shopping", WaitLogInterval, $"Farm session is still running currency shopping. status={manualCurrencyShoppingController.Status}");
            return;
        }

        logger.ResetThrottle("farm-running-shopping");

        switch (manualCurrencyShoppingController.LastStopKind)
        {
            case ManualCurrencyShoppingController.ShoppingStopKind.Failed:
                logger.Warning($"{BuildLogTag()} op=shopping-warning reason={manualCurrencyShoppingController.Status}");
                break;
            case ManualCurrencyShoppingController.ShoppingStopKind.Skipped:
                logger.Info($"{BuildLogTag()} op=shopping-skipped reason={manualCurrencyShoppingController.Status}");
                break;
            case ManualCurrencyShoppingController.ShoppingStopKind.Completed:
                logger.Info($"{BuildLogTag()} op=shopping-complete reason={manualCurrencyShoppingController.Status}");
                break;
        }

        TransitionTo(FarmSessionState.SelectingTarget, manualCurrencyShoppingController.Status, "Selecting target");
    }

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

    private void ConfigureDeathRecoveryRaiseWait(FarmSessionState currentState)
    {
        if (currentState == FarmSessionState.RunningVisibleCofferRoute)
        {
            return;
        }

        InterruptedActivityKind activity;
        lock (gate)
        {
            activity = interruptedActivity;
        }

        if (activity is InterruptedActivityKind.Ce or InterruptedActivityKind.Fate or InterruptedActivityKind.PotFate)
        {
            deathRecoveryController.WaitIndefinitelyForRaise($"Waiting for interrupted {activity} to end before starting the five-minute release timer.");
        }
    }

    private void TryStartDeathRecoveryRaiseTimeoutAfterActivityEnds()
    {
        InterruptedActivityKind activity;
        uint targetId;
        lock (gate)
        {
            activity = interruptedActivity;
            targetId = interruptedTargetId;
        }

        if (activity == InterruptedActivityKind.None || targetId == 0)
        {
            return;
        }

        var snapshot = scanner.Snapshot;
        var activityStillActive = activity switch
        {
            InterruptedActivityKind.Ce => snapshot.FindCriticalEncounter(targetId) != null,
            InterruptedActivityKind.Fate => snapshot.FindFateRunTarget(targetId, isPotTarget: false) != null,
            InterruptedActivityKind.PotFate => snapshot.FindFateRunTarget(targetId, isPotTarget: true) != null,
            _ => false,
        };

        if (!activityStillActive)
        {
            deathRecoveryController.StartRaiseTimeout($"Interrupted {activity} target {targetId} ended while dead.");
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
        if (TryHandlePendingReturnFateGearsetRestore($"resuming CE {ceTarget.Name} ({ceTarget.Id}) after raise", "CE resume"))
        {
            return;
        }

        if (!criticalEngagementAutomationController.Start(ceTarget, resumeAfterRaise: true, completeInPlace: true))
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
                potFarmController.Stop("Interrupted pot FATE was no longer active after death recovery.");
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
        if (TryHandlePendingReturnFateGearsetRestore($"resuming {activityLabel} {fateTarget.Name} ({fateTarget.Id}) after raise", $"{activityLabel} resume"))
        {
            return;
        }

        if (!fateAutomationController.ResumeAfterRaise(
                fateTarget,
                completionBehavior,
                $"Resuming {activityLabel} {fateTarget.Name} ({fateTarget.Id}) after raise."))
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

        if (!potFarmController.IsRunning && !potFarmController.Start(startedByFarmSession: true))
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

    private bool TryHandlePendingReturnFateGearsetRestore(string reason, string nextActivity)
    {
        if (!dangerousTreasureTravelController.HasEquippedNinjaGearset
            && !dangerousTreasureTravelController.IsFateGearsetRestorePending)
        {
            logger.ResetThrottle("farm-return-fate-gearset-restore");
            return false;
        }

        dangerousTreasureTravelController.RestoreFateGearset(reason);
        dangerousTreasureTravelController.TryProcessPendingFateGearsetRestore(nextActivity);
        if (!dangerousTreasureTravelController.IsFateGearsetRestorePending)
        {
            logger.ResetThrottle("farm-return-fate-gearset-restore");
            return false;
        }

        logger.DebugThrottled(
            "farm-return-fate-gearset-restore",
            WaitLogInterval,
            $"Farm session is waiting to restore the configured FATE gearset before {nextActivity}. reason=\"{dangerousTreasureTravelController.LastFateGearsetRestoreReason}\" targetGearset={dangerousTreasureTravelController.PendingFateGearsetNumber} restoreError={(string.IsNullOrEmpty(dangerousTreasureTravelController.LastFateGearsetRestoreError) ? "none" : dangerousTreasureTravelController.LastFateGearsetRestoreError)} currentClassJob={gameActionController.CurrentClassJobId}.");
        return true;
    }

    private static FarmSessionState MapPotFarmState(PotFarmState state)
        => state switch
        {
            PotFarmState.WaitingForPredictedWindow or PotFarmState.Bootstrapping => FarmSessionState.WaitingForPredictedPotWindow,
            PotFarmState.TravelingToSpawn or PotFarmState.WaitingAtSpawn => FarmSessionState.WaitingAtPotSpawn,
            PotFarmState.RunningPotFate or PotFarmState.RunningActiveRevival or PotFarmState.RunningPostActivityRevival or PotFarmState.RecoveringToBase => FarmSessionState.RunningPots,
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

    private bool TryBeginAutomaticTreasureCofferFlow(string context)
    {
        if (!configuration.EnableAutomaticTreasureCofferRoute)
        {
            SetAutomaticTreasureCofferStatus("Automatic overworld coffer mode is disabled.");
            return false;
        }

        if (!scanner.Snapshot.IsInSupportedTerritory || !scanner.Snapshot.CanRunVisibleCofferRoute)
        {
            SetAutomaticTreasureCofferStatus(scanner.Snapshot.IsInSupportedTerritory
                ? $"Automatic overworld coffer mode is unavailable in {scanner.Snapshot.TerritoryDisplayName}."
                : "Automatic overworld coffer mode requires a supported Occult Crescent territory.");
            return false;
        }

        var dependencyReport = Plugin.Current?.GetNormalAutomationDependencyReport();
        if (dependencyReport is { IsReady: false })
        {
            SetAutomaticTreasureCofferStatus(dependencyReport.FailureSummary);
            return false;
        }

        if (!TryVerifyAutomaticTreasureCofferInventory(out var inventoryReason))
        {
            SetAutomaticTreasureCofferStatus(inventoryReason);
            return false;
        }

        if (TryBeginAutomaticTreasureCofferRestoreRetry(context))
        {
            return true;
        }

        if (AutomaticTreasureCofferStatus.DisabledForCurrentRun)
        {
            SetAutomaticTreasureCofferStatus("Automatic overworld coffer mode is disabled for this farm-session run because Freelancer is below level 10.");
            return false;
        }

        var surveySnapshot = treasureHintTracker.CofferSurveySnapshot;
        logger.Info($"{BuildLogTag()} op=auto-coffer-survey-decision context=\"{context}\" surveyRevision={surveySnapshot.Revision} requiredFreshRevision={requiredFreshCofferSurveyRevision} silver={surveySnapshot.SilverCount}/{configuration.AutomaticTreasureCofferSilverThreshold} bronze={surveySnapshot.BronzeCount}/{configuration.AutomaticTreasureCofferBronzeThreshold} silverRemaining={AutomaticTreasureCofferStatus.RemainingSilverCompletionsUntilRescan} bronzeRemaining={AutomaticTreasureCofferStatus.RemainingBronzeCompletionsUntilRescan} disabledForRun={AutomaticTreasureCofferStatus.DisabledForCurrentRun}");
        if (surveySnapshot.Revision >= requiredFreshCofferSurveyRevision && SurveyMeetsAutomaticTreasureCofferThresholds(surveySnapshot))
        {
            SetAutomaticTreasureCofferStatus($"Survey silver={surveySnapshot.SilverCount} bronze={surveySnapshot.BronzeCount} met automatic coffer thresholds.");
            return StartAutomaticVisibleCofferRoute(context, surveySnapshot);
        }

        if (!IsAutomaticTreasureCofferRescanDue(surveySnapshot))
        {
            SetAutomaticTreasureCofferStatus($"Waiting for more completions before the next coffer rescan. silverRemaining={AutomaticTreasureCofferStatus.RemainingSilverCompletionsUntilRescan} bronzeRemaining={AutomaticTreasureCofferStatus.RemainingBronzeCompletionsUntilRescan}.");
            return false;
        }

        return BeginAutomaticTreasureCofferSurvey(context, surveySnapshot.Revision);
    }

    private bool TryVerifyAutomaticTreasureCofferInventory(out string reason)
    {
        reason = string.Empty;
        if (!InventorySpaceVerifier.TryGetFreeNormalInventorySlots(out var freeSlots, out var inventoryError))
        {
            reason = inventoryError.Length == 0
                ? $"Automatic overworld coffer mode is skipped until inventory has at least {RequiredAutomaticCofferInventoryFreeSlots} verified free slots."
                : $"Automatic overworld coffer mode is skipped until inventory has at least {RequiredAutomaticCofferInventoryFreeSlots} verified free slots. verification={inventoryError}.";
            return false;
        }

        if (freeSlots >= RequiredAutomaticCofferInventoryFreeSlots)
        {
            return true;
        }

        reason = $"Automatic overworld coffer mode is skipped until inventory has at least {RequiredAutomaticCofferInventoryFreeSlots} free slots. freeSlots={freeSlots}.";
        return false;
    }

    private bool TryBeginAutomaticTreasureCofferRestoreRetry(string context)
    {
        if (!AutomaticTreasureCofferStatus.RestoreRetryPending)
        {
            return false;
        }

        if (!gameActionController.TryGetCurrentSupportJob(out var currentJob))
        {
            SetAutomaticTreasureCofferStatus("Automatic coffer restore retry is waiting because the current phantom job could not be read.");
            return false;
        }

        if (currentJob == automaticTreasureCofferOriginalSupportJob)
        {
            lock (gate)
            {
                automaticTreasureCofferRestorePending = false;
                automaticTreasureCofferResumeAutomaticCheckAfterRestore = false;
            }

            SetAutomaticTreasureCofferStatus($"Original phantom job {currentJob} was already restored before retry.");
            return false;
        }

        if (!gameActionController.TryChangeSupportJob(automaticTreasureCofferOriginalSupportJob, $"automatic coffer restore retry after {context}"))
        {
            SetAutomaticTreasureCofferStatus($"Automatic coffer restore retry failed to request original phantom job {automaticTreasureCofferOriginalSupportJob}; will try again next recovery.");
            return false;
        }

        lock (gate)
        {
            automaticTreasureCofferResumeAutomaticCheckAfterRestore = true;
        }

        TransitionTo(FarmSessionState.RestoringOriginalJobAfterCofferSurvey, $"Retrying original phantom job restoration after {context}.", "Restoring original phantom job");
        return true;
    }

    private bool BeginAutomaticTreasureCofferSurvey(string context, int currentSurveyRevision)
    {
        if (!gameActionController.TryReadSupportJobState(out var currentJob, out var supportJobLevels))
        {
            SetAutomaticTreasureCofferStatus("Automatic coffer survey skipped because phantom job state could not be read.");
            return false;
        }

        var freelancerLevel = supportJobLevels.Length > GameActionController.FreelancerSupportJobId
            ? supportJobLevels[GameActionController.FreelancerSupportJobId]
            : (byte)0;
        if (freelancerLevel < 10)
        {
            lock (gate)
            {
                automaticTreasureCofferDisabledForRun = true;
            }

            logger.Warning($"{BuildLogTag()} op=auto-coffer-disabled reason=freelancer-level-too-low freelancerLevel={freelancerLevel} requiredLevel=10");
            SetAutomaticTreasureCofferStatus($"Automatic overworld coffer mode disabled for this farm-session run because Freelancer is only level {freelancerLevel}.");
            return false;
        }

        lock (gate)
        {
            automaticTreasureCofferOriginalSupportJob = currentJob;
            automaticTreasureCofferStartRouteAfterRestore = false;
            automaticTreasureCofferResumeAutomaticCheckAfterRestore = false;
            requiredFreshCofferSurveyRevision = Math.Max(requiredFreshCofferSurveyRevision, currentSurveyRevision + 1);
            automaticTreasureCofferSurveyDeadlineAt = DateTimeOffset.MinValue;
            automaticTreasureCofferNextSurveyActionAttemptAt = DateTimeOffset.MinValue;
        }

        if (currentJob == GameActionController.FreelancerSupportJobId)
        {
            logger.Info($"{BuildLogTag()} op=auto-coffer-survey-job-skip currentSupportJob={currentJob} targetSupportJob={GameActionController.FreelancerSupportJobId} originalSupportJob={automaticTreasureCofferOriginalSupportJob} freelancerLevel={freelancerLevel} surveyRevision={currentSurveyRevision} requiredFreshRevision={requiredFreshCofferSurveyRevision} reason=already-freelancer");
            TransitionTo(FarmSessionState.SwitchingToFreelancerForCofferSurvey, $"Waiting for Occult Treasuresight after {context}.", "Waiting for coffer survey action");
            return true;
        }

        if (!gameActionController.TryChangeSupportJob(GameActionController.FreelancerSupportJobId, $"automatic coffer survey after {context}"))
        {
            SetAutomaticTreasureCofferStatus($"Automatic coffer survey could not switch to Freelancer after {context}; will retry next recovery.");
            return false;
        }

        logger.Info($"{BuildLogTag()} op=auto-coffer-survey-job-switch-request fromSupportJob={currentJob} toSupportJob={GameActionController.FreelancerSupportJobId} originalSupportJob={automaticTreasureCofferOriginalSupportJob} freelancerLevel={freelancerLevel} surveyRevision={currentSurveyRevision} requiredFreshRevision={requiredFreshCofferSurveyRevision} context=\"{context}\"");
        TransitionTo(FarmSessionState.SwitchingToFreelancerForCofferSurvey, $"Switching phantom job to Freelancer before Occult Treasuresight after {context}.", "Switching to Freelancer");
        return true;
    }

    private bool BeginAutomaticTreasureCofferSurveyWait()
    {
        if (!gameActionController.TryExecuteAction(GameActionController.OccultTreasuresightActionId, "automatic coffer survey"))
        {
            return false;
        }

        lock (gate)
        {
            automaticTreasureCofferSurveyDeadlineAt = DateTimeOffset.UtcNow + CofferSurveyWaitTimeout;
        }

        TransitionTo(FarmSessionState.WaitingForCofferSurvey, "Waiting for an Occult Treasuresight coffer survey result.", "Waiting for coffer survey");
        return true;
    }

    private void TickSwitchingToFreelancerForCofferSurvey()
    {
        var now = DateTimeOffset.UtcNow;
        // Use the game log as confirmation even when ActionManager reports a failed dispatch.
        if (treasureHintTracker.TryGetLatestCofferSurveySince(requiredFreshCofferSurveyRevision - 1, out var surveySnapshot) && surveySnapshot != null)
        {
            ApplyAutomaticTreasureCofferSurveyResult(surveySnapshot);
            return;
        }

        if (gameActionController.TryGetCurrentSupportJob(out var currentJob) && currentJob == GameActionController.FreelancerSupportJobId)
        {
            if (!gameActionController.CanUseAction(GameActionController.OccultTreasuresightActionId))
            {
                logger.DebugThrottled("auto-coffer-survey-action-ready", WaitLogInterval,
                    $"{BuildLogTag()} op=auto-coffer-survey-wait reason=action-unavailable actionId={GameActionController.OccultTreasuresightActionId}");
            }
            else if (now >= automaticTreasureCofferNextSurveyActionAttemptAt)
            {
                automaticTreasureCofferNextSurveyActionAttemptAt = now + CofferSurveyActionRetryInterval;
                if (BeginAutomaticTreasureCofferSurveyWait())
                {
                    return;
                }

                logger.DebugThrottled("auto-coffer-survey-action-dispatch", WaitLogInterval,
                    $"{BuildLogTag()} op=auto-coffer-survey-wait reason=dispatch-failed actionId={GameActionController.OccultTreasuresightActionId}");
            }
        }

        if (now - stateEnteredAt >= SupportJobSwitchTimeout)
        {
            ContinueAfterAutomaticTreasureCofferSkip("Switching to Freelancer or waiting for Occult Treasuresight for automatic coffer survey timed out; retrying on the next recovery.");
        }
    }

    private void TickWaitingForCofferSurvey()
    {
        if (treasureHintTracker.TryGetLatestCofferSurveySince(requiredFreshCofferSurveyRevision - 1, out var surveySnapshot) && surveySnapshot != null)
        {
            ApplyAutomaticTreasureCofferSurveyResult(surveySnapshot);
            return;
        }

        if (automaticTreasureCofferSurveyDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= automaticTreasureCofferSurveyDeadlineAt)
        {
            ContinueAfterAutomaticTreasureCofferSkip("Occult Treasuresight did not produce a coffer survey log message before timeout; retrying on the next recovery.");
        }
    }

    private void ApplyAutomaticTreasureCofferSurveyResult(TreasureCofferSurveySnapshot surveySnapshot)
    {
        UpdateAutomaticTreasureCofferRescanCounters(surveySnapshot);
        var shouldStartRoute = SurveyMeetsAutomaticTreasureCofferThresholds(surveySnapshot);
        lock (gate)
        {
            automaticTreasureCofferStartRouteAfterRestore = shouldStartRoute;
        }

        SetAutomaticTreasureCofferStatus($"Survey silver={surveySnapshot.SilverCount} bronze={surveySnapshot.BronzeCount} thresholds={(shouldStartRoute ? "met" : "not-met")} silverRemaining={AutomaticTreasureCofferStatus.RemainingSilverCompletionsUntilRescan} bronzeRemaining={AutomaticTreasureCofferStatus.RemainingBronzeCompletionsUntilRescan}.");
        logger.Info($"{BuildLogTag()} op=auto-coffer-survey-result revision={surveySnapshot.Revision} logMessageId={surveySnapshot.LogMessageId} silver={surveySnapshot.SilverCount}/{configuration.AutomaticTreasureCofferSilverThreshold} bronze={surveySnapshot.BronzeCount}/{configuration.AutomaticTreasureCofferBronzeThreshold} routeFate={(shouldStartRoute ? "start-after-restore" : "decline")} originalSupportJob={automaticTreasureCofferOriginalSupportJob}");

        if (automaticTreasureCofferOriginalSupportJob == GameActionController.FreelancerSupportJobId)
        {
            if (shouldStartRoute && StartAutomaticVisibleCofferRoute("survey result", surveySnapshot))
            {
                return;
            }

            TransitionTo(FarmSessionState.SelectingTarget, shouldStartRoute
                ? "Automatic coffer survey met thresholds, but the route could not start."
                : "Automatic coffer survey did not meet thresholds.", "Selecting target");
            return;
        }

        if (!gameActionController.TryChangeSupportJob(automaticTreasureCofferOriginalSupportJob, "automatic coffer post-survey restore"))
        {
            lock (gate)
            {
                automaticTreasureCofferRestorePending = true;
            }

            ContinueAfterAutomaticTreasureCofferSkip($"Automatic coffer survey finished, but restoring the original phantom job {automaticTreasureCofferOriginalSupportJob} failed; will retry next recovery.");
            return;
        }

        logger.Info($"{BuildLogTag()} op=auto-coffer-survey-job-restore-request fromSupportJob={GameActionController.FreelancerSupportJobId} toSupportJob={automaticTreasureCofferOriginalSupportJob} surveyRevision={surveySnapshot.Revision} routeAfterRestore={shouldStartRoute}");
        TransitionTo(FarmSessionState.RestoringOriginalJobAfterCofferSurvey, "Restoring the original phantom job after automatic coffer survey.", "Restoring original phantom job");
    }

    private void TickRestoringOriginalJobAfterCofferSurvey()
    {
        if (gameActionController.TryGetCurrentSupportJob(out var currentJob) && currentJob == automaticTreasureCofferOriginalSupportJob)
        {
            var startRouteAfterRestore = automaticTreasureCofferStartRouteAfterRestore;
            var resumeAutomaticCheck = automaticTreasureCofferResumeAutomaticCheckAfterRestore;
            lock (gate)
            {
                automaticTreasureCofferRestorePending = false;
                automaticTreasureCofferResumeAutomaticCheckAfterRestore = false;
            }

            logger.Info($"{BuildLogTag()} op=auto-coffer-survey-job-restored currentSupportJob={currentJob} originalSupportJob={automaticTreasureCofferOriginalSupportJob} routeAfterRestore={startRouteAfterRestore} resumeAutomaticCheck={resumeAutomaticCheck} restorePending={automaticTreasureCofferRestorePending}");
            if (startRouteAfterRestore && StartAutomaticVisibleCofferRoute("post-survey restore", treasureHintTracker.CofferSurveySnapshot))
            {
                return;
            }

            if (resumeAutomaticCheck && TryBeginAutomaticTreasureCofferFlow("original phantom job restore completed"))
            {
                return;
            }

            TransitionTo(FarmSessionState.SelectingTarget, startRouteAfterRestore
                ? "Original phantom job restored after automatic coffer survey, but the route could not start."
                : "Original phantom job restored after automatic coffer survey.", "Selecting target");
            return;
        }

        if (DateTimeOffset.UtcNow - stateEnteredAt >= SupportJobSwitchTimeout)
        {
            lock (gate)
            {
                automaticTreasureCofferRestorePending = true;
                automaticTreasureCofferResumeAutomaticCheckAfterRestore = false;
            }

            ContinueAfterAutomaticTreasureCofferSkip($"Restoring the original phantom job {automaticTreasureCofferOriginalSupportJob} timed out; will retry on the next recovery.");
        }
    }

    private bool StartAutomaticVisibleCofferRoute(string context, TreasureCofferSurveySnapshot surveySnapshot)
    {
        if (treasureCofferFarmController.IsRunning)
        {
            TransitionTo(FarmSessionState.RunningVisibleCofferRoute, "Waiting for the automatic overworld coffer route to finish.", "Overworld coffer route");
            return true;
        }

        if (!treasureCofferFarmController.Start(startedByFarmSession: true))
        {
            SetAutomaticTreasureCofferStatus($"Automatic coffer route could not start after {context}. reason={treasureCofferFarmController.LastError}");
            return false;
        }

        logger.Info($"{BuildLogTag()} op=auto-coffer-route-start context=\"{context}\" surveySilver={surveySnapshot.SilverCount} surveyBronze={surveySnapshot.BronzeCount}");
        TransitionTo(FarmSessionState.RunningVisibleCofferRoute, $"Starting automatic overworld coffer route after {context}.", "Overworld coffer route");
        return true;
    }

    private void TickRunningVisibleCofferRoute()
    {
        if (treasureCofferFarmController.IsRunning)
        {
            return;
        }

        switch (treasureCofferFarmController.State)
        {
            case TreasureCofferFarmState.Completed:
                dangerousTreasureTravelController.RestoreFateGearset($"automatic overworld coffer route completed: {treasureCofferFarmController.LastTransition}");
                ResetAutomaticTreasureCofferSurveyTrustAfterRoute();
                if (treasureCofferFarmController.LastResult == TreasureCofferFarmResult.ReturnedToBase)
                {
                    if (TryBeginAutomaticTreasureCofferFlow("automatic overworld coffer route recovery completed"))
                    {
                        return;
                    }

                    TransitionTo(FarmSessionState.SelectingTarget, "Automatic overworld coffer route completed.", "Selecting target");
                    return;
                }

                logger.Info($"{BuildLogTag()} op=auto-coffer-route-follow-up-deferred result={treasureCofferFarmController.LastResult} reason=base-recovery-not-confirmed");
                StartRecoveryToBase("Automatic overworld coffer route ended without a confirmed Base Camp return; recovering before the next automatic coffer decision.");
                return;
            case TreasureCofferFarmState.Stopped:
                dangerousTreasureTravelController.RestoreFateGearset($"automatic overworld coffer route stopped: {treasureCofferFarmController.LastTransition}");
                TransitionTo(FarmSessionState.SelectingTarget, treasureCofferFarmController.LastTransition, "Selecting target");
                return;
            case TreasureCofferFarmState.Failed:
                dangerousTreasureTravelController.RestoreFateGearset($"automatic overworld coffer route failed: {(treasureCofferFarmController.LastError.Length == 0 ? treasureCofferFarmController.LastTransition : treasureCofferFarmController.LastError)}");
                TransitionTo(FarmSessionState.SelectingTarget, treasureCofferFarmController.LastError.Length == 0
                    ? treasureCofferFarmController.LastTransition
                    : treasureCofferFarmController.LastError, "Selecting target");
                return;
        }

        TransitionTo(FarmSessionState.SelectingTarget, treasureCofferFarmController.LastTransition, "Selecting target");
    }

    private void ContinueAfterAutomaticTreasureCofferSkip(string reason)
    {
        if (automaticTreasureCofferOriginalSupportJob != GameActionController.FreelancerSupportJobId
            && gameActionController.TryGetCurrentSupportJob(out var currentJob)
            && currentJob != automaticTreasureCofferOriginalSupportJob)
        {
            if (gameActionController.TryChangeSupportJob(automaticTreasureCofferOriginalSupportJob, "automatic coffer skip restore"))
            {
                lock (gate)
                {
                    automaticTreasureCofferStartRouteAfterRestore = false;
                    automaticTreasureCofferResumeAutomaticCheckAfterRestore = false;
                }

                SetAutomaticTreasureCofferStatus(reason);
                TransitionTo(FarmSessionState.RestoringOriginalJobAfterCofferSurvey, $"{reason} Restoring the original phantom job before resuming farming.", "Restoring original phantom job");
                return;
            }

            lock (gate)
            {
                automaticTreasureCofferRestorePending = true;
            }
        }

        SetAutomaticTreasureCofferStatus(reason);
        TransitionTo(FarmSessionState.SelectingTarget, reason, "Selecting target");
    }

    private bool TryHandlePendingAutomaticTreasureCofferCheckAfterExternalRecovery(string fallbackReason)
    {
        string recoverySource;
        lock (gate)
        {
            if (!pendingAutomaticTreasureCofferCheckAfterExternalRecovery)
            {
                return false;
            }

            pendingAutomaticTreasureCofferCheckAfterExternalRecovery = false;
            recoverySource = pendingAutomaticTreasureCofferCheckSource;
            pendingAutomaticTreasureCofferCheckSource = string.Empty;
        }

        logger.Info($"{BuildLogTag()} op=auto-coffer-recovery-consume source={FormatAutoCofferRecoverySource(recoverySource)}");
        if (TryBeginAutomaticTreasureCofferFlow($"{FormatAutoCofferRecoverySource(recoverySource)} recovery completed."))
        {
            return true;
        }

        TransitionTo(FarmSessionState.SelectingTarget, fallbackReason, "Selecting target");
        return true;
    }

    private void LatchAutomaticTreasureCofferCheckAfterExternalRecovery(string source)
    {
        lock (gate)
        {
            pendingAutomaticTreasureCofferCheckAfterExternalRecovery = true;
            pendingAutomaticTreasureCofferCheckSource = source;
        }

        logger.Info($"{BuildLogTag()} op=auto-coffer-recovery-latch source={FormatAutoCofferRecoverySource(source)}");
    }

    private static string FormatAutoCofferRecoverySource(string source)
        => string.IsNullOrWhiteSpace(source) ? "external-recovery" : source;

    private void ResetAutomaticTreasureCofferSurveyTrustAfterRoute()
    {
        var currentSurveyRevision = treasureHintTracker.CofferSurveySnapshot.Revision;
        lock (gate)
        {
            remainingSilverCompletionsUntilRescan = 0;
            remainingBronzeCompletionsUntilRescan = 0;
            requiredFreshCofferSurveyRevision = currentSurveyRevision + 1;
            automaticTreasureCofferStartRouteAfterRestore = false;
            automaticTreasureCofferSurveyDeadlineAt = DateTimeOffset.MinValue;
        }

        SetAutomaticTreasureCofferStatus("Automatic overworld coffer route completed; a fresh Occult Treasuresight survey is required on the next base-camp recovery.");
    }

    private void ResetAutomaticTreasureCofferSurveyAfterDeath()
    {
        var currentSurveyRevision = treasureHintTracker.CofferSurveySnapshot.Revision;
        lock (gate)
        {
            remainingSilverCompletionsUntilRescan = 0;
            remainingBronzeCompletionsUntilRescan = 0;
            requiredFreshCofferSurveyRevision = currentSurveyRevision + 1;
            automaticTreasureCofferRestorePending = false;
            automaticTreasureCofferStartRouteAfterRestore = false;
            automaticTreasureCofferResumeAutomaticCheckAfterRestore = false;
            automaticTreasureCofferOriginalSupportJob = 0;
            automaticTreasureCofferSurveyDeadlineAt = DateTimeOffset.MinValue;
            automaticTreasureCofferNextSurveyActionAttemptAt = DateTimeOffset.MinValue;
            pendingAutomaticTreasureCofferCheckAfterExternalRecovery = false;
            pendingAutomaticTreasureCofferCheckSource = string.Empty;
        }

        logger.Info($"{BuildLogTag()} op=auto-coffer-death-reset surveyRevision={currentSurveyRevision} requiredFreshRevision={currentSurveyRevision + 1} reason=overworld-route-abandoned");
        SetAutomaticTreasureCofferStatus("Overworld coffer route abandoned after death; a fresh Occult Treasuresight survey is required.");
    }

    private void UpdateAutomaticTreasureCofferRescanCounters(TreasureCofferSurveySnapshot surveySnapshot)
    {
        var silverDeficit = GetAutomaticTreasureCofferDeficit(configuration.AutomaticTreasureCofferSilverThreshold, surveySnapshot.SilverCount);
        var bronzeDeficit = GetAutomaticTreasureCofferDeficit(configuration.AutomaticTreasureCofferBronzeThreshold, surveySnapshot.BronzeCount);
        if (configuration.AutomaticTreasureCofferSilverThreshold == 0
            && configuration.AutomaticTreasureCofferBronzeThreshold == 0
            && surveySnapshot.SilverCount + surveySnapshot.BronzeCount > 0)
        {
            silverDeficit = 0;
            bronzeDeficit = 0;
        }

        lock (gate)
        {
            remainingSilverCompletionsUntilRescan = silverDeficit;
            remainingBronzeCompletionsUntilRescan = bronzeDeficit;
        }
    }

    private void DecrementAutomaticTreasureCofferRescanCounters(string reason)
    {
        lock (gate)
        {
            if (remainingSilverCompletionsUntilRescan > 0)
            {
                remainingSilverCompletionsUntilRescan--;
            }

            if (remainingBronzeCompletionsUntilRescan > 0)
            {
                remainingBronzeCompletionsUntilRescan--;
            }

            automaticTreasureCofferStatus = $"Automatic coffer rescan counters decremented after {reason}. silverRemaining={remainingSilverCompletionsUntilRescan} bronzeRemaining={remainingBronzeCompletionsUntilRescan}.";
        }
    }

    private bool IsAutomaticTreasureCofferRescanDue(TreasureCofferSurveySnapshot surveySnapshot)
    {
        if (surveySnapshot.Revision < requiredFreshCofferSurveyRevision)
        {
            return true;
        }

        return AutomaticTreasureCofferStatus.RemainingSilverCompletionsUntilRescan == 0
            && AutomaticTreasureCofferStatus.RemainingBronzeCompletionsUntilRescan == 0;
    }

    private bool SurveyMeetsAutomaticTreasureCofferThresholds(TreasureCofferSurveySnapshot surveySnapshot)
    {
        if (!surveySnapshot.HasSurvey)
        {
            return false;
        }

        if (configuration.AutomaticTreasureCofferSilverThreshold == 0
            && configuration.AutomaticTreasureCofferBronzeThreshold == 0)
        {
            return surveySnapshot.SilverCount + surveySnapshot.BronzeCount > 0;
        }

        return SurveyMeetsAutomaticTreasureCofferThreshold(configuration.AutomaticTreasureCofferSilverThreshold, surveySnapshot.SilverCount)
            && SurveyMeetsAutomaticTreasureCofferThreshold(configuration.AutomaticTreasureCofferBronzeThreshold, surveySnapshot.BronzeCount);
    }

    private static bool SurveyMeetsAutomaticTreasureCofferThreshold(int configuredThreshold, int observedCount)
        => configuredThreshold == 0 ? observedCount > 0 : observedCount >= configuredThreshold;

    private static int GetAutomaticTreasureCofferDeficit(int configuredThreshold, int observedCount)
    {
        var requiredCount = configuredThreshold == 0 ? 1 : configuredThreshold;
        return Math.Max(0, requiredCount - observedCount);
    }

    private void SetAutomaticTreasureCofferStatus(string reason)
    {
        lock (gate)
        {
            automaticTreasureCofferStatus = reason;
        }

        logger.Info($"{BuildLogTag()} op=auto-coffer-status reason={reason}");
    }

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

        if (treasureCofferFarmController.IsRunning)
        {
            treasureCofferFarmController.Stop(reason);
        }

        if (forkedTowerStagingController.IsRunning)
        {
            forkedTowerStagingController.Stop(reason);
        }

        movementController.Stop(reason);
        autorotationController.ReleaseOwnership(reason);
        autorotationController.DeleteManagedPreset(reason);

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
