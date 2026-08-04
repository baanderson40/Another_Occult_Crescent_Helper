using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace AOCCH.Automation;

public sealed class BuffRotationController : IDisposable
{
    private static int nextRunSequence;
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);
    private const float BuffFreshDuration = 600f;
    private const float BuffSettleSeconds = 1.5f;
    private const float BuffOutcomeTimeoutSeconds = 4f;
    private const float BuffTimeoutSeconds = 3f;
    private const float DismountTimeoutSeconds = 4f;
    private const int BuffVerifyRetries = 3;
    private const int DismountRetries = 2;

    private static readonly BuffAction[] BuffActions =
    [
        new(0, "Freelancer", 46606u, 15, "Inquiring Mind", 0, true, [4233u, 4239u, 4244u, 4799u]),
        new(1, "Knight", 41589u, 2, "Enduring Fortitude", 4233u, false, []),
        new(3, "Monk", 41597u, 3, "Fleetfooted", 4239u, false, []),
        new(6, "Bard", 41609u, 2, "Romeo's Ballad", 4244u, false, []),
        new(15, "Dancer", 46603u, 2, "Quicker Step", 4799u, false, []),
    ];

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly GameActionController gameActionController;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private BuffRotationState state = BuffRotationState.Idle;
    private string currentRunId = string.Empty;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private string currentAction = string.Empty;
    private string lastContext = string.Empty;
    private string missingRequiredStatuses = string.Empty;
    private DateTimeOffset stateEnteredAt = DateTimeOffset.MinValue;
    private byte originalSupportJob;
    private byte currentSupportJob;
    private byte? pendingSupportJobRestore;
    private bool restoreRequested;
    private byte[] supportJobLevels = [];
    private int currentEntryIndex;
    private int currentVerifyAttempt;
    private int dismountAttempt;
    private int moveAttemptIndex;
    private DateTimeOffset actionAttemptStartedAt = DateTimeOffset.MinValue;
    private List<Vector3> moveTargets = [];

    public BuffRotationController(
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

    public BuffRotationState State
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

    public string CurrentAction
    {
        get
        {
            lock (gate)
            {
                return currentAction;
            }
        }
    }

    public string LastContext
    {
        get
        {
            lock (gate)
            {
                return lastContext;
            }
        }
    }

    public string MissingRequiredStatuses
    {
        get
        {
            lock (gate)
            {
                return missingRequiredStatuses;
            }
        }
    }

    public byte OriginalSupportJob
    {
        get
        {
            lock (gate)
            {
                return originalSupportJob;
            }
        }
    }

    public byte CurrentSupportJob
    {
        get
        {
            lock (gate)
            {
                return currentSupportJob;
            }
        }
    }

    public byte? PendingSupportJobRestore
    {
        get
        {
            lock (gate)
            {
                return pendingSupportJobRestore;
            }
        }
    }

    public bool IsRunning
        => State is not BuffRotationState.Idle
            and not BuffRotationState.Completed
            and not BuffRotationState.Stopped
            and not BuffRotationState.Failed
            and not BuffRotationState.CriticalFailed;

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = BuffRotationState.Idle;
            currentRunId = string.Empty;
            lastTransition = "Idle";
            lastError = string.Empty;
            currentAction = string.Empty;
            lastContext = string.Empty;
            missingRequiredStatuses = string.Empty;
            stateEnteredAt = DateTimeOffset.MinValue;
            originalSupportJob = 0;
            currentSupportJob = 0;
            pendingSupportJobRestore = null;
            restoreRequested = false;
            supportJobLevels = [];
            currentEntryIndex = 0;
            currentVerifyAttempt = 0;
            dismountAttempt = 0;
            moveAttemptIndex = 0;
            actionAttemptStartedAt = DateTimeOffset.MinValue;
            moveTargets = [];
        }

        logger.Info($"[BuffRotation] op=reset reason={reason}");
    }

    public bool Start(string context = "manual")
    {
        if (IsRunning)
        {
            SetFailure("Buff rotation is already running.", critical: false);
            return false;
        }

        if (configuration.ScannerOnlyMode)
        {
            SetFailure("Buff rotation start blocked because scanner-only mode is enabled.", critical: false);
            return false;
        }

        lock (gate)
        {
            currentRunId = $"BuffRotation#{Interlocked.Increment(ref nextRunSequence)}";
            lastContext = context;
            lastError = string.Empty;
            currentAction = string.Empty;
            currentEntryIndex = 0;
            currentVerifyAttempt = 0;
            dismountAttempt = 0;
            moveAttemptIndex = 0;
            actionAttemptStartedAt = DateTimeOffset.MinValue;
            moveTargets = [];
            supportJobLevels = [];
            restoreRequested = false;
        }

        if (!configuration.EnableBuffRotation)
        {
            TransitionTo(BuffRotationState.Completed, $"Buff rotation skipped during {context}; disabled in configuration.");
            return true;
        }

        if (!scanner.Snapshot.IsInSupportedTerritory || !scanner.Snapshot.CanRunBuffRotation || !HasBuffZoneProfile())
        {
            SetFailure(scanner.Snapshot.IsInSupportedTerritory
                ? $"Buff rotation is unavailable in {scanner.Snapshot.TerritoryDisplayName}."
                : "Buff rotation requires a supported Occult Crescent territory.", critical: false);
            return false;
        }

        if (condition[ConditionFlag.InCombat] || IsPlayerDead())
        {
            SetFailure("Buff rotation skipped because the player is dead or in combat.", critical: false);
            return false;
        }

        if (!TryReadOccultCrescentState(out var currentJob, out var levels))
        {
            SetFailure("Buff rotation could not read support job state.", critical: false);
            return false;
        }

        lock (gate)
        {
            originalSupportJob = currentJob;
            currentSupportJob = currentJob;
            supportJobLevels = levels;
        }

        if (TryAuditRequiredBuffs(out var missingBefore) && missingBefore.Count == 0)
        {
            SetMissingStatuses(missingBefore);
            TransitionTo(BuffRotationState.Completed, "Buff rotation skipped because all required buffs are fresh.");
            return true;
        }

        SetMissingStatuses(missingBefore);
        lock (gate)
        {
            pendingSupportJobRestore = currentJob;
        }

        logger.Info($"{BuildLogTag()} op=start context=\"{context}\" supportJob={currentJob}");
        movementController.SetLogOwner(currentRunId);
        TransitionTo(BuffRotationState.Checking, $"Starting buff rotation during {context}.");
        ContinueRotation();
        return true;
    }

    public void Stop(string reason)
    {
        movementController.SetLogOwner(currentRunId);
        movementController.Stop(reason);
        if (PendingSupportJobRestore == null)
        {
            TransitionTo(BuffRotationState.Stopped, reason, clearError: false);
            logger.Info($"{BuildLogTag()} op=stop state={State} context=\"{LastContext}\" action=\"{CurrentAction}\" reason={reason}");
            return;
        }

        if (!RequestPendingSupportJobRestore(reason, out var restoreError))
        {
            SetFailure(restoreError, critical: true);
            return;
        }

        TransitionTo(BuffRotationState.RestoringJob, $"{reason} Restoring original support job.", clearError: false);
        logger.Info($"{BuildLogTag()} op=stop-request state={State} context=\"{LastContext}\" action=\"{CurrentAction}\" reason={reason}");
    }

    public void HandleDeath(string reason)
    {
        movementController.SetLogOwner(currentRunId);
        movementController.Stop(reason);
        SetFailure(reason, critical: false);
        logger.Warning($"{BuildLogTag()} op=death-stop state={State} context=\"{LastContext}\" reason={reason}");
    }

    public bool RestorePendingSupportJob(string context)
    {
        if (!RequestPendingSupportJobRestore(context, out var restoreError))
        {
            SetFailure(restoreError, critical: true);
            return false;
        }

        if (PendingSupportJobRestore == null)
        {
            TransitionTo(BuffRotationState.Completed, $"No support job restore was needed for {context}.");
            return true;
        }

        TransitionTo(BuffRotationState.RestoringJob, $"Restoring original support job for {context}.");
        return true;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        var pendingRestore = PendingSupportJobRestore;
        if (RequestPendingSupportJobRestore("plugin disposal", out var restoreError))
        {
            logger.Info(pendingRestore == null
                ? $"{BuildLogTag()} op=cleanup restoreNeeded=false reason=plugin-disposal"
                : $"{BuildLogTag()} op=cleanup restoreNeeded=true targetSupportJob={pendingRestore.Value} reason=plugin-disposal");
            return;
        }

        logger.Warning($"{BuildLogTag()} op=cleanup-failed reason=plugin-disposal error={restoreError}");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var currentState = State;
        if (currentState is BuffRotationState.Idle or BuffRotationState.Completed or BuffRotationState.Stopped or BuffRotationState.Failed or BuffRotationState.CriticalFailed)
        {
            return;
        }

        if (!scanner.Snapshot.IsInSupportedTerritory || !scanner.Snapshot.CanRunBuffRotation || !HasBuffZoneProfile())
        {
            SetFailure("Buff rotation stopped because its territory feature became unavailable.", critical: false);
            return;
        }

        if (IsPlayerDead() || condition[ConditionFlag.InCombat])
        {
            SetFailure("Buff rotation stopped because the player is dead or in combat.", critical: false);
            return;
        }

        if (TryGetCurrentSupportJob(out var currentJob))
        {
            lock (gate)
            {
                currentSupportJob = currentJob;
            }
        }

        switch (currentState)
        {
            case BuffRotationState.Checking:
                ContinueRotation();
                break;
            case BuffRotationState.MovingToBuffZone:
                TickMovingToBuffZone();
                break;
            case BuffRotationState.Dismounting:
                TickDismounting();
                break;
            case BuffRotationState.SwitchingJob:
                TickSwitchingJob();
                break;
            case BuffRotationState.Verifying:
                TickVerifying();
                break;
            case BuffRotationState.RestoringJob:
                TickRestoringJob();
                break;
        }
    }

    private void ContinueRotation()
    {
        while (true)
        {
            if (currentEntryIndex >= BuffActions.Length)
            {
                FinishRotation();
                return;
            }

            var entry = BuffActions[currentEntryIndex];
            var jobLevel = entry.JobId < supportJobLevels.Length ? supportJobLevels[entry.JobId] : (byte)0;
            if (jobLevel < entry.MinLevel)
            {
                logger.Info($"{BuildLogTag()} op=skip-action action=\"{entry.Name}\" reason=level-too-low currentLevel={jobLevel} requiredLevel={entry.MinLevel}");
                currentEntryIndex++;
                continue;
            }

            if (entry.AppliesAll)
            {
                if (TryAuditRequiredBuffs(out var missingStatuses) && missingStatuses.Count == 0)
                {
                    SetMissingStatuses(missingStatuses);
                    FinishRotation();
                    return;
                }
            }
            else if (GetStatusRemaining(entry.StatusId) >= BuffFreshDuration)
            {
                logger.Info($"{BuildLogTag()} op=skip-action action=\"{entry.BuffName}\" reason=buff-still-fresh");
                currentEntryIndex++;
                continue;
            }

            if (!IsWithinBuffZone())
            {
                actionAttemptStartedAt = DateTimeOffset.MinValue;
                BeginMoveToBuffZone();
                return;
            }

            if (condition[ConditionFlag.Mounted])
            {
                actionAttemptStartedAt = DateTimeOffset.MinValue;
                BeginDismount();
                return;
            }

            if (!TryGetCurrentSupportJob(out var currentJob))
            {
                SetFailure("Buff rotation could not read the current support job.", critical: false);
                return;
            }

            if (currentJob != entry.JobId)
            {
                actionAttemptStartedAt = DateTimeOffset.MinValue;
                BeginSwitchJob(entry);
                return;
            }

            BeginActionAttempt(entry);
            return;
        }
    }

    private void BeginMoveToBuffZone()
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition == null)
        {
            SetFailure("Buff rotation could not read player position.", critical: false);
            return;
        }

        moveTargets = BuildBuffZoneTargets(playerPosition.Value);
        moveAttemptIndex = 0;

        if (moveTargets.Count == 0)
        {
            SetFailure("Buff rotation failed to build a valid buff zone route.", critical: false);
            return;
        }

        StartPlannedMoveToBuffZone();
    }

    private void StartPlannedMoveToBuffZone()
    {
        var destination = moveTargets[moveAttemptIndex++];
        currentAction = "Moving to buff zone";
        movementController.SetLogOwner(currentRunId);
        var baseCamp = scanner.ActiveTerritoryData?.GetBaseCampAethernet();
        if (baseCamp == null)
        {
            SetFailure("Buff rotation requires Base Camp aethernet data in the active territory profile.", critical: false);
            return;
        }

        if (!movementController.PlanRouteToLocation(currentAction, baseCamp.Name, destination, 1f, shouldMountBeforeStep: false))
        {
            SetFailure(movementController.LastError.Length == 0
                ? "Buff rotation could not plan movement into the buff zone."
                : movementController.LastError, critical: false);
            return;
        }

        if (!movementController.StartPlannedRoute())
        {
            SetFailure(movementController.LastError.Length == 0
                ? "Buff rotation could not start movement into the buff zone."
                : movementController.LastError, critical: false);
            return;
        }

        TransitionTo(BuffRotationState.MovingToBuffZone, currentAction);
    }

    private void StartNextMoveAttempt()
    {
        if (moveAttemptIndex >= moveTargets.Count)
        {
            SetFailure("Buff rotation failed to reach the buff zone.", critical: false);
            return;
        }

        var destination = moveTargets[moveAttemptIndex++];
        currentAction = $"Moving to buff zone point {moveAttemptIndex}/{moveTargets.Count}";
        movementController.SetLogOwner(currentRunId);
        if (!movementController.StartDirectMove(currentAction, destination, 1f))
        {
            SetFailure(movementController.LastError.Length == 0
                ? "Buff rotation could not start movement into the buff zone."
                : movementController.LastError, critical: false);
            return;
        }

        TransitionTo(BuffRotationState.MovingToBuffZone, currentAction);
    }

    private void TickMovingToBuffZone()
    {
        if (IsWithinBuffZone())
        {
            movementController.Stop("Buff zone reached.");
            TransitionTo(BuffRotationState.Checking, "Buff rotation reached the buff zone.");
            ContinueRotation();
            return;
        }

        var movementState = movementController.State;
        if (movementState is MovementState.Pathfinding or MovementState.WaitingForArrival or MovementState.UsingReturn or MovementState.UsingAethernet)
        {
            logger.DebugThrottled(
                "buff-rotation-move",
                WaitLogInterval,
                $"Buff rotation is moving to the buff zone. MovementState={movementState} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
        }

        if (movementState == MovementState.Arrived)
        {
            StartNextMoveAttempt();
            return;
        }

        if (movementState is MovementState.Failed or MovementState.TimedOut or MovementState.Stopped)
        {
            StartNextMoveAttempt();
            return;
        }
    }

    private void BeginDismount()
    {
        dismountAttempt = 0;
        TryDismount();
    }

    private void TryDismount()
    {
        dismountAttempt++;
        currentAction = $"Dismount attempt {dismountAttempt}/{DismountRetries}";
        unsafe
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, GameActionController.DismountActionId);
        }

        TransitionTo(BuffRotationState.Dismounting, currentAction);
    }

    private void TickDismounting()
    {
        if (!condition[ConditionFlag.Mounted])
        {
            TransitionTo(BuffRotationState.Checking, "Buff rotation dismounted successfully.");
            ContinueRotation();
            return;
        }

        if ((DateTimeOffset.UtcNow - stateEnteredAt).TotalSeconds < DismountTimeoutSeconds)
        {
            return;
        }

        if (dismountAttempt < DismountRetries)
        {
            TryDismount();
            return;
        }

        SetFailure("Buff rotation failed to confirm dismounted state.", critical: false);
    }

    private void BeginSwitchJob(BuffAction entry)
    {
        currentAction = $"Switching to {entry.Name} ({entry.JobId})";
        unsafe
        {
            if (!PublicContentOccultCrescent.ChangeSupportJob(entry.JobId))
            {
                SetFailure($"Buff rotation failed to switch to {entry.Name}.", critical: false);
                return;
            }
        }

        TransitionTo(BuffRotationState.SwitchingJob, currentAction);
    }

    private void TickSwitchingJob()
    {
        var entry = BuffActions[currentEntryIndex];
        if (TryGetCurrentSupportJob(out var currentJob) && currentJob == entry.JobId)
        {
            lock (gate)
            {
                currentSupportJob = currentJob;
            }

            BeginActionAttempt(entry);
            return;
        }

        if ((DateTimeOffset.UtcNow - stateEnteredAt).TotalSeconds >= BuffTimeoutSeconds)
        {
            SetFailure($"Buff rotation timed out switching to {entry.Name}.", critical: false);
        }
    }

    private void BeginActionAttempt(BuffAction entry)
    {
        var nextAttempt = currentVerifyAttempt + 1;
        if (nextAttempt > BuffVerifyRetries)
        {
            SetFailure($"Buff rotation failed to verify {entry.BuffName}.", critical: false);
            return;
        }

        currentAction = $"Casting {entry.BuffName} attempt {nextAttempt}/{BuffVerifyRetries}";
        if (!gameActionController.CanUseAction(entry.ActionId))
        {
            actionAttemptStartedAt = DateTimeOffset.MinValue;
            TransitionTo(BuffRotationState.Verifying, $"Waiting to cast {entry.BuffName} attempt {nextAttempt}/{BuffVerifyRetries}");
            return;
        }

        if (!gameActionController.TryExecuteAction(entry.ActionId, currentAction))
        {
            actionAttemptStartedAt = DateTimeOffset.MinValue;
            TransitionTo(BuffRotationState.Verifying, $"Waiting to cast {entry.BuffName} attempt {nextAttempt}/{BuffVerifyRetries}");
            return;
        }

        currentVerifyAttempt = nextAttempt;
        actionAttemptStartedAt = DateTimeOffset.UtcNow;
        TransitionTo(BuffRotationState.Verifying, currentAction);
    }

    private void TickVerifying()
    {
        var entry = BuffActions[currentEntryIndex];
        var elapsed = actionAttemptStartedAt == DateTimeOffset.MinValue
            ? DateTimeOffset.UtcNow - stateEnteredAt
            : DateTimeOffset.UtcNow - actionAttemptStartedAt;

        var applied = entry.AppliesAll
            ? TryAuditRequiredBuffs(out var missingStatuses) && missingStatuses.Count == 0
            : GetStatusRemaining(entry.StatusId) >= BuffFreshDuration;

        if (applied)
        {
            actionAttemptStartedAt = DateTimeOffset.MinValue;
            if (entry.AppliesAll)
            {
                SetMissingStatuses([]);
                logger.Info($"{BuildLogTag()} op=verify action=\"Freelancer\" result=covered-all-required-buffs");
                FinishRotation();
                return;
            }

            logger.Info($"{BuildLogTag()} op=verify action=\"{entry.BuffName}\" result=verified");
            currentEntryIndex++;
            currentVerifyAttempt = 0;
            TransitionTo(BuffRotationState.Checking, $"Verified {entry.BuffName}.");
            ContinueRotation();
            return;
        }

        if (actionAttemptStartedAt == DateTimeOffset.MinValue)
        {
            if (!IsWithinBuffZone())
            {
                BeginMoveToBuffZone();
                return;
            }

            if (gameActionController.CanUseAction(entry.ActionId))
            {
                BeginActionAttempt(entry);
                return;
            }

            logger.DebugThrottled(
                "buff-rotation-verify",
                WaitLogInterval,
                $"Buff rotation is waiting to cast {entry.BuffName}. attempt={currentVerifyAttempt + 1}/{BuffVerifyRetries} canUseAction={gameActionController.CanUseAction(entry.ActionId)} casting={condition[ConditionFlag.Casting]} elapsed={elapsed.TotalSeconds:0.0}s.");

            if (elapsed.TotalSeconds >= BuffOutcomeTimeoutSeconds)
            {
                SetFailure($"Buff rotation timed out waiting to cast {entry.BuffName}.", critical: false);
            }

            return;
        }

        if ((DateTimeOffset.UtcNow - stateEnteredAt).TotalSeconds < BuffSettleSeconds)
        {
            return;
        }

        if (condition[ConditionFlag.Casting] || !gameActionController.CanUseAction(entry.ActionId))
        {
            logger.DebugThrottled(
                "buff-rotation-verify",
                WaitLogInterval,
                $"Buff rotation is waiting for {entry.BuffName} to resolve. attempt={currentVerifyAttempt}/{BuffVerifyRetries} canUseAction={gameActionController.CanUseAction(entry.ActionId)} casting={condition[ConditionFlag.Casting]} elapsed={elapsed.TotalSeconds:0.0}s.");

            if (elapsed.TotalSeconds < BuffOutcomeTimeoutSeconds)
            {
                return;
            }

            logger.Warning($"{BuildLogTag()} op=verify-timeout action=\"{entry.BuffName}\" stage=resolve-before-reusable");
        }
        else if (elapsed.TotalSeconds < BuffOutcomeTimeoutSeconds)
        {
            logger.DebugThrottled(
                "buff-rotation-verify",
                WaitLogInterval,
                $"Buff rotation is waiting for {entry.BuffName} to apply after cast completion. attempt={currentVerifyAttempt}/{BuffVerifyRetries} elapsed={elapsed.TotalSeconds:0.0}s.");
            return;
        }

        if (currentVerifyAttempt < BuffVerifyRetries)
        {
            if (!IsWithinBuffZone())
            {
                BeginMoveToBuffZone();
                return;
            }

            actionAttemptStartedAt = DateTimeOffset.MinValue;
            BeginActionAttempt(entry);
            return;
        }

        actionAttemptStartedAt = DateTimeOffset.MinValue;
        if (entry.AppliesAll)
        {
            logger.Warning($"{BuildLogTag()} op=verify-fallback action=\"Freelancer\" reason=verification-failed fallback=individual-buffs");
            currentEntryIndex++;
            currentVerifyAttempt = 0;
            TransitionTo(BuffRotationState.Checking, "Freelancer buff failed verification; falling back to individual buffs.");
            ContinueRotation();
            return;
        }

        SetFailure($"Buff rotation failed to verify {entry.BuffName}.", critical: false);
    }

    private void FinishRotation()
    {
        if (!TryAuditRequiredBuffs(out var missingStatuses))
        {
            SetFailure("Buff rotation could not audit required buffs after completion.", critical: false);
            return;
        }

        SetMissingStatuses(missingStatuses);
        if (missingStatuses.Count > 0)
        {
            SetFailure($"Buff rotation ended with missing required buffs: {string.Join(", ", missingStatuses)}.", critical: false);
            return;
        }

        TransitionTo(BuffRotationState.RestoringJob, "Restoring original support job.");
    }

    private void TickRestoringJob()
    {
        var targetJob = PendingSupportJobRestore;
        if (targetJob == null)
        {
            TransitionTo(BuffRotationState.Completed, "Buff rotation completed and restored the original support job.");
            logger.Info($"{BuildLogTag()} op=complete restoreRequested=true reason=original-support-job-restored");
            return;
        }

        if (TryGetCurrentSupportJob(out var currentJob) && currentJob == targetJob.Value)
        {
            lock (gate)
            {
                pendingSupportJobRestore = null;
                restoreRequested = false;
                currentSupportJob = currentJob;
            }

            TransitionTo(BuffRotationState.Completed, "Buff rotation completed and restored the original support job.");
            logger.Info($"{BuildLogTag()} op=complete restoreRequested=true reason=original-support-job-restored");
            return;
        }

        if (!restoreRequested)
        {
            if (!RequestPendingSupportJobRestore("buff rotation cleanup", out var restoreError))
            {
                SetFailure(restoreError, critical: true);
                return;
            }
        }

        if ((DateTimeOffset.UtcNow - stateEnteredAt).TotalSeconds >= BuffTimeoutSeconds)
        {
            SetFailure($"buff rotation cleanup: original support job restoration timed out; expected job {targetJob.Value}.", critical: true);
        }
    }

    private bool RequestPendingSupportJobRestore(string context, out string restoreError)
    {
        restoreError = string.Empty;
        var targetJob = PendingSupportJobRestore;
        if (targetJob == null)
        {
            return true;
        }

        if (!TryGetCurrentSupportJob(out var currentJob))
        {
            restoreError = $"{context}: could not read the current support job for restoration.";
            return false;
        }

        if (currentJob == targetJob.Value)
        {
            lock (gate)
            {
                pendingSupportJobRestore = null;
                restoreRequested = false;
                currentSupportJob = currentJob;
            }

            logger.Info($"{BuildLogTag()} op=restore-skip context=\"{context}\" targetSupportJob={targetJob.Value} reason=already-active");
            return true;
        }

        unsafe
        {
            if (!PublicContentOccultCrescent.ChangeSupportJob(targetJob.Value))
            {
                restoreError = $"{context}: failed to restore original support job {targetJob.Value}.";
                return false;
            }
        }

        lock (gate)
        {
            restoreRequested = true;
        }

        logger.Info($"{BuildLogTag()} op=restore-request context=\"{context}\" targetSupportJob={targetJob.Value}");
        return true;
    }

    private bool TryAuditRequiredBuffs(out List<uint> missingStatuses)
    {
        missingStatuses = [];
        foreach (var statusId in BuffActions[0].CheckStatusIds)
        {
            if (GetStatusRemaining(statusId) < BuffFreshDuration)
            {
                missingStatuses.Add(statusId);
            }
        }

        return true;
    }

    private float GetStatusRemaining(uint statusId)
        => objectTable.LocalPlayer?.StatusList.FirstOrDefault(status => status.StatusId == statusId)?.RemainingTime ?? 0f;

    private bool IsPlayerDead()
        => objectTable.LocalPlayer == null || objectTable.LocalPlayer.CurrentHp == 0;

    private bool IsWithinBuffZone()
    {
        var position = objectTable.LocalPlayer?.Position;
        if (position == null)
        {
            return false;
        }

        if (!TryGetBuffZoneProfile(out var profile))
        {
            return false;
        }

        var distance = CalculateFlatDistance(position.Value, profile.BuffZoneCenter.ToVector3());
        return distance >= profile.BuffZoneRadiusMin && distance <= profile.BuffZoneRadiusMax;
    }

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private List<Vector3> BuildBuffZoneTargets(Vector3 playerPosition)
    {
        if (!TryGetBuffZoneProfile(out var profile))
        {
            return [];
        }

        var buffZoneCenter = profile.BuffZoneCenter.ToVector3();
        var targets = new List<Vector3>();
        var direction = playerPosition - buffZoneCenter;
        direction.Y = 0f;

        if (direction.LengthSquared() > 0.001f)
        {
            direction = Vector3.Normalize(direction);
            targets.Add(buffZoneCenter + (direction * profile.BuffZoneRadiusMin));
        }

        var angles = new[] { 0f, 72f, 144f, 216f, 288f };
        foreach (var angle in angles)
        {
            var radians = MathF.PI * angle / 180f;
            targets.Add(new Vector3(
                buffZoneCenter.X + (MathF.Cos(radians) * profile.BuffZoneRadiusMin),
                buffZoneCenter.Y,
                buffZoneCenter.Z + (MathF.Sin(radians) * profile.BuffZoneRadiusMin)));
        }

        return targets;
    }

    private bool TryGetBuffZoneProfile(out AOCCH.Data.BuffRotationData profile)
    {
        var territory = scanner.ActiveTerritoryData;
        if (territory == null)
        {
            profile = new AOCCH.Data.BuffRotationData();
            return false;
        }

        profile = territory.BuffRotation;
        var center = profile.BuffZoneCenter;
        return !(center.X == 0f && center.Y == 0f && center.Z == 0f)
            && profile.BuffZoneRadiusMin > 0f
            && profile.BuffZoneRadiusMax >= profile.BuffZoneRadiusMin;
    }

    private bool HasBuffZoneProfile()
        => TryGetBuffZoneProfile(out _);

    private unsafe bool TryReadOccultCrescentState(out byte currentJob, out byte[] levels)
    {
        currentJob = 0;
        levels = [];

        var state = PublicContentOccultCrescent.GetState();
        if (state == null)
        {
            return false;
        }

        currentJob = state->CurrentSupportJob;
        levels = state->SupportJobLevels.ToArray();
        return levels.Length >= 16;
    }

    private unsafe bool TryGetCurrentSupportJob(out byte currentJob)
    {
        currentJob = 0;
        var state = PublicContentOccultCrescent.GetState();
        if (state == null)
        {
            return false;
        }

        currentJob = state->CurrentSupportJob;
        return true;
    }

    private void TransitionTo(BuffRotationState nextState, string transition, bool clearError = true)
    {
        BuffRotationState previousState;
        lock (gate)
        {
            previousState = state;
            state = nextState;
            lastTransition = transition;
            stateEnteredAt = DateTimeOffset.UtcNow;
            if (clearError)
            {
                lastError = string.Empty;
            }
        }

        logger.Info($"{BuildLogTag()} op=transition from={previousState} to={nextState} context=\"{LastContext}\" action=\"{CurrentAction}\" reason={transition}");
    }

    private void SetFailure(string error, bool critical)
    {
        lock (gate)
        {
            state = critical ? BuffRotationState.CriticalFailed : BuffRotationState.Failed;
            lastTransition = error;
            lastError = error;
            stateEnteredAt = DateTimeOffset.UtcNow;
        }

        if (critical)
        {
            logger.Error($"{BuildLogTag()} op=failure state={BuffRotationState.CriticalFailed} context=\"{LastContext}\" action=\"{CurrentAction}\" reason={error}");
        }
        else
        {
            logger.Warning($"{BuildLogTag()} op=failure state={BuffRotationState.Failed} context=\"{LastContext}\" action=\"{CurrentAction}\" reason={error}");
        }

        RestoreSupportJobOnFailure(critical);
    }

    private void RestoreSupportJobOnFailure(bool critical)
    {
        var targetJob = PendingSupportJobRestore;
        if (targetJob == null)
        {
            return;
        }

        if (RequestPendingSupportJobRestore("failure cleanup", out var restoreError))
        {
            logger.Info($"{BuildLogTag()} op=failure-cleanup restoreRequested=true targetSupportJob={targetJob.Value}");
            return;
        }

        if (critical)
        {
            logger.Error($"{BuildLogTag()} op=failure-cleanup restoreRequested=false error={restoreError}");
        }
        else
        {
            logger.Warning($"{BuildLogTag()} op=failure-cleanup restoreRequested=false error={restoreError}");
        }
    }

    private string BuildLogTag()
        => currentRunId.Length == 0 ? "[BuffRotation]" : $"[BuffRotation run={currentRunId}]";

    private void SetMissingStatuses(IReadOnlyCollection<uint> missingStatuses)
    {
        lock (gate)
        {
            missingRequiredStatuses = missingStatuses.Count == 0
                ? "None"
                : string.Join(", ", missingStatuses);
        }
    }

    private sealed record BuffAction(
        byte JobId,
        string Name,
        uint ActionId,
        byte MinLevel,
        string BuffName,
        uint StatusId,
        bool AppliesAll,
        uint[] CheckStatusIds);
}
