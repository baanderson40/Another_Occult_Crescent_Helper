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
    private sealed record RefinementMovePlan(
        Vector3 RawTarget,
        Vector3 SnappedTarget,
        string SnapMethod,
        float SnapRadius,
        float SnapDistance,
        float VerticalSnapDistance,
        float ForwardDistance,
        float LateralDistance,
        float TargetDistance,
        float Step,
        float Multiplier,
        float Score);

    private enum CandidateHandoffResult
    {
        None,
        Updated,
        DangerousTransitionStarted,
    }

    private const float CandidateArrivalTolerance = 5f;
    private const float MaximumTrustedAttributionDistance = 25f;
    private const float CandidateHandoffRadius = 25f;
    private const float CandidateHandoffAdvantage = 10f;
    private const float MappedPointRetryArrivalTolerance = 4.5f;
    private const float LocalMoveSkipDistance = 3f;
    private const int MaximumCandidateRefinementSteps = 12;
    private const float NavmeshMinimumValidY = -400f;
    private const float NavmeshMaximumValidY = 500f;
    private const float NavmeshSentinelY = -500f;
    private const float NavmeshSentinelTolerance = 0.5f;
    private const float NavmeshMaxVerticalSnap = 180f;
    private const float NavmeshCoordinateAbsLimit = 100000f;
    private static readonly float[] RefinementSearchRadii = [2f, 4f, 6f, 10f, 20f];
    private static readonly float[] RefinementStepMultipliers = [1f, 0.75f, 0.5f, 0.25f];
    private static readonly TimeSpan CandidateProbeSettleDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan CandidateProbeTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CandidateProbeRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RevealedCofferAcquireTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);
    private const int MaximumCandidateProbeAttempts = 3;

    private readonly IFramework framework;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly GameActionController gameActionController;
    private readonly TreasureHintTracker treasureHintTracker;
    private readonly DangerousTreasureTravelController dangerousTreasureTravelController;
    private readonly CofferPositionOverrideStore cofferPositionOverrideStore;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly float mountedTravelSpeed;
    private readonly Dictionary<uint, Dictionary<string, TreasureCofferGroupData>> groupsByFateId;
    private readonly object gate = new();
    private readonly HashSet<string> handledCandidateLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TreasureCofferCandidateData> orderedCandidates = [];

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
    private Vector3 traversalOriginCenter;
    private DateTimeOffset candidateTravelDeadlineAt = DateTimeOffset.MinValue;
    private DateTimeOffset candidateArrivedAt = DateTimeOffset.MinValue;
    private DateTimeOffset candidateProbeDeadlineAt = DateTimeOffset.MinValue;
    private DateTimeOffset candidateProbeLastAttemptAt = DateTimeOffset.MinValue;
    private int candidateProbeAttemptCount;
    private int candidateProbeBaselineSessionId;
    private int candidateProbeBaselineRevision;
    private TreasureHintEvent? refinementEvent;
    private DateTimeOffset refinementProbeDeadlineAt = DateTimeOffset.MinValue;
    private DateTimeOffset refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
    private int refinementProbeAttemptCount;
    private int refinementProbeBaselineSessionId;
    private int refinementProbeBaselineRevision;
    private DateTimeOffset refinementMoveDeadlineAt = DateTimeOffset.MinValue;
    private int refinementStepIndex;
    private bool mappedPointRetryUsed;
    private VisibleCofferMatch? activeVisibleCofferMatch;
    private TreasureCandidateKey? activeCandidateKey;
    private bool activeCandidateUsesOverride;
    private Vector3 activeCandidateResolvedPosition;
    private DateTimeOffset revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
    private bool revealedCofferLatched;
    private string lastNavmeshRejectionSummary = string.Empty;
    private string pendingCandidateAdvanceReason = string.Empty;

    public TreasureSearchController(
        IFramework framework,
        OccultCrescentScanner scanner,
        MovementController movementController,
        GameActionController gameActionController,
        TreasureHintTracker treasureHintTracker,
        DangerousTreasureTravelController dangerousTreasureTravelController,
        OccultCrescentData data,
        CofferPositionOverrideStore cofferPositionOverrideStore,
        Configuration configuration,
        AocchLogger logger)
    {
        this.framework = framework;
        this.scanner = scanner;
        this.movementController = movementController;
        this.gameActionController = gameActionController;
        this.treasureHintTracker = treasureHintTracker;
        this.dangerousTreasureTravelController = dangerousTreasureTravelController;
        this.cofferPositionOverrideStore = cofferPositionOverrideStore;
        this.configuration = configuration;
        this.logger = logger;
        mountedTravelSpeed = data.MountedTravelSpeed > 0 ? data.MountedTravelSpeed : 14.13f;
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
        => State is TreasureSearchState.TravelingToCandidate
            or TreasureSearchState.ProbingCandidate
            or TreasureSearchState.RefiningCandidate
            or TreasureSearchState.AcquiringRevealedCoffer;

    public bool ActiveCandidateUsesOverride
    {
        get
        {
            lock (gate)
            {
                return activeCandidateUsesOverride;
            }
        }
    }

    public Vector3 ActiveCandidateResolvedPosition
    {
        get
        {
            lock (gate)
            {
                return activeCandidateResolvedPosition;
            }
        }
    }

    public IReadOnlyList<string> OrderedCandidateLabels
    {
        get
        {
            lock (gate)
            {
                return orderedCandidates.Select(candidate => candidate.Label).ToArray();
            }
        }
    }

    public IReadOnlyList<string> HandledCandidateLabels
    {
        get
        {
            lock (gate)
            {
                return handledCandidateLabels.OrderBy(label => label, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    public bool Start(uint fateId, string fateName)
        => Start(fateId, fateName, Vector3.Zero);

    public bool Start(uint fateId, string fateName, Vector3 originCenter)
    {
        if (IsRunning)
        {
            logger.Debug($"Treasure search start ignored because a run is already active. fate={activeFateName} ({activeFateId}) state={State}");
            return true;
        }

        var hintSnapshot = treasureHintTracker.Snapshot;
        logger.Info($"{BuildLogTag(hintSnapshot.SessionId)} op=start fate=\"{fateName}\" ({fateId}) origin=<{originCenter.X:0.0}, {originCenter.Y:0.0}, {originCenter.Z:0.0}>");
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

        activeFateName = fateName;
        var runOrderedCandidates = BuildOrderedCandidates(group, originCenter);
        if (runOrderedCandidates.Count == 0)
        {
            SetFailure($"Treasure search has no eligible coffer candidates for fate {fateId} group {groupKey}.");
            return false;
        }

        lock (gate)
        {
            activeFateId = fateId;
            activeFateName = fateName;
            activeGroupKey = group.GroupKey;
            handledCandidateLabels.Clear();
            orderedCandidates.Clear();
            orderedCandidates.AddRange(runOrderedCandidates);
            currentCandidateIndex = 0;
            consumedHintRevision = hintSnapshot.Revision;
            lastHandoffReason = $"Selected initial treasure group {group.GroupKey} from first hint revision {hintSnapshot.InitialHintEvent?.Revision ?? 0}.";
            traversalOriginCenter = originCenter;
            candidateTravelDeadlineAt = DateTimeOffset.MinValue;
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
            activeCandidateUsesOverride = false;
            activeCandidateResolvedPosition = Vector3.Zero;
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
            revealedCofferLatched = false;
            lastError = string.Empty;
            lastResult = TreasureSearchRunResult.None;
        }

        movementController.SetLogOwner($"TreasureSession#{hintSnapshot.SessionId}");
        return BeginCurrentCandidate($"Starting treasure traversal for {fateName} from first-hint group {group.GroupKey}.");
    }

    public void Stop(string reason)
    {
        logger.Info($"{BuildLogTag()} op=stop state={State} fate=\"{activeFateName}\" ({activeFateId}) candidate={activeCandidateKey?.Label ?? "none"} reason={reason}");
        if (dangerousTreasureTravelController.IsRunning)
        {
            dangerousTreasureTravelController.Stop(reason);
        }

        if (IsRunning && movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop(reason);
        }

        TransitionTo(TreasureSearchState.Stopped, reason, error: reason, result: TreasureSearchRunResult.Stopped);
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = TreasureSearchState.Idle;
            lastResult = TreasureSearchRunResult.None;
            lastTransition = "Idle";
            lastError = string.Empty;
            activeFateId = 0;
            activeFateName = string.Empty;
            activeGroupKey = string.Empty;
            currentCandidateIndex = -1;
            consumedHintRevision = 0;
            lastHandoffReason = string.Empty;
            handledCandidateLabels.Clear();
            orderedCandidates.Clear();
            traversalOriginCenter = Vector3.Zero;
            candidateTravelDeadlineAt = DateTimeOffset.MinValue;
            candidateArrivedAt = DateTimeOffset.MinValue;
            candidateProbeDeadlineAt = DateTimeOffset.MinValue;
            candidateProbeLastAttemptAt = DateTimeOffset.MinValue;
            candidateProbeAttemptCount = 0;
            candidateProbeBaselineSessionId = 0;
            candidateProbeBaselineRevision = 0;
            refinementEvent = null;
            refinementProbeDeadlineAt = DateTimeOffset.MinValue;
            refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
            refinementProbeAttemptCount = 0;
            refinementProbeBaselineSessionId = 0;
            refinementProbeBaselineRevision = 0;
            refinementMoveDeadlineAt = DateTimeOffset.MinValue;
            refinementStepIndex = 0;
            mappedPointRetryUsed = false;
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
            activeCandidateUsesOverride = false;
            activeCandidateResolvedPosition = Vector3.Zero;
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
            revealedCofferLatched = false;
        }

        logger.Info($"[Treasure] op=reset reason={reason}");
    }

    public bool StartNextCandidateAfterInteractionLoss(string reason)
    {
        logger.Info($"{BuildLogTag()} op=interaction-loss-advance candidate={ActiveCandidateKey?.Label ?? "none"} reason={reason}");
        if (State != TreasureSearchState.ReadyForInteraction)
        {
            SetFailure("Treasure traversal cannot resume after coffer interaction loss because it is not waiting on a matched coffer.");
            return false;
        }

        if (orderedCandidates.Count == 0)
        {
            SetFailure("Treasure traversal lost its candidate group while handling coffer interaction loss.");
            return false;
        }

        logger.ResetThrottle("treasure-search-travel");
        if (CurrentCandidateIndex + 1 >= orderedCandidates.Count)
        {
            TransitionTo(TreasureSearchState.CandidatesExhausted, reason, result: TreasureSearchRunResult.CandidatesExhausted);
            return false;
        }

        lock (gate)
        {
            currentCandidateIndex++;
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
            activeCandidateUsesOverride = false;
            activeCandidateResolvedPosition = Vector3.Zero;
            refinementProbeDeadlineAt = DateTimeOffset.MinValue;
            refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
            refinementProbeAttemptCount = 0;
            refinementProbeBaselineSessionId = 0;
            refinementProbeBaselineRevision = 0;
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
            revealedCofferLatched = false;
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
        if (State is not TreasureSearchState.TravelingToCandidate
            and not TreasureSearchState.ProbingCandidate
            and not TreasureSearchState.RefiningCandidate
            and not TreasureSearchState.AcquiringRevealedCoffer)
        {
            return;
        }

        if (!scanner.Snapshot.IsInSouthHorn)
        {
            SetFailure("Left South Horn while treasure search was active.");
            return;
        }

        if (TryResumeDeferredCandidateAdvance())
        {
            return;
        }

        if (State == TreasureSearchState.RefiningCandidate)
        {
            TickRefiningCandidate();
            return;
        }

        if (State == TreasureSearchState.AcquiringRevealedCoffer)
        {
            TickAcquiringRevealedCoffer();
            return;
        }

        if (State == TreasureSearchState.ProbingCandidate)
        {
            TickProbingCandidate();
            return;
        }

        if (TryHandleHintHandoff())
        {
            return;
        }

        if (TryHandleDangerousTravelTerminalResult())
        {
            return;
        }

        if (candidateTravelDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= candidateTravelDeadlineAt)
        {
            if (dangerousTreasureTravelController.IsRunning)
            {
                dangerousTreasureTravelController.Stop("Treasure candidate travel timed out.");
            }

            movementController.Stop("Treasure candidate travel timed out.");
            AdvanceCandidate($"Timed out while traveling to treasure candidate {activeCandidateKey?.Label}.");
            return;
        }

        if (dangerousTreasureTravelController.IsRunning)
        {
            logger.DebugThrottled(
                "treasure-search-travel",
                WaitLogInterval,
                $"Treasure search is running dangerous travel for candidate {activeCandidateKey?.Label} in group {activeGroupKey}. DangerousState={dangerousTreasureTravelController.State} transition={dangerousTreasureTravelController.LastTransition}.");
            return;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached treasure candidate.");
                lock (gate)
                {
                    candidateArrivedAt = DateTimeOffset.UtcNow;
                    candidateProbeDeadlineAt = DateTimeOffset.MinValue;
                    candidateProbeLastAttemptAt = DateTimeOffset.MinValue;
                    candidateProbeAttemptCount = 0;
                    candidateProbeBaselineSessionId = 0;
                    candidateProbeBaselineRevision = 0;
                    refinementProbeDeadlineAt = DateTimeOffset.MinValue;
                    refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
                    refinementProbeAttemptCount = 0;
                    refinementProbeBaselineSessionId = 0;
                    refinementProbeBaselineRevision = 0;
                }

                logger.ResetThrottle("treasure-search-probe");
                TransitionTo(TreasureSearchState.ProbingCandidate, $"Reached treasure candidate {activeCandidateKey?.Label}; checking for a visible coffer before local Magical Elixir probing.");
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

    private void TickProbingCandidate()
    {
        if (TryAcquireVisibleCofferFromActiveCandidate("probe-scan"))
        {
            return;
        }

        var scannerSnapshot = scanner.Snapshot;
        if (!scannerSnapshot.HasTreasureBuff)
        {
            if (revealedCofferLatched)
            {
                logger.Info($"{BuildLogTag()} op=reveal-latched candidate={activeCandidateKey?.Label ?? "none"} source=probe-expiry action=continue-post-reveal");
                BeginRevealedCofferAcquisition(refinementEvent);
                return;
            }

            AdvanceCandidate($"Treasure buff expired before probing treasure candidate {activeCandidateKey?.Label}.");
            return;
        }

        if (candidateArrivedAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - candidateArrivedAt < CandidateProbeSettleDelay)
        {
            logger.DebugThrottled(
                "treasure-search-probe-settle",
                TimeSpan.FromMilliseconds(250),
                $"Treasure search is settling at candidate {activeCandidateKey?.Label} before local Magical Elixir probing. elapsed={(DateTimeOffset.UtcNow - candidateArrivedAt).TotalSeconds:0.00}s required={CandidateProbeSettleDelay.TotalSeconds:0.00}s.");
            return;
        }

        if (candidateProbeBaselineSessionId != 0
            && treasureHintTracker.TryGetLatestEventSince(candidateProbeBaselineSessionId, candidateProbeBaselineRevision, out var latestEvent)
            && latestEvent != null)
        {
            lock (gate)
            {
                consumedHintRevision = Math.Max(consumedHintRevision, latestEvent.Revision);
            }

            switch (latestEvent.Kind)
            {
                case TreasureHintKind.Hint:
                    if (IsLocalTreasureDistance(latestEvent.DistanceBucket))
                    {
                        BeginCandidateRefinement(latestEvent);
                        return;
                    }

                    AdvanceCandidate($"Treasure candidate {activeCandidateKey?.Label} is not local yet ({latestEvent.DistanceBucket} {latestEvent.Direction}); trying the next candidate.");
                    return;
                case TreasureHintKind.CofferReveal:
                case TreasureHintKind.CofferMessage:
                    BeginRevealedCofferAcquisition(latestEvent);
                    return;
                case TreasureHintKind.ElixirPrompt:
                case TreasureHintKind.BonusOffer:
                    logger.Info($"{BuildLogTag()} op=probe-event candidate={activeCandidateKey?.Label ?? "none"} event={latestEvent.Kind} action=continue-probing");
                    ContinueProbingAfterEvent(latestEvent.Revision);
                    return;
            }
        }

        if (candidateProbeDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= candidateProbeDeadlineAt)
        {
            if (candidateProbeAttemptCount >= MaximumCandidateProbeAttempts)
            {
                AdvanceCandidate($"Treasure candidate {activeCandidateKey?.Label} produced no usable event after {candidateProbeAttemptCount} local Magical Elixir attempt(s).");
                return;
            }

            if (!TryStartCandidateProbe($"No usable treasure event arrived at candidate {activeCandidateKey?.Label} after Magical Elixir attempt {candidateProbeAttemptCount}; retrying."))
            {
                return;
            }

            return;
        }

        if (candidateProbeAttemptCount == 0)
        {
            if (!TryStartCandidateProbe($"Starting local treasure probe at candidate {activeCandidateKey?.Label}."))
            {
                return;
            }

            return;
        }

        logger.DebugThrottled(
            "treasure-search-probe",
            WaitLogInterval,
            $"Treasure search is probing candidate {activeCandidateKey?.Label}. attempts={candidateProbeAttemptCount}/{MaximumCandidateProbeAttempts} probeDeadline={candidateProbeDeadlineAt:O} baselineSession={candidateProbeBaselineSessionId} baselineRevision={candidateProbeBaselineRevision} hint={treasureHintTracker.Snapshot.GetHintSummary()}.");
    }

    private void TickRefiningCandidate()
    {
        if (TryAcquireVisibleCofferFromActiveCandidate("refine-scan"))
        {
            return;
        }

        var scannerSnapshot = scanner.Snapshot;
        if (!scannerSnapshot.HasTreasureBuff && !revealedCofferLatched)
        {
            AdvanceCandidate($"Treasure buff expired while refining candidate {activeCandidateKey?.Label}.");
            return;
        }

        if (!scannerSnapshot.HasTreasureBuff && revealedCofferLatched)
        {
            logger.Info($"{BuildLogTag()} op=reveal-latched candidate={activeCandidateKey?.Label ?? "none"} source=refine-expiry action=continue-post-reveal");
            BeginRevealedCofferAcquisition(refinementEvent);
            return;
        }

        if (!TryGetCurrentCandidate(out var activeCandidate))
        {
            SetFailure("Treasure refinement lost the active candidate.");
            return;
        }

        if (refinementMoveDeadlineAt != DateTimeOffset.MinValue)
        {
            if (!TickRefinementMovement(activeCandidate))
            {
                return;
            }
        }

        if (refinementEvent == null
            && refinementProbeBaselineSessionId != 0
            && treasureHintTracker.TryGetLatestEventSince(refinementProbeBaselineSessionId, refinementProbeBaselineRevision, out var refinementLatestEvent)
            && refinementLatestEvent != null)
        {
            lock (gate)
            {
                consumedHintRevision = Math.Max(consumedHintRevision, refinementLatestEvent.Revision);
                refinementEvent = refinementLatestEvent;
                refinementProbeDeadlineAt = DateTimeOffset.MinValue;
            }
        }

        if (refinementEvent == null)
        {
            if (refinementProbeDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= refinementProbeDeadlineAt)
            {
                logger.Info($"{BuildLogTag()} op=refine-probe-empty candidate={activeCandidateKey?.Label ?? "none"} attempt={refinementProbeAttemptCount} action=retry-current-position");
            }

            if (!TryStartRefinementProbe($"Refinement is probing candidate {activeCandidateKey?.Label} after local movement."))
            {
                return;
            }

            return;
        }

        if (refinementEvent.Kind == TreasureHintKind.BonusOffer)
        {
            TransitionTo(TreasureSearchState.ReadyForInteraction, $"Treasure candidate {activeCandidateKey?.Label} produced a bonus-offer event during refinement.", result: TreasureSearchRunResult.ReadyForInteraction);
            return;
        }

        if (refinementEvent.Kind is TreasureHintKind.CofferReveal or TreasureHintKind.CofferMessage)
        {
            BeginRevealedCofferAcquisition(refinementEvent);
            return;
        }

        if (refinementEvent.Kind != TreasureHintKind.Hint)
        {
            logger.Info($"{BuildLogTag()} op=refine-non-hint candidate={activeCandidateKey?.Label ?? "none"} event={refinementEvent.Kind} action=probe-current-position");
            refinementEvent = null;
            return;
        }

        if (!IsLocalTreasureDistance(refinementEvent.DistanceBucket))
        {
            AdvanceCandidate($"Treasure candidate {activeCandidateKey?.Label} returned a non-local hint ({refinementEvent.DistanceBucket} {refinementEvent.Direction}) during refinement; trying the next candidate.");
            return;
        }

        var candidatePosition = ActiveCandidateResolvedPosition != Vector3.Zero
            ? ActiveCandidateResolvedPosition
            : ResolveCandidatePosition(activeCandidate);
        var playerPosition = Plugin.ObjectTable.LocalPlayer?.Position ?? candidatePosition;
        var positionDistance = CalculateFlatDistance(playerPosition, candidatePosition);

        if (!mappedPointRetryUsed && positionDistance > MappedPointRetryArrivalTolerance)
        {
            mappedPointRetryUsed = true;
            logger.Info($"{BuildLogTag()} op=refine-mapped-retry candidate={activeCandidateKey?.Label ?? "none"} position=<{candidatePosition.X:0.0}, {candidatePosition.Y:0.0}, {candidatePosition.Z:0.0}> distance={positionDistance:0.0}y");
            if (!TryStartRefinementMove(activeCandidate, candidatePosition, MappedPointRetryArrivalTolerance, $"Treasure candidate {activeCandidate.Label} mapped retry"))
            {
                return;
            }

            refinementEvent = null;
            return;
        }

        if (refinementStepIndex >= MaximumCandidateRefinementSteps)
        {
            AdvanceCandidate($"Treasure candidate {activeCandidateKey?.Label} exceeded {MaximumCandidateRefinementSteps} local refinement step(s); trying the next candidate.");
            return;
        }

        var moveStep = GetRefinementStepSize(refinementEvent.DistanceBucket);
        var movePlan = ResolveRefinementMove(playerPosition, refinementEvent.Direction, moveStep);
        if (movePlan == null)
        {
            if (lastNavmeshRejectionSummary.Length > 0)
            {
                logger.Info($"{BuildLogTag()} op=refine-no-nav candidate={activeCandidateKey?.Label ?? "none"} distance={refinementEvent.DistanceBucket} direction={refinementEvent.Direction} rejections={lastNavmeshRejectionSummary} action=probe-current-position");
            }
            else
            {
                logger.Info($"{BuildLogTag()} op=refine-no-nav candidate={activeCandidateKey?.Label ?? "none"} distance={refinementEvent.DistanceBucket} direction={refinementEvent.Direction} action=probe-current-position");
            }

            refinementEvent = null;
            return;
        }

        var target = movePlan.SnappedTarget;
        var targetDistance = CalculateFlatDistance(playerPosition, target);
        if (targetDistance <= LocalMoveSkipDistance)
        {
            logger.Info($"{BuildLogTag()} op=refine-underfoot candidate={activeCandidateKey?.Label ?? "none"} distance={targetDistance:0.0}y action=probe-without-moving");
            refinementEvent = null;
            return;
        }

        refinementStepIndex++;
        logger.Info($"{BuildLogTag()} op=refine-move candidate={activeCandidateKey?.Label ?? "none"} stepIndex={refinementStepIndex}/{MaximumCandidateRefinementSteps} distance={refinementEvent.DistanceBucket} direction={refinementEvent.Direction} raw=<{movePlan.RawTarget.X:0.0}, {movePlan.RawTarget.Y:0.0}, {movePlan.RawTarget.Z:0.0}> resolved=<{target.X:0.0}, {target.Y:0.0}, {target.Z:0.0}> snapMethod={movePlan.SnapMethod} snapRadius={movePlan.SnapRadius:0} actualTarget={targetDistance:0.0}y");
        if (!TryStartRefinementMove(activeCandidate, target, Math.Max(2.5f, 8f / 2f), $"Treasure candidate {activeCandidate.Label} local refinement {refinementStepIndex}/{MaximumCandidateRefinementSteps}", targetAlreadyResolved: true))
        {
            return;
        }

        refinementEvent = null;
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

        var runOrderedCandidates = BuildOrderedCandidates(group, traversalOriginCenter);
        if (runOrderedCandidates.Count == 0)
        {
            lock (gate)
            {
                consumedHintRevision = hintSnapshot.Revision;
                lastHandoffReason = $"Ignored treasure hint revision {hintSnapshot.Revision}; group {groupKey} has no eligible mapped candidates.";
            }

            return false;
        }

        if (dangerousTreasureTravelController.IsRunning)
        {
            dangerousTreasureTravelController.Stop($"Treasure hint handoff to group {group.GroupKey}.");
        }

        movementController.Stop($"Treasure hint handoff to group {group.GroupKey}.");
        lock (gate)
        {
            activeGroupKey = group.GroupKey;
            handledCandidateLabels.Clear();
            orderedCandidates.Clear();
            orderedCandidates.AddRange(runOrderedCandidates);
            currentCandidateIndex = 0;
            consumedHintRevision = hintSnapshot.Revision;
            lastHandoffReason = $"Handoff to treasure group {group.GroupKey} from hint revision {hintSnapshot.Revision}.";
            candidateArrivedAt = DateTimeOffset.MinValue;
            candidateProbeDeadlineAt = DateTimeOffset.MinValue;
            candidateProbeLastAttemptAt = DateTimeOffset.MinValue;
            candidateProbeAttemptCount = 0;
            candidateProbeBaselineSessionId = 0;
            candidateProbeBaselineRevision = 0;
            refinementEvent = null;
            refinementProbeDeadlineAt = DateTimeOffset.MinValue;
            refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
            refinementProbeAttemptCount = 0;
            refinementProbeBaselineSessionId = 0;
            refinementProbeBaselineRevision = 0;
            refinementMoveDeadlineAt = DateTimeOffset.MinValue;
            refinementStepIndex = 0;
            mappedPointRetryUsed = false;
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
            activeCandidateUsesOverride = false;
            activeCandidateResolvedPosition = Vector3.Zero;
            revealedCofferLatched = false;
        }

        logger.ResetThrottle("treasure-search-travel");
        return BeginCurrentCandidate(lastHandoffReason);
    }

    private bool TryHandleVisibleCoffer()
    {
        if (State != TreasureSearchState.AcquiringRevealedCoffer)
        {
            return false;
        }

        return TryAcquireVisibleCofferFromActiveCandidate("revealed-acquire");
    }

    private bool TryAcquireVisibleCofferFromActiveCandidate(string source)
    {
        var scannerSnapshot = scanner.Snapshot;
        if (scannerSnapshot.VisibleCoffers.Count == 0)
        {
            return false;
        }

        if (orderedCandidates.Count == 0 || ActiveCandidateKey == null)
        {
            return false;
        }

        var activeCandidateKey = ActiveCandidateKey;
        if (activeCandidateKey == null)
        {
            return false;
        }

        if (!TryGetCurrentCandidate(out var activeCandidate))
        {
            return false;
        }

        var coffer = scannerSnapshot.VisibleCoffers[0];
        var activePosition = ActiveCandidateResolvedPosition != Vector3.Zero
            ? ActiveCandidateResolvedPosition
            : ResolveCandidatePosition(activeCandidate);
        var distanceToActive = CalculateFlatDistance(coffer.Position, activePosition);
        var nearestOtherDistance = float.MaxValue;
        TreasureCandidateKey? nearestOtherCandidateKey = null;

        foreach (var candidate in orderedCandidates)
        {
            if (string.Equals(candidate.CandidateKey, activeCandidateKey.CandidateKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidateDistance = CalculateFlatDistance(coffer.Position, ResolveCandidatePosition(candidate));
            if (candidateDistance >= nearestOtherDistance)
            {
                continue;
            }

            nearestOtherDistance = candidateDistance;
            nearestOtherCandidateKey = ToCandidateKey(candidate);
        }

        var isTrustworthy = distanceToActive <= MaximumTrustedAttributionDistance
            && (nearestOtherCandidateKey == null || distanceToActive <= nearestOtherDistance);
        var reason = isTrustworthy
            ? $"Attributed visible coffer {coffer.Name} to active route candidate {activeCandidateKey.Label} by route context. candidateDistance={distanceToActive:0.0}y"
            : nearestOtherCandidateKey == null
                ? $"Visible coffer {coffer.Name} was found while routing {activeCandidateKey.Label}, but the mapped candidate distance {distanceToActive:0.0}y exceeds the trust threshold {MaximumTrustedAttributionDistance:0.0}y. Interaction will continue without learning an override."
                : $"Visible coffer {coffer.Name} was found while routing {activeCandidateKey.Label}, but {nearestOtherCandidateKey.Label} is closer ({nearestOtherDistance:0.0}y vs {distanceToActive:0.0}y). Interaction will continue without learning an override.";

        CompleteWithVisibleCoffer(
            coffer,
            activeCandidateKey,
            distanceToActive,
            isTrustworthy,
            nearestOtherDistance,
            $"{reason} source={source}.");
        return true;
    }

    private void CompleteWithVisibleCoffer(VisibleCoffer coffer, TreasureCandidateKey candidateKey, float matchDistance, bool isTrustworthy, float nearestOtherDistance, string reason)
    {
        if (dangerousTreasureTravelController.IsRunning)
        {
            dangerousTreasureTravelController.Stop($"Visible coffer matched during dangerous travel for {candidateKey.Label}.");
            dangerousTreasureTravelController.AcknowledgeTerminalState();
        }

        if (movementController.State is not MovementState.Idle and not MovementState.Stopped and not MovementState.Arrived)
        {
            movementController.Stop($"Visible coffer matched for {candidateKey.Label}.");
        }

        lock (gate)
        {
            activeCandidateKey = candidateKey;
            revealedCofferLatched = true;
            activeVisibleCofferMatch = new VisibleCofferMatch
            {
                CandidateKey = candidateKey,
                Coffer = coffer,
                MatchDistance = matchDistance,
                IsTrustworthy = isTrustworthy,
                DistanceToNearestOtherCandidate = nearestOtherDistance,
                AttributionReason = reason,
            };
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
        }

        logger.ResetThrottle("treasure-search-travel");
        TransitionTo(TreasureSearchState.ReadyForInteraction, reason, result: TreasureSearchRunResult.ReadyForInteraction);
    }

    private void AdvanceCandidate(string reason)
    {
        if (movementController.IsPathBusy)
        {
            QueueCandidateAdvance(reason);
            return;
        }

        AdvanceCandidateCore(reason);
    }

    private void QueueCandidateAdvance(string reason)
    {
        logger.ResetThrottle("treasure-search-travel");
        lock (gate)
        {
            pendingCandidateAdvanceReason = reason;
        }
    }

    private bool TryResumeDeferredCandidateAdvance()
    {
        string reason;
        lock (gate)
        {
            reason = pendingCandidateAdvanceReason;
        }

        if (reason.Length == 0)
        {
            return false;
        }

        if (movementController.IsPathBusy)
        {
            logger.DebugThrottled(
                "treasure-search-advance-wait",
                TimeSpan.FromSeconds(1),
                $"Treasure search is waiting for vnavmesh to settle before advancing candidates. reason={reason} movementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}.");
            return true;
        }

        lock (gate)
        {
            pendingCandidateAdvanceReason = string.Empty;
        }

        logger.Info($"{BuildLogTag()} op=advance-resumed candidate={activeCandidateKey?.Label ?? "none"} reason={reason}");
        AdvanceCandidateCore(reason);
        return true;
    }

    private void AdvanceCandidateCore(string reason)
    {
        logger.ResetThrottle("treasure-search-travel");

        if (orderedCandidates.Count == 0)
        {
            SetFailure(reason);
            return;
        }

        if (CurrentCandidateIndex + 1 >= orderedCandidates.Count)
        {
            TransitionTo(TreasureSearchState.CandidatesExhausted, reason, result: TreasureSearchRunResult.CandidatesExhausted);
            return;
        }

        lock (gate)
        {
            currentCandidateIndex++;
            candidateArrivedAt = DateTimeOffset.MinValue;
            candidateProbeDeadlineAt = DateTimeOffset.MinValue;
            candidateProbeLastAttemptAt = DateTimeOffset.MinValue;
            candidateProbeAttemptCount = 0;
            candidateProbeBaselineSessionId = 0;
            candidateProbeBaselineRevision = 0;
            refinementEvent = null;
            refinementProbeDeadlineAt = DateTimeOffset.MinValue;
            refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
            refinementProbeAttemptCount = 0;
            refinementProbeBaselineSessionId = 0;
            refinementProbeBaselineRevision = 0;
            refinementMoveDeadlineAt = DateTimeOffset.MinValue;
            refinementStepIndex = 0;
            mappedPointRetryUsed = false;
            activeVisibleCofferMatch = null;
            activeCandidateUsesOverride = false;
            activeCandidateResolvedPosition = Vector3.Zero;
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
            revealedCofferLatched = false;
            lastNavmeshRejectionSummary = string.Empty;
            pendingCandidateAdvanceReason = string.Empty;
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
        var isDangerousCandidate = IsDangerousCandidate(candidate);
        var destination = movementController.FindNearestNavigablePoint(targetPosition, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!destination.HasValue)
        {
            var navFailureReason = $"Treasure candidate {candidate.Label} has no reliable vnavmesh point near <{targetPosition.X:0.0}, {targetPosition.Y:0.0}, {targetPosition.Z:0.0}>.";
            if (isDangerousCandidate)
            {
                return SkipDangerousCandidate(navFailureReason);
            }

            AdvanceCandidate(navFailureReason);
            return false;
        }

        logger.Info($"{BuildLogTag()} op=candidate-start candidate={candidate.Label} fate=\"{activeFateName}\" ({activeFateId}) group={candidate.GroupKey} dangerous={isDangerousCandidate} override={usedOverride} target=<{targetPosition.X:0.0}, {targetPosition.Y:0.0}, {targetPosition.Z:0.0}> destination=<{destination.Value.X:0.0}, {destination.Value.Y:0.0}, {destination.Value.Z:0.0}> reason={reason}");
        if (isDangerousCandidate)
        {
            if (!configuration.UseNinjaForDangerousArea)
            {
                return SkipDangerousCandidate($"Skipping dangerous treasure candidate {candidate.Label} because aggro level {candidate.AggroLevel} exceeds Maximum Aggro Level {configuration.MaximumAggroLevel} and Ninja travel is disabled.");
            }

            if (!dangerousTreasureTravelController.Start(candidate, destination.Value, CandidateArrivalTolerance))
            {
                if (dangerousTreasureTravelController.LastResult == DangerousTreasureTravelResult.CandidateSkipped)
                {
                    return SkipDangerousCandidate(dangerousTreasureTravelController.LastTransition);
                }

                SetFailure(dangerousTreasureTravelController.LastError.Length == 0
                    ? $"Failed to start dangerous travel for treasure candidate {candidate.Label}."
                    : dangerousTreasureTravelController.LastError);
                return false;
            }
        }
        else if (!movementController.StartDirectMove($"Treasure candidate {candidate.Label} for {activeFateName}", destination.Value, CandidateArrivalTolerance))
        {
            SetFailure(movementController.LastError.Length == 0
                ? $"Failed to start movement to treasure candidate {candidate.Label}."
                : movementController.LastError);
            return false;
        }

        var travelTimeout = TimeSpan.FromSeconds(Math.Max(30, candidate.TravelTimeoutSeconds ?? 180));
        lock (gate)
        {
            handledCandidateLabels.Add(candidate.Label);
            activeCandidateKey = candidateKey;
            candidateTravelDeadlineAt = DateTimeOffset.UtcNow + travelTimeout;
            candidateArrivedAt = DateTimeOffset.MinValue;
            candidateProbeDeadlineAt = DateTimeOffset.MinValue;
            candidateProbeLastAttemptAt = DateTimeOffset.MinValue;
            candidateProbeAttemptCount = 0;
            candidateProbeBaselineSessionId = 0;
            candidateProbeBaselineRevision = 0;
            refinementEvent = null;
            refinementProbeDeadlineAt = DateTimeOffset.MinValue;
            refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
            refinementProbeAttemptCount = 0;
            refinementProbeBaselineSessionId = 0;
            refinementProbeBaselineRevision = 0;
            refinementMoveDeadlineAt = DateTimeOffset.MinValue;
            refinementStepIndex = 0;
            mappedPointRetryUsed = false;
            activeVisibleCofferMatch = null;
            activeCandidateUsesOverride = usedOverride;
            activeCandidateResolvedPosition = targetPosition;
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
            revealedCofferLatched = false;
            lastNavmeshRejectionSummary = string.Empty;
            pendingCandidateAdvanceReason = string.Empty;
        }

        TransitionTo(
            TreasureSearchState.TravelingToCandidate,
            $"{reason} Moving to treasure candidate {candidate.Label} in group {candidate.GroupKey} using {(usedOverride ? "override" : "canonical")} position{(isDangerousCandidate ? " with Ninja/Hide dangerous-area flow" : string.Empty)}.");
        return true;
    }

    private Vector3 ResolveCandidatePosition(TreasureCofferCandidateData candidate)
    {
        var candidateKey = ToCandidateKey(candidate);
        return cofferPositionOverrideStore.TryResolvePosition(candidateKey, out var overridePosition)
            ? overridePosition
            : candidate.Position.ToVector3();
    }

    private bool TryHandleDangerousTravelTerminalResult()
    {
        switch (dangerousTreasureTravelController.State)
        {
            case DangerousTreasureTravelState.Arrived:
                var arriveReason = dangerousTreasureTravelController.LastTransition;
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                lock (gate)
                {
                    candidateArrivedAt = DateTimeOffset.UtcNow;
                    candidateProbeDeadlineAt = DateTimeOffset.MinValue;
                    candidateProbeLastAttemptAt = DateTimeOffset.MinValue;
                    candidateProbeAttemptCount = 0;
                    candidateProbeBaselineSessionId = 0;
                    candidateProbeBaselineRevision = 0;
                    refinementEvent = null;
                    refinementProbeDeadlineAt = DateTimeOffset.MinValue;
                    refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
                    refinementProbeAttemptCount = 0;
                    refinementProbeBaselineSessionId = 0;
                    refinementProbeBaselineRevision = 0;
                    refinementMoveDeadlineAt = DateTimeOffset.MinValue;
                    refinementStepIndex = 0;
                    mappedPointRetryUsed = false;
                }

                logger.ResetThrottle("treasure-search-probe");
                TransitionTo(TreasureSearchState.ProbingCandidate, $"{arriveReason} No visible coffer was found at treasure candidate {activeCandidateKey?.Label}; starting local Magical Elixir probing.");
                return true;
            case DangerousTreasureTravelState.CandidateSkipped:
                var skipReason = dangerousTreasureTravelController.LastTransition;
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                AdvanceCandidate(skipReason);
                return true;
            case DangerousTreasureTravelState.Failed:
                var failureReason = dangerousTreasureTravelController.LastError.Length == 0
                    ? dangerousTreasureTravelController.LastTransition
                    : dangerousTreasureTravelController.LastError;
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                SetFailure(failureReason);
                return true;
            case DangerousTreasureTravelState.Stopped:
                var stoppedReason = dangerousTreasureTravelController.LastError.Length == 0
                    ? dangerousTreasureTravelController.LastTransition
                    : dangerousTreasureTravelController.LastError;
                dangerousTreasureTravelController.AcknowledgeTerminalState();
                SetFailure(stoppedReason);
                return true;
            default:
                return false;
        }
    }

    private bool SkipDangerousCandidate(string reason)
    {
        logger.ResetThrottle("treasure-search-travel");

        if (orderedCandidates.Count == 0)
        {
            SetFailure(reason);
            return false;
        }

        if (CurrentCandidateIndex + 1 >= orderedCandidates.Count)
        {
            TransitionTo(TreasureSearchState.CandidatesExhausted, reason, result: TreasureSearchRunResult.CandidatesExhausted);
            return false;
        }

        lock (gate)
        {
            currentCandidateIndex++;
            candidateArrivedAt = DateTimeOffset.MinValue;
            candidateProbeDeadlineAt = DateTimeOffset.MinValue;
            candidateProbeLastAttemptAt = DateTimeOffset.MinValue;
            candidateProbeAttemptCount = 0;
            candidateProbeBaselineSessionId = 0;
            candidateProbeBaselineRevision = 0;
            refinementEvent = null;
            refinementProbeDeadlineAt = DateTimeOffset.MinValue;
            refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
            refinementProbeAttemptCount = 0;
            refinementProbeBaselineSessionId = 0;
            refinementProbeBaselineRevision = 0;
            refinementMoveDeadlineAt = DateTimeOffset.MinValue;
            refinementStepIndex = 0;
            mappedPointRetryUsed = false;
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
            activeCandidateUsesOverride = false;
            activeCandidateResolvedPosition = Vector3.Zero;
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
            revealedCofferLatched = false;
        }

        return BeginCurrentCandidate(reason);
    }

    private bool TryGetCurrentCandidate(out TreasureCofferCandidateData candidate)
    {
        candidate = new TreasureCofferCandidateData();
        if (CurrentCandidateIndex < 0
            || CurrentCandidateIndex >= orderedCandidates.Count)
        {
            return false;
        }

        candidate = orderedCandidates[CurrentCandidateIndex];
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

    private void BeginCandidateRefinement(TreasureHintEvent latestEvent)
    {
        lock (gate)
        {
            refinementEvent = latestEvent;
            refinementProbeDeadlineAt = DateTimeOffset.MinValue;
            refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
            refinementProbeAttemptCount = 0;
            refinementProbeBaselineSessionId = 0;
            refinementProbeBaselineRevision = 0;
            refinementMoveDeadlineAt = DateTimeOffset.MinValue;
            refinementStepIndex = 0;
            mappedPointRetryUsed = false;
            candidateProbeDeadlineAt = DateTimeOffset.MinValue;
            candidateProbeLastAttemptAt = DateTimeOffset.MinValue;
        }

        logger.ResetThrottle("treasure-search-refine");
        TransitionTo(TreasureSearchState.RefiningCandidate, $"Treasure candidate {activeCandidateKey?.Label} produced a local hint ({latestEvent.DistanceBucket} {latestEvent.Direction}); starting local refinement.");
    }

    private bool TickRefinementMovement(TreasureCofferCandidateData activeCandidate)
    {
        if (dangerousTreasureTravelController.IsRunning)
        {
            switch (dangerousTreasureTravelController.State)
            {
                case DangerousTreasureTravelState.Arrived:
                    dangerousTreasureTravelController.AcknowledgeTerminalState();
                    var dangerousHandoffResult = TryApplyCandidateHandoff(Plugin.ObjectTable.LocalPlayer?.Position ?? activeCandidateResolvedPosition, "dangerous refinement arrival");
                    refinementMoveDeadlineAt = DateTimeOffset.MinValue;
                    return dangerousHandoffResult != CandidateHandoffResult.DangerousTransitionStarted;
                case DangerousTreasureTravelState.CandidateSkipped:
                    var skipReason = dangerousTreasureTravelController.LastTransition;
                    dangerousTreasureTravelController.AcknowledgeTerminalState();
                    AdvanceCandidate(skipReason);
                    return false;
                case DangerousTreasureTravelState.Failed:
                case DangerousTreasureTravelState.Stopped:
                    var failureReason = dangerousTreasureTravelController.LastError.Length == 0
                        ? dangerousTreasureTravelController.LastTransition
                        : dangerousTreasureTravelController.LastError;
                    dangerousTreasureTravelController.AcknowledgeTerminalState();
                    AdvanceCandidate(failureReason);
                    return false;
            }

            logger.DebugThrottled(
                "treasure-search-refine",
                WaitLogInterval,
                $"Treasure refinement is running dangerous travel for candidate {activeCandidateKey?.Label}. DangerousState={dangerousTreasureTravelController.State} transition={dangerousTreasureTravelController.LastTransition}.");
            return false;
        }

        if (refinementMoveDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= refinementMoveDeadlineAt)
        {
            movementController.Stop("Treasure refinement move timed out.");
            QueueCandidateAdvance($"Treasure candidate {activeCandidateKey?.Label} local refinement move timed out.");
            return false;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached local treasure refinement target.");
                var handoffResult = TryApplyCandidateHandoff(Plugin.ObjectTable.LocalPlayer?.Position ?? activeCandidateResolvedPosition, "local refinement arrival");
                refinementMoveDeadlineAt = DateTimeOffset.MinValue;
                return handoffResult != CandidateHandoffResult.DangerousTransitionStarted;
            case MovementState.Failed:
                QueueCandidateAdvance(movementController.LastError.Length == 0
                    ? $"Treasure candidate {activeCandidateKey?.Label} local refinement move failed."
                    : movementController.LastError);
                return false;
            case MovementState.TimedOut:
                movementController.Stop("Treasure refinement move timed out.");
                QueueCandidateAdvance($"Treasure candidate {activeCandidateKey?.Label} local refinement move timed out.");
                return false;
        }

        logger.DebugThrottled(
            "treasure-search-refine",
            WaitLogInterval,
            $"Treasure refinement is moving for candidate {activeCandidateKey?.Label}. MovementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()} deadline={refinementMoveDeadlineAt:O}.");
        return false;
    }

    private bool TryStartRefinementMove(TreasureCofferCandidateData activeCandidate, Vector3 target, float arrivalTolerance, string description, bool targetAlreadyResolved = false)
    {
        var destination = targetAlreadyResolved
            ? new Vector3?(target)
            : movementController.FindNearestNavigablePoint(target, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!destination.HasValue)
        {
            AdvanceCandidate($"Treasure candidate {activeCandidate.Label} local refinement target has no reliable vnavmesh point near <{target.X:0.0}, {target.Y:0.0}, {target.Z:0.0}>.");
            return false;
        }

        var resolvedDestination = destination.Value;

        var isDangerousCandidate = IsDangerousCandidate(activeCandidate);
        if (isDangerousCandidate)
        {
            if (!configuration.UseNinjaForDangerousArea)
            {
                AdvanceCandidate($"Skipping dangerous treasure refinement for candidate {activeCandidate.Label} because Ninja travel is disabled.");
                return false;
            }

            if (!dangerousTreasureTravelController.Start(activeCandidate, resolvedDestination, arrivalTolerance))
            {
                if (dangerousTreasureTravelController.LastResult == DangerousTreasureTravelResult.CandidateSkipped)
                {
                    AdvanceCandidate(dangerousTreasureTravelController.LastTransition);
                }
                else
                {
                    SetFailure(dangerousTreasureTravelController.LastError.Length == 0
                        ? $"Failed to start dangerous local refinement for candidate {activeCandidate.Label}."
                        : dangerousTreasureTravelController.LastError);
                }

                return false;
            }
        }
        else if (!movementController.StartDirectMove(description, resolvedDestination, arrivalTolerance, destinationAlreadyResolved: targetAlreadyResolved))
        {
            AdvanceCandidate(movementController.LastError.Length == 0
                ? $"Failed to start local refinement movement for candidate {activeCandidate.Label}."
                : movementController.LastError);
            return false;
        }

        refinementMoveDeadlineAt = DateTimeOffset.UtcNow + GetRefinementMoveTimeout(CalculateFlatDistance(Plugin.ObjectTable.LocalPlayer?.Position ?? target, resolvedDestination));
        return true;
    }

    private CandidateHandoffResult TryApplyCandidateHandoff(Vector3 referencePosition, string reason)
    {
        var currentIndex = CurrentCandidateIndex;
        if (currentIndex < 0 || currentIndex >= orderedCandidates.Count)
        {
            return CandidateHandoffResult.None;
        }

        var currentCandidate = orderedCandidates[currentIndex];
        var currentPosition = ResolveCandidatePosition(currentCandidate);
        var currentDistance = CalculateFlatDistance(referencePosition, currentPosition);
        var bestIndex = -1;
        var bestDistance = float.MaxValue;

        for (var i = 0; i < orderedCandidates.Count; i++)
        {
            if (i == currentIndex)
            {
                continue;
            }

            var candidate = orderedCandidates[i];
            if (IsHandledCandidate(candidate.Label))
            {
                continue;
            }

            var distance = CalculateFlatDistance(referencePosition, ResolveCandidatePosition(candidate));
            var advantage = currentDistance - distance;
            if (distance > CandidateHandoffRadius || advantage < CandidateHandoffAdvantage)
            {
                continue;
            }

            if (IsDangerousCandidate(candidate) && !configuration.UseNinjaForDangerousArea)
            {
                continue;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            return CandidateHandoffResult.None;
        }

        var handoffCandidate = orderedCandidates[bestIndex];
        var handoffIsDangerous = IsDangerousCandidate(handoffCandidate);
        var handoffKey = ToCandidateKey(handoffCandidate);
        var usedOverride = cofferPositionOverrideStore.TryResolvePosition(handoffKey, out var overridePosition);
        var handoffResolvedPosition = usedOverride ? overridePosition : handoffCandidate.Position.ToVector3();
        lock (gate)
        {
            handledCandidateLabels.Add(currentCandidate.Label);
            handledCandidateLabels.Add(handoffCandidate.Label);
            currentCandidateIndex = bestIndex;
            activeCandidateKey = handoffKey;
            activeCandidateUsesOverride = usedOverride;
            activeCandidateResolvedPosition = handoffResolvedPosition;
            mappedPointRetryUsed = false;
            lastHandoffReason = $"Handoff from {currentCandidate.Label} to {handoffCandidate.Label} using {reason}.";
        }

        logger.Info($"{BuildLogTag()} op=handoff fromCandidate={currentCandidate.Label} toCandidate={handoffCandidate.Label} reason={reason} currentDistance={currentDistance:0.0}y handoffDistance={bestDistance:0.0}y advantage={(currentDistance - bestDistance):0.0}y");
        if (!handoffIsDangerous)
        {
            return CandidateHandoffResult.Updated;
        }

        var destination = movementController.FindNearestNavigablePoint(handoffResolvedPosition, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!destination.HasValue)
        {
            AdvanceCandidate($"Treasure candidate {handoffCandidate.Label} handoff target has no reliable vnavmesh point near <{handoffResolvedPosition.X:0.0}, {handoffResolvedPosition.Y:0.0}, {handoffResolvedPosition.Z:0.0}>.");
            return CandidateHandoffResult.DangerousTransitionStarted;
        }

        if (!dangerousTreasureTravelController.Start(handoffCandidate, destination.Value, CandidateArrivalTolerance))
        {
            if (dangerousTreasureTravelController.LastResult == DangerousTreasureTravelResult.CandidateSkipped)
            {
                AdvanceCandidate(dangerousTreasureTravelController.LastTransition);
            }
            else
            {
                SetFailure(dangerousTreasureTravelController.LastError.Length == 0
                    ? $"Failed to start dangerous travel after handoff to treasure candidate {handoffCandidate.Label}."
                    : dangerousTreasureTravelController.LastError);
            }

            return CandidateHandoffResult.DangerousTransitionStarted;
        }

        logger.Info($"{BuildLogTag()} op=handoff-dangerous candidate={handoffCandidate.Label} action=start-dangerous-travel");
        return CandidateHandoffResult.DangerousTransitionStarted;
    }

    private List<TreasureCofferCandidateData> BuildOrderedCandidates(TreasureCofferGroupData group, Vector3 originCenter)
    {
        var safeCandidates = new List<TreasureCofferCandidateData>();
        var dangerousCandidates = new List<TreasureCofferCandidateData>();

        foreach (var candidate in group.Candidates)
        {
            if (IsDangerousCandidate(candidate))
            {
                if (!configuration.UseNinjaForDangerousArea)
                {
                    continue;
                }

                dangerousCandidates.Add(candidate);
            }
            else
            {
                safeCandidates.Add(candidate);
            }
        }

        var orderedSafeCandidates = OrderCandidatesNearestNeighbor(safeCandidates, originCenter);
        var dangerousOrigin = orderedSafeCandidates.Count > 0
            ? orderedSafeCandidates[^1].Position.ToVector3()
            : originCenter;
        var orderedDangerousCandidates = OrderCandidatesNearestNeighbor(dangerousCandidates, dangerousOrigin);
        foreach (var dangerousCandidate in orderedDangerousCandidates)
        {
            logger.Info($"{BuildLogTag()} op=dangerous-order candidate={dangerousCandidate.Label} reason=ninja-dangerous-area-enabled");
        }

        var finalOrder = new List<TreasureCofferCandidateData>(orderedSafeCandidates.Count + orderedDangerousCandidates.Count);
        finalOrder.AddRange(orderedSafeCandidates);
        finalOrder.AddRange(orderedDangerousCandidates);
        logger.Info($"{BuildLogTag()} op=candidate-order fate=\"{activeFateName}\" group={group.GroupKey} safe=[{string.Join(", ", orderedSafeCandidates.Select(candidate => candidate.Label))}] dangerous=[{string.Join(", ", orderedDangerousCandidates.Select(candidate => candidate.Label))}] final=[{string.Join(", ", finalOrder.Select(candidate => candidate.Label))}]");
        return finalOrder;
    }

    private List<TreasureCofferCandidateData> OrderCandidatesNearestNeighbor(IReadOnlyList<TreasureCofferCandidateData> candidates, Vector3 originCenter)
    {
        var remaining = candidates.ToList();
        var ordered = new List<TreasureCofferCandidateData>(remaining.Count);
        var currentPosition = originCenter;

        while (remaining.Count > 0)
        {
            var nextCandidate = remaining
                .OrderBy(candidate => CalculateFlatDistance(currentPosition, candidate.Position.ToVector3()))
                .First();
            ordered.Add(nextCandidate);
            remaining.Remove(nextCandidate);
            currentPosition = nextCandidate.Position.ToVector3();
        }

        return ordered;
    }

    private bool IsDangerousCandidate(TreasureCofferCandidateData candidate)
        => candidate.AggroLevel > configuration.MaximumAggroLevel;

    private bool IsHandledCandidate(string label)
        => handledCandidateLabels.Contains(label);

    private RefinementMovePlan? ResolveRefinementMove(Vector3 playerPosition, TreasureDirection direction, float baseStep)
    {
        var directionVector = GetDirectionVector(direction);
        if (directionVector == Vector3.Zero)
        {
            lastNavmeshRejectionSummary = string.Empty;
            return null;
        }

        var rejectionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        RefinementMovePlan? bestPlan = null;

        void RecordRejection(string reason)
            => rejectionCounts[reason] = rejectionCounts.GetValueOrDefault(reason, 0) + 1;

        RefinementMovePlan? ValidateMeshTarget(Vector3 rawTarget, Vector3? meshTarget, string method, float radius, float step, float multiplier)
        {
            if (!meshTarget.HasValue)
            {
                return null;
            }

            if (!TryValidateNavmeshResolution(rawTarget, meshTarget.Value, NavmeshMaxVerticalSnap, out var rejectionReason, out var verticalDelta))
            {
                RecordRejection(rejectionReason);
                logger.Debug($"Treasure target <{rawTarget.X:0.0}, {rawTarget.Y:0.0}, {rawTarget.Z:0.0}> resolved via {method}(r={radius:0}) to <{meshTarget.Value.X:0.0}, {meshTarget.Value.Y:0.0}, {meshTarget.Value.Z:0.0}>, but navmesh validation rejected it: reason={rejectionReason} verticalSnap={(verticalDelta.HasValue ? $"{verticalDelta.Value:0.0}y" : "n/a")}.");
                return null;
            }

            var snappedTarget = meshTarget.Value;
            var snapDistance = CalculateFlatDistance(rawTarget, snappedTarget);
            var moveX = snappedTarget.X - playerPosition.X;
            var moveZ = snappedTarget.Z - playerPosition.Z;
            var targetDistance = MathF.Sqrt((moveX * moveX) + (moveZ * moveZ));
            var forwardDistance = (moveX * directionVector.X) + (moveZ * directionVector.Z);
            var lateralDistance = MathF.Abs((moveX * directionVector.Z) - (moveZ * directionVector.X));
            var maxSnapDistance = MathF.Max(3.5f, step * 0.75f);
            var minimumForwardDistance = MathF.Max(1f, step * 0.20f);
            var maxLateralDistance = MathF.Max(3f, step * 0.75f);

            if (snapDistance > maxSnapDistance)
            {
                RecordRejection("horizontal_snap");
                logger.Debug($"Treasure target <{rawTarget.X:0.0}, {rawTarget.Y:0.0}, {rawTarget.Z:0.0}> resolved via {method}(r={radius:0}) to <{snappedTarget.X:0.0}, {snappedTarget.Y:0.0}, {snappedTarget.Z:0.0}>, but snap drift {snapDistance:0.0}y exceeds {maxSnapDistance:0.0}y; rejecting mesh point.");
                return null;
            }

            if (forwardDistance < minimumForwardDistance)
            {
                RecordRejection("insufficient_forward");
                logger.Debug($"Treasure target <{rawTarget.X:0.0}, {rawTarget.Y:0.0}, {rawTarget.Z:0.0}> resolved via {method}(r={radius:0}) to <{snappedTarget.X:0.0}, {snappedTarget.Y:0.0}, {snappedTarget.Z:0.0}>, but it only advances {forwardDistance:0.0}y in the intended {direction} direction; rejecting mesh point.");
                return null;
            }

            if (lateralDistance > maxLateralDistance)
            {
                RecordRejection("lateral_drift");
                logger.Debug($"Treasure target <{rawTarget.X:0.0}, {rawTarget.Y:0.0}, {rawTarget.Z:0.0}> resolved via {method}(r={radius:0}) to <{snappedTarget.X:0.0}, {snappedTarget.Y:0.0}, {snappedTarget.Z:0.0}>, but lateral drift {lateralDistance:0.0}y exceeds {maxLateralDistance:0.0}y for {direction}; rejecting mesh point.");
                return null;
            }

            if (!movementController.HasPathfindRoute(playerPosition, snappedTarget))
            {
                RecordRejection("no_route");
                logger.Debug($"Treasure target <{rawTarget.X:0.0}, {rawTarget.Y:0.0}, {rawTarget.Z:0.0}> resolved via {method}(r={radius:0}) to <{snappedTarget.X:0.0}, {snappedTarget.Y:0.0}, {snappedTarget.Z:0.0}> but pathfind returned no route.");
                return null;
            }

            return new RefinementMovePlan(
                rawTarget,
                snappedTarget,
                method,
                radius,
                snapDistance,
                verticalDelta ?? 0f,
                forwardDistance,
                lateralDistance,
                targetDistance,
                step,
                multiplier,
                snapDistance + (lateralDistance * 0.5f) + (MathF.Abs(targetDistance - step) * 0.25f));
        }

        foreach (var multiplier in RefinementStepMultipliers)
        {
            var step = baseStep * multiplier;
            var rawTarget = BuildTreasureTarget(playerPosition, directionVector, step);
            foreach (var radius in RefinementSearchRadii)
            {
                RefinementMovePlan? radiusBestPlan = null;

                var floorPoint = movementController.FindPointOnFloor(rawTarget, radius);
                var floorPlan = ValidateMeshTarget(rawTarget, floorPoint, "PointOnFloor", radius, step, multiplier);
                if (floorPlan != null)
                {
                    radiusBestPlan = floorPlan;
                }

                var nearestPoint = movementController.FindNearestNavigablePoint(rawTarget, radius, MathF.Max(8f, radius * 0.4f));
                var nearestPlan = ValidateMeshTarget(rawTarget, nearestPoint, "NearestPoint", radius, step, multiplier);
                if (nearestPlan != null && (radiusBestPlan == null || nearestPlan.Score < radiusBestPlan.Score))
                {
                    radiusBestPlan = nearestPlan;
                }

                if (radiusBestPlan != null && (bestPlan == null || radiusBestPlan.Score < bestPlan.Score))
                {
                    bestPlan = radiusBestPlan;
                }

                if (floorPoint == null && nearestPoint == null)
                {
                    logger.Debug($"Treasure target <{rawTarget.X:0.0}, {rawTarget.Y:0.0}, {rawTarget.Z:0.0}> had no navmesh point from PointOnFloor or NearestPoint at radius {radius:0}.");
                }
            }
        }

        lastNavmeshRejectionSummary = rejectionCounts.Count == 0
            ? string.Empty
            : string.Join(", ", rejectionCounts.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}={entry.Value}"));
        return bestPlan;
    }

    private float GetRefinementStepSize(string distanceBucket)
        => distanceBucket switch
        {
            "beyond_far" => 100f,
            "far" => 40f,
            "immediately" => 8f,
            _ => 20f,
        };

    private TimeSpan GetRefinementMoveTimeout(float step)
    {
        var timeoutSeconds = ((step / Math.Max(0.1f, mountedTravelSpeed)) * 2f) + 15f;
        return TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 12f, 45f));
    }

    private static Vector3 GetDirectionVector(TreasureDirection direction)
        => direction switch
        {
            TreasureDirection.North => new Vector3(0f, 0f, -1f),
            TreasureDirection.Northeast => new Vector3(0.70710678f, 0f, -0.70710678f),
            TreasureDirection.East => new Vector3(1f, 0f, 0f),
            TreasureDirection.Southeast => new Vector3(0.70710678f, 0f, 0.70710678f),
            TreasureDirection.South => new Vector3(0f, 0f, 1f),
            TreasureDirection.Southwest => new Vector3(-0.70710678f, 0f, 0.70710678f),
            TreasureDirection.West => new Vector3(-1f, 0f, 0f),
            TreasureDirection.Northwest => new Vector3(-0.70710678f, 0f, -0.70710678f),
            _ => Vector3.Zero,
        };

    private static Vector3 BuildTreasureTarget(Vector3 position, Vector3 directionVector, float step)
        => new(
            position.X + (directionVector.X * step),
            position.Y,
            position.Z + (directionVector.Z * step));

    private static bool TryValidateNavmeshResolution(Vector3 referencePoint, Vector3 meshPoint, float maxVerticalDelta, out string rejectionReason, out float? verticalDelta)
    {
        if (!TryGetPlausibleNavmeshPoint(meshPoint, out rejectionReason, out _, out var meshY, out _))
        {
            verticalDelta = null;
            return false;
        }

        if (float.IsNaN(referencePoint.Y) || float.IsInfinity(referencePoint.Y))
        {
            rejectionReason = "invalid_reference";
            verticalDelta = null;
            return false;
        }

        verticalDelta = MathF.Abs(meshY - referencePoint.Y);
        if (verticalDelta.Value > maxVerticalDelta)
        {
            rejectionReason = "vertical_snap";
            return false;
        }

        rejectionReason = string.Empty;
        return true;
    }

    private static bool TryGetPlausibleNavmeshPoint(Vector3 point, out string rejectionReason, out float x, out float y, out float z)
    {
        x = point.X;
        y = point.Y;
        z = point.Z;
        if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z))
        {
            rejectionReason = "nan";
            return false;
        }

        if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z))
        {
            rejectionReason = "infinite";
            return false;
        }

        if (MathF.Abs(x) > NavmeshCoordinateAbsLimit
            || MathF.Abs(y) > NavmeshCoordinateAbsLimit
            || MathF.Abs(z) > NavmeshCoordinateAbsLimit)
        {
            rejectionReason = "absurd_coordinate";
            return false;
        }

        if (MathF.Abs(y - NavmeshSentinelY) < NavmeshSentinelTolerance)
        {
            rejectionReason = "sentinel_elevation";
            return false;
        }

        if (y < NavmeshMinimumValidY || y > NavmeshMaximumValidY)
        {
            rejectionReason = "elevation_out_of_range";
            return false;
        }

        rejectionReason = string.Empty;
        return true;
    }

    private void SetFailure(string reason)
        => TransitionTo(TreasureSearchState.Failed, reason, error: reason, result: TreasureSearchRunResult.Failed);

    private void BeginRevealedCofferAcquisition(TreasureHintEvent? revealEvent)
    {
        var revision = revealEvent?.Revision ?? treasureHintTracker.Snapshot.Revision;
        var kind = revealEvent?.Kind ?? TreasureHintKind.CofferReveal;
        var deadline = DateTimeOffset.UtcNow + RevealedCofferAcquireTimeout;
        lock (gate)
        {
            consumedHintRevision = Math.Max(consumedHintRevision, revision);
            candidateProbeBaselineSessionId = treasureHintTracker.Snapshot.SessionId;
            candidateProbeBaselineRevision = revision;
            candidateProbeDeadlineAt = DateTimeOffset.MinValue;
            candidateArrivedAt = DateTimeOffset.MinValue;
            refinementEvent = revealEvent;
            refinementProbeDeadlineAt = DateTimeOffset.MinValue;
            refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
            refinementProbeAttemptCount = 0;
            refinementProbeBaselineSessionId = 0;
            refinementProbeBaselineRevision = 0;
            refinementMoveDeadlineAt = DateTimeOffset.MinValue;
            revealedCofferAcquireDeadlineAt = deadline;
            revealedCofferLatched = true;
        }

        logger.ResetThrottle("treasure-search-revealed-acquire");
        logger.Info($"{BuildLogTag()} op=reveal-latched candidate={activeCandidateKey?.Label ?? "none"} source={(revealEvent == null ? "fallback" : kind.ToString())}");
        TransitionTo(
            TreasureSearchState.AcquiringRevealedCoffer,
            $"Treasure candidate {activeCandidateKey?.Label} produced event {kind}; entering post-reveal coffer acquisition until {deadline:O}.");
    }

    private void TickAcquiringRevealedCoffer()
    {
        if (TryHandleVisibleCoffer())
        {
            return;
        }

        if (revealedCofferAcquireDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= revealedCofferAcquireDeadlineAt)
        {
            logger.ResetThrottle("treasure-search-revealed-acquire");
            AdvanceCandidate($"Revealed coffer for treasure candidate {activeCandidateKey?.Label} was not acquired within {RevealedCofferAcquireTimeout.TotalSeconds:0.0}s.");
            return;
        }

        var scannerSnapshot = scanner.Snapshot;
        logger.DebugThrottled(
            "treasure-search-revealed-acquire",
            WaitLogInterval,
            $"Treasure search is acquiring a revealed coffer for candidate {activeCandidateKey?.Label}. buffActive={scannerSnapshot.HasTreasureBuff} visibleCoffers={scannerSnapshot.VisibleCoffers.Count} deadline={revealedCofferAcquireDeadlineAt:O}.");
    }

    private bool TryStartCandidateProbe(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (candidateProbeLastAttemptAt != DateTimeOffset.MinValue && now - candidateProbeLastAttemptAt < CandidateProbeRetryDelay)
        {
            logger.DebugThrottled(
                "treasure-search-probe-retry",
                WaitLogInterval,
                $"Treasure candidate probe retry is waiting for {CandidateProbeRetryDelay.TotalSeconds:0.0}s between attempts. candidate={activeCandidateKey?.Label} attempts={candidateProbeAttemptCount}/{MaximumCandidateProbeAttempts}.");
            return true;
        }

        if (!gameActionController.HasMagicalElixir())
        {
            SetFailure($"Treasure candidate probe cannot continue because {GameActionController.MagicalElixirKeyItemName} is unavailable.");
            return false;
        }

        var hintSnapshot = treasureHintTracker.Snapshot;
        var used = gameActionController.TryUseMagicalElixirViaInventory($"treasure candidate probe {activeCandidateKey?.Label}");
        lock (gate)
        {
            candidateProbeAttemptCount++;
            candidateProbeLastAttemptAt = now;
            candidateProbeDeadlineAt = now + CandidateProbeTimeout;
            candidateProbeBaselineSessionId = hintSnapshot.SessionId;
            candidateProbeBaselineRevision = hintSnapshot.Revision;
            candidateArrivedAt = DateTimeOffset.MinValue;
        }

        logger.ResetThrottle("treasure-search-probe");
        logger.ResetThrottle("treasure-search-probe-retry");
        logger.Info($"{BuildLogTag()} op=probe-attempt candidate={activeCandidateKey?.Label ?? "none"} attempt={candidateProbeAttemptCount}/{MaximumCandidateProbeAttempts} inventoryUseAccepted={used} baselineSession={candidateProbeBaselineSessionId} baselineRevision={candidateProbeBaselineRevision} probeDeadline={candidateProbeDeadlineAt:O} reason={reason}");
        TransitionTo(TreasureSearchState.ProbingCandidate, $"{reason} Waiting for a new treasure event after baseline revision {candidateProbeBaselineRevision} in session {candidateProbeBaselineSessionId} for candidate {activeCandidateKey?.Label}.");
        return true;
    }

    private bool TryStartRefinementProbe(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (refinementProbeLastAttemptAt != DateTimeOffset.MinValue && now - refinementProbeLastAttemptAt < CandidateProbeRetryDelay)
        {
            logger.DebugThrottled(
                "treasure-search-refine-probe-retry",
                TimeSpan.FromMilliseconds(250),
                $"Treasure refinement probe retry is waiting for {CandidateProbeRetryDelay.TotalSeconds:0.0}s between attempts. candidate={activeCandidateKey?.Label} refinementStep={refinementStepIndex}/{MaximumCandidateRefinementSteps} attempts={refinementProbeAttemptCount}.");
            return true;
        }

        if (refinementProbeDeadlineAt != DateTimeOffset.MinValue && now < refinementProbeDeadlineAt)
        {
            logger.DebugThrottled(
                "treasure-search-refine-probe",
                WaitLogInterval,
                $"Treasure refinement is probing candidate {activeCandidateKey?.Label}. attempts={refinementProbeAttemptCount} probeDeadline={refinementProbeDeadlineAt:O} baselineSession={refinementProbeBaselineSessionId} baselineRevision={refinementProbeBaselineRevision} step={refinementStepIndex}/{MaximumCandidateRefinementSteps} hint={treasureHintTracker.Snapshot.GetHintSummary()}.");
            return true;
        }

        if (!gameActionController.HasMagicalElixir())
        {
            SetFailure($"Treasure refinement cannot continue because {GameActionController.MagicalElixirKeyItemName} is unavailable.");
            return false;
        }

        var hintSnapshot = treasureHintTracker.Snapshot;
        var used = gameActionController.TryUseMagicalElixirViaInventory($"treasure refinement probe {activeCandidateKey?.Label}");
        lock (gate)
        {
            refinementProbeAttemptCount++;
            refinementProbeLastAttemptAt = now;
            refinementProbeDeadlineAt = now + CandidateProbeTimeout;
            refinementProbeBaselineSessionId = hintSnapshot.SessionId;
            refinementProbeBaselineRevision = hintSnapshot.Revision;
        }

        logger.ResetThrottle("treasure-search-refine-probe");
        logger.ResetThrottle("treasure-search-refine-probe-retry");
        logger.Info($"{BuildLogTag()} op=refine-probe-attempt candidate={activeCandidateKey?.Label ?? "none"} attempt={refinementProbeAttemptCount} inventoryUseAccepted={used} baselineSession={refinementProbeBaselineSessionId} baselineRevision={refinementProbeBaselineRevision} probeDeadline={refinementProbeDeadlineAt:O} step={refinementStepIndex}/{MaximumCandidateRefinementSteps} reason={reason}");
        return true;
    }

    private void ContinueProbingAfterEvent(int revision)
    {
        lock (gate)
        {
            candidateProbeBaselineSessionId = treasureHintTracker.Snapshot.SessionId;
            candidateProbeBaselineRevision = revision;
            candidateProbeDeadlineAt = DateTimeOffset.UtcNow + CandidateProbeTimeout;
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
        }
    }

    private void TransitionTo(TreasureSearchState nextState, string reason, string? error = null, TreasureSearchRunResult? result = null)
    {
        TreasureSearchState previousState;
        lock (gate)
        {
            previousState = state;
            if (nextState is TreasureSearchState.Idle
                or TreasureSearchState.Stopped
                or TreasureSearchState.Failed
                or TreasureSearchState.CandidatesExhausted)
            {
                handledCandidateLabels.Clear();
                orderedCandidates.Clear();
                traversalOriginCenter = Vector3.Zero;
                pendingCandidateAdvanceReason = string.Empty;
                lastNavmeshRejectionSummary = string.Empty;
                revealedCofferLatched = false;
            }

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

        logger.Info($"{BuildLogTag()} op=transition from={previousState} to={nextState} fate=\"{activeFateName}\" ({activeFateId}) group={activeGroupKey} candidate={activeCandidateKey?.Label ?? "none"} reason={reason}");
    }

    private string BuildLogTag(int? sessionId = null)
    {
        var resolvedSessionId = sessionId ?? treasureHintTracker.Snapshot.SessionId;
        return resolvedSessionId > 0 ? $"[Treasure session={resolvedSessionId}]" : "[Treasure]";
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

    private static bool IsLocalTreasureDistance(string distanceBucket)
        => string.Equals(distanceBucket, "close", StringComparison.Ordinal)
            || string.Equals(distanceBucket, "immediately", StringComparison.Ordinal);

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }
}
