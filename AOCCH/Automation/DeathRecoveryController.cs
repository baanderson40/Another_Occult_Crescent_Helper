using System;
using AOCCH.Logging;
using AOCCH.Movement;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AOCCH.Automation;

public sealed class DeathRecoveryController : IDisposable
{
    private static readonly TimeSpan RaiseTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RaiseSettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReviveConfirmTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReleaseConfirmTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);
    private static readonly uint[] RaiseStatusIds = [148u, 1140u];

    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly MovementController movementController;
    private readonly AutorotationController autorotationController;
    private readonly BuffRotationController buffRotationController;
    private readonly CriticalEngagementAutomationController criticalEngagementAutomationController;
    private readonly FateAutomationController fateAutomationController;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private DeathRecoveryState state = DeathRecoveryState.Idle;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private bool raiseDetected;
    private bool cleanupApplied;
    private DateTimeOffset stateEnteredAt = DateTimeOffset.MinValue;
    private DateTimeOffset deathDetectedAt = DateTimeOffset.MinValue;
    private DateTimeOffset raiseDetectedAt = DateTimeOffset.MinValue;
    private DateTimeOffset actionStartedAt = DateTimeOffset.MinValue;

    public DeathRecoveryController(
        IFramework framework,
        IObjectTable objectTable,
        IGameGui gameGui,
        MovementController movementController,
        AutorotationController autorotationController,
        BuffRotationController buffRotationController,
        CriticalEngagementAutomationController criticalEngagementAutomationController,
        FateAutomationController fateAutomationController,
        AocchLogger logger)
    {
        this.framework = framework;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.movementController = movementController;
        this.autorotationController = autorotationController;
        this.buffRotationController = buffRotationController;
        this.criticalEngagementAutomationController = criticalEngagementAutomationController;
        this.fateAutomationController = fateAutomationController;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
    }

    public DeathRecoveryState State
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

    public bool RaiseDetected
    {
        get
        {
            lock (gate)
            {
                return raiseDetected;
            }
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (gate)
            {
                if (deathDetectedAt == DateTimeOffset.MinValue)
                {
                    return TimeSpan.Zero;
                }

                return DateTimeOffset.UtcNow - deathDetectedAt;
            }
        }
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = DeathRecoveryState.Idle;
            lastTransition = "Idle";
            lastError = string.Empty;
            raiseDetected = false;
            cleanupApplied = false;
            stateEnteredAt = DateTimeOffset.MinValue;
            deathDetectedAt = DateTimeOffset.MinValue;
            raiseDetectedAt = DateTimeOffset.MinValue;
            actionStartedAt = DateTimeOffset.MinValue;
        }

        logger.Info($"Death recovery reset: {reason}");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        lock (gate)
        {
            state = DeathRecoveryState.Stopped;
            lastTransition = "Death recovery disposed.";
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsPlayerDead())
        {
            if (State == DeathRecoveryState.Recovered)
            {
                TransitionTo(DeathRecoveryState.Idle, "Idle");
                return;
            }

            if (State is not DeathRecoveryState.Idle and not DeathRecoveryState.Stopped)
            {
                FinishRecovery("Player revived.");
            }

            return;
        }

        if (State == DeathRecoveryState.Idle)
        {
            BeginDeathRecovery();
        }

        if (HasRaiseStatus())
        {
            NoteRaiseDetected();
        }

        switch (State)
        {
            case DeathRecoveryState.DetectedDead:
            case DeathRecoveryState.WaitingForRaise:
                TickWaitingForRaise();
                break;
            case DeathRecoveryState.WaitingForRaiseDialog:
                TickWaitingForRaiseDialog();
                break;
            case DeathRecoveryState.AcceptingRaise:
                TickAcceptingRaise();
                break;
            case DeathRecoveryState.Releasing:
                TickReleasing();
                break;
        }
    }

    private void BeginDeathRecovery()
    {
        lock (gate)
        {
            deathDetectedAt = DateTimeOffset.UtcNow;
            raiseDetectedAt = DateTimeOffset.MinValue;
            actionStartedAt = DateTimeOffset.MinValue;
            raiseDetected = false;
            cleanupApplied = false;
            lastError = string.Empty;
        }

        TransitionTo(DeathRecoveryState.DetectedDead, "Player is dead. Waiting up to 5 minutes for raise.");
        ApplyCleanupOnce();
        TransitionTo(DeathRecoveryState.WaitingForRaise, "Waiting for raise.");
    }

    private void ApplyCleanupOnce()
    {
        lock (gate)
        {
            if (cleanupApplied)
            {
                return;
            }

            cleanupApplied = true;
        }

        const string reason = "Player died; stopping automation and cleanup.";
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
            buffRotationController.HandleDeath(reason);
        }

        movementController.Stop(reason);
        autorotationController.ReleaseOwnership(reason);
        logger.Info("Death recovery applied cleanup to active controllers.");
    }

    private void TickWaitingForRaise()
    {
        if (RaiseDetected && IsSelectYesnoReady())
        {
            logger.ResetThrottle("death-waiting-raise");
            TransitionTo(DeathRecoveryState.WaitingForRaiseDialog, "Raise dialog detected; waiting for settle.");
            lock (gate)
            {
                actionStartedAt = DateTimeOffset.UtcNow;
            }

            return;
        }

        logger.DebugThrottled("death-waiting-raise", WaitLogInterval, $"Death recovery is still waiting for a raise. elapsed={Elapsed:mm\\:ss} raiseDetected={RaiseDetected}.");

        if (deathDetectedAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - deathDetectedAt >= RaiseTimeout)
        {
            logger.ResetThrottle("death-waiting-raise");
            logger.Warning("No raise arrived before timeout; releasing to home point.");
            TransitionTo(DeathRecoveryState.Releasing, "Raise timed out; waiting to confirm release dialog.");
            lock (gate)
            {
                actionStartedAt = DateTimeOffset.MinValue;
            }
        }
    }

    private void TickAcceptingRaise()
    {
        if (!IsPlayerDead())
        {
            logger.ResetThrottle("death-accepting-raise");
            FinishRecovery("Raised successfully.");
            return;
        }

        logger.DebugThrottled("death-accepting-raise", WaitLogInterval, "Death recovery is still waiting for raise acceptance to revive the player.");

        if (actionStartedAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - actionStartedAt >= ReviveConfirmTimeout)
        {
            logger.ResetThrottle("death-accepting-raise");
            lock (gate)
            {
                raiseDetected = false;
                raiseDetectedAt = DateTimeOffset.MinValue;
                actionStartedAt = DateTimeOffset.MinValue;
            }

            TransitionTo(DeathRecoveryState.WaitingForRaise, "Raise acceptance did not revive the player; waiting for another raise.");
        }
    }

    private void TickReleasing()
    {
        if (actionStartedAt == DateTimeOffset.MinValue)
        {
            if (TryConfirmSelectYesno())
            {
                logger.ResetThrottle("death-releasing");
                logger.Info("Death recovery accepted release dialog.");
                lock (gate)
                {
                    actionStartedAt = DateTimeOffset.UtcNow;
                }
            }
            else if (stateEnteredAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - stateEnteredAt >= ReleaseConfirmTimeout)
            {
                logger.ResetThrottle("death-releasing");
                SetFailure("Raise timed out and release dialog was unavailable.");
            }
        }
        else
        {
            logger.DebugThrottled("death-releasing", WaitLogInterval, "Death recovery is still waiting for release confirmation to revive the player.");
        }

        if (!IsPlayerDead())
        {
            logger.ResetThrottle("death-releasing");
            FinishRecovery("Released successfully.");
            return;
        }

        if (actionStartedAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - actionStartedAt >= ReleaseConfirmTimeout)
        {
            logger.ResetThrottle("death-releasing");
            SetFailure("Release did not revive player.");
        }
    }

    private void NoteRaiseDetected()
    {
        lock (gate)
        {
            if (raiseDetected)
            {
                return;
            }

            raiseDetected = true;
            raiseDetectedAt = DateTimeOffset.UtcNow;
        }

        logger.Info("Death recovery detected raise status.");
    }

    private void FinishRecovery(string reason)
    {
        TransitionTo(DeathRecoveryState.Recovered, reason);
        lock (gate)
        {
            raiseDetected = false;
            cleanupApplied = false;
            deathDetectedAt = DateTimeOffset.MinValue;
            raiseDetectedAt = DateTimeOffset.MinValue;
            actionStartedAt = DateTimeOffset.MinValue;
        }

        logger.Info(reason);
    }

    private void SetFailure(string reason)
    {
        lock (gate)
        {
            lastError = reason;
        }

        TransitionTo(DeathRecoveryState.Failed, reason, clearError: false);
        logger.Warning(reason);
    }

    private bool IsPlayerDead()
        => objectTable.LocalPlayer?.CurrentHp == 0;

    private bool HasRaiseStatus()
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        foreach (var status in player.StatusList)
        {
            if (status.StatusId == RaiseStatusIds[0] || status.StatusId == RaiseStatusIds[1])
            {
                return true;
            }
        }

        return false;
    }

    private unsafe bool IsSelectYesnoReady()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("SelectYesno", 1).Address;
        return addon != null && addon->IsReady;
    }

    private unsafe bool TryConfirmSelectYesno()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("SelectYesno", 1).Address;
        if (addon == null || !addon->IsReady)
        {
            return false;
        }

        addon->FireCallbackInt(0);
        return true;
    }

    private void TickWaitingForRaiseDialog()
    {
        if (actionStartedAt == DateTimeOffset.MinValue || DateTimeOffset.UtcNow - actionStartedAt < RaiseSettleDelay)
        {
            logger.DebugThrottled("death-waiting-dialog", WaitLogInterval, "Death recovery is still waiting for the raise dialog to settle.");
            return;
        }

        if (!TryConfirmSelectYesno())
        {
            logger.ResetThrottle("death-waiting-dialog");
            TransitionTo(DeathRecoveryState.WaitingForRaise, "Raise dialog disappeared before confirmation; waiting for another raise.");
            return;
        }

        logger.ResetThrottle("death-waiting-dialog");
        TransitionTo(DeathRecoveryState.AcceptingRaise, "Accepted raise dialog.");
        lock (gate)
        {
            actionStartedAt = DateTimeOffset.UtcNow;
        }
    }

    private void TransitionTo(DeathRecoveryState nextState, string reason, bool clearError = true)
    {
        lock (gate)
        {
            state = nextState;
            stateEnteredAt = DateTimeOffset.UtcNow;
            lastTransition = reason;
            if (clearError)
            {
                lastError = string.Empty;
            }
        }

        logger.Info($"Death recovery state -> {nextState}: {reason}");
    }
}
