using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    private const float BuffFreshDuration = 600f;
    private const float BuffSettleSeconds = 1f;
    private const float BuffTimeoutSeconds = 3f;
    private const float DismountTimeoutSeconds = 4f;
    private const int BuffVerifyRetries = 3;
    private const int DismountRetries = 2;
    private static readonly Vector3 BuffZoneCenter = new(836.07f, 73.12f, -709.45f);
    private const float BuffZoneRadiusMin = 2.5f;
    private const float BuffZoneRadiusMax = 4.5f;

    private static readonly BuffAction[] BuffActions =
    [
        new(0, "Freelancer", 46606u, 15, "Inquiring Mind", 0, true, [4233u, 4239u, 4244u, 4799u]),
        new(1, "Knight", 41589u, 2, "Enduring Fortitude", 4233u, false, []),
        new(3, "Monk", 41597u, 3, "Fleetfooted", 4239u, false, []),
        new(6, "Bard", 41609u, 2, "Romeo's Ballad", 4244u, false, []),
        new(15, "Dancer", 41603u, 2, "Quick Step", 4799u, false, []),
    ];

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private BuffRotationState state = BuffRotationState.Idle;
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
    private List<Vector3> moveTargets = [];

    public BuffRotationController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        OccultCrescentScanner scanner,
        MovementController movementController,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.scanner = scanner;
        this.movementController = movementController;
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
            lastContext = context;
            lastError = string.Empty;
            currentAction = string.Empty;
            currentEntryIndex = 0;
            currentVerifyAttempt = 0;
            dismountAttempt = 0;
            moveAttemptIndex = 0;
            moveTargets = [];
            supportJobLevels = [];
            restoreRequested = false;
        }

        if (!configuration.EnableBuffRotation)
        {
            TransitionTo(BuffRotationState.Completed, $"Buff rotation skipped during {context}; disabled in configuration.");
            return true;
        }

        if (!scanner.Snapshot.IsInSouthHorn)
        {
            SetFailure("Buff rotation requires South Horn.", critical: false);
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

        logger.Info($"Starting buff rotation during {context} from support job {currentJob}.");
        TransitionTo(BuffRotationState.Checking, $"Starting buff rotation during {context}.");
        ContinueRotation();
        return true;
    }

    public void Stop(string reason)
    {
        movementController.Stop(reason);
        if (PendingSupportJobRestore == null)
        {
            TransitionTo(BuffRotationState.Stopped, reason, clearError: false);
            logger.Info($"Buff rotation stopped: {reason}");
            return;
        }

        if (!RequestPendingSupportJobRestore(reason, out var restoreError))
        {
            SetFailure(restoreError, critical: true);
            return;
        }

        TransitionTo(BuffRotationState.RestoringJob, $"{reason} Restoring original support job.", clearError: false);
        logger.Info($"Buff rotation stop requested: {reason}");
    }

    public void HandleDeath(string reason)
    {
        movementController.Stop(reason);
        SetFailure(reason, critical: false);
        logger.Warning($"Buff rotation stopped for death recovery: {reason}");
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
                ? "Buff rotation cleanup: no support job restore was needed on plugin disposal."
                : $"Buff rotation cleanup: requested support job restore to {pendingRestore.Value} on plugin disposal.");
            return;
        }

        logger.Warning($"Buff rotation cleanup failed during plugin disposal: {restoreError}");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var currentState = State;
        if (currentState is BuffRotationState.Idle or BuffRotationState.Completed or BuffRotationState.Stopped or BuffRotationState.Failed or BuffRotationState.CriticalFailed)
        {
            return;
        }

        if (!scanner.Snapshot.IsInSouthHorn)
        {
            SetFailure("Buff rotation stopped because the player left South Horn.", critical: false);
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
                logger.Info($"Buff rotation: skipping {entry.Name} because level {jobLevel} is below {entry.MinLevel}.");
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
                logger.Info($"Buff rotation: skipping {entry.BuffName} because it is still fresh.");
                currentEntryIndex++;
                continue;
            }

            if (!IsWithinBuffZone())
            {
                BeginMoveToBuffZone();
                return;
            }

            if (condition[ConditionFlag.Mounted])
            {
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
        StartNextMoveAttempt();
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
        if (!movementController.StartDirectMove(currentAction, destination, 1f))
        {
            SetFailure("Buff rotation could not start movement into the buff zone.", critical: false);
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

        if (movementController.State == MovementState.Arrived)
        {
            StartNextMoveAttempt();
            return;
        }

        if (movementController.State is MovementState.Failed or MovementState.TimedOut or MovementState.Stopped)
        {
            StartNextMoveAttempt();
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
        currentVerifyAttempt++;
        currentAction = $"Casting {entry.BuffName} attempt {currentVerifyAttempt}/{BuffVerifyRetries}";
        unsafe
        {
            ActionManager.Instance()->UseAction(ActionType.Action, entry.ActionId);
        }

        TransitionTo(BuffRotationState.Verifying, currentAction);
    }

    private void TickVerifying()
    {
        if ((DateTimeOffset.UtcNow - stateEnteredAt).TotalSeconds < BuffSettleSeconds)
        {
            return;
        }

        var entry = BuffActions[currentEntryIndex];
        var applied = entry.AppliesAll
            ? TryAuditRequiredBuffs(out var missingStatuses) && missingStatuses.Count == 0
            : GetStatusRemaining(entry.StatusId) >= BuffFreshDuration;

        if (applied)
        {
            if (entry.AppliesAll)
            {
                SetMissingStatuses([]);
                logger.Info("Buff rotation: Freelancer buff covered all required buffs.");
                FinishRotation();
                return;
            }

            logger.Info($"Buff rotation: verified {entry.BuffName}.");
            currentEntryIndex++;
            currentVerifyAttempt = 0;
            TransitionTo(BuffRotationState.Checking, $"Verified {entry.BuffName}.");
            ContinueRotation();
            return;
        }

        if (currentVerifyAttempt < BuffVerifyRetries)
        {
            if (!IsWithinBuffZone())
            {
                BeginMoveToBuffZone();
                return;
            }

            BeginActionAttempt(entry);
            return;
        }

        if (entry.AppliesAll)
        {
            logger.Warning("Buff rotation: Freelancer buff failed verification; continuing with individual buffs.");
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
            logger.Info("Buff rotation complete; original support job restored.");
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
            logger.Info("Buff rotation complete; original support job restored.");
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

            logger.Info($"{context}: original support job {targetJob.Value} was already active.");
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

        logger.Info($"{context}: requested restore of original support job {targetJob.Value}.");
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

        var distance = CalculateFlatDistance(position.Value, BuffZoneCenter);
        return distance >= BuffZoneRadiusMin && distance <= BuffZoneRadiusMax;
    }

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private static List<Vector3> BuildBuffZoneTargets(Vector3 playerPosition)
    {
        var targets = new List<Vector3>();
        var direction = playerPosition - BuffZoneCenter;
        direction.Y = 0f;

        if (direction.LengthSquared() > 0.001f)
        {
            direction = Vector3.Normalize(direction);
            targets.Add(BuffZoneCenter + (direction * BuffZoneRadiusMin));
        }

        var angles = new[] { 0f, 72f, 144f, 216f, 288f };
        foreach (var angle in angles)
        {
            var radians = MathF.PI * angle / 180f;
            targets.Add(new Vector3(
                BuffZoneCenter.X + (MathF.Cos(radians) * BuffZoneRadiusMin),
                BuffZoneCenter.Y,
                BuffZoneCenter.Z + (MathF.Sin(radians) * BuffZoneRadiusMin)));
        }

        return targets;
    }

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
        lock (gate)
        {
            state = nextState;
            lastTransition = transition;
            stateEnteredAt = DateTimeOffset.UtcNow;
            if (clearError)
            {
                lastError = string.Empty;
            }
        }

        logger.Info($"Buff rotation state -> {nextState}: {transition}");
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
            logger.Error(error);
        }
        else
        {
            logger.Warning(error);
        }
    }

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
