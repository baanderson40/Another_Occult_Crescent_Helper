using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class TreasureSearchController : IDisposable
{
    private const float CandidateArrivalTolerance = 5f;
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);

    private readonly IFramework framework;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly TreasureHintTracker treasureHintTracker;
    private readonly CofferPositionOverrideStore cofferPositionOverrideStore;
    private readonly AocchLogger logger;
    private readonly Dictionary<uint, Dictionary<string, TreasureCofferGroupData>> groupsByFateId;
    private readonly object gate = new();

    private TreasureSearchState state = TreasureSearchState.Idle;
    private TreasureSearchRunResult lastResult;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private uint activeFateId;
    private string activeFateName = string.Empty;
    private string activeGroupKey = string.Empty;
    private int currentCandidateIndex = -1;
    private int consumedHintRevision;
    private string lastHandoffReason = string.Empty;
    private DateTimeOffset candidateTravelDeadlineAt = DateTimeOffset.MinValue;
    private VisibleCofferMatch? activeVisibleCofferMatch;
    private TreasureCandidateKey? activeCandidateKey;

    public TreasureSearchController(
        IFramework framework,
        OccultCrescentScanner scanner,
        MovementController movementController,
        TreasureHintTracker treasureHintTracker,
        OccultCrescentData data,
        CofferPositionOverrideStore cofferPositionOverrideStore,
        AocchLogger logger)
    {
        this.framework = framework;
        this.scanner = scanner;
        this.movementController = movementController;
        this.treasureHintTracker = treasureHintTracker;
        this.cofferPositionOverrideStore = cofferPositionOverrideStore;
        this.logger = logger;
        groupsByFateId = data.TreasureCofferGroups
            .GroupBy(group => group.FateId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(entry => entry.GroupKey, StringComparer.OrdinalIgnoreCase));

        framework.Update += OnFrameworkUpdate;
    }

    public TreasureSearchState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public TreasureSearchRunResult LastResult
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

    public string ActiveGroupKey
    {
        get
        {
            lock (gate)
            {
                return activeGroupKey;
            }
        }
    }

    public int CurrentCandidateIndex
    {
        get
        {
            lock (gate)
            {
                return currentCandidateIndex;
            }
        }
    }

    public TreasureCandidateKey? ActiveCandidateKey
    {
        get
        {
            lock (gate)
            {
                return activeCandidateKey;
            }
        }
    }

    public string LastHandoffReason
    {
        get
        {
            lock (gate)
            {
                return lastHandoffReason;
            }
        }
    }

    public VisibleCofferMatch? ActiveVisibleCofferMatch
    {
        get
        {
            lock (gate)
            {
                return activeVisibleCofferMatch;
            }
        }
    }

    public bool IsRunning
        => State == TreasureSearchState.TravelingToCandidate;

    public bool Start(uint fateId, string fateName)
    {
        if (IsRunning)
        {
            return true;
        }

        var hintSnapshot = treasureHintTracker.Snapshot;
        if (!hintSnapshot.HasActiveSession || !hintSnapshot.HasInitialHint)
        {
            SetFailure("Treasure search requires an active treasure session with an initial hint.");
            return false;
        }

        // Item 12 starts from the first parsed hint; later revisions may hand off.
        var groupKey = GetGroupKey(hintSnapshot.InitialHintEvent?.Direction ?? TreasureDirection.Unknown);
        if (groupKey.Length == 0)
        {
            SetFailure("Treasure search could not map the current treasure hint to a coffer group.");
            return false;
        }

        if (!TryGetGroup(fateId, groupKey, out var group) || group.Candidates.Count == 0)
        {
            SetFailure($"Treasure search has no coffer candidates for fate {fateId} group {groupKey}.");
            return false;
        }

        lock (gate)
        {
            activeFateId = fateId;
            activeFateName = fateName;
            activeGroupKey = group.GroupKey;
            currentCandidateIndex = 0;
            consumedHintRevision = hintSnapshot.Revision;
            lastHandoffReason = $"Selected initial treasure group {group.GroupKey} from first hint revision {hintSnapshot.InitialHintEvent?.Revision ?? 0}.";
            candidateTravelDeadlineAt = DateTimeOffset.MinValue;
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
            lastError = string.Empty;
            lastResult = TreasureSearchRunResult.None;
        }

        return BeginCurrentCandidate($"Starting treasure traversal for {fateName} from first-hint group {group.GroupKey}.");
    }

    public void Stop(string reason)
    {
        if (IsRunning && movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop(reason);
        }

        TransitionTo(TreasureSearchState.Stopped, reason, error: reason, result: TreasureSearchRunResult.Stopped);
    }

    public bool StartNextCandidateAfterInteractionLoss(string reason)
    {
        if (State != TreasureSearchState.ReadyForInteraction)
        {
            SetFailure("Treasure traversal cannot resume after coffer interaction loss because it is not waiting on a matched coffer.");
            return false;
        }

        if (!TryGetGroup(activeFateId, ActiveGroupKey, out var group) || group.Candidates.Count == 0)
        {
            SetFailure("Treasure traversal lost its candidate group while handling coffer interaction loss.");
            return false;
        }

        logger.ResetThrottle("treasure-search-travel");
        if (CurrentCandidateIndex + 1 >= group.Candidates.Count)
        {
            TransitionTo(TreasureSearchState.CandidatesExhausted, reason, result: TreasureSearchRunResult.CandidatesExhausted);
            return false;
        }

        lock (gate)
        {
            currentCandidateIndex++;
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
        }

        return BeginCurrentCandidate(reason);
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        if (IsRunning)
        {
            Stop("Treasure search disposal");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (State != TreasureSearchState.TravelingToCandidate)
        {
            return;
        }

        if (!scanner.Snapshot.IsInSouthHorn)
        {
            SetFailure("Left South Horn while treasure search was active.");
            return;
        }

        if (TryHandleHintHandoff() || TryHandleVisibleCoffer())
        {
            return;
        }

        if (candidateTravelDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= candidateTravelDeadlineAt)
        {
            movementController.Stop("Treasure candidate travel timed out.");
            AdvanceCandidate($"Timed out while traveling to treasure candidate {activeCandidateKey?.Label}.");
            return;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached treasure candidate.");
                if (TryHandleVisibleCoffer())
                {
                    return;
                }

                AdvanceCandidate($"Reached treasure candidate {activeCandidateKey?.Label} with no visible coffer match.");
                return;
            case MovementState.Failed:
            case MovementState.TimedOut:
                AdvanceCandidate(movementController.LastError.Length == 0
                    ? $"Failed to reach treasure candidate {activeCandidateKey?.Label}."
                    : movementController.LastError);
                return;
        }

        logger.DebugThrottled(
            "treasure-search-travel",
            WaitLogInterval,
            $"Treasure search is traveling to candidate {activeCandidateKey?.Label} in group {activeGroupKey}. MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
    }

    private bool TryHandleHintHandoff()
    {
        var hintSnapshot = treasureHintTracker.Snapshot;
        if (!hintSnapshot.HasActiveSession || !hintSnapshot.HasInitialHint || hintSnapshot.Revision <= consumedHintRevision)
        {
            return false;
        }

        var groupKey = GetGroupKey(hintSnapshot.LastHintEvent?.Direction ?? hintSnapshot.InitialHintEvent?.Direction ?? TreasureDirection.Unknown);
        if (groupKey.Length == 0)
        {
            lock (gate)
            {
                consumedHintRevision = hintSnapshot.Revision;
            }

            return false;
        }

        if (string.Equals(groupKey, ActiveGroupKey, StringComparison.OrdinalIgnoreCase))
        {
            lock (gate)
            {
                consumedHintRevision = hintSnapshot.Revision;
                lastHandoffReason = $"Consumed treasure hint revision {hintSnapshot.Revision} with no group change.";
            }

            return false;
        }

        if (!TryGetGroup(activeFateId, groupKey, out var group) || group.Candidates.Count == 0)
        {
            lock (gate)
            {
                consumedHintRevision = hintSnapshot.Revision;
                lastHandoffReason = $"Ignored treasure hint revision {hintSnapshot.Revision}; group {groupKey} has no mapped candidates.";
            }

            return false;
        }

        movementController.Stop($"Treasure hint handoff to group {group.GroupKey}.");
        lock (gate)
        {
            activeGroupKey = group.GroupKey;
            currentCandidateIndex = 0;
            consumedHintRevision = hintSnapshot.Revision;
            lastHandoffReason = $"Handoff to treasure group {group.GroupKey} from hint revision {hintSnapshot.Revision}.";
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
        }

        logger.ResetThrottle("treasure-search-travel");
        return BeginCurrentCandidate(lastHandoffReason);
    }

    private bool TryHandleVisibleCoffer()
    {
        var scannerSnapshot = scanner.Snapshot;
        if (scannerSnapshot.VisibleCoffers.Count == 0)
        {
            return false;
        }

        if (!TryGetGroup(activeFateId, ActiveGroupKey, out var group) || group.Candidates.Count == 0 || ActiveCandidateKey == null)
        {
            return false;
        }

        foreach (var coffer in scannerSnapshot.VisibleCoffers)
        {
            CompleteWithVisibleCoffer(
                coffer,
                ActiveCandidateKey,
                float.MaxValue,
                $"Attributed visible coffer {coffer.Name} to active route candidate {ActiveCandidateKey.Label} by route context.");
            return true;
        }

        return false;
    }

    private void CompleteWithVisibleCoffer(VisibleCoffer coffer, TreasureCandidateKey candidateKey, float matchDistance, string reason)
    {
        lock (gate)
        {
            activeCandidateKey = candidateKey;
            activeVisibleCofferMatch = new VisibleCofferMatch
            {
                CandidateKey = candidateKey,
                Coffer = coffer,
                MatchDistance = matchDistance,
                AttributionReason = reason,
            };
        }

        logger.ResetThrottle("treasure-search-travel");
        TransitionTo(TreasureSearchState.ReadyForInteraction, reason, result: TreasureSearchRunResult.ReadyForInteraction);
    }

    private void AdvanceCandidate(string reason)
    {
        logger.ResetThrottle("treasure-search-travel");

        if (!TryGetGroup(activeFateId, ActiveGroupKey, out var group) || group.Candidates.Count == 0)
        {
            SetFailure(reason);
            return;
        }

        if (CurrentCandidateIndex + 1 >= group.Candidates.Count)
        {
            TransitionTo(TreasureSearchState.CandidatesExhausted, reason, result: TreasureSearchRunResult.CandidatesExhausted);
            return;
        }

        lock (gate)
        {
            currentCandidateIndex++;
            activeVisibleCofferMatch = null;
        }

        BeginCurrentCandidate(reason);
    }

    private bool BeginCurrentCandidate(string reason)
    {
        if (!TryGetCurrentCandidate(out var candidate))
        {
            SetFailure("Treasure search could not resolve the active candidate.");
            return false;
        }

        var candidateKey = ToCandidateKey(candidate);
        var canonicalPosition = candidate.Position.ToVector3();
        var usedOverride = cofferPositionOverrideStore.TryResolvePosition(candidateKey, out var overridePosition);
        var targetPosition = usedOverride ? overridePosition : canonicalPosition;
        var destination = movementController.FindNearestNavigablePoint(targetPosition, halfExtentXZ: 5f, halfExtentY: 5f)
            ?? targetPosition;
        if (!movementController.StartDirectMove($"Treasure candidate {candidate.Label} for {activeFateName}", destination, CandidateArrivalTolerance))
        {
            SetFailure(movementController.LastError.Length == 0
                ? $"Failed to start movement to treasure candidate {candidate.Label}."
                : movementController.LastError);
            return false;
        }

        var travelTimeout = TimeSpan.FromSeconds(Math.Max(30, candidate.TravelTimeoutSeconds ?? 180));
        lock (gate)
        {
            activeCandidateKey = candidateKey;
            candidateTravelDeadlineAt = DateTimeOffset.UtcNow + travelTimeout;
            activeVisibleCofferMatch = null;
        }

        TransitionTo(
            TreasureSearchState.TravelingToCandidate,
            $"{reason} Moving to treasure candidate {candidate.Label} in group {candidate.GroupKey} using {(usedOverride ? "override" : "canonical")} position.");
        return true;
    }

    private bool TryGetCurrentCandidate(out TreasureCofferCandidateData candidate)
    {
        candidate = new TreasureCofferCandidateData();
        if (!TryGetGroup(activeFateId, ActiveGroupKey, out var group)
            || CurrentCandidateIndex < 0
            || CurrentCandidateIndex >= group.Candidates.Count)
        {
            return false;
        }

        candidate = group.Candidates[CurrentCandidateIndex];
        return true;
    }

    private bool TryGetGroup(uint fateId, string groupKey, out TreasureCofferGroupData group)
    {
        group = new TreasureCofferGroupData();
        if (!groupsByFateId.TryGetValue(fateId, out var groups) || groups == null)
        {
            return false;
        }

        if (!groups.TryGetValue(groupKey, out var resolvedGroup) || resolvedGroup == null)
        {
            return false;
        }

        group = resolvedGroup;
        return true;
    }

    private void SetFailure(string reason)
        => TransitionTo(TreasureSearchState.Failed, reason, error: reason, result: TreasureSearchRunResult.Failed);

    private void TransitionTo(TreasureSearchState nextState, string reason, string? error = null, TreasureSearchRunResult? result = null)
    {
        lock (gate)
        {
            state = nextState;
            lastTransition = reason;
            if (error != null)
            {
                lastError = error;
            }
            else if (nextState is not TreasureSearchState.Failed and not TreasureSearchState.Stopped)
            {
                lastError = string.Empty;
            }

            if (result.HasValue)
            {
                lastResult = result.Value;
            }
        }

        logger.Info($"Treasure search state -> {nextState}: {reason}");
    }

    private static TreasureCandidateKey ToCandidateKey(TreasureCofferCandidateData candidate)
        => new()
        {
            FateId = candidate.FateId,
            GroupKey = candidate.GroupKey,
            CandidateKey = candidate.CandidateKey,
            Label = candidate.Label,
        };

    private static string GetGroupKey(TreasureDirection direction)
        => direction switch
        {
            TreasureDirection.North => "north",
            TreasureDirection.Northeast => "northeast",
            TreasureDirection.East => "east",
            TreasureDirection.Southeast => "southeast",
            TreasureDirection.South => "south",
            TreasureDirection.Southwest => "southwest",
            TreasureDirection.West => "west",
            TreasureDirection.Northwest => "northwest",
            _ => string.Empty,
        };
}
