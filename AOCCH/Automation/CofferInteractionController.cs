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
    private const int RequiredMissingConfirmations = 2;
    private const int MaxInteractionAttempts = 3;
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(4);
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
            treasureFlagReadFailureLoggedPhases.Clear();
            ResetHiddenApproachReadiness();
        }

        logger.Info($"[Coffer] op=reset reason={reason}");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        if (IsRunning)
        {
            Stop("Coffer interaction disposal");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
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

        var destination = movementController.FindNearestNavigablePoint(interactionPosition, halfExtentXZ: 3f, halfExtentY: 3f);
        if (!destination.HasValue)
        {
            SetFailure($"No reliable vnavmesh point is available near coffer {liveObject.Name.TextValue} ({liveObject.GameObjectId:X}).");
            return false;
        }

        logger.Debug($"Coffer interaction moving toward {liveObject.Name.TextValue} ({liveObject.GameObjectId:X}). destination=<{destination.Value.X:0.0}, {destination.Value.Y:0.0}, {destination.Value.Z:0.0}> reason={reason}");
        movementController.SetLogOwner(currentRunId);
        if (!movementController.StartDirectMove($"Approach coffer {liveObject.Name.TextValue}", destination.Value, PreferredOpenDistance, shouldMountBeforeStep: ActiveMatch?.MustStayHidden != true))
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

        TransitionTo(CofferInteractionState.InteractingWithCoffer, $"Targeted coffer {liveObject.Name.TextValue} at {distance:0.0}y; attempting interaction.");
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
            BeginApproachOrTarget(liveObject, $"Interaction was deferred because the player is {distance:0.0}y from the matched coffer.");
            return;
        }

        if (ActiveMatch?.MustStayHidden == true && EnsureHiddenApproachReady(liveObject, "Hidden coffer interaction") != HiddenApproachReadiness.Ready)
        {
            return;
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

        if (!gameActionController.TryInteractWithObject(liveObject, "coffer interaction"))
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

            TransitionTo(CofferInteractionState.TargetingCoffer, $"Retrying coffer interaction attempt {interactionAttemptCount + 1} of {MaxInteractionAttempts}.");
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

            TransitionTo(CofferInteractionState.TargetingCoffer, $"Coffer is still visible after interaction attempt {interactionAttemptCount}; retrying.");
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
        if (ActiveMatch?.Flow != CofferInteractionFlow.PotReveal
            || MathF.Abs(liveObject.Position.Y + 500f) >= 0.5f)
        {
            return liveObject.Position;
        }

        return ActiveMatch.Coffer.Position;
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
