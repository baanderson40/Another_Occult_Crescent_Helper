using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using AOCCH.Data;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using TreasureFlags = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags;

namespace AOCCH.Automation;

public sealed class CofferInteractionController : IDisposable
{
    private sealed record InventorySnapshot(Dictionary<uint, uint> ItemCounts, int NonEmptySlots);

    private enum HiddenApproachReadiness
    {
        Ready,
        Pending,
        Failed,
    }

    private static int nextRunSequence;
    private const float MaxInteractRange = 4.5f;
    private const float PreferredOpenDistance = 3.25f;
    private const uint JumpActionId = 2;
    private const float JumpAssistTriggerDistance = 10f;
    private const float PotRevealConfirmationFallbackRadius = 8f;
    private const float PotRevealRetryPassDistance = 10f;
    private const float PotRevealRetryPassSearchRadius = 8f;
    private const float PotRevealRetryPassVerticalSearch = 8f;
    private const float PotRevealRetryPassMaxVerticalDelta = 8f;
    private const float PotRevealRetryPassMinimumForwardDistance = 0.5f;
    private const float PotRevealMinimumValidY = -400f;
    private const float PotRevealMaximumValidY = 500f;
    private const float PotRevealSentinelY = -500f;
    private const float PotRevealSentinelTolerance = 0.5f;
    private const int RequiredMissingConfirmations = 2;
    private const int MaxInteractionAttempts = 3;
    private const int MaxLockOnReleaseAttempts = 3;
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PotRevealLockOnSettleDuration = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PotRevealRetryMovementTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LockOnReleaseRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HiddenDismountTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HiddenDismountRequestInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HideStateSettleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HideReadyTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan HideDispatchRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HideVerifyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);
    private static readonly InventoryType[] NormalInventoryContainers = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly GameActionController gameActionController;
    private readonly CofferPositionOverrideStore cofferPositionOverrideStore;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    public event Action<VisibleCofferMatch>? CofferOpened;

    private CofferInteractionState state = CofferInteractionState.Idle;
    private CofferInteractionResult lastResult;
    private string currentRunId = string.Empty;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private VisibleCofferMatch? activeMatch;
    private DateTimeOffset confirmationDeadlineAt = DateTimeOffset.MinValue;
    private int interactionAttemptCount;
    private int missingConfirmationCount;
    private TreasureFlags lastObservedTreasureFlags;
    private bool jumpAssistFiredThisApproach;
    private InventorySnapshot? preInteractionInventorySnapshot;
    private bool preInteractionInventorySnapshotValid;
    private bool inventoryFallbackLoggedThisAttempt;
    private bool potRevealLockOnOwned;
    private DateTimeOffset potRevealInteractionAvailableAt = DateTimeOffset.MinValue;
    private DateTimeOffset potRevealLockOnReleaseRetryAt = DateTimeOffset.MinValue;
    private int potRevealLockOnReleaseAttemptCount;
    private bool potRevealRetryMovementStarted;
    private DateTimeOffset potRevealRetryMovementDeadlineAt = DateTimeOffset.MinValue;
    private readonly HashSet<string> treasureFlagReadFailureLoggedPhases = new(StringComparer.Ordinal);
    private DateTimeOffset hiddenDismountStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset hiddenDismountRequestAvailableAt = DateTimeOffset.MinValue;
    private DateTimeOffset hiddenHideReadyDeadlineAt = DateTimeOffset.MinValue;
    private DateTimeOffset hiddenHideDispatchRetryAt = DateTimeOffset.MinValue;
    private DateTimeOffset hiddenHideVerificationDeadlineAt = DateTimeOffset.MinValue;
    private bool hiddenHideVerificationRetryUsed;

    public CofferInteractionController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        OccultCrescentScanner scanner,
        MovementController movementController,
        GameActionController gameActionController,
        CofferPositionOverrideStore cofferPositionOverrideStore,
        AocchLogger logger)
    {
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.scanner = scanner;
        this.movementController = movementController;
        this.gameActionController = gameActionController;
        this.cofferPositionOverrideStore = cofferPositionOverrideStore;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
    }

    public CofferInteractionState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public CofferInteractionResult LastResult
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

    public VisibleCofferMatch? ActiveMatch
    {
        get
        {
            lock (gate)
            {
                return activeMatch;
            }
        }
    }

    public DateTimeOffset ConfirmationDeadlineAt
    {
        get
        {
            lock (gate)
            {
                return confirmationDeadlineAt;
            }
        }
    }

    public int InteractionAttemptCount
    {
        get
        {
            lock (gate)
            {
                return interactionAttemptCount;
            }
        }
    }

    public bool IsRunning
        => State is not CofferInteractionState.Idle
            and not CofferInteractionState.Opened
            and not CofferInteractionState.LostCoffer
            and not CofferInteractionState.TimedOut
            and not CofferInteractionState.Stopped
            and not CofferInteractionState.Failed;

    public bool Start(VisibleCofferMatch match)
    {
        if (IsRunning)
        {
            logger.Debug($"Coffer interaction start ignored because a run is already active. candidate={match.CandidateKey} currentState={State}.");
            return true;
        }

        if (potRevealLockOnOwned)
        {
            ReleasePotRevealLockOn("before starting a new coffer interaction");
            if (potRevealLockOnOwned)
            {
                logger.Warning("[Coffer] op=start-blocked reason=stale-pot-reveal-lockon");
                return false;
            }
        }

        logger.Info($"[Coffer] op=start-request flow={match.Flow} candidate={match.CandidateKey.Label} coffer=\"{match.Coffer.Name}\" ({match.Coffer.GameObjectId:X}) trustworthy={match.IsTrustworthy} attribution=\"{match.AttributionReason}\"");

        var liveObject = ResolveObject(match.Coffer.GameObjectId);
        if (liveObject == null)
        {
            TransitionTo(CofferInteractionState.LostCoffer, "Matched coffer is no longer available to interact with.", result: CofferInteractionResult.LostCoffer);
            return false;
        }

        lock (gate)
        {
            currentRunId = $"Coffer#{Interlocked.Increment(ref nextRunSequence)}";
            activeMatch = match;
            confirmationDeadlineAt = DateTimeOffset.MinValue;
            interactionAttemptCount = 0;
            missingConfirmationCount = 0;
            lastObservedTreasureFlags = TreasureFlags.None;
            jumpAssistFiredThisApproach = false;
            preInteractionInventorySnapshot = null;
            preInteractionInventorySnapshotValid = false;
            inventoryFallbackLoggedThisAttempt = false;
            potRevealInteractionAvailableAt = DateTimeOffset.MinValue;
            potRevealLockOnReleaseRetryAt = DateTimeOffset.MinValue;
            potRevealLockOnReleaseAttemptCount = 0;
            potRevealRetryMovementStarted = false;
            potRevealRetryMovementDeadlineAt = DateTimeOffset.MinValue;
            treasureFlagReadFailureLoggedPhases.Clear();
            ResetHiddenApproachReadiness();
            lastError = string.Empty;
            lastResult = CofferInteractionResult.None;
        }

        movementController.SetLogOwner(currentRunId);

        if (TryReadTreasureFlags(liveObject, out var currentFlags))
        {
            lock (gate)
            {
                lastObservedTreasureFlags = currentFlags;
            }
        }
        else
        {
            LogTreasureFlagReadFailureOnce("start", liveObject);
        }

        return BeginApproachOrTarget(liveObject, "Starting coffer interaction.");
    }

    public void Stop(string reason)
    {
        logger.Info($"{BuildLogTag()} op=stop-request state={State} candidate={ActiveMatch?.CandidateKey.Label ?? "none"} reason={reason}");
        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop(reason);
        }

        TransitionTo(CofferInteractionState.Stopped, reason, error: reason, result: CofferInteractionResult.Stopped);
    }

    public void ResetInstanceState(string reason)
    {
        ReleasePotRevealLockOn($"reset: {reason}");
        lock (gate)
        {
            state = CofferInteractionState.Idle;
            lastResult = CofferInteractionResult.None;
            currentRunId = string.Empty;
            lastTransition = "Idle";
            lastError = string.Empty;
            activeMatch = null;
            confirmationDeadlineAt = DateTimeOffset.MinValue;
            interactionAttemptCount = 0;
            missingConfirmationCount = 0;
            lastObservedTreasureFlags = TreasureFlags.None;
            jumpAssistFiredThisApproach = false;
            preInteractionInventorySnapshot = null;
            preInteractionInventorySnapshotValid = false;
            inventoryFallbackLoggedThisAttempt = false;
            potRevealInteractionAvailableAt = DateTimeOffset.MinValue;
            potRevealLockOnReleaseRetryAt = potRevealLockOnOwned
                ? DateTimeOffset.UtcNow + LockOnReleaseRetryDelay
                : DateTimeOffset.MinValue;
            if (!potRevealLockOnOwned)
            {
                potRevealLockOnReleaseAttemptCount = 0;
            }
            potRevealRetryMovementStarted = false;
            potRevealRetryMovementDeadlineAt = DateTimeOffset.MinValue;
            treasureFlagReadFailureLoggedPhases.Clear();
            ResetHiddenApproachReadiness();
        }

        logger.Info($"[Coffer] op=reset reason={reason}");
    }

    public void Dispose()
    {
        if (IsRunning)
        {
            Stop("Coffer interaction disposal");
        }

        while (potRevealLockOnOwned && potRevealLockOnReleaseAttemptCount < MaxLockOnReleaseAttempts)
        {
            ReleasePotRevealLockOn("disposal retry");
        }

        framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (potRevealLockOnOwned
            && potRevealLockOnReleaseRetryAt != DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow >= potRevealLockOnReleaseRetryAt)
        {
            ReleasePotRevealLockOn("scheduled release retry");
        }

        if (!IsRunning)
        {
            return;
        }

        var activeFlow = ActiveMatch?.Flow;
        var featureAvailable = activeFlow switch
        {
            CofferInteractionFlow.VisibleRoute => scanner.Snapshot.CanRunVisibleCofferRoute,
            CofferInteractionFlow.PotReveal => scanner.Snapshot.CanRunPotTreasure,
            _ => false,
        };
        if (!scanner.Snapshot.IsInSupportedTerritory || !featureAvailable)
        {
            SetFailure($"Coffer interaction stopped because {activeFlow} data became unavailable.");
            return;
        }

        switch (State)
        {
            case CofferInteractionState.ApproachingCoffer:
                TickApproach();
                break;
            case CofferInteractionState.TargetingCoffer:
                TickTargeting();
                break;
            case CofferInteractionState.InteractingWithCoffer:
                TickInteraction();
                break;
            case CofferInteractionState.WaitingForOpenConfirmation:
                TickConfirmation();
                break;
            case CofferInteractionState.RepositioningPastCoffer:
                TickRepositioningPastCoffer();
                break;
            case CofferInteractionState.ReturningToCoffer:
                TickReturningToCoffer();
                break;
        }
    }

    private bool BeginApproachOrTarget(IGameObject liveObject, string reason)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition == null)
        {
            SetFailure("Player position is unavailable for coffer interaction.");
            return false;
        }

        var interactionPosition = GetInteractionPosition(liveObject);
        var distance = CalculateFlatDistance(playerPosition.Value, interactionPosition);
        logger.Debug($"Coffer interaction evaluating approach for {liveObject.Name.TextValue} ({liveObject.GameObjectId:X}). distance={distance:0.0} preferredOpenDistance={PreferredOpenDistance:0.00} maxInteractRange={MaxInteractRange:0.0} reason={reason}");
        if (distance <= PreferredOpenDistance)
        {
            TransitionTo(CofferInteractionState.TargetingCoffer, $"{reason} Coffer is within the preferred opening distance ({distance:0.0}y <= {PreferredOpenDistance:0.00}y).");
            return true;
        }

        if (ActiveMatch?.MustStayHidden == true)
        {
            var hiddenReadiness = EnsureHiddenApproachReady(liveObject, reason);
            if (hiddenReadiness == HiddenApproachReadiness.Failed)
            {
                return false;
            }

            if (hiddenReadiness == HiddenApproachReadiness.Pending)
            {
                TransitionTo(CofferInteractionState.ApproachingCoffer, $"{reason} Preparing hidden approach to {liveObject.Name.TextValue}. {FormatHiddenContextReason()}");
                return true;
            }
        }

        var destination = interactionPosition;
        var destinationAlreadyResolved = ActiveMatch?.Flow == CofferInteractionFlow.VisibleRoute;
        if (!destinationAlreadyResolved)
        {
            var navigableDestination = movementController.FindNearestNavigablePoint(interactionPosition, halfExtentXZ: 3f, halfExtentY: 3f);
            if (!navigableDestination.HasValue)
            {
                SetFailure($"No reliable vnavmesh point is available near coffer {liveObject.Name.TextValue} ({liveObject.GameObjectId:X}).");
                return false;
            }

            destination = navigableDestination.Value;
        }

        logger.Debug($"Coffer interaction moving toward {liveObject.Name.TextValue} ({liveObject.GameObjectId:X}). destination=<{destination.X:0.0}, {destination.Y:0.0}, {destination.Z:0.0}> reason={reason}");
        movementController.SetLogOwner(currentRunId);
        if (!movementController.StartDirectMove($"Approach coffer {liveObject.Name.TextValue}", destination, PreferredOpenDistance, shouldMountBeforeStep: ActiveMatch?.MustStayHidden != true, destinationAlreadyResolved: destinationAlreadyResolved))
        {
            SetFailure(movementController.LastError.Length == 0
                ? "Failed to begin movement into coffer interact range."
                : movementController.LastError);
            return false;
        }

        lock (gate)
        {
            jumpAssistFiredThisApproach = false;
        }

        TransitionTo(CofferInteractionState.ApproachingCoffer, $"{reason} Moving within {PreferredOpenDistance:0.00}y of {liveObject.Name.TextValue} before attempting to open it.");
        return true;
    }

    private void TickApproach()
    {
        if (ActiveMatch?.MustStayHidden == true)
        {
            var liveObject = ResolveActiveObject();
            if (liveObject == null)
            {
                TransitionTo(CofferInteractionState.LostCoffer, "Matched coffer disappeared before the approach completed.", result: CofferInteractionResult.LostCoffer);
                return;
            }

            var hiddenReadiness = EnsureHiddenApproachReady(liveObject, "Hidden coffer approach");
            if (hiddenReadiness != HiddenApproachReadiness.Ready)
            {
                return;
            }

            if (movementController.State is MovementState.Idle or MovementState.Stopped)
            {
                BeginApproachOrTarget(liveObject, "Hidden coffer approach is ready.");
                return;
            }
        }

        TryApplyJumpAssistDuringApproach();

        switch (movementController.State)
        {
            case MovementState.Arrived:
                var liveObject = ResolveActiveObject();
                if (liveObject == null)
                {
                    TransitionTo(CofferInteractionState.LostCoffer, "Matched coffer disappeared before the approach completed.", result: CofferInteractionResult.LostCoffer);
                    return;
                }

                var playerPosition = objectTable.LocalPlayer?.Position;
                if (playerPosition == null)
                {
                    SetFailure("Player position is unavailable after arriving at the matched coffer.");
                    return;
                }

                var distance = CalculateFlatDistance(playerPosition.Value, GetInteractionPosition(liveObject));
                if (distance > PreferredOpenDistance)
                {
                    BeginApproachOrTarget(liveObject, $"Movement reported arrival, but the player is still {distance:0.0}y from {liveObject.Name.TextValue}.");
                    return;
                }

                movementController.Stop("Reached coffer interact range.");
                TransitionTo(CofferInteractionState.TargetingCoffer, $"Reached the preferred opening distance for {liveObject.Name.TextValue} ({distance:0.0}y).");
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                SetFailure(movementController.LastError.Length == 0
                    ? "Failed to move into coffer interact range."
                    : movementController.LastError);
                return;
        }

        logger.DebugThrottled("coffer-approach", WaitLogInterval, $"Coffer interaction is approaching {ActiveMatch?.Coffer.Name ?? "unknown"}. targetDistance<={PreferredOpenDistance:0.00}y movementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
    }

    private void TickTargeting()
    {
        var liveObject = ResolveActiveObject();
        if (liveObject == null)
        {
            TransitionTo(CofferInteractionState.LostCoffer, "Matched coffer disappeared before it could be targeted.", result: CofferInteractionResult.LostCoffer);
            return;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition == null)
        {
            SetFailure("Player position is unavailable while targeting the matched coffer.");
            return;
        }

        var distance = CalculateFlatDistance(playerPosition.Value, GetInteractionPosition(liveObject));
        if (distance > MaxInteractRange)
        {
            if (ActiveMatch?.Flow == CofferInteractionFlow.PotReveal
                && !TryReleasePotRevealLockOn("player drifted outside targeting range"))
            {
                return;
            }

            BeginApproachOrTarget(liveObject, $"Player drifted outside the coffer interaction range ({distance:0.0}y > {MaxInteractRange:0.0}y).");
            return;
        }

        if (ActiveMatch?.MustStayHidden == true && EnsureHiddenApproachReady(liveObject, "Hidden coffer targeting") != HiddenApproachReadiness.Ready)
        {
            return;
        }

        logger.ResetThrottle("coffer-approach");
        if (!gameActionController.TrySetTarget(liveObject, "coffer interaction"))
        {
            SetFailure("Failed to target the matched coffer.");
            return;
        }

        if (ActiveMatch?.Flow == CofferInteractionFlow.PotReveal)
        {
            if (!gameActionController.TrySetLockOn(enabled: true, "pot-reveal coffer interaction"))
            {
                SetFailure("Failed to lock the camera onto the targeted pot-reveal coffer.");
                return;
            }

            var interactionPosition = GetInteractionPosition(liveObject);
            lock (gate)
            {
                potRevealLockOnOwned = true;
                potRevealInteractionAvailableAt = DateTimeOffset.UtcNow + PotRevealLockOnSettleDuration;
                potRevealLockOnReleaseRetryAt = DateTimeOffset.MinValue;
                potRevealLockOnReleaseAttemptCount = 0;
            }

            logger.Info($"{BuildLogTag()} op=pot-reveal-lockon-request candidate={DescribeActiveCandidate()} objectId={liveObject.GameObjectId:X} attempt={interactionAttemptCount + 1} livePosition=<{liveObject.Position.X:0.0}, {liveObject.Position.Y:0.0}, {liveObject.Position.Z:0.0}> interactionPosition=<{interactionPosition.X:0.0}, {interactionPosition.Y:0.0}, {interactionPosition.Z:0.0}> sentinelY={MathF.Abs(liveObject.Position.Y + 500f) < 0.5f} settleMs={PotRevealLockOnSettleDuration.TotalMilliseconds:0} readyAt={potRevealInteractionAvailableAt:O}");
        }

        TransitionTo(CofferInteractionState.InteractingWithCoffer, ActiveMatch?.Flow == CofferInteractionFlow.PotReveal
            ? $"Targeted and locked onto coffer {liveObject.Name.TextValue} at {distance:0.0}y; settling the camera before interaction."
            : $"Targeted coffer {liveObject.Name.TextValue} at {distance:0.0}y; attempting interaction.");
    }

    private void TickInteraction()
    {
        var liveObject = ResolveActiveObject();
        if (liveObject == null)
        {
            TransitionTo(CofferInteractionState.LostCoffer, "Matched coffer disappeared before interaction could start.", result: CofferInteractionResult.LostCoffer);
            return;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition == null)
        {
            SetFailure("Player position is unavailable while interacting with the matched coffer.");
            return;
        }

        var distance = CalculateFlatDistance(playerPosition.Value, GetInteractionPosition(liveObject));
        if (distance > MaxInteractRange)
        {
            if (!TryReleasePotRevealLockOn("player drifted outside interaction range"))
            {
                return;
            }

            BeginApproachOrTarget(liveObject, $"Interaction was deferred because the player is {distance:0.0}y from the matched coffer.");
            return;
        }

        if (ActiveMatch?.MustStayHidden == true && EnsureHiddenApproachReady(liveObject, "Hidden coffer interaction") != HiddenApproachReadiness.Ready)
        {
            return;
        }

        if (ActiveMatch?.Flow == CofferInteractionFlow.PotReveal)
        {
            var now = DateTimeOffset.UtcNow;
            if (potRevealInteractionAvailableAt != DateTimeOffset.MinValue && now < potRevealInteractionAvailableAt)
            {
                logger.DebugThrottled(
                    "coffer-pot-reveal-lockon-settle",
                    TimeSpan.FromMilliseconds(200),
                    $"{BuildLogTag()} op=pot-reveal-lockon-settle candidate={DescribeActiveCandidate()} objectId={liveObject.GameObjectId:X} attempt={interactionAttemptCount + 1} remainingMs={(potRevealInteractionAvailableAt - now).TotalMilliseconds:0}");
                return;
            }

            if (!gameActionController.IsCurrentTarget(liveObject))
            {
                if (!TryReleasePotRevealLockOn("target changed during camera settle"))
                {
                    return;
                }

                TransitionTo(CofferInteractionState.TargetingCoffer, $"Pot-reveal coffer target changed during lock-on settle; retargeting before interaction attempt {interactionAttemptCount + 1}.");
                return;
            }

            lock (gate)
            {
                potRevealInteractionAvailableAt = DateTimeOffset.MinValue;
            }

            logger.Info($"{BuildLogTag()} op=pot-reveal-lockon-settled candidate={DescribeActiveCandidate()} objectId={liveObject.GameObjectId:X} attempt={interactionAttemptCount + 1} currentTarget=True");
        }

        if (ActiveMatch?.Flow == CofferInteractionFlow.PotReveal)
        {
            if (TryCaptureNormalInventorySnapshot(out var snapshot, out var error))
            {
                lock (gate)
                {
                    preInteractionInventorySnapshot = snapshot;
                    preInteractionInventorySnapshotValid = true;
                    inventoryFallbackLoggedThisAttempt = false;
                }

                logger.Info($"{BuildLogTag()} op=pot-reveal-inventory-baseline attempt={interactionAttemptCount + 1} nonEmptySlots={snapshot.NonEmptySlots}");
            }
            else
            {
                lock (gate)
                {
                    preInteractionInventorySnapshot = null;
                    preInteractionInventorySnapshotValid = false;
                    inventoryFallbackLoggedThisAttempt = true;
                }

                logger.Info($"{BuildLogTag()} op=pot-reveal-inventory-baseline-missing attempt={interactionAttemptCount + 1} reason={error}");
            }
        }

        var interactionDispatched = gameActionController.TryInteractWithObject(liveObject, "coffer interaction");
        if (!interactionDispatched)
        {
            logger.Warning($"{BuildLogTag()} op=interaction-action-failed candidate={DescribeActiveCandidate()} objectId={liveObject.GameObjectId:X} baseId={liveObject.BaseId} distance={distance:0.0}y attempt={interactionAttemptCount + 1} maxAttempts={MaxInteractionAttempts}");
            if (interactionAttemptCount + 1 >= MaxInteractionAttempts)
            {
                TransitionTo(CofferInteractionState.TimedOut, $"Coffer interaction failed after {MaxInteractionAttempts} attempts.", error: "Coffer interaction failed repeatedly.", result: CofferInteractionResult.TimedOut);
                return;
            }

            lock (gate)
            {
                interactionAttemptCount++;
            }

            if (ActiveMatch?.Flow == CofferInteractionFlow.PotReveal)
            {
                BeginPotRevealRetry("Interaction dispatch failed; changing the approach side before retrying.");
            }
            else
            {
                TransitionTo(CofferInteractionState.TargetingCoffer, $"Retrying coffer interaction attempt {interactionAttemptCount + 1} of {MaxInteractionAttempts}.");
            }
            return;
        }

        lock (gate)
        {
            interactionAttemptCount++;
            missingConfirmationCount = 0;
            confirmationDeadlineAt = DateTimeOffset.UtcNow + ConfirmationTimeout;
        }

        TransitionTo(CofferInteractionState.WaitingForOpenConfirmation, $"Interaction attempt {interactionAttemptCount} started; waiting for coffer open confirmation.");
    }

    private void BeginPotRevealRetry(string reason)
    {
        lock (gate)
        {
            potRevealRetryMovementStarted = false;
            potRevealRetryMovementDeadlineAt = DateTimeOffset.MinValue;
        }

        TransitionTo(CofferInteractionState.RepositioningPastCoffer, reason);
    }

    private void TickRepositioningPastCoffer()
    {
        if (!TryReleasePotRevealLockOn("before retry repositioning"))
        {
            return;
        }

        if (!potRevealRetryMovementStarted)
        {
            var liveObject = ResolveActiveObject();
            var playerPosition = objectTable.LocalPlayer?.Position;
            if (liveObject == null)
            {
                TransitionTo(CofferInteractionState.LostCoffer, "Matched coffer disappeared before retry repositioning.", result: CofferInteractionResult.LostCoffer);
                return;
            }

            if (playerPosition == null)
            {
                logger.Warning($"{BuildLogTag()} op=pot-reveal-retry-fallback phase=pass reason=player-position-unavailable");
                TransitionTo(CofferInteractionState.TargetingCoffer, "Retry pass-point movement was unavailable; retrying from the current position.");
                return;
            }

            if (!TryBuildPotRevealPassPoint(liveObject, playerPosition.Value, out var passPoint, out var reason))
            {
                logger.Warning($"{BuildLogTag()} op=pot-reveal-retry-fallback phase=pass reason={reason}");
                TransitionTo(CofferInteractionState.TargetingCoffer, "Retry pass-point movement was unavailable; retrying from the current position.");
                return;
            }

            if (!movementController.StartDirectMove(
                    $"Pass coffer {liveObject.Name.TextValue} for interaction retry",
                    passPoint,
                    arrivalTolerance: 1.5f,
                    shouldMountBeforeStep: true,
                    destinationAlreadyResolved: true))
            {
                logger.Warning($"{BuildLogTag()} op=pot-reveal-retry-fallback phase=pass reason=movement-start-failed error={movementController.LastError}");
                TransitionTo(CofferInteractionState.TargetingCoffer, "Retry pass movement could not start; retrying from the current position.");
                return;
            }

            lock (gate)
            {
                potRevealRetryMovementStarted = true;
                potRevealRetryMovementDeadlineAt = DateTimeOffset.UtcNow + PotRevealRetryMovementTimeout;
            }

            logger.Info($"{BuildLogTag()} op=pot-reveal-pass-movement-start candidate={DescribeActiveCandidate()} objectId={liveObject.GameObjectId:X} attempt={interactionAttemptCount + 1} destination=<{passPoint.X:0.0}, {passPoint.Y:0.0}, {passPoint.Z:0.0}> requestedDistance={PotRevealRetryPassDistance:0.0}y");
            return;
        }

        if (DateTimeOffset.UtcNow >= potRevealRetryMovementDeadlineAt)
        {
            StopRetryMovement("pass movement timed out");
            logger.Warning($"{BuildLogTag()} op=pot-reveal-retry-fallback phase=pass reason=movement-timeout");
            TransitionTo(CofferInteractionState.TargetingCoffer, "Retry pass movement timed out; retrying from the current position.");
            return;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached retry pass point.");
                lock (gate)
                {
                    potRevealRetryMovementStarted = false;
                    potRevealRetryMovementDeadlineAt = DateTimeOffset.MinValue;
                }

                logger.Info($"{BuildLogTag()} op=pot-reveal-pass-movement-arrived candidate={DescribeActiveCandidate()} attempt={interactionAttemptCount + 1}");
                TransitionTo(CofferInteractionState.ReturningToCoffer, "Reached the retry pass point; returning toward the coffer from the opposite side.");
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                StopRetryMovement($"pass movement ended in {movementController.State}");
                logger.Warning($"{BuildLogTag()} op=pot-reveal-retry-fallback phase=pass reason=movement-{movementController.State.ToString().ToLowerInvariant()} error={movementController.LastError}");
                TransitionTo(CofferInteractionState.TargetingCoffer, "Retry pass movement failed; retrying from the current position.");
                return;
        }
    }

    private void TickReturningToCoffer()
    {
        if (!potRevealRetryMovementStarted)
        {
            var liveObject = ResolveActiveObject();
            var playerPosition = objectTable.LocalPlayer?.Position;
            if (liveObject == null)
            {
                TransitionTo(CofferInteractionState.LostCoffer, "Matched coffer disappeared while returning from retry repositioning.", result: CofferInteractionResult.LostCoffer);
                return;
            }

            if (playerPosition == null)
            {
                TransitionTo(CofferInteractionState.TargetingCoffer, "Player position was unavailable while returning from retry repositioning.");
                return;
            }

            var interactionPosition = GetInteractionPosition(liveObject);
            var distance = CalculateFlatDistance(playerPosition.Value, interactionPosition);
            if (distance <= PreferredOpenDistance)
            {
                TransitionTo(CofferInteractionState.TargetingCoffer, $"Already within coffer interaction range after retry repositioning ({distance:0.0}y).");
                return;
            }

            var returnPoint = movementController.FindNearestNavigablePoint(interactionPosition, halfExtentXZ: 3f, halfExtentY: 3f);
            if (!returnPoint.HasValue || !IsPlausiblePotRevealPosition(returnPoint.Value, interactionPosition.Y, PotRevealRetryPassMaxVerticalDelta))
            {
                logger.Warning($"{BuildLogTag()} op=pot-reveal-retry-fallback phase=return reason=invalid-return-point point={(returnPoint.HasValue ? $"<{returnPoint.Value.X:0.0}, {returnPoint.Value.Y:0.0}, {returnPoint.Value.Z:0.0}>" : "none")}");
                TransitionTo(CofferInteractionState.TargetingCoffer, "Retry return point was unavailable; retrying from the current position.");
                return;
            }

            if (!movementController.StartDirectMove(
                    $"Return to coffer {liveObject.Name.TextValue} after retry reposition",
                    returnPoint.Value,
                    PreferredOpenDistance,
                    shouldMountBeforeStep: true,
                    destinationAlreadyResolved: true))
            {
                logger.Warning($"{BuildLogTag()} op=pot-reveal-retry-fallback phase=return reason=movement-start-failed error={movementController.LastError}");
                TransitionTo(CofferInteractionState.TargetingCoffer, "Retry return movement could not start; retrying from the current position.");
                return;
            }

            lock (gate)
            {
                potRevealRetryMovementStarted = true;
                potRevealRetryMovementDeadlineAt = DateTimeOffset.UtcNow + PotRevealRetryMovementTimeout;
            }

            logger.Info($"{BuildLogTag()} op=pot-reveal-return-movement-start candidate={DescribeActiveCandidate()} objectId={liveObject.GameObjectId:X} attempt={interactionAttemptCount + 1} destination=<{returnPoint.Value.X:0.0}, {returnPoint.Value.Y:0.0}, {returnPoint.Value.Z:0.0}> arrivalTolerance={PreferredOpenDistance:0.0}");
            return;
        }

        if (DateTimeOffset.UtcNow >= potRevealRetryMovementDeadlineAt)
        {
            StopRetryMovement("return movement timed out");
            logger.Warning($"{BuildLogTag()} op=pot-reveal-retry-fallback phase=return reason=movement-timeout");
            TransitionTo(CofferInteractionState.TargetingCoffer, "Retry return movement timed out; retrying from the current position.");
            return;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached coffer range after retry reposition.");
                lock (gate)
                {
                    potRevealRetryMovementStarted = false;
                    potRevealRetryMovementDeadlineAt = DateTimeOffset.MinValue;
                }

                logger.Info($"{BuildLogTag()} op=pot-reveal-return-movement-arrived candidate={DescribeActiveCandidate()} attempt={interactionAttemptCount + 1}");
                TransitionTo(CofferInteractionState.TargetingCoffer, "Returned to coffer range from the opposite side; retargeting for retry.");
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                StopRetryMovement($"return movement ended in {movementController.State}");
                logger.Warning($"{BuildLogTag()} op=pot-reveal-retry-fallback phase=return reason=movement-{movementController.State.ToString().ToLowerInvariant()} error={movementController.LastError}");
                TransitionTo(CofferInteractionState.TargetingCoffer, "Retry return movement failed; retrying from the current position.");
                return;
        }
    }

    private bool TryBuildPotRevealPassPoint(IGameObject liveObject, Vector3 playerPosition, out Vector3 passPoint, out string? reason)
    {
        passPoint = default;
        reason = null;

        var cofferPosition = GetInteractionPosition(liveObject);
        var delta = new Vector2(cofferPosition.X - playerPosition.X, cofferPosition.Z - playerPosition.Z);
        var distance = delta.Length();
        if (distance <= float.Epsilon)
        {
            reason = "player-and-coffer-horizontal-positions-overlap";
            return false;
        }

        if (!TryGetReliablePotRevealElevation(liveObject, playerPosition.Y, out var elevation))
        {
            reason = "no-reliable-coffer-elevation";
            return false;
        }

        var direction = delta / distance;
        var requested = new Vector3(
            cofferPosition.X + (direction.X * PotRevealRetryPassDistance),
            elevation,
            cofferPosition.Z + (direction.Y * PotRevealRetryPassDistance));

        var nearestPoint = movementController.FindNearestNavigablePoint(
            requested,
            halfExtentXZ: PotRevealRetryPassSearchRadius,
            halfExtentY: PotRevealRetryPassVerticalSearch);
        if (TryAcceptPotRevealPassPoint(nearestPoint, cofferPosition, direction, elevation, out passPoint, out var nearestReason))
        {
            return true;
        }

        var floorPoint = movementController.FindPointOnFloor(requested, PotRevealRetryPassSearchRadius);
        if (TryAcceptPotRevealPassPoint(floorPoint, cofferPosition, direction, elevation, out passPoint, out var floorReason))
        {
            return true;
        }

        reason = $"nearest={nearestReason}; floor={floorReason}; requested=<{requested.X:0.0}, {requested.Y:0.0}, {requested.Z:0.0}>";
        return false;
    }

    private bool TryAcceptPotRevealPassPoint(Vector3? candidate, Vector3 cofferPosition, Vector2 direction, float elevation, out Vector3 passPoint, out string reason)
    {
        passPoint = default;
        if (!candidate.HasValue)
        {
            reason = "no-point";
            return false;
        }

        var point = candidate.Value;
        if (!IsPlausiblePotRevealPosition(point, elevation, PotRevealRetryPassMaxVerticalDelta))
        {
            reason = "invalid-or-vertically-distant-point";
            logger.Debug($"{BuildLogTag()} op=pot-reveal-pass-point-rejected point=<{point.X:0.0}, {point.Y:0.0}, {point.Z:0.0}> referenceY={elevation:0.0} reason={reason}");
            return false;
        }

        var forward = ((point.X - cofferPosition.X) * direction.X) + ((point.Z - cofferPosition.Z) * direction.Y);
        if (forward < PotRevealRetryPassMinimumForwardDistance)
        {
            reason = $"point-not-beyond-coffer-forward={forward:0.0}y";
            logger.Debug($"{BuildLogTag()} op=pot-reveal-pass-point-rejected point=<{point.X:0.0}, {point.Y:0.0}, {point.Z:0.0}> reason={reason}");
            return false;
        }

        passPoint = point;
        reason = string.Empty;
        return true;
    }

    private bool TryGetReliablePotRevealElevation(IGameObject liveObject, float playerY, out float elevation)
    {
        var candidates = new[]
        {
            liveObject.Position.Y,
            ActiveMatch?.Coffer.Position.Y ?? float.NaN,
            playerY,
        };

        foreach (var candidate in candidates)
        {
            if (IsPlausiblePotRevealElevation(candidate))
            {
                elevation = candidate;
                return true;
            }
        }

        elevation = 0f;
        return false;
    }

    private static bool IsPlausiblePotRevealElevation(float value)
        => !float.IsNaN(value)
            && !float.IsInfinity(value)
            && MathF.Abs(value - PotRevealSentinelY) >= PotRevealSentinelTolerance
            && value >= PotRevealMinimumValidY
            && value <= PotRevealMaximumValidY;

    private static bool IsPlausiblePotRevealPosition(Vector3 point, float referenceY, float maxVerticalDelta)
        => !float.IsNaN(point.X)
            && !float.IsNaN(point.Y)
            && !float.IsNaN(point.Z)
            && !float.IsInfinity(point.X)
            && !float.IsInfinity(point.Y)
            && !float.IsInfinity(point.Z)
            && IsPlausiblePotRevealElevation(point.Y)
            && !float.IsNaN(referenceY)
            && !float.IsInfinity(referenceY)
            && MathF.Abs(point.Y - referenceY) <= maxVerticalDelta;

    private void StopRetryMovement(string reason)
    {
        if (movementController.State is not MovementState.Idle
            and not MovementState.Stopped
            and not MovementState.Arrived
            and not MovementState.Failed
            and not MovementState.TimedOut)
        {
            movementController.Stop(reason);
        }

        lock (gate)
        {
            potRevealRetryMovementStarted = false;
            potRevealRetryMovementDeadlineAt = DateTimeOffset.MinValue;
        }
    }

    private void TickConfirmation()
    {
        var active = ActiveMatch;
        if (active == null)
        {
            SetFailure("Coffer interaction lost its active match during confirmation.");
            return;
        }

        var liveObject = ResolveActiveObject();
        if (active.Flow != CofferInteractionFlow.PotReveal && liveObject != null)
        {
            if (TryReadTreasureFlags(liveObject, out var currentFlags))
            {
                var previousFlags = lastObservedTreasureFlags;
                lock (gate)
                {
                    lastObservedTreasureFlags = currentFlags;
                }

                if (!previousFlags.HasFlag(TreasureFlags.Opened) && currentFlags.HasFlag(TreasureFlags.Opened))
                {
                    PersistConfirmedOverride(active);
                    NotifyCofferOpened(active);
                    logger.ResetThrottle("coffer-confirmation");
                    logger.Info($"{BuildLogTag()} op=open-confirmed method=opened-flag flow={active.Flow} attempts={interactionAttemptCount} missingConfirmations={missingConfirmationCount} objectId={active.Coffer.GameObjectId:X}");
                    TransitionTo(CofferInteractionState.Opened, $"Confirmed coffer open via the treasure opened flag after {interactionAttemptCount} interaction attempt(s).", result: CofferInteractionResult.Opened);
                    return;
                }
            }
            else
            {
                LogTreasureFlagReadFailureOnce("confirmation", liveObject);
            }
        }

        if (active.Flow == CofferInteractionFlow.PotReveal && TryConfirmPotRevealOpenViaInventoryDelta(active))
        {
            return;
        }

        var stillVisible = IsStillVisibleForConfirmation(active);
        logger.DebugThrottled(
            $"coffer-confirmation-state-{currentRunId}",
            TimeSpan.FromMilliseconds(500),
            $"{BuildLogTag()} op=confirmation-observation candidate={DescribeActiveCandidate()} objectId={active.Coffer.GameObjectId:X} flow={active.Flow} attempt={interactionAttemptCount} flags={lastObservedTreasureFlags} visible={stillVisible} missingConfirmations={missingConfirmationCount} deadline={ConfirmationDeadlineAt:O} inventoryBaseline={preInteractionInventorySnapshotValid}");
        if (!stillVisible)
        {
            lock (gate)
            {
                missingConfirmationCount++;
            }

            if (missingConfirmationCount >= RequiredMissingConfirmations)
            {
                if (active.Flow == CofferInteractionFlow.PotReveal)
                {
                    logger.ResetThrottle("coffer-confirmation");
                    TransitionTo(
                        CofferInteractionState.LostCoffer,
                        $"Pot-reveal coffer disappeared after {interactionAttemptCount} interaction attempt(s) without inventory confirmation ({(preInteractionInventorySnapshotValid ? "baseline-present" : "baseline-missing")}).",
                        error: "Pot-reveal coffer disappeared without inventory confirmation.",
                        result: CofferInteractionResult.LostCoffer);
                    return;
                }

                PersistConfirmedOverride(active);
                NotifyCofferOpened(active);
                logger.ResetThrottle("coffer-confirmation");
                logger.Info($"{BuildLogTag()} op=open-confirmed method=object-disappeared flow={active.Flow} attempts={interactionAttemptCount} missingConfirmations={missingConfirmationCount} requiredMissingConfirmations={RequiredMissingConfirmations} objectId={active.Coffer.GameObjectId:X}");
                TransitionTo(CofferInteractionState.Opened, $"Confirmed coffer open after {interactionAttemptCount} interaction attempt(s).", result: CofferInteractionResult.Opened);
            }

            return;
        }

        lock (gate)
        {
            missingConfirmationCount = 0;
        }

        if (ConfirmationDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= ConfirmationDeadlineAt)
        {
            logger.ResetThrottle("coffer-confirmation");
            if (interactionAttemptCount >= MaxInteractionAttempts)
            {
                TransitionTo(CofferInteractionState.TimedOut, $"Coffer did not disappear after {interactionAttemptCount} interaction attempt(s).", error: "Coffer open confirmation timed out.", result: CofferInteractionResult.TimedOut);
                return;
            }

            if (active.Flow == CofferInteractionFlow.PotReveal)
            {
                BeginPotRevealRetry($"Coffer is still visible after interaction attempt {interactionAttemptCount}; changing the approach side before retrying.");
            }
            else
            {
                TransitionTo(CofferInteractionState.TargetingCoffer, $"Coffer is still visible after interaction attempt {interactionAttemptCount}; retrying.");
            }
            return;
        }

        logger.DebugThrottled("coffer-confirmation", WaitLogInterval, $"Waiting for coffer open confirmation on {active.Coffer.Name} ({active.Coffer.GameObjectId:X}). flow={active.Flow} attempt={interactionAttemptCount} deadline={ConfirmationDeadlineAt:O}.");
    }

    private bool IsStillVisibleForConfirmation(VisibleCofferMatch match)
        => match.Flow switch
        {
            CofferInteractionFlow.PotReveal => IsStillVisibleViaPotRevealObjectLookup(match),
            _ => IsStillVisibleViaScanner(match),
        };

    private bool IsStillVisibleViaScanner(VisibleCofferMatch match)
        => scanner.Snapshot.VisibleCoffers.Any(coffer => coffer.GameObjectId == match.Coffer.GameObjectId);

    private bool IsStillVisibleViaPotRevealObjectLookup(VisibleCofferMatch match)
    {
        var exact = ResolveObject(match.Coffer.GameObjectId);
        if (exact != null)
        {
            logger.DebugThrottled(
                $"coffer-confirmation-pot-{match.CandidateKey.CandidateKey}",
                TimeSpan.FromSeconds(1),
                $"Pot reveal confirmation kept exact object match for {match.Coffer.Name} ({match.Coffer.GameObjectId:X}).");
            return true;
        }

        foreach (var gameObject in objectTable)
        {
            if (gameObject is not IGameObject objectEntry || !objectEntry.IsValid())
            {
                continue;
            }

            if (objectEntry.BaseId != match.Coffer.DataId)
            {
                continue;
            }

            var distanceFromObserved = CalculateFlatDistance(objectEntry.Position, match.Coffer.Position);
            if (distanceFromObserved > PotRevealConfirmationFallbackRadius)
            {
                continue;
            }

            logger.DebugThrottled(
                $"coffer-confirmation-pot-{match.CandidateKey.CandidateKey}",
                TimeSpan.FromSeconds(1),
                $"Pot reveal confirmation found fallback object for {match.Coffer.Name}. originalObjectId={match.Coffer.GameObjectId:X} fallbackObjectId={objectEntry.GameObjectId:X} baseId={objectEntry.BaseId} distanceFromObserved={distanceFromObserved:0.0}y.");
            return true;
        }

        return false;
    }

    private bool TryConfirmPotRevealOpenViaInventoryDelta(VisibleCofferMatch match)
    {
        if (!preInteractionInventorySnapshotValid || preInteractionInventorySnapshot == null)
        {
            if (!inventoryFallbackLoggedThisAttempt)
            {
                inventoryFallbackLoggedThisAttempt = true;
                logger.Info($"{BuildLogTag()} op=pot-reveal-inventory-fallback attempt={interactionAttemptCount} reason=no-valid-baseline");
            }

            return false;
        }

        if (!TryCaptureNormalInventorySnapshot(out var currentSnapshot, out var error))
        {
            if (!inventoryFallbackLoggedThisAttempt)
            {
                inventoryFallbackLoggedThisAttempt = true;
                logger.Info($"{BuildLogTag()} op=pot-reveal-inventory-fallback attempt={interactionAttemptCount} reason={error}");
            }

            return false;
        }

        var deltas = FindPositiveInventoryDeltas(preInteractionInventorySnapshot, currentSnapshot);
        if (deltas.Count == 0)
        {
            return false;
        }

        PersistConfirmedOverride(match);
        NotifyCofferOpened(match);
        logger.ResetThrottle("coffer-confirmation");
        logger.Info($"{BuildLogTag()} op=pot-reveal-inventory-confirm attempt={interactionAttemptCount} deltas={string.Join(", ", deltas)}");
        logger.Info($"{BuildLogTag()} op=open-confirmed method=inventory-delta flow={match.Flow} attempts={interactionAttemptCount} missingConfirmations={missingConfirmationCount} objectId={match.Coffer.GameObjectId:X}");
        ReleasePotRevealLockOn("inventory delta confirmed coffer open");
        if (potRevealLockOnOwned)
        {
            ForgetPotRevealLockOn("unlock command failed after inventory confirmation; coffer despawn will auto-unlock");
        }

        TransitionTo(CofferInteractionState.Opened, $"Confirmed coffer open via inventory delta after {interactionAttemptCount} interaction attempt(s).", result: CofferInteractionResult.Opened);
        return true;
    }

    private static List<string> FindPositiveInventoryDeltas(InventorySnapshot before, InventorySnapshot after)
    {
        var deltas = new List<string>();
        foreach (var pair in after.ItemCounts)
        {
            before.ItemCounts.TryGetValue(pair.Key, out var beforeCount);
            if (pair.Value <= beforeCount)
            {
                continue;
            }

            deltas.Add($"itemId={pair.Key} before={beforeCount} after={pair.Value} added={pair.Value - beforeCount}");
        }

        deltas.Sort(StringComparer.Ordinal);
        return deltas;
    }

    private static unsafe bool TryCaptureNormalInventorySnapshot(out InventorySnapshot snapshot, out string error)
    {
        snapshot = new InventorySnapshot(new Dictionary<uint, uint>(), 0);
        error = string.Empty;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            error = "inventory-manager-unavailable";
            return false;
        }

        var itemCounts = new Dictionary<uint, uint>();
        var nonEmptySlots = 0;
        foreach (var containerType in NormalInventoryContainers)
        {
            var container = inventoryManager->GetInventoryContainer(containerType);
            if (container == null)
            {
                error = $"container-unavailable:{containerType}";
                return false;
            }

            if (!container->IsLoaded || container->Size <= 0 || container->Items == null)
            {
                error = $"container-not-ready:{containerType}:loaded={container->IsLoaded}:size={container->Size}";
                return false;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->IsEmpty())
                {
                    continue;
                }

                nonEmptySlots++;
                var itemId = item->ItemId;
                if (itemId == 0)
                {
                    continue;
                }

                var quantity = checked((uint)item->Quantity);

                itemCounts[itemId] = itemCounts.TryGetValue(itemId, out var count)
                    ? count + quantity
                    : quantity;
            }
        }

        snapshot = new InventorySnapshot(itemCounts, nonEmptySlots);
        return true;
    }

    private IGameObject? ResolveActiveObject()
        => ActiveMatch == null ? null : ResolveObject(ActiveMatch.Coffer.GameObjectId);

    private Vector3 GetInteractionPosition(IGameObject liveObject)
    {
        if (ActiveMatch?.Flow != CofferInteractionFlow.PotReveal)
        {
            return liveObject.Position;
        }

        var elevation = liveObject.Position.Y;
        if (!IsPlausiblePotRevealElevation(elevation))
        {
            elevation = ActiveMatch.Coffer.Position.Y;
        }

        if (!IsPlausiblePotRevealElevation(elevation))
        {
            elevation = objectTable.LocalPlayer?.Position.Y ?? liveObject.Position.Y;
        }

        return new Vector3(liveObject.Position.X, elevation, liveObject.Position.Z);
    }

    private IGameObject? ResolveObject(ulong gameObjectId)
    {
        foreach (var gameObject in objectTable)
        {
            if (gameObject is IGameObject objectEntry && objectEntry.GameObjectId == gameObjectId && objectEntry.IsValid())
            {
                return objectEntry;
            }
        }

        return null;
    }

    private static unsafe bool TryReadTreasureFlags(IGameObject gameObject, out TreasureFlags flags)
        => TreasureObjectState.TryReadTreasureFlags(gameObject, out flags);

    private void LogTreasureFlagReadFailureOnce(string phase, IGameObject gameObject)
    {
        lock (gate)
        {
            if (!treasureFlagReadFailureLoggedPhases.Add(phase))
            {
                return;
            }
        }

        logger.Debug($"{BuildLogTag()} op=treasure-flag-read-failed phase={phase} flow={ActiveMatch?.Flow.ToString() ?? "none"} candidate={DescribeActiveCandidate()} objectId={gameObject.GameObjectId:X} baseId={gameObject.BaseId} address={gameObject.Address}");
    }

    private void SetFailure(string reason)
    {
        logger.Warning($"{BuildLogTag()} op=failure state={CofferInteractionState.Failed} candidate={DescribeActiveCandidate()} coffer={DescribeActiveCoffer()} reason={reason}");
        TransitionTo(CofferInteractionState.Failed, reason, error: reason, result: CofferInteractionResult.Failed);
    }

    private void PersistConfirmedOverride(VisibleCofferMatch match)
    {
        if (!match.IsTrustworthy)
        {
            logger.Info($"{BuildLogTag()} op=override-skip candidate={match.CandidateKey.Label} reason=untrustworthy attribution=\"{match.AttributionReason}\"");
            return;
        }

        if (cofferPositionOverrideStore.SaveConfirmedPosition(match))
        {
            return;
        }

        logger.Warning($"{BuildLogTag()} op=override-save-failed candidate={match.CandidateKey.Label} reason=save-confirmed-position-returned-false");
    }

    private void NotifyCofferOpened(VisibleCofferMatch match)
    {
        try
        {
            CofferOpened?.Invoke(match);
        }
        catch (Exception ex)
        {
            logger.Error($"{BuildLogTag()} op=coffer-open-notification-failed error={ex}");
        }
    }

    private void TryApplyJumpAssistDuringApproach()
    {
        var match = ActiveMatch;
        if (match?.RequiresJumpAssist != true)
        {
            return;
        }

        if (movementController.State is not MovementState.Pathfinding and not MovementState.WaitingForArrival)
        {
            return;
        }

        var liveObject = ResolveActiveObject();
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (liveObject == null || playerPosition == null)
        {
            return;
        }

        var remaining = CalculateFlatDistance(playerPosition.Value, GetInteractionPosition(liveObject));
        if (remaining > JumpAssistTriggerDistance)
        {
            return;
        }

        lock (gate)
        {
            if (jumpAssistFiredThisApproach)
            {
                return;
            }

            jumpAssistFiredThisApproach = true;
        }

        logger.Info($"{BuildLogTag()} op=jump-assist candidate={DescribeActiveCandidate()} coffer={DescribeActiveCoffer()} remaining={remaining:0.0}y trigger={JumpAssistTriggerDistance:0.0}y");
        if (!gameActionController.TryExecuteGeneralAction(JumpActionId, $"Jump assist for {DescribeActiveCandidate()}"))
        {
            logger.Warning($"{BuildLogTag()} op=jump-assist-failed candidate={DescribeActiveCandidate()} coffer={DescribeActiveCoffer()} remaining={remaining:0.0}y actionId={JumpActionId}");
        }
    }

    private HiddenApproachReadiness EnsureHiddenApproachReady(IGameObject liveObject, string context)
    {
        var now = DateTimeOffset.UtcNow;
        if (condition[ConditionFlag.InCombat])
        {
            SetFailure($"Combat started while a hidden coffer approach was required for {liveObject.Name.TextValue}. {FormatHiddenContextReason()}");
            return HiddenApproachReadiness.Failed;
        }

        if (condition[ConditionFlag.Mounted])
        {
            if (hiddenDismountStartedAt == DateTimeOffset.MinValue)
            {
                hiddenDismountStartedAt = now;
            }

            if (now - hiddenDismountStartedAt >= HiddenDismountTimeout)
            {
                SetFailure($"Timed out dismounting before hidden coffer approach to {liveObject.Name.TextValue}. context={context} {FormatHiddenContextReason()} {gameActionController.GetChangeableStateSummary()}");
                return HiddenApproachReadiness.Failed;
            }

            if (now >= hiddenDismountRequestAvailableAt)
            {
                hiddenDismountRequestAvailableAt = now + HiddenDismountRequestInterval;
                if (gameActionController.TryExecuteGeneralAction(GameActionController.DismountActionId, $"hidden coffer approach for {liveObject.Name.TextValue}"))
                {
                    logger.Info($"{BuildLogTag()} op=hidden-approach-dismount-request coffer={DescribeActiveCoffer()} context={context} nextAttemptAt={hiddenDismountRequestAvailableAt:O}");
                }
                else
                {
                    logger.DebugThrottled("coffer-hidden-dismount-dispatch", TimeSpan.FromSeconds(1), $"Coffer interaction could not dispatch dismount before hidden approach to {liveObject.Name.TextValue}. context={context} retryAt={hiddenDismountRequestAvailableAt:O} changeableState=\"{gameActionController.GetChangeableStateSummary()}\"");
                }
            }

            logger.DebugThrottled("coffer-hidden-dismount", WaitLogInterval, $"Coffer interaction is waiting to dismount before hidden approach to {liveObject.Name.TextValue}. context={context} elapsed={(now - hiddenDismountStartedAt).TotalSeconds:0.0}s timeout={HiddenDismountTimeout.TotalSeconds:0.0}s nextAttemptIn={Math.Max(0, (hiddenDismountRequestAvailableAt - now).TotalSeconds):0.0}s reason={FormatHiddenContextReason()} changeableState=\"{gameActionController.GetChangeableStateSummary()}\"");
            return HiddenApproachReadiness.Pending;
        }

        if (hiddenDismountStartedAt != DateTimeOffset.MinValue && now - hiddenDismountStartedAt < HideStateSettleDelay)
        {
            logger.DebugThrottled("coffer-hidden-hide-settle", TimeSpan.FromMilliseconds(250), $"Coffer interaction is settling after dismount before Hide at {liveObject.Name.TextValue}. context={context} elapsed={(now - hiddenDismountStartedAt).TotalSeconds:0.00}s required={HideStateSettleDelay.TotalSeconds:0.00}s.");
            return HiddenApproachReadiness.Pending;
        }

        if (gameActionController.IsStealthed)
        {
            ResetHiddenApproachThrottles();
            return HiddenApproachReadiness.Ready;
        }

        if (hiddenHideVerificationDeadlineAt != DateTimeOffset.MinValue)
        {
            if (now < hiddenHideVerificationDeadlineAt)
            {
                logger.DebugThrottled("coffer-hidden-hide-verify", WaitLogInterval, $"Coffer interaction is waiting for Hide confirmation before hidden approach to {liveObject.Name.TextValue}. context={context} deadlineIn={(hiddenHideVerificationDeadlineAt - now).TotalSeconds:0.0}s reason={FormatHiddenContextReason()} stealthed={gameActionController.IsStealthed} mounted={condition[ConditionFlag.Mounted]}");
                return HiddenApproachReadiness.Pending;
            }

            if (!hiddenHideVerificationRetryUsed)
            {
                hiddenHideVerificationRetryUsed = true;
                hiddenHideVerificationDeadlineAt = DateTimeOffset.MinValue;
                hiddenHideDispatchRetryAt = now + HideDispatchRetryDelay;
                logger.Warning($"{BuildLogTag()} op=hidden-approach-hide-verify-timeout coffer={DescribeActiveCoffer()} context={context} action=retry retryAt={hiddenHideDispatchRetryAt:O} reason={FormatHiddenContextReason()}");
                return HiddenApproachReadiness.Pending;
            }

            SetFailure($"Hide did not apply before hidden coffer approach to {liveObject.Name.TextValue} after two attempts. context={context} {FormatHiddenContextReason()} mounted={condition[ConditionFlag.Mounted]} stealthed={gameActionController.IsStealthed} changeableState={gameActionController.GetChangeableStateSummary()}");
            return HiddenApproachReadiness.Failed;
        }

        if (hiddenHideReadyDeadlineAt == DateTimeOffset.MinValue)
        {
            hiddenHideReadyDeadlineAt = now + HideReadyTimeout;
        }

        if (now >= hiddenHideReadyDeadlineAt)
        {
            SetFailure($"Hide did not become ready before hidden coffer approach to {liveObject.Name.TextValue} within {HideReadyTimeout.TotalSeconds:0.0}s. context={context} {FormatHiddenContextReason()} currentClassJob={gameActionController.CurrentClassJobId} changeableState={gameActionController.GetChangeableStateSummary()}");
            return HiddenApproachReadiness.Failed;
        }

        if (now < hiddenHideDispatchRetryAt)
        {
            return HiddenApproachReadiness.Pending;
        }

        if (!gameActionController.IsPlayerInChangeableState() || !gameActionController.CanUseHide())
        {
            logger.DebugThrottled("coffer-hidden-hide-ready", WaitLogInterval, $"Coffer interaction is waiting for Hide before hidden approach to {liveObject.Name.TextValue}. context={context} deadlineIn={(hiddenHideReadyDeadlineAt - now).TotalSeconds:0.0}s reason={FormatHiddenContextReason()} currentClassJob={gameActionController.CurrentClassJobId} canUseHide={gameActionController.CanUseHide()} changeableState=\"{gameActionController.GetChangeableStateSummary()}\"");
            return HiddenApproachReadiness.Pending;
        }

        if (!gameActionController.TryExecuteAction(GameActionController.HideActionId, $"hidden coffer approach for {liveObject.Name.TextValue}"))
        {
            hiddenHideDispatchRetryAt = now + HideDispatchRetryDelay;
            logger.DebugThrottled("coffer-hidden-hide-dispatch", TimeSpan.FromSeconds(1), $"Coffer interaction received an ambiguous Hide dispatch result before hidden approach to {liveObject.Name.TextValue}. context={context} retryAt={hiddenHideDispatchRetryAt:O} reason={FormatHiddenContextReason()} changeableState=\"{gameActionController.GetChangeableStateSummary()}\"");
            return HiddenApproachReadiness.Pending;
        }

        hiddenHideVerificationDeadlineAt = now + HideVerifyTimeout;
        logger.Info($"{BuildLogTag()} op=hidden-approach-hide-request coffer={DescribeActiveCoffer()} context={context} verifyDeadline={hiddenHideVerificationDeadlineAt:O} reason={FormatHiddenContextReason()}");
        return HiddenApproachReadiness.Pending;
    }

    private void ResetHiddenApproachReadiness()
    {
        hiddenDismountStartedAt = DateTimeOffset.MinValue;
        hiddenDismountRequestAvailableAt = DateTimeOffset.MinValue;
        hiddenHideReadyDeadlineAt = DateTimeOffset.MinValue;
        hiddenHideDispatchRetryAt = DateTimeOffset.MinValue;
        hiddenHideVerificationDeadlineAt = DateTimeOffset.MinValue;
        hiddenHideVerificationRetryUsed = false;
        ResetHiddenApproachThrottles();
    }

    private void ResetHiddenApproachThrottles()
    {
        logger.ResetThrottle("coffer-hidden-dismount");
        logger.ResetThrottle("coffer-hidden-dismount-dispatch");
        logger.ResetThrottle("coffer-hidden-hide-settle");
        logger.ResetThrottle("coffer-hidden-hide-ready");
        logger.ResetThrottle("coffer-hidden-hide-dispatch");
        logger.ResetThrottle("coffer-hidden-hide-verify");
    }

    private void ReleasePotRevealLockOn(string reason)
    {
        if (!potRevealLockOnOwned)
        {
            return;
        }

        if (!gameActionController.TrySetLockOn(enabled: false, $"pot-reveal coffer interaction: {reason}"))
        {
            lock (gate)
            {
                potRevealLockOnReleaseAttemptCount++;
                if (potRevealLockOnReleaseAttemptCount >= MaxLockOnReleaseAttempts)
                {
                    potRevealLockOnOwned = false;
                    potRevealInteractionAvailableAt = DateTimeOffset.MinValue;
                    potRevealLockOnReleaseRetryAt = DateTimeOffset.MinValue;
                }
                else
                {
                    potRevealLockOnReleaseRetryAt = DateTimeOffset.UtcNow + LockOnReleaseRetryDelay;
                }
            }

            logger.Warning($"{BuildLogTag()} op=pot-reveal-lockon-release-failed candidate={DescribeActiveCandidate()} attempt={potRevealLockOnReleaseAttemptCount}/{MaxLockOnReleaseAttempts} retryAt={potRevealLockOnReleaseRetryAt:O} reason={reason}");
            return;
        }

        lock (gate)
        {
            potRevealLockOnOwned = false;
            potRevealInteractionAvailableAt = DateTimeOffset.MinValue;
            potRevealLockOnReleaseRetryAt = DateTimeOffset.MinValue;
            potRevealLockOnReleaseAttemptCount = 0;
        }

        logger.Info($"{BuildLogTag()} op=pot-reveal-lockon-release candidate={DescribeActiveCandidate()} reason={reason}");
        logger.ResetThrottle("coffer-pot-reveal-lockon-settle");
    }

    private bool TryReleasePotRevealLockOn(string reason)
    {
        if (!potRevealLockOnOwned)
        {
            return true;
        }

        if (potRevealLockOnReleaseRetryAt != DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow < potRevealLockOnReleaseRetryAt)
        {
            return false;
        }

        ReleasePotRevealLockOn(reason);
        return !potRevealLockOnOwned;
    }

    private void ForgetPotRevealLockOn(string reason)
    {
        if (!potRevealLockOnOwned)
        {
            return;
        }

        lock (gate)
        {
            potRevealLockOnOwned = false;
            potRevealInteractionAvailableAt = DateTimeOffset.MinValue;
            potRevealLockOnReleaseRetryAt = DateTimeOffset.MinValue;
            potRevealLockOnReleaseAttemptCount = 0;
        }

        logger.Info($"{BuildLogTag()} op=pot-reveal-lockon-auto-release candidate={DescribeActiveCandidate()} reason={reason}");
        logger.ResetThrottle("coffer-pot-reveal-lockon-settle");
    }

    private string FormatHiddenContextReason()
        => ActiveMatch?.HiddenContextReason is { Length: > 0 } reason ? $"reason={reason}" : "reason=hidden-context";

    private void TransitionTo(CofferInteractionState nextState, string reason, string? error = null, CofferInteractionResult? result = null)
    {
        CofferInteractionState previousState;
        lock (gate)
        {
            previousState = state;
            state = nextState;
            lastTransition = reason;
            if (error != null)
            {
                lastError = error;
            }
            else if (nextState is not CofferInteractionState.Failed and not CofferInteractionState.Stopped and not CofferInteractionState.TimedOut)
            {
                lastError = string.Empty;
            }

            if (result.HasValue)
            {
                lastResult = result.Value;
            }
        }

        if (nextState is CofferInteractionState.Opened or CofferInteractionState.LostCoffer)
        {
            ForgetPotRevealLockOn($"coffer auto-unlocked in terminal state {nextState}");
        }
        else if (nextState is CofferInteractionState.TimedOut
            or CofferInteractionState.Stopped
            or CofferInteractionState.Failed)
        {
            ReleasePotRevealLockOn($"terminal state {nextState}");
        }

        logger.Info($"{BuildLogTag()} op=transition from={previousState} to={nextState} flow={ActiveMatch?.Flow.ToString() ?? "none"} candidate={DescribeActiveCandidate()} coffer={DescribeActiveCoffer()} attempts={interactionAttemptCount} trustworthy={ActiveMatch?.IsTrustworthy ?? false} reason={reason}");
    }

    private string BuildLogTag()
        => currentRunId.Length == 0 ? "[Coffer]" : $"[Coffer run={currentRunId}]";

    private string DescribeActiveCandidate()
        => ActiveMatch?.CandidateKey.Label ?? "none";

    private string DescribeActiveCoffer()
        => ActiveMatch == null ? "\"unknown\" (0)" : $"\"{ActiveMatch.Coffer.Name}\" ({ActiveMatch.Coffer.GameObjectId:X})";

    private static float CalculateFlatDistance(System.Numerics.Vector3 left, System.Numerics.Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }
}
