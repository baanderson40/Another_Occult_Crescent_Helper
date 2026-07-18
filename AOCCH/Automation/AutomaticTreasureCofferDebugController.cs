using System;

using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class AutomaticTreasureCofferDebugController : IDisposable
{
    private enum DebugState
    {
        Idle,
        SwitchingToFreelancer,
        WaitingForSurvey,
        RestoringOriginalJob,
        Completed,
        Failed,
    }

    private static readonly TimeSpan SupportJobSwitchTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SurveyWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SurveyActionRetryInterval = TimeSpan.FromMilliseconds(250);

    private readonly IFramework framework;
    private readonly OccultCrescentScanner scanner;
    private readonly GameActionController gameActionController;
    private readonly DeathRecoveryController deathRecoveryController;
    private readonly TreasureHintTracker treasureHintTracker;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private DebugState state = DebugState.Idle;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private byte originalSupportJob;
    private int requiredSurveyRevision;
    private DateTimeOffset stateEnteredAt = DateTimeOffset.MinValue;
    private DateTimeOffset surveyDeadlineAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextSurveyActionAttemptAt = DateTimeOffset.MinValue;

    public AutomaticTreasureCofferDebugController(
        IFramework framework,
        OccultCrescentScanner scanner,
        GameActionController gameActionController,
        DeathRecoveryController deathRecoveryController,
        TreasureHintTracker treasureHintTracker,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.scanner = scanner;
        this.gameActionController = gameActionController;
        this.deathRecoveryController = deathRecoveryController;
        this.treasureHintTracker = treasureHintTracker;
        this.configuration = configuration;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
    }

    public bool IsRunning
        => State is not DebugState.Idle and not DebugState.Completed and not DebugState.Failed;

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

    private DebugState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public bool Start()
    {
        if (IsRunning)
        {
            SetFailure("Automatic coffer debug survey is already running.");
            return false;
        }

        if (!scanner.Snapshot.IsInSupportedTerritory || !scanner.Snapshot.CanRunVisibleCofferRoute)
        {
            SetFailure(scanner.Snapshot.IsInSupportedTerritory
                ? $"Automatic coffer debug survey is unavailable in {scanner.Snapshot.TerritoryDisplayName}."
                : "Automatic coffer debug survey requires a supported Occult Crescent territory.");
            return false;
        }

        if (deathRecoveryController.State is not DeathRecoveryState.Idle and not DeathRecoveryState.Recovered and not DeathRecoveryState.Stopped)
        {
            SetFailure($"Automatic coffer debug survey is blocked by death recovery state {deathRecoveryController.State}.");
            return false;
        }

        if (!gameActionController.TryReadSupportJobState(out var currentJob, out var supportJobLevels))
        {
            SetFailure("Automatic coffer debug survey could not read phantom job state.");
            return false;
        }

        var freelancerLevel = supportJobLevels.Length > GameActionController.FreelancerSupportJobId
            ? supportJobLevels[GameActionController.FreelancerSupportJobId]
            : (byte)0;
        if (freelancerLevel < 10)
        {
            SetFailure($"Automatic coffer debug survey requires Freelancer level 10. currentLevel={freelancerLevel}.");
            return false;
        }

        lock (gate)
        {
            state = DebugState.Idle;
            lastError = string.Empty;
            originalSupportJob = currentJob;
            requiredSurveyRevision = treasureHintTracker.CofferSurveySnapshot.Revision + 1;
            surveyDeadlineAt = DateTimeOffset.MinValue;
            nextSurveyActionAttemptAt = DateTimeOffset.MinValue;
        }

        logger.Info($"[AutoCofferDebug] op=start originalSupportJob={currentJob} freelancerLevel={freelancerLevel} thresholds=silver:{configuration.AutomaticTreasureCofferSilverThreshold} bronze:{configuration.AutomaticTreasureCofferBronzeThreshold}");

        if (currentJob == GameActionController.FreelancerSupportJobId)
        {
            TransitionTo(DebugState.SwitchingToFreelancer, "Waiting for Occult Treasuresight for automatic coffer debug survey.");
            return true;
        }

        if (!gameActionController.TryChangeSupportJob(GameActionController.FreelancerSupportJobId, "automatic coffer debug survey"))
        {
            SetFailure("Automatic coffer debug survey failed to switch to Freelancer.");
            return false;
        }

        TransitionTo(DebugState.SwitchingToFreelancer, "Switching to Freelancer for automatic coffer debug survey.");
        return true;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var currentState = State;
        if (IsRunning && currentState != DebugState.RestoringOriginalJob)
        {
            if (!scanner.Snapshot.IsInSupportedTerritory || !scanner.Snapshot.CanRunVisibleCofferRoute)
            {
                FailAndRestore("Automatic coffer debug survey stopped because visible coffer data became unavailable.");
                return;
            }

            if (deathRecoveryController.State is not DeathRecoveryState.Idle and not DeathRecoveryState.Recovered and not DeathRecoveryState.Stopped)
            {
                FailAndRestore($"Automatic coffer debug survey stopped because death recovery became active. state={deathRecoveryController.State}.");
                return;
            }
        }

        switch (currentState)
        {
            case DebugState.SwitchingToFreelancer:
                TickSwitchingToFreelancer();
                break;
            case DebugState.WaitingForSurvey:
                TickWaitingForSurvey();
                break;
            case DebugState.RestoringOriginalJob:
                TickRestoringOriginalJob();
                break;
        }
    }

    private void TickSwitchingToFreelancer()
    {
        var now = DateTimeOffset.UtcNow;
        // Use the game log as confirmation even when ActionManager reports a failed dispatch.
        if (treasureHintTracker.TryGetLatestCofferSurveySince(requiredSurveyRevision - 1, out var surveySnapshot) && surveySnapshot != null)
        {
            var wouldStartRoute = SurveyMeetsAutomaticTreasureCofferThresholds(surveySnapshot);
            logger.Info($"[AutoCofferDebug] op=survey-result silver={surveySnapshot.SilverCount} bronze={surveySnapshot.BronzeCount} revision={surveySnapshot.Revision} thresholdsMet={wouldStartRoute} summary={treasureHintTracker.GetDebugLogMessageCaptureSummary()}");
            CompleteAndRestore($"Automatic coffer debug survey captured silver={surveySnapshot.SilverCount} bronze={surveySnapshot.BronzeCount}. thresholdsMet={wouldStartRoute}; route-not-started-by-debug.");
            return;
        }

        if (gameActionController.TryGetCurrentSupportJob(out var currentJob) && currentJob == GameActionController.FreelancerSupportJobId)
        {
            if (!gameActionController.CanUseAction(GameActionController.OccultTreasuresightActionId))
            {
                logger.DebugThrottled("auto-coffer-debug-survey-action-ready", TimeSpan.FromSeconds(10),
                    $"[AutoCofferDebug] op=survey-wait reason=action-unavailable actionId={GameActionController.OccultTreasuresightActionId}");
            }
            else if (now >= nextSurveyActionAttemptAt)
            {
                nextSurveyActionAttemptAt = now + SurveyActionRetryInterval;
                if (BeginSurveyWait())
                {
                    return;
                }

                logger.DebugThrottled("auto-coffer-debug-survey-action-dispatch", TimeSpan.FromSeconds(10),
                    $"[AutoCofferDebug] op=survey-wait reason=dispatch-failed actionId={GameActionController.OccultTreasuresightActionId}");
            }
        }

        if (now - stateEnteredAt >= SupportJobSwitchTimeout)
        {
            FailAndRestore("Automatic coffer debug survey timed out switching to Freelancer or waiting for Occult Treasuresight.");
        }
    }

    private bool BeginSurveyWait()
    {
        if (!gameActionController.TryExecuteAction(GameActionController.OccultTreasuresightActionId, "automatic coffer debug survey"))
        {
            return false;
        }

        treasureHintTracker.ArmDebugLogMessageCapture("automatic coffer debug survey", SurveyWaitTimeout);
        lock (gate)
        {
            surveyDeadlineAt = DateTimeOffset.UtcNow + SurveyWaitTimeout;
        }

        TransitionTo(DebugState.WaitingForSurvey, "Waiting for automatic coffer debug survey result.");
        return true;
    }

    private void TickWaitingForSurvey()
    {
        if (treasureHintTracker.TryGetLatestCofferSurveySince(requiredSurveyRevision - 1, out var surveySnapshot) && surveySnapshot != null)
        {
            var wouldStartRoute = SurveyMeetsAutomaticTreasureCofferThresholds(surveySnapshot);
            logger.Info($"[AutoCofferDebug] op=survey-result silver={surveySnapshot.SilverCount} bronze={surveySnapshot.BronzeCount} revision={surveySnapshot.Revision} thresholdsMet={wouldStartRoute} summary={treasureHintTracker.GetDebugLogMessageCaptureSummary()}");
            CompleteAndRestore($"Automatic coffer debug survey captured silver={surveySnapshot.SilverCount} bronze={surveySnapshot.BronzeCount}. thresholdsMet={wouldStartRoute}; route-not-started-by-debug.");
            return;
        }

        if (surveyDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= surveyDeadlineAt)
        {
            FailAndRestore($"Automatic coffer debug survey timed out waiting for a coffer survey log message. capture={treasureHintTracker.GetDebugLogMessageCaptureSummary()}");
        }
    }

    private void TickRestoringOriginalJob()
    {
        if (gameActionController.TryGetCurrentSupportJob(out var currentJob) && currentJob == originalSupportJob)
        {
            var wasFailure = LastError.Length > 0;
            TransitionTo(wasFailure ? DebugState.Failed : DebugState.Completed, wasFailure
                ? $"Automatic coffer debug survey ended after restoring phantom job {currentJob}."
                : $"Automatic coffer debug survey completed and restored phantom job {currentJob}.");
            return;
        }

        if (DateTimeOffset.UtcNow - stateEnteredAt >= SupportJobSwitchTimeout)
        {
            SetFailure($"Automatic coffer debug survey timed out restoring phantom job {originalSupportJob}.");
        }
    }

    private void CompleteAndRestore(string reason)
    {
        if (originalSupportJob == GameActionController.FreelancerSupportJobId)
        {
            TransitionTo(DebugState.Completed, reason);
            return;
        }

        logger.Info($"[AutoCofferDebug] op=restore-request originalSupportJob={originalSupportJob}");
        if (!gameActionController.TryChangeSupportJob(originalSupportJob, "automatic coffer debug survey restore"))
        {
            SetFailure($"{reason} Failed to restore original phantom job {originalSupportJob}.");
            return;
        }

        TransitionTo(DebugState.RestoringOriginalJob, reason);
    }

    private bool FailAndRestore(string error)
    {
        lock (gate)
        {
            lastError = error;
        }

        logger.Warning($"[AutoCofferDebug] op=failure reason={error}");
        if (originalSupportJob == GameActionController.FreelancerSupportJobId)
        {
            TransitionTo(DebugState.Failed, error, clearError: false);
            return false;
        }

        if (!gameActionController.TryChangeSupportJob(originalSupportJob, "automatic coffer debug survey failure restore"))
        {
            SetFailure($"{error} Failed to restore original phantom job {originalSupportJob}.");
            return false;
        }

        TransitionTo(DebugState.RestoringOriginalJob, error, clearError: false);
        return false;
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

    private void TransitionTo(DebugState nextState, string reason, bool clearError = true)
    {
        DebugState previousState;
        lock (gate)
        {
            previousState = state;
            state = nextState;
            lastTransition = reason;
            stateEnteredAt = DateTimeOffset.UtcNow;
            if (clearError)
            {
                lastError = string.Empty;
            }
        }

        logger.Info($"[AutoCofferDebug] op=transition from={previousState} to={nextState} reason={reason}");
    }

    private void SetFailure(string error)
    {
        lock (gate)
        {
            state = DebugState.Failed;
            lastTransition = error;
            lastError = error;
            stateEnteredAt = DateTimeOffset.UtcNow;
        }

        logger.Warning($"[AutoCofferDebug] op=failure reason={error}");
    }
}
