using System;
using System.Linq;
using System.Numerics;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AOCCH.Automation;

public sealed class PostActivityRevivalController : IDisposable
{
    private const float ScanRadius = 50f;
    private const float PostActivityReviveRange = 15f;
    private const float ActiveReviveRange = 25f;
    private const byte ChemistJobId = 10;
    private const byte WhiteMageJobId = 17;
    private const byte ChemistMinimumLevel = 3;
    private const byte WhiteMageMinimumLevel = 4;
    private const uint ChemistReviveActionId = GameActionController.OccultChemistRaiseActionId;
    private const uint WhiteMageRaiseActionId = GameActionController.OccultWhiteMageRaiseActionId;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CompletionSettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan JobSwitchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan JobSwitchRetryInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ActiveActionPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CastAttemptTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(2);
    private static readonly uint[] RaiseStatusIds = [148u, 1140u];

    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly GameActionController gameActionController;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private PostActivityRevivalState state = PostActivityRevivalState.Idle;
    private Vector3 scanAnchor;
    private ulong targetObjectId;
    private string targetName = string.Empty;
    private byte originalJob;
    private byte selectedJob;
    private uint selectedActionId;
    private string selectedJobName = string.Empty;
    private DateTimeOffset stateEnteredAt = DateTimeOffset.MinValue;
    private DateTimeOffset flowStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextScanAt = DateTimeOffset.MinValue;
    private DateTimeOffset completionSettleUntilAt = DateTimeOffset.MinValue;
    private DateTimeOffset jobSwitchDeadlineAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextJobSwitchAttemptAt = DateTimeOffset.MinValue;
    private DateTimeOffset activeActionPollAt = DateTimeOffset.MinValue;
    private int activeCastAttempt;
    private bool activeMode;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;

    public PostActivityRevivalController(
        IFramework framework,
        IObjectTable objectTable,
        OccultCrescentScanner scanner,
        MovementController movementController,
        GameActionController gameActionController,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.objectTable = objectTable;
        this.scanner = scanner;
        this.movementController = movementController;
        this.gameActionController = gameActionController;
        this.configuration = configuration;
        this.logger = logger;
        framework.Update += OnFrameworkUpdate;
    }

    public PostActivityRevivalState State { get { lock (gate) return state; } }
    public string LastTransition { get { lock (gate) return lastTransition; } }
    public string LastError { get { lock (gate) return lastError; } }
    public string TargetName { get { lock (gate) return targetName; } }
    public string SelectedJobName { get { lock (gate) return selectedJobName; } }
    public bool IsRunning => State is not PostActivityRevivalState.Idle
        and not PostActivityRevivalState.Completed
        and not PostActivityRevivalState.Skipped
        and not PostActivityRevivalState.Failed;

    public bool IsActiveMode { get { lock (gate) return activeMode; } }

    public bool Start(Vector3 anchor, string context)
    {
        if (IsRunning)
        {
            return false;
        }

        if (!configuration.EnablePostActivityRevival)
        {
            TransitionTo(PostActivityRevivalState.Skipped, "Post-activity revival is disabled.");
            return true;
        }

        if (!gameActionController.TryReadSupportJobState(out var currentJob, out var levels))
        {
            TransitionTo(PostActivityRevivalState.Skipped, "Post-activity revival skipped because phantom-job state was unavailable.");
            return true;
        }

        if (!TrySelectJob(currentJob, levels, out var selectedJob, out var actionId, out var jobName))
        {
            TransitionTo(PostActivityRevivalState.Skipped, "Post-activity revival skipped because neither Chemist nor White Mage met the required level.");
            return true;
        }

        lock (gate)
        {
            scanAnchor = anchor;
            originalJob = currentJob;
            this.selectedJob = selectedJob;
            selectedActionId = actionId;
            selectedJobName = jobName;
            targetObjectId = 0;
            targetName = string.Empty;
            flowStartedAt = DateTimeOffset.UtcNow;
            nextScanAt = DateTimeOffset.MinValue;
            completionSettleUntilAt = flowStartedAt + CompletionSettleDelay;
            jobSwitchDeadlineAt = flowStartedAt + JobSwitchTimeout;
            nextJobSwitchAttemptAt = DateTimeOffset.MinValue;
            activeActionPollAt = DateTimeOffset.MinValue;
            activeCastAttempt = 0;
            lastError = string.Empty;
            activeMode = false;
        }

        logger.Info($"[Revival] op=start context=\"{context}\" anchor={FormatVector(anchor)} originalJob={currentJob} selectedJob={jobName} actionId={actionId} candidates=deferred-until-recast-ready");
        TransitionTo(PostActivityRevivalState.SwitchingJob, $"Waiting for the FATE/CE completion transition before using {jobName}.");

        return true;
    }

