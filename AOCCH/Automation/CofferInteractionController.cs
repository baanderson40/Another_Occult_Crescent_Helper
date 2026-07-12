using System;
using System.Linq;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using AOCCH.Data;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class CofferInteractionController : IDisposable
{
    private const float MaxInteractRange = 4.5f;
    private const float PreferredOpenDistance = 3.25f;
    private const int RequiredMissingConfirmations = 2;
    private const int MaxInteractionAttempts = 3;
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(5);

    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly GameActionController gameActionController;
    private readonly CofferPositionOverrideStore cofferPositionOverrideStore;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private CofferInteractionState state = CofferInteractionState.Idle;
    private CofferInteractionResult lastResult;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private VisibleCofferMatch? activeMatch;
    private DateTimeOffset confirmationDeadlineAt = DateTimeOffset.MinValue;
    private int interactionAttemptCount;
    private int missingConfirmationCount;

    public CofferInteractionController(
        IFramework framework,
        IObjectTable objectTable,
        OccultCrescentScanner scanner,
        MovementController movementController,
        GameActionController gameActionController,
        CofferPositionOverrideStore cofferPositionOverrideStore,
        AocchLogger logger)
    {
        this.framework = framework;
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
            return true;
        }

        var liveObject = ResolveObject(match.Coffer.GameObjectId);
        if (liveObject == null)
        {
            TransitionTo(CofferInteractionState.LostCoffer, "Matched coffer is no longer available to interact with.", result: CofferInteractionResult.LostCoffer);
            return false;
        }

        lock (gate)
        {
            activeMatch = match;
            confirmationDeadlineAt = DateTimeOffset.MinValue;
            interactionAttemptCount = 0;
            missingConfirmationCount = 0;
            lastError = string.Empty;
            lastResult = CofferInteractionResult.None;
        }

        return BeginApproachOrTarget(liveObject, "Starting coffer interaction.");
    }

    public void Stop(string reason)
    {
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
            lastTransition = "Idle";
            lastError = string.Empty;
            activeMatch = null;
            confirmationDeadlineAt = DateTimeOffset.MinValue;
            interactionAttemptCount = 0;
            missingConfirmationCount = 0;
        }

        logger.Info($"Coffer interaction reset: {reason}");
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

        if (!scanner.Snapshot.IsInSouthHorn)
        {
            SetFailure("Left South Horn while coffer interaction was active.");
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

        var distance = CalculateFlatDistance(playerPosition.Value, liveObject.Position);
        if (distance <= PreferredOpenDistance)
        {
            TransitionTo(CofferInteractionState.TargetingCoffer, $"{reason} Coffer is within the preferred opening distance ({distance:0.0}y <= {PreferredOpenDistance:0.00}y).");
            return true;
        }

        var destination = movementController.FindNearestNavigablePoint(liveObject.Position, halfExtentXZ: 3f, halfExtentY: 3f) ?? liveObject.Position;
        if (!movementController.StartDirectMove($"Approach coffer {liveObject.Name.TextValue}", destination, PreferredOpenDistance))
        {
            SetFailure(movementController.LastError.Length == 0
                ? "Failed to begin movement into coffer interact range."
                : movementController.LastError);
            return false;
        }

        TransitionTo(CofferInteractionState.ApproachingCoffer, $"{reason} Moving within {PreferredOpenDistance:0.00}y of {liveObject.Name.TextValue} before attempting to open it.");
        return true;
    }

    private void TickApproach()
    {
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

                var distance = CalculateFlatDistance(playerPosition.Value, liveObject.Position);
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

        var distance = CalculateFlatDistance(playerPosition.Value, liveObject.Position);
        if (distance > MaxInteractRange)
        {
            BeginApproachOrTarget(liveObject, $"Player drifted outside the coffer interaction range ({distance:0.0}y > {MaxInteractRange:0.0}y).");
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

        var distance = CalculateFlatDistance(playerPosition.Value, liveObject.Position);
        if (distance > MaxInteractRange)
        {
            BeginApproachOrTarget(liveObject, $"Interaction was deferred because the player is {distance:0.0}y from the matched coffer.");
            return;
        }

        if (!gameActionController.TryInteractWithObject(liveObject, "coffer interaction"))
        {
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

        TransitionTo(CofferInteractionState.WaitingForOpenConfirmation, $"Interaction attempt {interactionAttemptCount} started; waiting for coffer disappearance confirmation.");
    }

    private void TickConfirmation()
    {
        var active = ActiveMatch;
        if (active == null)
        {
            SetFailure("Coffer interaction lost its active match during confirmation.");
            return;
        }

        var stillVisible = scanner.Snapshot.VisibleCoffers.Any(coffer => coffer.GameObjectId == active.Coffer.GameObjectId);
        if (!stillVisible)
        {
            lock (gate)
            {
                missingConfirmationCount++;
            }

            if (missingConfirmationCount >= RequiredMissingConfirmations)
            {
                PersistConfirmedOverride(active);
                logger.ResetThrottle("coffer-confirmation");
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

        logger.DebugThrottled("coffer-confirmation", WaitLogInterval, $"Waiting for coffer open confirmation on {active.Coffer.Name} ({active.Coffer.GameObjectId:X}). attempt={interactionAttemptCount} deadline={ConfirmationDeadlineAt:O}.");
    }

    private IGameObject? ResolveActiveObject()
        => ActiveMatch == null ? null : ResolveObject(ActiveMatch.Coffer.GameObjectId);

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

    private void SetFailure(string reason)
        => TransitionTo(CofferInteractionState.Failed, reason, error: reason, result: CofferInteractionResult.Failed);

    private void PersistConfirmedOverride(VisibleCofferMatch match)
    {
        if (!match.IsTrustworthy)
        {
            logger.Info($"Skipping coffer override persistence for {match.CandidateKey} because the attribution was not trustworthy. {match.AttributionReason}");
            return;
        }

        if (cofferPositionOverrideStore.SaveConfirmedPosition(match))
        {
            return;
        }

        logger.Warning($"Failed to persist confirmed coffer position override for {match.CandidateKey}.");
    }

    private void TransitionTo(CofferInteractionState nextState, string reason, string? error = null, CofferInteractionResult? result = null)
    {
        lock (gate)
        {
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

        logger.Info($"Coffer interaction state -> {nextState}: {reason}");
    }

    private static float CalculateFlatDistance(System.Numerics.Vector3 left, System.Numerics.Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }
}