    public bool StartActive(string context)
    {
        if (IsRunning || !configuration.EnablePostActivityRevival)
        {
            return false;
        }

        var position = objectTable.LocalPlayer?.Position;
        if (!position.HasValue
            || !gameActionController.TryReadSupportJobState(out var currentJob, out var levels)
            || !TrySelectCurrentJob(currentJob, levels, out var actionId, out var jobName))
        {
            return false;
        }

        var recastTime = gameActionController.GetActionRecastTime(actionId);
        if (recastTime > 0f)
        {
            logger.VerboseThrottled(
                "revival-active-recast-wait",
                TimeSpan.FromSeconds(2),
                $"[Revival] op=active-wait actionId={actionId} job={jobName} recast={recastTime:0.00} conditions={gameActionController.GetActionConditionSummary()} target={gameActionController.GetCurrentTargetSummary()}");
            return false;
        }

        var candidates = FindDeadPlayers(position.Value);
        if (candidates.Length == 0)
        {
            return false;
        }

        lock (gate)
        {
            scanAnchor = position.Value;
            originalJob = currentJob;
            selectedJob = currentJob;
            selectedActionId = actionId;
            selectedJobName = jobName;
            targetObjectId = 0;
            targetName = string.Empty;
            flowStartedAt = DateTimeOffset.UtcNow;
            nextScanAt = DateTimeOffset.MinValue;
            completionSettleUntilAt = DateTimeOffset.MinValue;
            jobSwitchDeadlineAt = DateTimeOffset.MinValue;
            nextJobSwitchAttemptAt = DateTimeOffset.MinValue;
            activeActionPollAt = DateTimeOffset.MinValue;
            activeCastAttempt = 0;
            lastError = string.Empty;
            activeMode = true;
        }

        logger.Info($"[Revival] op=active-start context=\"{context}\" anchor={FormatVector(position.Value)} currentJob={currentJob} selectedJob={jobName} actionId={actionId} candidates={candidates.Length}");
        TransitionTo(PostActivityRevivalState.WaitingForAction, $"Active revival is ready with current {jobName}; waiting to select a dead player.");
        return true;
    }

    public bool Start(string context)
    {
        var position = objectTable.LocalPlayer?.Position;
        if (!position.HasValue)
        {
            TransitionTo(PostActivityRevivalState.Skipped, "Post-activity revival skipped because the player position was unavailable.");
            return true;
        }

        return Start(position.Value, context);
    }

    public void Stop(string reason)
    {
        movementController.Stop(reason);
        gameActionController.TryClearTarget(targetObjectId, reason);
        FinishWithRestore(PostActivityRevivalState.Skipped, reason);
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = PostActivityRevivalState.Idle;
            lastTransition = "Idle";
            lastError = string.Empty;
            targetObjectId = 0;
            targetName = string.Empty;
            selectedJobName = string.Empty;
            activeMode = false;
            flowStartedAt = DateTimeOffset.MinValue;
            completionSettleUntilAt = DateTimeOffset.MinValue;
            jobSwitchDeadlineAt = DateTimeOffset.MinValue;
            nextJobSwitchAttemptAt = DateTimeOffset.MinValue;
        }

        logger.Info($"[Revival] op=reset reason={reason}");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        if (IsRunning)
        {
            Stop("Post-activity revival disposal.");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsRunning)
        {
            return;
        }

        if (!scannerReady() || objectTable.LocalPlayer == null || objectTable.LocalPlayer.CurrentHp == 0)
        {
            FinishWithRestore(PostActivityRevivalState.Skipped, "Post-activity revival stopped because the player or territory became unavailable.");
            return;
        }

        if (DateTimeOffset.UtcNow - flowStartedAt >= OverallTimeout)
        {
            FinishWithRestore(PostActivityRevivalState.Skipped, "Post-activity revival reached its overall timeout; continuing farm recovery.");
            return;
        }

        switch (State)
        {
            case PostActivityRevivalState.SwitchingJob:
                TickSwitchingJob();
                break;
            case PostActivityRevivalState.WaitingForAction:
                TickWaitingForAction();
                break;
            case PostActivityRevivalState.MovingToTarget:
                TickMovingToTarget();
                break;
            case PostActivityRevivalState.Casting:
                TickCasting();
                break;
            case PostActivityRevivalState.WaitingForRaiseStatus:
                TickWaitingForRaiseStatus();
                break;
            case PostActivityRevivalState.RestoringJob:
                TickRestoringJob();
                break;
        }
    }

    private void TickSwitchingJob()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < completionSettleUntilAt)
        {
            return;
        }

        if (!gameActionController.TryGetCurrentSupportJob(out var currentJob))
        {
            return;
        }

        if (currentJob == selectedJob)
        {
            TransitionTo(PostActivityRevivalState.WaitingForAction, $"Switched to {selectedJobName}.");
            return;
        }

        if (now >= jobSwitchDeadlineAt)
        {
            FinishWithRestore(PostActivityRevivalState.Skipped, $"Timed out switching to {selectedJobName}; continuing farm recovery.");
            return;
        }

        if (now < nextJobSwitchAttemptAt)
        {
            return;
        }

        nextJobSwitchAttemptAt = now + JobSwitchRetryInterval;
        if (!gameActionController.IsPlayerInChangeableState())
        {
            logger.VerboseThrottled(
                "revival-job-switch-blocked",
                TimeSpan.FromSeconds(2),
                $"[Revival] op=job-switch-wait targetJob={selectedJob} job={selectedJobName} conditions={gameActionController.GetChangeableStateSummary()} deadline={jobSwitchDeadlineAt:O}");
            return;
        }

        if (!gameActionController.TryChangeSupportJob(selectedJob, $"post-activity revival {selectedJobName}"))
        {
            logger.VerboseThrottled(
                "revival-job-switch-rejected",
                TimeSpan.FromSeconds(2),
                $"[Revival] op=job-switch-retry targetJob={selectedJob} job={selectedJobName} conditions={gameActionController.GetChangeableStateSummary()} deadline={jobSwitchDeadlineAt:O}");
        }
    }

    private void TickWaitingForAction()
    {
        if (DateTimeOffset.UtcNow < nextScanAt)
        {
            return;
        }

        nextScanAt = DateTimeOffset.UtcNow + ScanInterval;
        if (!activeMode)
        {
            var recastTime = gameActionController.GetActionRecastTime(selectedActionId);
            if (recastTime > 0f)
            {
                logger.VerboseThrottled(
                    "revival-post-recast-wait",
                    TimeSpan.FromSeconds(2),
                    $"[Revival] op=post-wait actionId={selectedActionId} job={selectedJobName} recast={recastTime:0.00}.");
                return;
            }
        }

        var candidates = FindDeadPlayers(scanAnchor);
        if (candidates.Length == 0)
        {
            FinishWithRestore(PostActivityRevivalState.Completed, "No dead players without Raise status remain.");
            return;
        }

        if (!gameActionController.CanUseAction(selectedActionId))
        {
            var actionStatus = gameActionController.GetActionStatusCode(selectedActionId);
            var statusWithoutRecast = gameActionController.GetActionStatusCode(selectedActionId, checkRecastActive: false);
            var statusWithoutCasting = gameActionController.GetActionStatusCode(selectedActionId, checkCastingActive: false);
            var statusWithoutRecastOrCasting = gameActionController.GetActionStatusCode(selectedActionId, checkRecastActive: false, checkCastingActive: false);
            logger.VerboseThrottled(
                "revival-action-wait",
                TimeSpan.FromSeconds(2),
                $"[Revival] op=action-wait actionId={selectedActionId} job={selectedJobName} statusCode={actionStatus} statusWithoutRecast={statusWithoutRecast} statusWithoutCasting={statusWithoutCasting} statusWithoutRecastOrCasting={statusWithoutRecastOrCasting} recast={gameActionController.GetActionRecastTime(selectedActionId):0.00} candidates={candidates.Length} conditions={gameActionController.GetActionConditionSummary()} target={gameActionController.GetCurrentTargetSummary()}");
            return;
        }

        var target = candidates[0];
        lock (gate)
        {
            targetObjectId = target.GameObjectId;
            targetName = target.Name.ToString();
        }

        if (IsWithinRange(target))
        {
            movementController.Stop("Reached revival target range.");
            TransitionTo(PostActivityRevivalState.Casting, $"Preparing to raise {targetName}.");
            return;
        }

        if (!movementController.StartDirectMove($"Move within {CurrentReviveRange:0}y of dead player {targetName}", target.Position, CurrentReviveRange))
        {
            ClearTargetAndRescan($"Could not move within range of {targetName}; returning to the revival scan.");
            return;
        }

        TransitionTo(PostActivityRevivalState.MovingToTarget, $"Moving within {CurrentReviveRange:0}y of {targetName}.");
    }

    private void TickMovingToTarget()
    {
        var target = FindTarget();
        if (target == null || !IsEligibleDeadPlayer(target))
        {
            if (activeMode)
            {
                FinishWithRestore(PostActivityRevivalState.Skipped, "Active revival target was no longer eligible during movement; returning to FATE/CE combat.");
            }
            else
            {
                ClearTargetAndRescan("Revival target was no longer eligible during movement.");
            }
            return;
        }

        if (movementController.State is MovementState.Failed or MovementState.TimedOut)
        {
            if (activeMode)
            {
                FinishWithRestore(PostActivityRevivalState.Skipped, $"Movement to {targetName} failed; returning to FATE/CE combat.");
            }
            else
            {
                ClearTargetAndRescan($"Movement to {targetName} failed; returning to the revival scan.");
            }
            return;
        }

        if (movementController.State == MovementState.Arrived || IsWithinRange(target))
        {
            movementController.Stop("Reached revival target range.");
            TransitionTo(PostActivityRevivalState.Casting, $"Reached {targetName}; preparing to cast.");
        }
    }

    private void TickCasting()
    {
        var target = FindTarget();
        if (target == null || !IsEligibleDeadPlayer(target))
        {
            if (activeMode)
            {
                FinishWithRestore(PostActivityRevivalState.Skipped, "Active revival target was raised by another player before casting.");
            }
            else
            {
                ClearTargetAndRescan("Revival target was raised by another player before casting.");
            }
            return;
        }

        if (!IsWithinRange(target))
        {
            if (activeMode)
            {
                FinishWithRestore(PostActivityRevivalState.Skipped, "Active revival target was out of range at cast time; returning to FATE/CE combat.");
            }
            else
            {
                ClearTargetAndRescan("Revival target was out of range at cast time; returning to the revival scan.");
            }
            return;
        }

        var targetDescription = activeMode ? "active FATE/CE revival target" : "post-activity revival target";
        if (!gameActionController.TrySetTarget(target, $"{targetDescription} {targetName}"))
        {
            if (activeMode)
            {
                FinishWithRestore(PostActivityRevivalState.Skipped, "Could not target the dead player during active revival; returning to FATE/CE combat.");
            }
            else
            {
                ClearTargetAndRescan("Could not target the dead player; returning to the revival scan.");
            }
            return;
        }

        if (!gameActionController.CanUseAction(selectedActionId, target.GameObjectId))
        {
            if (activeMode)
            {
                FinishWithRestore(PostActivityRevivalState.Skipped, "Active revive action was no longer castable at the target; returning to FATE/CE combat.");
            }
            else
            {
                ClearTargetAndRescan("Revive action was no longer castable at the target; returning to the revival scan.");
            }
            return;
        }

        if (!gameActionController.TryExecuteAction(selectedActionId, $"{selectedJobName} raise for {targetName}", target.GameObjectId))
        {
            activeCastAttempt++;
            activeActionPollAt = DateTimeOffset.UtcNow + ActiveActionPollInterval;
            TransitionTo(PostActivityRevivalState.WaitingForRaiseStatus, $"Raise dispatch attempt {activeCastAttempt}/3 did not confirm; polling action readiness.");
            if (activeCastAttempt >= 3 && DateTimeOffset.UtcNow - stateEnteredAt >= CastAttemptTimeout)
            {
                FinishWithRestore(PostActivityRevivalState.Skipped, BuildRaiseFailureReason($"Timed out dispatching a raise for {targetName}."));
            }

            return;
        }

        activeCastAttempt = 1;
        activeActionPollAt = DateTimeOffset.UtcNow + ActiveActionPollInterval;
        TransitionTo(PostActivityRevivalState.WaitingForRaiseStatus, $"Raise dispatched attempt 1/3; polling action readiness.");
    }

    private void TickWaitingForRaiseStatus()
    {
        TickActionPolling();
    }

    private void TickActionPolling()
    {
        if (DateTimeOffset.UtcNow < activeActionPollAt)
        {
            return;
        }

        if (gameActionController.GetActionRecastTime(selectedActionId) <= 0f)
        {
            if (activeCastAttempt >= 3)
            {
                FinishWithRestore(PostActivityRevivalState.Skipped, BuildRaiseFailureReason($"{selectedJobName} raise action remained off cooldown after {activeCastAttempt} attempt(s)."));
                return;
            }

            logger.VerboseThrottled(
                "revival-raise-status-wait",
                TimeSpan.FromSeconds(2),
                $"[Revival] op=raise-wait actionId={selectedActionId} job={selectedJobName} recast=0 attempt={activeCastAttempt}/3.");
        }
        else
        {
            var reason = $"{selectedJobName} raise action entered cooldown after {activeCastAttempt} attempt(s).";
            if (activeMode)
            {
                FinishWithRestore(PostActivityRevivalState.Completed, BuildRaiseSuccessReason(reason));
            }
            else
            {
                ClearTargetAndRescan($"{reason} Continuing the revival scan.");
            }
            return;
        }

        if (activeCastAttempt >= 3)
        {
            FinishWithRestore(PostActivityRevivalState.Skipped, BuildRaiseFailureReason($"{selectedJobName} raise action remained castable after 3 attempts."));
            return;
        }

        var target = FindTarget();
        if (target == null || !IsEligibleDeadPlayer(target) || !IsWithinRange(target))
        {
            if (activeMode)
            {
                FinishWithRestore(PostActivityRevivalState.Skipped, BuildRaiseFailureReason("Revival target was no longer valid while retrying the raise action."));
            }
            else
            {
                ClearTargetAndRescan("Revival target was no longer valid while retrying the raise action.");
            }
            return;
        }

        activeCastAttempt++;
        if (!gameActionController.TryExecuteAction(selectedActionId, $"{selectedJobName} raise retry {activeCastAttempt}/3 for {targetName}", target.GameObjectId))
        {
            logger.VerboseThrottled(
                "active-revival-dispatch-retry",
                TimeSpan.FromSeconds(2),
                $"[Revival] op=active-raise-dispatch-retry target=\"{targetName}\" attempt={activeCastAttempt}/3 actionId={selectedActionId}");
        }

        activeActionPollAt = DateTimeOffset.UtcNow + ActiveActionPollInterval;
    }

    private string BuildRaiseSuccessReason(string reason)
        => activeMode ? $"{reason} Returning to FATE/CE combat." : $"{reason} Continuing the revival pass.";

    private string BuildRaiseFailureReason(string reason)
        => activeMode ? $"{reason} Returning to FATE/CE combat." : $"{reason} Continuing farm recovery.";

    private void TickRestoringJob()
    {
        if (gameActionController.TryGetCurrentSupportJob(out var currentJob) && currentJob == originalJob)
        {
            TransitionTo(PostActivityRevivalState.Completed, "Original phantom job restored after post-activity revival.");
            return;
        }

        if (DateTimeOffset.UtcNow - stateEnteredAt >= CastAttemptTimeout)
        {
            TransitionTo(PostActivityRevivalState.Skipped, $"Original phantom job restoration timed out; continuing farm recovery. expectedJob={originalJob}.");
        }
    }

    private void ClearTargetAndRescan(string reason)
    {
        var objectId = targetObjectId;
        movementController.Stop(reason);
        if (objectId != 0)
        {
            gameActionController.TryClearTarget(objectId, reason);
        }

        lock (gate)
        {
            targetObjectId = 0;
            targetName = string.Empty;
            nextScanAt = DateTimeOffset.MinValue;
        }

        TransitionTo(PostActivityRevivalState.WaitingForAction, reason);
    }

    private void FinishWithRestore(PostActivityRevivalState terminalState, string reason)
    {
        movementController.Stop(reason);
        if (targetObjectId != 0)
        {
            gameActionController.TryClearTarget(targetObjectId, reason);
        }

        if (terminalState is PostActivityRevivalState.Completed or PostActivityRevivalState.Skipped)
        {
            if (gameActionController.TryGetCurrentSupportJob(out var currentJob) && currentJob == originalJob)
            {
                TransitionTo(terminalState, reason);
                return;
            }

            if (!gameActionController.TryChangeSupportJob(originalJob, "post-activity revival restore"))
            {
                lastError = $"{reason} Failed to restore original phantom job {originalJob}.";
                TransitionTo(PostActivityRevivalState.Skipped, lastError);
                return;
            }

            TransitionTo(PostActivityRevivalState.RestoringJob, reason);
            return;
        }

        TransitionTo(terminalState, reason);
    }

    private bool scannerReady()
        => scanner.Snapshot.IsInSupportedTerritory;

    private IGameObject[] FindDeadPlayers(Vector3 anchor)
    {
        var players = objectTable
            .Where(IsPlayerCharacter)
            .ToArray();
        var deadPlayers = players
            .Where(gameObject => gameObject is ICharacter character && character.CurrentHp == 0)
            .ToArray();
        var candidates = deadPlayers
            .Where(gameObject => IsEligibleDeadPlayer(gameObject))
            .Where(gameObject => CalculateFlatDistance(anchor, gameObject.Position) <= ScanRadius)
            .OrderBy(gameObject => CalculateFlatDistance(anchor, gameObject.Position))
            .ToArray();

        var scanMessage = $"[Revival] op=scan players={players.Length} dead={deadPlayers.Length} eligible={candidates.Length} radius={ScanRadius:0}.";
        if (candidates.Length > 0)
        {
            logger.DebugThrottled("revival-scan", TimeSpan.FromSeconds(10), scanMessage);
        }
        else
        {
            logger.VerboseThrottled("revival-scan-empty", TimeSpan.FromSeconds(10), scanMessage);
        }
        if (deadPlayers.Length > 0)
        {
            logger.VerboseThrottled(
                "revival-dead-player-diagnostics",
                TimeSpan.FromSeconds(10),
                $"[Revival] op=dead-player-diagnostics {string.Join(" | ", deadPlayers.Select(gameObject => DescribeDeadPlayer(gameObject, anchor)))}");
        }

        return candidates;
    }

    private IGameObject? FindTarget()
        => targetObjectId == 0 ? null : objectTable.FirstOrDefault(gameObject => gameObject.GameObjectId == targetObjectId);

    private bool IsEligibleDeadPlayer(IGameObject gameObject)
        => IsPlayerCharacter(gameObject)
            && gameObject is ICharacter player
            && player.IsValid()
            && player.GameObjectId != objectTable.LocalPlayer?.GameObjectId
            && player.CurrentHp == 0
            && !HasRaiseStatus(player);

    private static bool IsPlayerCharacter(IGameObject gameObject)
        => gameObject.ObjectKind == ObjectKind.Pc
            && gameObject is ICharacter;

    private string DescribeDeadPlayer(IGameObject gameObject, Vector3 anchor)
    {
        var character = (ICharacter)gameObject;
        var distance = CalculateFlatDistance(anchor, gameObject.Position);
        var valid = character.IsValid();
        var isLocalPlayer = character.GameObjectId == objectTable.LocalPlayer?.GameObjectId;
        var hasRaiseStatus = HasRaiseStatus(character);
        var eligible = valid
            && !isLocalPlayer
            && distance <= ScanRadius
            && !hasRaiseStatus;
        return $"name=\"{character.Name}\" objectId={character.GameObjectId:X} distance={distance:0.0} withinRadius={distance <= ScanRadius} valid={valid} local={isLocalPlayer} hasRaiseStatus={hasRaiseStatus} eligible={eligible}";
    }

    private bool IsWithinRange(IGameObject target)
    {
        var player = objectTable.LocalPlayer;
        return player != null && CalculateFlatDistance(player.Position, target.Position) <= CurrentReviveRange;
    }

    private float CurrentReviveRange => activeMode ? ActiveReviveRange : PostActivityReviveRange;

    private static unsafe bool HasRaiseStatus(ICharacter character)
    {
        var characterPointer = (Character*)character.Address;
        return characterPointer != null
            && characterPointer->VirtualTable != null
            && RaiseStatusIds.Any(statusId => characterPointer->HasStatus(statusId));
    }

    private static bool TrySelectJob(byte currentJob, byte[] levels, out byte selectedJob, out uint actionId, out string jobName)
    {
        var chemistUsable = levels.Length > ChemistJobId && levels[ChemistJobId] >= ChemistMinimumLevel;
        var whiteMageUsable = levels.Length > WhiteMageJobId && levels[WhiteMageJobId] >= WhiteMageMinimumLevel;

        if (currentJob == ChemistJobId && chemistUsable)
        {
            selectedJob = ChemistJobId;
            actionId = ChemistReviveActionId;
            jobName = "Chemist";
            return true;
        }

        if (currentJob == WhiteMageJobId && whiteMageUsable)
        {
            selectedJob = WhiteMageJobId;
            actionId = WhiteMageRaiseActionId;
            jobName = "White Mage";
            return true;
        }

        if (chemistUsable)
        {
            selectedJob = ChemistJobId;
            actionId = ChemistReviveActionId;
            jobName = "Chemist";
            return true;
        }

        if (whiteMageUsable)
        {
            selectedJob = WhiteMageJobId;
            actionId = WhiteMageRaiseActionId;
            jobName = "White Mage";
            return true;
        }

        selectedJob = 0;
        actionId = 0;
        jobName = string.Empty;
        return false;
    }

    private static bool TrySelectCurrentJob(byte currentJob, byte[] levels, out uint actionId, out string jobName)
    {
        if (currentJob == ChemistJobId
            && levels.Length > ChemistJobId
            && levels[ChemistJobId] >= ChemistMinimumLevel)
        {
            actionId = ChemistReviveActionId;
            jobName = "Chemist";
            return true;
        }

        if (currentJob == WhiteMageJobId
            && levels.Length > WhiteMageJobId
            && levels[WhiteMageJobId] >= WhiteMageMinimumLevel)
        {
            actionId = WhiteMageRaiseActionId;
            jobName = "White Mage";
            return true;
        }

        actionId = 0;
        jobName = string.Empty;
        return false;
    }

    private void TransitionTo(PostActivityRevivalState nextState, string reason)
    {
        PostActivityRevivalState previous;
        lock (gate)
        {
            previous = state;
            state = nextState;
            lastTransition = reason;
            stateEnteredAt = DateTimeOffset.UtcNow;
        }

        logger.Info($"[Revival] op=transition from={previous} to={nextState} target=\"{targetName}\" reason={reason}");
    }

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private static string FormatVector(Vector3 value)
        => $"<{value.X:0.0},{value.Y:0.0},{value.Z:0.0}>";
}
