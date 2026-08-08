using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class TreasureSearchController : IDisposable
{
    private static int nextSearchRunSequence;
    private sealed record RefinementMovePlan(
        Vector3 RawTarget,
        Vector3 SnappedTarget,
        TreasureDirection Direction,
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
    private const float ConfiguredAlternateMaximumHintAngleDegrees = 50f;
    private const float MappedPointRetryArrivalTolerance = 4.5f;
    private const float LocalMoveSkipDistance = 3f;
    private const float PotRevealCofferScanRadius = 28f;
    private const float CandidateTravelProgressThreshold = 2f;
    private const float PointOnFloorOverrideScoreAdvantage = 1f;
    private const int MaximumCandidateRefinementSteps = 12;
    private const int MaximumRefinementMoveRecoveryAttempts = 2;
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
    private static readonly TimeSpan CandidateTravelStallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DangerousCandidateTravelStallTimeout = TimeSpan.FromSeconds(90);
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
    private readonly object gate = new();
    private readonly HashSet<string> handledCandidateLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TreasureCofferCandidateData> orderedCandidates = [];
    private readonly List<TreasureHintObservation> geometricObservations = [];
    private TreasureSearchState state = TreasureSearchState.Idle;
    private TreasureSearchRunResult lastResult;
    private string currentSearchRunId = string.Empty;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private uint activeFateId;
    private string activeFateName = string.Empty;
    private string activeGroupKey = string.Empty;
    private int currentCandidateIndex = -1;
    private int activeCandidateApproachWaypointIndex = -1;
    private int consumedHintRevision;
    private int searchStartSessionId;
    private int searchStartRevision;
    private string lastHandoffReason = string.Empty;
    private Vector3 traversalOriginCenter;
    private DateTimeOffset candidateTravelLastProgressAt = DateTimeOffset.MinValue;
    private float candidateTravelProgressDistance = float.MaxValue;
    private Vector3 candidateTravelLastObservedPosition;
    private bool hasCandidateTravelObservedPosition;
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
    private int refinementMoveRecoveryAttemptCount;
    private bool refinementCandidateLocked;
    private bool mappedPointRetryUsed;
    private bool confirmedCofferVerificationUsed;
    private bool confirmedCofferVerificationMovePending;
    private VisibleCofferMatch? activeVisibleCofferMatch;
    private TreasureCandidateKey? activeCandidateKey;
    private bool activeCandidateUsesOverride;
    private Vector3 activeCandidateResolvedPosition;
    private Vector3 candidateTravelTarget;
    private DateTimeOffset revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
    private bool revealedCofferLatched;
    private string lastNavmeshRejectionSummary = string.Empty;
    private string pendingCandidateAdvanceReason = string.Empty;
    private int candidateGeneration;
    private int probeOperationSequence;
    private string activeCandidateProbeOperationId = string.Empty;
    private int refinementOperationSequence;
    private string activeRefinementProbeOperationId = string.Empty;

    public TreasureSearchController(
        IFramework framework,
        OccultCrescentScanner scanner,
        MovementController movementController,
        GameActionController gameActionController,
        TreasureHintTracker treasureHintTracker,
        DangerousTreasureTravelController dangerousTreasureTravelController,
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
        => StartInternal(fateId, fateName, originCenter, null, null);

    public bool StartSecondChance(uint fateId, string fateName, Vector3 originCenter, SecondChanceAreaData area)
        => StartInternal(fateId, fateName, originCenter, "second-chance", area.CandidateKeys);

    private bool StartInternal(
        uint fateId,
        string fateName,
        Vector3 originCenter,
        string? groupKeyOverride,
        IReadOnlyCollection<string>? candidateKeyFilter)
    {
        if (IsRunning)
        {
            logger.Debug($"Treasure search start ignored because a run is already active. fate={activeFateName} ({activeFateId}) state={State}");
            return true;
        }

        var dependencyReport = Plugin.Current?.GetNormalAutomationDependencyReport();
        if (dependencyReport is { IsReady: false })
        {
            Plugin.Current?.TryOpenDependencyWindow();
            SetFailure(dependencyReport.FailureSummary);
            return false;
        }

        var hintSnapshot = treasureHintTracker.Snapshot;
        if (!hintSnapshot.HasActiveSession || !hintSnapshot.HasInitialHint)
        {
            SetFailure("Treasure search requires an active treasure session with an initial hint.");
            return false;
        }

        var scannerSnapshot = scanner.Snapshot;
        if (!string.Equals(hintSnapshot.TerritoryKey, scannerSnapshot.TerritoryKey, StringComparison.OrdinalIgnoreCase)
            || hintSnapshot.TerritoryTypeId != scannerSnapshot.TerritoryTypeId)
        {
            SetFailure("Treasure search cannot use a treasure session from a different territory.");
            return false;
        }

        var searchStrategy = scanner.ActiveTerritoryData?.PotTreasure.SearchStrategy ?? TreasureSearchStrategy.DirectionGroups;
        var initialHint = hintSnapshot.InitialHintEvent;
        var groupKey = groupKeyOverride
            ?? (searchStrategy == TreasureSearchStrategy.GeometricCandidates
            ? "geometry"
            : GetGroupKey(initialHint?.Direction ?? TreasureDirection.Unknown));
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
            handledCandidateLabels.Clear();
            orderedCandidates.Clear();
            geometricObservations.Clear();
        }

        activeFateName = fateName;
        var initialObservationPosition = initialHint != null && initialHint.ObservationPosition != Vector3.Zero
            ? initialHint.ObservationPosition
            : originCenter;
        var initialObservations = new List<TreasureHintObservation>();
        if (searchStrategy == TreasureSearchStrategy.GeometricCandidates && initialHint != null)
        {
            initialObservations.Add(new TreasureHintObservation(initialObservationPosition, initialHint.Direction));
            if (hintSnapshot.LastHintEvent is { } lastHint && lastHint.Revision != initialHint.Revision)
            {
                var lastObservationPosition = lastHint.ObservationPosition != Vector3.Zero
                    ? lastHint.ObservationPosition
                    : initialObservationPosition;
                initialObservations.Add(new TreasureHintObservation(lastObservationPosition, lastHint.Direction));
            }
        }

        var rankingOrigin = initialObservations.Count > 0 ? initialObservations[^1].Position : initialObservationPosition;
        if (candidateKeyFilter is { Count: > 0 })
        {
            group = new TreasureCofferGroupData
            {
                FateId = group.FateId,
                GroupKey = group.GroupKey,
                DisplayName = group.DisplayName,
                Candidates = group.Candidates
                    .Where(candidate => candidateKeyFilter.Contains(candidate.CandidateKey, StringComparer.OrdinalIgnoreCase))
                    .ToList(),
            };
        }

        var runOrderedCandidates = searchStrategy == TreasureSearchStrategy.GeometricCandidates
            ? BuildGeometricCandidates(group, initialObservations, rankingOrigin)
            : BuildOrderedCandidates(group, originCenter);
        if (runOrderedCandidates.Count == 0)
        {
            SetFailure($"Treasure search has no eligible coffer candidates for fate {fateId} group {groupKey}.");
            return false;
        }

        lock (gate)
        {
            currentSearchRunId = $"TreasureSearch#{Interlocked.Increment(ref nextSearchRunSequence)}";
            activeFateId = fateId;
            activeFateName = fateName;
            activeGroupKey = group.GroupKey;
            handledCandidateLabels.Clear();
            orderedCandidates.Clear();
            orderedCandidates.AddRange(runOrderedCandidates);
            geometricObservations.Clear();
            geometricObservations.AddRange(initialObservations);
            currentCandidateIndex = 0;
            activeCandidateApproachWaypointIndex = -1;
            consumedHintRevision = hintSnapshot.Revision;
            searchStartSessionId = hintSnapshot.SessionId;
            searchStartRevision = hintSnapshot.Revision;
            lastHandoffReason = $"Selected initial treasure group {group.GroupKey} from first hint revision {hintSnapshot.InitialHintEvent?.Revision ?? 0}.";
            traversalOriginCenter = originCenter;
            ClearCandidateTravelProgressTracking();
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
            activeCandidateUsesOverride = false;
            activeCandidateResolvedPosition = Vector3.Zero;
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
            revealedCofferLatched = false;
            lastError = string.Empty;
            lastResult = TreasureSearchRunResult.None;
            candidateGeneration = 0;
            probeOperationSequence = 0;
            activeCandidateProbeOperationId = string.Empty;
            refinementOperationSequence = 0;
            activeRefinementProbeOperationId = string.Empty;
        }

        logger.Info($"{BuildLogTag(hintSnapshot.SessionId)} op=start fate=\"{fateName}\" ({fateId}) group={group.GroupKey} initialHintRevision={hintSnapshot.InitialHintEvent?.Revision ?? 0} searchStartSession={searchStartSessionId} searchStartRevision={searchStartRevision} origin=<{originCenter.X:0.0}, {originCenter.Y:0.0}, {originCenter.Z:0.0}>");
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

        lock (gate)
        {
            ClearCandidateTravelProgressTracking();
        }

        TransitionTo(TreasureSearchState.Stopped, reason, error: reason, result: TreasureSearchRunResult.Stopped);
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            state = TreasureSearchState.Idle;
            lastResult = TreasureSearchRunResult.None;
            currentSearchRunId = string.Empty;
            lastTransition = "Idle";
            lastError = string.Empty;
            activeFateId = 0;
            activeFateName = string.Empty;
            activeGroupKey = string.Empty;
            currentCandidateIndex = -1;
            activeCandidateApproachWaypointIndex = -1;
            consumedHintRevision = 0;
            searchStartSessionId = 0;
            searchStartRevision = 0;
            lastHandoffReason = string.Empty;
            handledCandidateLabels.Clear();
            orderedCandidates.Clear();
            geometricObservations.Clear();
            traversalOriginCenter = Vector3.Zero;
            ClearCandidateTravelProgressTracking();
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
            refinementMoveRecoveryAttemptCount = 0;
            refinementCandidateLocked = false;
            mappedPointRetryUsed = false;
            confirmedCofferVerificationUsed = false;
            confirmedCofferVerificationMovePending = false;
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
            activeCandidateUsesOverride = false;
            activeCandidateResolvedPosition = Vector3.Zero;
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
            revealedCofferLatched = false;
            candidateGeneration = 0;
            probeOperationSequence = 0;
            activeCandidateProbeOperationId = string.Empty;
            refinementOperationSequence = 0;
            activeRefinementProbeOperationId = string.Empty;
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
            activeCandidateApproachWaypointIndex = -1;
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
            activeCandidateUsesOverride = false;
            activeCandidateResolvedPosition = Vector3.Zero;
            refinementProbeDeadlineAt = DateTimeOffset.MinValue;
            refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
            refinementProbeAttemptCount = 0;
            refinementProbeBaselineSessionId = 0;
            refinementProbeBaselineRevision = 0;
            refinementMoveRecoveryAttemptCount = 0;
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
            revealedCofferLatched = false;
        }

        return BeginCurrentCandidate(reason);
    }

    public bool TryPrepareActivePotRevealInteractionMatch(out VisibleCofferMatch? match, out string reason)
    {
        match = null;

        if (!scanner.Snapshot.IsInSupportedTerritory || !scanner.Snapshot.CanRunPotTreasure)
        {
            reason = scanner.Snapshot.IsInSupportedTerritory
                ? $"Pot reveal interaction debugging is unavailable in {scanner.Snapshot.TerritoryDisplayName}."
                : "Pot reveal interaction debugging requires a supported Occult Crescent territory.";
            return false;
        }

        var candidateKey = ActiveCandidateKey;
        if (candidateKey == null || orderedCandidates.Count == 0)
        {
            reason = "Pot reveal interaction debugging requires an active treasure candidate context.";
            return false;
        }

        if (State == TreasureSearchState.ReadyForInteraction)
        {
            match = ActiveVisibleCofferMatch;
            if (match != null)
            {
                reason = $"Using existing revealed coffer match for candidate {candidateKey.Label}.";
                return true;
            }

            reason = $"Treasure search is waiting for interaction on {candidateKey.Label}, but no active visible coffer match is available.";
            return false;
        }

        if (State != TreasureSearchState.AcquiringRevealedCoffer)
        {
            reason = $"Pot reveal interaction debugging requires treasure search state {TreasureSearchState.AcquiringRevealedCoffer} or {TreasureSearchState.ReadyForInteraction}. currentState={State}.";
            return false;
        }

        if (!TryAcquireVisibleCofferFromActiveCandidate("debug-command"))
        {
            reason = $"No visible coffer has been acquired yet for active candidate {candidateKey.Label}.";
            return false;
        }

        match = ActiveVisibleCofferMatch;
        if (match != null)
        {
            reason = $"Acquired revealed coffer match for candidate {candidateKey.Label} via debug command.";
            return true;
        }

        reason = $"Visible coffer acquisition succeeded for candidate {candidateKey.Label}, but no interaction match was recorded.";
        return false;
    }

    public bool TryFindNearbyPotRevealCofferForDebug(out VisibleCoffer coffer, out string recognitionSource, out string objectKind)
        => TryFindNearbyPotRevealCoffer(out coffer, out recognitionSource, out objectKind);

    private bool TryBeginLatchedRevealAcquisition(TreasureHintSnapshot hintSnapshot, string source)
    {
        var revealEvent = hintSnapshot.RevealLatchedEvent;
        if (revealEvent == null)
        {
            return false;
        }

        if (hintSnapshot.SessionId != searchStartSessionId
            || revealEvent.Revision <= searchStartRevision)
        {
            logger.DebugThrottled(
                "treasure-search-stale-reveal-latch",
                WaitLogInterval,
                $"Treasure search ignored a stale latched reveal while probing candidate {activeCandidateKey?.Label ?? "none"}. source={source} revealSession={hintSnapshot.SessionId} revealRevision={revealEvent.Revision} searchStartSession={searchStartSessionId} searchStartRevision={searchStartRevision}.");
            return false;
        }

        logger.Info($"{BuildLogTag(hintSnapshot.SessionId)} op=reveal-latch-honored candidate={activeCandidateKey?.Label ?? "none"} source={source} revision={revealEvent.Revision} kind={revealEvent.Kind} buffActive={scanner.Snapshot.HasTreasureBuff}");
        BeginRevealedCofferAcquisition(revealEvent);
        return true;
    }

    private bool TryBeginNearbyRevealFallback(string source)
    {
        if (!TryFindNearbyPotRevealCoffer(out var coffer, out var recognitionSource, out var objectKind))
        {
            return false;
        }

        logger.Info($"{BuildLogTag()} op=nearby-reveal-fallback candidate={activeCandidateKey?.Label ?? "none"} source={source} recognition={recognitionSource} objectKind={objectKind} objectId={coffer.GameObjectId:X} baseId={coffer.DataId} playerDistance={coffer.DistanceToPlayer:0.0}y targetable={coffer.IsTargetable}");
        BeginRevealedCofferAcquisition(null);
        return true;
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

        if (!scanner.Snapshot.IsInSupportedTerritory || !scanner.Snapshot.CanRunPotTreasure)
        {
            SetFailure("Left a pot-supported territory while treasure search was active.");
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

        if (TryStartKnowledgeThreatTravel())
        {
            return;
        }

        if (TryHandleCandidateTravelStall())
        {
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
                if (TryContinueCandidateApproachTravel())
                {
                    return;
                }

                lock (gate)
                {
                    ClearCandidateTravelProgressTracking();
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
        var scannerSnapshot = scanner.Snapshot;
        var hintSnapshot = treasureHintTracker.Snapshot;

        if (candidateArrivedAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - candidateArrivedAt < CandidateProbeSettleDelay)
        {
            logger.DebugThrottled(
                "treasure-search-probe-settle",
                TimeSpan.FromMilliseconds(250),
                $"Treasure search is settling at candidate {activeCandidateKey?.Label} before local Magical Elixir probing. elapsed={(DateTimeOffset.UtcNow - candidateArrivedAt).TotalSeconds:0.00}s required={CandidateProbeSettleDelay.TotalSeconds:0.00}s.");
            return;
        }

        if (!configuration.UseNinjaForDangerousArea
            && TryGetCurrentCandidate(out var probeCandidate)
            && IsAbovePotTreasureAggroLimit(probeCandidate))
        {
            AdvanceCandidate($"Skipping treasure probe at candidate {probeCandidate.Label} because aggro level {probeCandidate.AggroLevel} exceeds the current no-Ninja cutoff {GetPotTreasureAggroLimit()} ({GetPotTreasureAggroLimitSource()}).");
            return;
        }

        if (candidateProbeBaselineSessionId != 0
            && treasureHintTracker.TryGetLatestEventSince(candidateProbeBaselineSessionId, candidateProbeBaselineRevision, out var latestEvent)
            && latestEvent != null)
        {
            LogEventConsume("candidate-probe", candidateProbeBaselineSessionId, candidateProbeBaselineRevision, latestEvent, DescribeProbeOperation());
            lock (gate)
            {
                consumedHintRevision = Math.Max(consumedHintRevision, latestEvent.Revision);
            }

            switch (latestEvent.Kind)
            {
                case TreasureHintKind.Hint:
                    if (IsLocalTreasureDistance(latestEvent.DistanceBucket))
                    {
                        if (TryGetCurrentCandidate(out var currentProbeCandidate)
                            && TryBeginConfirmedCofferVerification(currentProbeCandidate, latestEvent))
                        {
                            return;
                        }

                        BeginCandidateRefinement(latestEvent);
                        return;
                    }

                    if (IsGeometricSearch() && TryReplanGeometricCandidates(latestEvent, $"Non-local probe at {activeCandidateKey?.Label}"))
                    {
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

        if (hintSnapshot.HasRevealLatched && TryBeginLatchedRevealAcquisition(hintSnapshot, "probe-latched"))
        {
            return;
        }

        if (hintSnapshot.IsPostBuffGraceActive)
        {
            logger.DebugThrottled(
                "treasure-search-post-buff-grace-probe",
                TimeSpan.FromMilliseconds(250),
                $"Treasure search is holding probe candidate {activeCandidateKey?.Label} during post-buff grace until {hintSnapshot.PostBuffGraceDeadlineAt:O}.");
            return;
        }

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

        if (candidateProbeDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= candidateProbeDeadlineAt)
        {
            if (candidateProbeAttemptCount >= MaximumCandidateProbeAttempts)
            {
                if (TryBeginNearbyRevealFallback("probe-timeout-fallback"))
                {
                    return;
                }

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
        var scannerSnapshot = scanner.Snapshot;
        var hintSnapshot = treasureHintTracker.Snapshot;

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
            LogEventConsume("refinement-probe", refinementProbeBaselineSessionId, refinementProbeBaselineRevision, refinementLatestEvent, DescribeRefinementOperation());
            lock (gate)
            {
                consumedHintRevision = Math.Max(consumedHintRevision, refinementLatestEvent.Revision);
                refinementEvent = refinementLatestEvent;
                refinementCandidateLocked |= IsImmediateTreasureDistance(refinementLatestEvent.DistanceBucket);
                refinementProbeBaselineSessionId = treasureHintTracker.Snapshot.SessionId;
                refinementProbeBaselineRevision = Math.Max(refinementProbeBaselineRevision, refinementLatestEvent.Revision);
                refinementProbeDeadlineAt = DateTimeOffset.MinValue;
            }
        }

        if (refinementEvent != null && (refinementEvent.Kind is TreasureHintKind.CofferReveal or TreasureHintKind.CofferMessage))
        {
            BeginRevealedCofferAcquisition(refinementEvent);
            return;
        }

        if (hintSnapshot.HasRevealLatched && TryBeginLatchedRevealAcquisition(hintSnapshot, "refine-latched"))
        {
            return;
        }

        if (hintSnapshot.IsPostBuffGraceActive)
        {
            logger.DebugThrottled(
                "treasure-search-post-buff-grace-refine",
                TimeSpan.FromMilliseconds(250),
                $"Treasure refinement is holding candidate {activeCandidateKey?.Label} during post-buff grace until {hintSnapshot.PostBuffGraceDeadlineAt:O}.");
            return;
        }

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

        if (refinementEvent == null)
        {
            if (refinementProbeDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= refinementProbeDeadlineAt)
            {
                logger.Info($"{BuildLogTag()} op=refine-probe-empty candidate={activeCandidateKey?.Label ?? "none"} attempt={refinementProbeAttemptCount} action=retry-current-position");

                if (TryBeginNearbyRevealFallback("refine-timeout-fallback"))
                {
                    return;
                }

                if (refinementProbeAttemptCount >= MaximumCandidateRefinementSteps)
                {
                    AdvanceCandidate($"Treasure candidate {activeCandidateKey?.Label} produced no usable event after {refinementProbeAttemptCount} refinement probe attempt(s); trying the next candidate.");
                    return;
                }
            }

            if (!EnsureRefinementProbeReady(activeCandidate))
            {
                return;
            }

            if (!TryStartRefinementProbe($"Refinement is probing candidate {activeCandidateKey?.Label} after local movement."))
            {
                return;
            }

            return;
        }

        if (refinementEvent.Kind == TreasureHintKind.BonusOffer)
        {
            var bonusOfferRevision = refinementEvent.Revision;
            logger.Info($"{BuildLogTag()} op=bonus-offer-latched candidate={activeCandidateKey?.Label ?? "none"} action=continue-search");
            refinementEvent = null;
            ContinueProbingAfterEvent(bonusOfferRevision);
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
            if (IsGeometricSearch() && TryReplanGeometricCandidates(refinementEvent, $"Non-local refinement at {activeCandidateKey?.Label}"))
            {
                return;
            }

            AdvanceCandidate($"Treasure candidate {activeCandidateKey?.Label} returned a non-local hint ({refinementEvent.DistanceBucket} {refinementEvent.Direction}) during refinement; trying the next candidate.");
            return;
        }

        if (confirmedCofferVerificationUsed)
        {
            confirmedCofferVerificationUsed = false;
            if (TrySwitchToConfiguredAlternate(activeCandidate, refinementEvent))
            {
                return;
            }
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

        refinementStepIndex++;

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

        logger.Info($"{BuildLogTag()} op=refine-move candidate={activeCandidateKey?.Label ?? "none"} stepIndex={refinementStepIndex}/{MaximumCandidateRefinementSteps} distance={refinementEvent.DistanceBucket} direction={refinementEvent.Direction} resolvedDirection={movePlan.Direction} raw=<{movePlan.RawTarget.X:0.0}, {movePlan.RawTarget.Y:0.0}, {movePlan.RawTarget.Z:0.0}> resolved=<{target.X:0.0}, {target.Y:0.0}, {target.Z:0.0}> snapMethod={movePlan.SnapMethod} snapRadius={movePlan.SnapRadius:0} actualTarget={targetDistance:0.0}y");
        // Keep the arrival radius below the requested displacement. Passing an equal radius
        // lets the movement controller report arrival without moving from the current position.
        var refinementArrivalTolerance = MathF.Min(
            Math.Max(2.5f, 8f / 2f),
            MathF.Max(1.5f, targetDistance - 0.5f));
        if (!TryStartRefinementMove(activeCandidate, target, refinementArrivalTolerance, $"Treasure candidate {activeCandidate.Label} local refinement {refinementStepIndex}/{MaximumCandidateRefinementSteps}", targetAlreadyResolved: true))
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

        if (IsGeometricSearch())
        {
            var geometricHint = hintSnapshot.LastHintEvent ?? hintSnapshot.InitialHintEvent;
            if (geometricHint == null)
            {
                return false;
            }

            return TryReplanGeometricCandidates(geometricHint, $"Hint revision {hintSnapshot.Revision} received during candidate travel");
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
            logger.Warning($"{BuildLogTag(hintSnapshot.SessionId)} op=hint-handoff-declined revision={hintSnapshot.Revision} direction={hintSnapshot.LastHintEvent?.Direction ?? hintSnapshot.InitialHintEvent?.Direction ?? TreasureDirection.Unknown} priorGroup={ActiveGroupKey} proposedGroup={groupKey} targetFate=\"{activeFateName}\" ({activeFateId}) candidateCount=0 handoff=declined flow=continue reason=missing-mapped-group");
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
            logger.Info($"{BuildLogTag(hintSnapshot.SessionId)} op=hint-handoff-declined revision={hintSnapshot.Revision} direction={hintSnapshot.LastHintEvent?.Direction ?? hintSnapshot.InitialHintEvent?.Direction ?? TreasureDirection.Unknown} priorGroup={ActiveGroupKey} proposedGroup={groupKey} targetFate=\"{activeFateName}\" ({activeFateId}) candidateCount=0 handoff=declined flow=continue reason=no-eligible-candidates");
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
            activeCandidateApproachWaypointIndex = -1;
            consumedHintRevision = hintSnapshot.Revision;
            lastHandoffReason = $"Handoff to treasure group {group.GroupKey} from hint revision {hintSnapshot.Revision}.";
            ClearCandidateTravelProgressTracking();
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
            refinementMoveRecoveryAttemptCount = 0;
            refinementCandidateLocked = false;
            mappedPointRetryUsed = false;
            confirmedCofferVerificationUsed = false;
            confirmedCofferVerificationMovePending = false;
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
            activeCandidateUsesOverride = false;
            activeCandidateResolvedPosition = Vector3.Zero;
            revealedCofferLatched = false;
        }

        logger.ResetThrottle("treasure-search-travel");
        return BeginCurrentCandidate(lastHandoffReason);
    }

    private bool TryReplanGeometricCandidates(TreasureHintEvent hintEvent, string reason)
    {
        if (hintEvent.Direction == TreasureDirection.Unknown
            || !TryGetGroup(activeFateId, "geometry", out var group))
        {
            lock (gate)
            {
                consumedHintRevision = Math.Max(consumedHintRevision, hintEvent.Revision);
            }

            return false;
        }

        var observationPosition = hintEvent.ObservationPosition != Vector3.Zero
            ? hintEvent.ObservationPosition
            : Plugin.ObjectTable.LocalPlayer?.Position ?? traversalOriginCenter;
        var observations = geometricObservations.ToList();
        if (!observations.Any(observation => observation.Position == observationPosition && observation.Direction == hintEvent.Direction))
        {
            observations.Add(new TreasureHintObservation(observationPosition, hintEvent.Direction));
        }

        var runOrderedCandidates = BuildGeometricCandidates(group, observations, observationPosition);
        if (runOrderedCandidates.Count == 0)
        {
            if (dangerousTreasureTravelController.IsRunning)
            {
                dangerousTreasureTravelController.Stop("Geometric treasure search has no consistent candidates.");
            }

            movementController.Stop("Geometric treasure search has no consistent candidates.");
            SetFailure($"Geometric treasure search has no candidates consistent with hint revision {hintEvent.Revision}.");
            return true;
        }

        if (dangerousTreasureTravelController.IsRunning)
        {
            dangerousTreasureTravelController.Stop("Geometric treasure hint replan.");
        }

        movementController.Stop("Geometric treasure hint replan.");
        lock (gate)
        {
            geometricObservations.Clear();
            geometricObservations.AddRange(observations);
            orderedCandidates.Clear();
            orderedCandidates.AddRange(runOrderedCandidates);
            currentCandidateIndex = 0;
            activeCandidateApproachWaypointIndex = -1;
            consumedHintRevision = Math.Max(consumedHintRevision, hintEvent.Revision);
            lastHandoffReason = $"{reason}; geometrically ranked {runOrderedCandidates.Count} remaining candidate(s).";
            ClearCandidateTravelProgressTracking();
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
            refinementMoveRecoveryAttemptCount = 0;
            refinementCandidateLocked = false;
            mappedPointRetryUsed = false;
            confirmedCofferVerificationUsed = false;
            confirmedCofferVerificationMovePending = false;
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
            activeCandidateUsesOverride = false;
            activeCandidateResolvedPosition = Vector3.Zero;
            revealedCofferLatched = false;
        }

        logger.Info($"{BuildLogTag()} op=geometric-replan revision={hintEvent.Revision} direction={hintEvent.Direction} observation=<{observationPosition.X:0.0}, {observationPosition.Y:0.0}, {observationPosition.Z:0.0}> observations={observations.Count} candidates={runOrderedCandidates.Count}");
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
        if (orderedCandidates.Count == 0 || ActiveCandidateKey == null)
        {
            return false;
        }

        var activeCandidateKey = ActiveCandidateKey;
        if (activeCandidateKey == null)
        {
            return false;
        }

        if (!TryFindNearbyPotRevealCoffer(out var coffer, out var recognitionSource, out var objectKind))
        {
            return false;
        }

        if (!TryGetCurrentCandidate(out var activeCandidate))
        {
            return false;
        }

        var activePosition = ActiveCandidateResolvedPosition != Vector3.Zero
            ? ActiveCandidateResolvedPosition
            : ResolveCandidatePosition(activeCandidate);
        coffer = NormalizePotRevealCofferPosition(coffer, activePosition);
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

        if (!coffer.IsTargetable)
        {
            logger.DebugThrottled(
                $"pot-reveal-match-wait-{activeCandidateKey.CandidateKey}",
                TimeSpan.FromSeconds(1),
                $"Pot reveal coffer {coffer.Name} for candidate {activeCandidateKey.Label} was recognized via {recognitionSource} as {objectKind}, but it is not targetable yet. playerDistance={coffer.DistanceToPlayer:0.0}y candidateDistance={distanceToActive:0.0}y.");
            return false;
        }

        logger.Info($"{BuildLogTag()} op=pot-reveal-match candidate={activeCandidateKey.Label} source={source} recognition={recognitionSource} objectKind={objectKind} baseId={coffer.DataId} objectId={coffer.GameObjectId:X} playerDistance={coffer.DistanceToPlayer:0.0}y candidateDistance={distanceToActive:0.0}y");

        CompleteWithVisibleCoffer(
            coffer,
            activeCandidateKey,
            distanceToActive,
            isTrustworthy,
            nearestOtherDistance,
            $"{reason} source={source} recognition={recognitionSource} objectKind={objectKind}.");
        return true;
    }

    private bool TryFindNearbyPotRevealCoffer(out VisibleCoffer coffer, out string recognitionSource, out string objectKind)
    {
        coffer = new VisibleCoffer();
        recognitionSource = string.Empty;
        objectKind = string.Empty;

        var playerPosition = Plugin.ObjectTable.LocalPlayer?.Position;
        if (playerPosition == null)
        {
            return false;
        }

        VisibleCoffer? bestMatch = null;
        string bestRecognitionSource = string.Empty;
        string bestObjectKind = string.Empty;
        var bestDistance = float.MaxValue;
        var bestTargetable = false;

        foreach (var gameObject in Plugin.ObjectTable)
        {
            if (gameObject is not Dalamud.Game.ClientState.Objects.Types.IGameObject objectEntry)
            {
                continue;
            }

            if (!objectEntry.IsValid())
            {
                continue;
            }

            var territory = scanner.ActiveTerritoryData;
            var nextRecognitionSource = string.Empty;
            if (territory == null || !CofferRecognition.TryRecognizePotReveal(territory.VisibleCoffers, objectEntry, out nextRecognitionSource))
            {
                continue;
            }

            var distanceToPlayer = CalculateFlatDistance(playerPosition.Value, objectEntry.Position);
            if (distanceToPlayer > PotRevealCofferScanRadius)
            {
                continue;
            }

            var targetable = objectEntry.IsTargetable;
            if (bestMatch != null)
            {
                if (targetable != bestTargetable)
                {
                    if (!targetable)
                    {
                        continue;
                    }
                }
                else if (distanceToPlayer >= bestDistance)
                {
                    continue;
                }
            }

            bestTargetable = targetable;
            bestDistance = distanceToPlayer;
            bestRecognitionSource = nextRecognitionSource;
            bestObjectKind = objectEntry.ObjectKind.ToString();
            bestMatch = new VisibleCoffer
            {
                GameObjectId = objectEntry.GameObjectId,
                DataId = objectEntry.BaseId,
                Name = objectEntry.Name.ToString(),
                ObjectKind = bestObjectKind,
                RecognitionSource = nextRecognitionSource,
                Position = objectEntry.Position,
                DistanceToPlayer = distanceToPlayer,
                IsTargetable = targetable,
            };
        }

        if (bestMatch == null)
        {
            return false;
        }

        coffer = bestMatch;
        recognitionSource = bestRecognitionSource;
        objectKind = bestObjectKind;
        return true;
    }

    private static VisibleCoffer NormalizePotRevealCofferPosition(VisibleCoffer coffer, Vector3 fallbackPosition)
    {
        if (MathF.Abs(coffer.Position.Y + 500f) >= 0.5f)
        {
            return coffer;
        }

        return new VisibleCoffer
        {
            GameObjectId = coffer.GameObjectId,
            DataId = coffer.DataId,
            Name = coffer.Name,
            ObjectKind = coffer.ObjectKind,
            RecognitionSource = coffer.RecognitionSource,
            Position = new Vector3(coffer.Position.X, fallbackPosition.Y, coffer.Position.Z),
            DistanceToPlayer = coffer.DistanceToPlayer,
            IsTargetable = coffer.IsTargetable,
        };
    }

    private void CompleteWithVisibleCoffer(VisibleCoffer coffer, TreasureCandidateKey candidateKey, float matchDistance, bool isTrustworthy, float nearestOtherDistance, string reason)
    {
        if (dangerousTreasureTravelController.IsRunning)
        {
            dangerousTreasureTravelController.Stop($"Visible coffer matched during dangerous travel for {candidateKey.Label}.");
            dangerousTreasureTravelController.AcknowledgeTerminalState("TreasureSearch");
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
                Flow = CofferInteractionFlow.PotReveal,
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
            activeCandidateApproachWaypointIndex = -1;
            candidateGeneration++;
            ClearCandidateTravelProgressTracking();
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
            refinementMoveRecoveryAttemptCount = 0;
            refinementCandidateLocked = false;
            mappedPointRetryUsed = false;
            confirmedCofferVerificationUsed = false;
            confirmedCofferVerificationMovePending = false;
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
        var exactCandidatePosition = IsGeometricSearch();
        var overridePosition = Vector3.Zero;
        var usedOverride = !exactCandidatePosition && cofferPositionOverrideStore.TryResolvePosition(candidateKey, out overridePosition);
        var targetPosition = usedOverride ? overridePosition : canonicalPosition;
        ForayThreatEntity? threat = null;
        var hideAtOrAbove = 0;
        var hasKnowledgeThreat = configuration.UseNinjaForDangerousArea
            && TryGetPotKnowledgeThreat(configuration.KnowledgeThreatEnterDistance, out threat, out hideAtOrAbove);
        var isDangerousCandidate = configuration.UseNinjaForDangerousArea
            ? !scanner.Snapshot.PlayerForayLevel.HasValue && IsDangerousCandidate(candidate)
            : IsAbovePotTreasureAggroLimit(candidate);
        var requiresDangerousTravel = isDangerousCandidate || hasKnowledgeThreat;
        var destination = exactCandidatePosition
            ? targetPosition
            : movementController.FindNearestNavigablePoint(targetPosition, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!destination.HasValue)
        {
            var navFailureReason = $"Treasure candidate {candidate.Label} has no reliable vnavmesh point near <{targetPosition.X:0.0}, {targetPosition.Y:0.0}, {targetPosition.Z:0.0}>.";
            if (requiresDangerousTravel)
            {
                return SkipDangerousCandidate(navFailureReason);
            }

            AdvanceCandidate(navFailureReason);
            return false;
        }

        logger.Info($"{BuildLogTag()} op=candidate-start candidate={candidate.Label} fate=\"{activeFateName}\" ({activeFateId}) group={candidate.GroupKey} dangerous={requiresDangerousTravel} staticFallbackDangerous={isDangerousCandidate} knowledgeThreat={hasKnowledgeThreat} threatEntity='{threat?.Name ?? "none"}' threatLevel={threat?.KnowledgeLevel ?? 0} hideAtOrAbove={hideAtOrAbove} override={usedOverride} target=<{targetPosition.X:0.0}, {targetPosition.Y:0.0}, {targetPosition.Z:0.0}> destination=<{destination.Value.X:0.0}, {destination.Value.Y:0.0}, {destination.Value.Z:0.0}> reason={reason}");
        activeCandidateApproachWaypointIndex = candidate.ApproachWaypoints.Count > 0 ? 0 : -1;
        if (activeCandidateApproachWaypointIndex >= 0)
        {
            if (!TryStartCandidateApproachWaypoint(candidate, activeCandidateApproachWaypointIndex))
            {
                SetFailure($"Failed to start approach waypoint {activeCandidateApproachWaypointIndex + 1} for treasure candidate {candidate.Label}.");
                return false;
            }
        }
        else if (!TryStartFinalCandidateTravel(candidate, destination.Value, requiresDangerousTravel, hasKnowledgeThreat, exactCandidatePosition))
        {
            return false;
        }

        var currentPlayerPosition = Plugin.ObjectTable.LocalPlayer?.Position;
        var initialTravelDistance = currentPlayerPosition.HasValue
            ? CalculateFlatDistance(currentPlayerPosition.Value, candidateTravelTarget)
            : float.MaxValue;
        lock (gate)
        {
            candidateGeneration++;
            handledCandidateLabels.Add(candidate.Label);
            activeCandidateKey = candidateKey;
            candidateTravelLastProgressAt = DateTimeOffset.UtcNow;
            candidateTravelProgressDistance = initialTravelDistance;
            candidateTravelLastObservedPosition = currentPlayerPosition ?? Vector3.Zero;
            hasCandidateTravelObservedPosition = currentPlayerPosition.HasValue;
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
            refinementMoveRecoveryAttemptCount = 0;
            refinementCandidateLocked = false;
            mappedPointRetryUsed = false;
            confirmedCofferVerificationUsed = false;
            confirmedCofferVerificationMovePending = false;
            activeVisibleCofferMatch = null;
            activeCandidateUsesOverride = usedOverride;
            activeCandidateResolvedPosition = targetPosition;
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
            revealedCofferLatched = false;
            lastNavmeshRejectionSummary = string.Empty;
            pendingCandidateAdvanceReason = string.Empty;
            activeCandidateProbeOperationId = string.Empty;
            activeRefinementProbeOperationId = string.Empty;
        }

        TransitionTo(
            TreasureSearchState.TravelingToCandidate,
            $"{reason} Moving to treasure candidate {candidate.Label} in group {candidate.GroupKey} using {(usedOverride ? "override" : "canonical")} position{(requiresDangerousTravel ? " with Ninja/Hide dangerous-area flow" : string.Empty)}.");
        return true;
    }

    private KnowledgeThreatPolicy GetPotKnowledgeThreatPolicy()
        => new(
            configuration.PotKnowledgeHideOffset,
            configuration.KnowledgeThreatEnterDistance,
            configuration.KnowledgeThreatExitDistance,
            scanner.ActiveTerritoryData?.MaximumKnowledgeLevel ?? 28);

    private bool TryGetPotKnowledgeThreat(float radius, out ForayThreatEntity? threat, out int hideAtOrAbove)
        => KnowledgeThreatEvaluator.TryFindThreat(scanner.Snapshot, GetPotKnowledgeThreatPolicy(), radius, out threat, out hideAtOrAbove);

    private bool TryStartKnowledgeThreatTravel()
    {
        if (!configuration.UseNinjaForDangerousArea
            || dangerousTreasureTravelController.IsRunning
            || activeCandidateApproachWaypointIndex >= 0
            || !TryGetPotKnowledgeThreat(configuration.KnowledgeThreatEnterDistance, out var threat, out var hideAtOrAbove)
            || !TryGetCurrentCandidate(out var candidate))
        {
            return false;
        }

        var exactCandidatePosition = IsGeometricSearch();
        var destination = exactCandidatePosition
            ? activeCandidateResolvedPosition
            : movementController.FindNearestNavigablePoint(activeCandidateResolvedPosition, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!destination.HasValue)
        {
            SetFailure($"Treasure candidate {candidate.Label} has no reliable vnavmesh point while responding to a live knowledge threat.");
            return true;
        }

        movementController.Stop("Live knowledge threat entered the pot treasure Hide range.");
        logger.Info($"{BuildLogTag()} op=knowledge-threat-enter mode=pot-reveal candidate={candidate.Label} entity='{threat?.Name ?? "unknown"}' objectId={threat?.ObjectId:X} playerForayLevel={scanner.Snapshot.PlayerForayLevel?.ToString() ?? "unavailable"} offset={configuration.PotKnowledgeHideOffset} entityLevel={threat?.KnowledgeLevel ?? 0} hideAtOrAbove={hideAtOrAbove} enterRange={configuration.KnowledgeThreatEnterDistance:0.0} exitRange={configuration.KnowledgeThreatExitDistance:0.0} distance={threat?.DistanceToPlayer:0.0}");
        if (!dangerousTreasureTravelController.Start("TreasureSearch", GetTraversalPreviousCandidate(CurrentCandidateIndex), candidate, destination.Value, CandidateArrivalTolerance, GetDangerousTravelOptions(exactCandidatePosition), GetPotKnowledgeThreatPolicy()))
        {
            SetFailure(dangerousTreasureTravelController.LastError.Length == 0
                ? $"Failed to start Ninja/Hide travel after detecting a live knowledge threat for {candidate.Label}."
                : dangerousTreasureTravelController.LastError);
        }

        return true;
    }

    private bool TryStartCandidateApproachWaypoint(TreasureCofferCandidateData candidate, int waypointIndex)
    {
        if (waypointIndex < 0 || waypointIndex >= candidate.ApproachWaypoints.Count)
        {
            return false;
        }

        var waypoint = candidate.ApproachWaypoints[waypointIndex];
        var canonicalPosition = waypoint.Position.ToVector3();
        var destination = movementController.FindNearestNavigablePoint(canonicalPosition, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!destination.HasValue)
        {
            logger.Warning($"{BuildLogTag()} op=approach-waypoint-unreachable candidate={candidate.Label} index={waypointIndex + 1}/{candidate.ApproachWaypoints.Count} canonical=<{canonicalPosition.X:0.0}, {canonicalPosition.Y:0.0}, {canonicalPosition.Z:0.0}>");
            return false;
        }

        var arrivalDistance = waypoint.ArrivalDistance ?? CandidateArrivalTolerance;
        candidateTravelTarget = destination.Value;
        logger.Info($"{BuildLogTag()} op=approach-waypoint-start candidate={candidate.Label} index={waypointIndex + 1}/{candidate.ApproachWaypoints.Count} canonical=<{canonicalPosition.X:0.0}, {canonicalPosition.Y:0.0}, {canonicalPosition.Z:0.0}> destination=<{destination.Value.X:0.0}, {destination.Value.Y:0.0}, {destination.Value.Z:0.0}> arrivalDistance={arrivalDistance:0.0}");
        if (!movementController.StartDirectMove($"Treasure candidate {candidate.Label} approach waypoint {waypointIndex + 1}", destination.Value, arrivalDistance))
        {
            logger.Warning($"{BuildLogTag()} op=approach-waypoint-start-failed candidate={candidate.Label} index={waypointIndex + 1}/{candidate.ApproachWaypoints.Count} error={movementController.LastError}");
            return false;
        }

        return true;
    }

    private bool TryContinueCandidateApproachTravel()
    {
        if (activeCandidateApproachWaypointIndex < 0 || !TryGetCurrentCandidate(out var candidate))
        {
            return false;
        }

        var nextWaypointIndex = activeCandidateApproachWaypointIndex + 1;
        if (nextWaypointIndex < candidate.ApproachWaypoints.Count)
        {
            activeCandidateApproachWaypointIndex = nextWaypointIndex;
            if (TryStartCandidateApproachWaypoint(candidate, nextWaypointIndex))
            {
                ResetCandidateTravelProgressTracking(candidateTravelTarget);
                return true;
            }

            SetFailure($"Failed to start approach waypoint {nextWaypointIndex + 1} for treasure candidate {candidate.Label}.");
            return true;
        }

        activeCandidateApproachWaypointIndex = -1;
        var hasKnowledgeThreat = configuration.UseNinjaForDangerousArea
            && TryGetPotKnowledgeThreat(configuration.KnowledgeThreatEnterDistance, out _, out _);
        var requiresDangerousTravel = configuration.UseNinjaForDangerousArea
            ? (!scanner.Snapshot.PlayerForayLevel.HasValue && IsDangerousCandidate(candidate)) || hasKnowledgeThreat
            : IsAbovePotTreasureAggroLimit(candidate);
        var exactCandidatePosition = IsGeometricSearch();
        var destination = exactCandidatePosition
            ? activeCandidateResolvedPosition
            : movementController.FindNearestNavigablePoint(activeCandidateResolvedPosition, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!destination.HasValue)
        {
            SetFailure($"Treasure candidate {candidate.Label} has no reliable vnavmesh point near <{activeCandidateResolvedPosition.X:0.0}, {activeCandidateResolvedPosition.Y:0.0}, {activeCandidateResolvedPosition.Z:0.0}> after its approach waypoint.");
            return true;
        }

        if (!TryStartFinalCandidateTravel(candidate, destination.Value, requiresDangerousTravel, hasKnowledgeThreat, exactCandidatePosition))
        {
            return true;
        }

        ResetCandidateTravelProgressTracking(candidateTravelTarget);
        return true;
    }

    private bool TryStartFinalCandidateTravel(TreasureCofferCandidateData candidate, Vector3 destination, bool requiresDangerousTravel, bool hasKnowledgeThreat, bool destinationAlreadyResolved)
    {
        candidateTravelTarget = destination;
        if (requiresDangerousTravel)
        {
            if (!configuration.UseNinjaForDangerousArea)
            {
                SkipDangerousCandidate($"Skipping dangerous treasure candidate {candidate.Label} because it requires dangerous-area Ninja travel and Ninja travel is disabled.");
                return false;
            }

            KnowledgeThreatPolicy? knowledgeThreatPolicy = hasKnowledgeThreat ? GetPotKnowledgeThreatPolicy() : null;
            if (!dangerousTreasureTravelController.Start("TreasureSearch", GetTraversalPreviousCandidate(CurrentCandidateIndex), candidate, destination, CandidateArrivalTolerance, GetDangerousTravelOptions(destinationAlreadyResolved), knowledgeThreatPolicy))
            {
                if (dangerousTreasureTravelController.LastResult == DangerousTreasureTravelResult.CandidateSkipped)
                {
                    SkipDangerousCandidate(dangerousTreasureTravelController.LastTransition);
                    return false;
                }

                SetFailure(dangerousTreasureTravelController.LastError.Length == 0
                    ? $"Failed to start dangerous travel for treasure candidate {candidate.Label}."
                    : dangerousTreasureTravelController.LastError);
                return false;
            }

            return true;
        }

        if (movementController.StartDirectMove($"Treasure candidate {candidate.Label} for {activeFateName}", destination, CandidateArrivalTolerance, destinationAlreadyResolved: destinationAlreadyResolved))
        {
            return true;
        }

        SetFailure(movementController.LastError.Length == 0
            ? $"Failed to start movement to treasure candidate {candidate.Label}."
            : movementController.LastError);
        return false;
    }

    private Vector3 ResolveCandidatePosition(TreasureCofferCandidateData candidate)
    {
        if (IsGeometricSearch())
        {
            return candidate.Position.ToVector3();
        }

        var candidateKey = ToCandidateKey(candidate);
        return cofferPositionOverrideStore.TryResolvePosition(candidateKey, out var overridePosition)
            ? overridePosition
            : candidate.Position.ToVector3();
    }

    private bool TryHandleDangerousTravelTerminalResult()
    {
        if (!dangerousTreasureTravelController.IsTerminalStateOwnedBy("TreasureSearch"))
        {
            return false;
        }

        switch (dangerousTreasureTravelController.State)
        {
            case DangerousTreasureTravelState.Arrived:
                var arriveReason = dangerousTreasureTravelController.LastTransition;
                dangerousTreasureTravelController.AcknowledgeTerminalState("TreasureSearch");
                lock (gate)
                {
                    ClearCandidateTravelProgressTracking();
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
                    refinementMoveRecoveryAttemptCount = 0;
                    refinementCandidateLocked = false;
                    mappedPointRetryUsed = false;
                }

                logger.ResetThrottle("treasure-search-probe");
                TransitionTo(TreasureSearchState.ProbingCandidate, $"{arriveReason} No visible coffer was found at treasure candidate {activeCandidateKey?.Label}; starting local Magical Elixir probing.");
                return true;
            case DangerousTreasureTravelState.CandidateSkipped:
                var skipReason = dangerousTreasureTravelController.LastTransition;
                dangerousTreasureTravelController.AcknowledgeTerminalState("TreasureSearch");
                AdvanceCandidate(skipReason);
                return true;
            case DangerousTreasureTravelState.Failed:
                var failureReason = dangerousTreasureTravelController.LastError.Length == 0
                    ? dangerousTreasureTravelController.LastTransition
                    : dangerousTreasureTravelController.LastError;
                dangerousTreasureTravelController.AcknowledgeTerminalState("TreasureSearch");
                SetFailure(failureReason);
                return true;
            case DangerousTreasureTravelState.Stopped:
                var stoppedReason = dangerousTreasureTravelController.LastError.Length == 0
                    ? dangerousTreasureTravelController.LastTransition
                    : dangerousTreasureTravelController.LastError;
                dangerousTreasureTravelController.AcknowledgeTerminalState("TreasureSearch");
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
            candidateGeneration++;
            ClearCandidateTravelProgressTracking();
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
            refinementMoveRecoveryAttemptCount = 0;
            refinementCandidateLocked = false;
            mappedPointRetryUsed = false;
            activeVisibleCofferMatch = null;
            activeCandidateKey = null;
            activeCandidateUsesOverride = false;
            activeCandidateResolvedPosition = Vector3.Zero;
            revealedCofferAcquireDeadlineAt = DateTimeOffset.MinValue;
            revealedCofferLatched = false;
            activeCandidateProbeOperationId = string.Empty;
            activeRefinementProbeOperationId = string.Empty;
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
        var groupsByFateId = GetGroupsByFateId();
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
            refinementMoveRecoveryAttemptCount = 0;
            refinementCandidateLocked = IsImmediateTreasureDistance(latestEvent.DistanceBucket);
            mappedPointRetryUsed = false;
            candidateProbeDeadlineAt = DateTimeOffset.MinValue;
            candidateProbeLastAttemptAt = DateTimeOffset.MinValue;
        }

        logger.ResetThrottle("treasure-search-refine");
        logger.Info($"{BuildLogTag()} op=refinement-start refinementStep=0 candidate={activeCandidateKey?.Label ?? "none"} sourceEventRevision={latestEvent.Revision} direction={latestEvent.Direction} distance={latestEvent.DistanceBucket}");
        TransitionTo(TreasureSearchState.RefiningCandidate, $"Treasure candidate {activeCandidateKey?.Label} produced a local hint ({latestEvent.DistanceBucket} {latestEvent.Direction}); starting local refinement.");
    }

    private bool TryBeginConfirmedCofferVerification(TreasureCofferCandidateData candidate, TreasureHintEvent hintEvent)
    {
        var alternateKeys = GetConfiguredAlternateKeys(candidate, hintEvent.DistanceBucket);
        if (alternateKeys.Count == 0 || candidate.ConfirmedCofferPosition is not { } confirmedCofferPosition)
        {
            return false;
        }

        var confirmedPosition = confirmedCofferPosition.ToVector3();
        var destination = movementController.FindNearestNavigablePoint(confirmedPosition, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!destination.HasValue)
        {
            logger.Info($"{BuildLogTag()} op=confirmed-coffer-verify-declined candidate={candidate.Label} distance={hintEvent.DistanceBucket} reason=no-navigable-confirmed-position action=normal-refinement");
            return false;
        }

        var playerPosition = Plugin.ObjectTable.LocalPlayer?.Position ?? destination.Value;
        var targetDistance = CalculateFlatDistance(playerPosition, destination.Value);
        lock (gate)
        {
            refinementEvent = null;
            refinementProbeDeadlineAt = DateTimeOffset.MinValue;
            refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
            refinementProbeAttemptCount = 0;
            refinementProbeBaselineSessionId = 0;
            refinementProbeBaselineRevision = 0;
            refinementMoveDeadlineAt = DateTimeOffset.MinValue;
            refinementStepIndex = 0;
            refinementMoveRecoveryAttemptCount = 0;
            refinementCandidateLocked = true;
            mappedPointRetryUsed = true;
            confirmedCofferVerificationUsed = targetDistance <= LocalMoveSkipDistance;
            confirmedCofferVerificationMovePending = targetDistance > LocalMoveSkipDistance;
            candidateProbeDeadlineAt = DateTimeOffset.MinValue;
            candidateProbeLastAttemptAt = DateTimeOffset.MinValue;
        }

        logger.Info($"{BuildLogTag()} op=confirmed-coffer-verify candidate={candidate.Label} sourceDistance={hintEvent.DistanceBucket} confirmed=<{confirmedPosition.X:0.0}, {confirmedPosition.Y:0.0}, {confirmedPosition.Z:0.0}> destination=<{destination.Value.X:0.0}, {destination.Value.Y:0.0}, {destination.Value.Z:0.0}> travelDistance={targetDistance:0.0}y alternates=[{string.Join(", ", alternateKeys)}]");
        TransitionTo(TreasureSearchState.RefiningCandidate, $"Treasure candidate {candidate.Label} produced {hintEvent.DistanceBucket}; verifying its confirmed coffer position before considering configured alternates.");

        if (targetDistance <= LocalMoveSkipDistance)
        {
            return true;
        }

        TryStartRefinementMove(
            candidate,
            destination.Value,
            Math.Max(2.5f, LocalMoveSkipDistance),
            $"Treasure candidate {candidate.Label} confirmed coffer verification",
            targetAlreadyResolved: true);
        return true;
    }

    private bool TrySwitchToConfiguredAlternate(TreasureCofferCandidateData currentCandidate, TreasureHintEvent hintEvent)
    {
        var alternateKeys = GetConfiguredAlternateKeys(currentCandidate, hintEvent.DistanceBucket);
        if (alternateKeys.Count == 0
            || hintEvent.Direction == TreasureDirection.Unknown
            || !TryGetGroup(activeFateId, currentCandidate.GroupKey, out var group))
        {
            return false;
        }

        var alternatives = group.Candidates
            .Where(candidate => alternateKeys.Contains(candidate.CandidateKey, StringComparer.OrdinalIgnoreCase))
            .Where(candidate => !IsHandledCandidate(candidate.Label))
            .Where(candidate => configuration.UseNinjaForDangerousArea || !IsAbovePotTreasureAggroLimit(candidate))
            .ToArray();
        if (alternatives.Length == 0)
        {
            logger.Info($"{BuildLogTag()} op=configured-alternate-declined candidate={currentCandidate.Label} distance={hintEvent.DistanceBucket} reason=no-unhandled-eligible-alternate action=normal-refinement");
            return false;
        }

        var observationPosition = hintEvent.ObservationPosition != Vector3.Zero
            ? hintEvent.ObservationPosition
            : Plugin.ObjectTable.LocalPlayer?.Position ?? currentCandidate.ConfirmedCofferPosition?.ToVector3() ?? currentCandidate.Position.ToVector3();
        var rankedAlternatives = GeometricTreasureCandidatePlanner.Rank(
            alternatives,
            [new TreasureHintObservation(observationPosition, hintEvent.Direction)],
            handledCandidateLabels,
            observationPosition,
            ConfiguredAlternateMaximumHintAngleDegrees);
        if (rankedAlternatives.Count == 0)
        {
            logger.Info($"{BuildLogTag()} op=configured-alternate-declined candidate={currentCandidate.Label} distance={hintEvent.DistanceBucket} direction={hintEvent.Direction} reason=no-directionally-consistent-alternate action=normal-refinement");
            return false;
        }

        var alternate = rankedAlternatives[0];
        lock (gate)
        {
            var priorCurrentIndex = currentCandidateIndex;
            var alternateIndex = orderedCandidates.FindIndex(candidate => string.Equals(candidate.CandidateKey, alternate.CandidateKey, StringComparison.OrdinalIgnoreCase));
            if (alternateIndex >= 0)
            {
                orderedCandidates.RemoveAt(alternateIndex);
                if (alternateIndex < priorCurrentIndex)
                {
                    priorCurrentIndex--;
                }
            }

            alternateIndex = Math.Min(priorCurrentIndex + 1, orderedCandidates.Count);
            orderedCandidates.Insert(alternateIndex, alternate);
            currentCandidateIndex = alternateIndex;
            activeCandidateApproachWaypointIndex = -1;
            lastHandoffReason = $"Verified {hintEvent.DistanceBucket} handoff from {currentCandidate.Label} to configured alternate {alternate.Label}.";
        }

        if (dangerousTreasureTravelController.IsRunning)
        {
            dangerousTreasureTravelController.Stop($"Configured treasure alternate handoff to {alternate.Label}.");
        }

        if (movementController.IsPathBusy)
        {
            movementController.Stop($"Configured treasure alternate handoff to {alternate.Label}.");
        }

        logger.Info($"{BuildLogTag()} op=configured-alternate fromCandidate={currentCandidate.Label} toCandidate={alternate.Label} distance={hintEvent.DistanceBucket} direction={hintEvent.Direction} observation=<{observationPosition.X:0.0}, {observationPosition.Y:0.0}, {observationPosition.Z:0.0}> action=travel-alternate");
        return BeginCurrentCandidate(lastHandoffReason);
    }

    private static IReadOnlyList<string> GetConfiguredAlternateKeys(TreasureCofferCandidateData candidate, string distanceBucket)
        => IsImmediateTreasureDistance(distanceBucket)
            ? candidate.ImmediateAlternateCandidateKeys
            : string.Equals(distanceBucket, "close", StringComparison.Ordinal)
                ? candidate.CloseAlternateCandidateKeys
                : [];

    private bool TickRefinementMovement(TreasureCofferCandidateData activeCandidate)
    {
        switch (dangerousTreasureTravelController.State)
        {
            case DangerousTreasureTravelState.Arrived:
            dangerousTreasureTravelController.AcknowledgeTerminalState("TreasureSearch");
                if (confirmedCofferVerificationMovePending)
                {
                    confirmedCofferVerificationMovePending = false;
                    confirmedCofferVerificationUsed = true;
                }
                var dangerousHandoffResult = TryApplyRefinementCandidateHandoff(Plugin.ObjectTable.LocalPlayer?.Position ?? activeCandidateResolvedPosition, "dangerous refinement arrival");
                refinementMoveDeadlineAt = DateTimeOffset.MinValue;
                refinementMoveRecoveryAttemptCount = 0;
                return dangerousHandoffResult != CandidateHandoffResult.DangerousTransitionStarted;
            case DangerousTreasureTravelState.CandidateSkipped:
                var skipReason = dangerousTreasureTravelController.LastTransition;
                dangerousTreasureTravelController.AcknowledgeTerminalState("TreasureSearch");
                refinementMoveDeadlineAt = DateTimeOffset.MinValue;
                AdvanceCandidate(skipReason);
                return false;
            case DangerousTreasureTravelState.Failed:
                var failureReason = dangerousTreasureTravelController.LastError.Length == 0
                    ? dangerousTreasureTravelController.LastTransition
                    : dangerousTreasureTravelController.LastError;
                dangerousTreasureTravelController.AcknowledgeTerminalState("TreasureSearch");
                refinementMoveDeadlineAt = DateTimeOffset.MinValue;
                AdvanceCandidate(failureReason);
                return false;
            case DangerousTreasureTravelState.Stopped:
                var stoppedReason = dangerousTreasureTravelController.LastError.Length == 0
                    ? dangerousTreasureTravelController.LastTransition
                    : dangerousTreasureTravelController.LastError;
                dangerousTreasureTravelController.AcknowledgeTerminalState("TreasureSearch");
                refinementMoveDeadlineAt = DateTimeOffset.MinValue;
                Stop(stoppedReason);
                return false;
        }

        if (dangerousTreasureTravelController.IsRunning)
        {
            logger.DebugThrottled(
                "treasure-search-refine",
                WaitLogInterval,
                $"Treasure refinement is running dangerous travel for candidate {activeCandidateKey?.Label}. DangerousState={dangerousTreasureTravelController.State} transition={dangerousTreasureTravelController.LastTransition}.");
            return false;
        }

        if (refinementMoveDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= refinementMoveDeadlineAt)
        {
            movementController.Stop("Treasure refinement move timed out.");
            TryRecoverFromRefinementMoveFailure($"Treasure candidate {activeCandidateKey?.Label} local refinement move timed out.");
            return false;
        }

        switch (movementController.State)
        {
            case MovementState.Arrived:
                movementController.Stop("Reached local treasure refinement target.");
                if (confirmedCofferVerificationMovePending)
                {
                    confirmedCofferVerificationMovePending = false;
                    confirmedCofferVerificationUsed = true;
                }
                var handoffResult = TryApplyRefinementCandidateHandoff(Plugin.ObjectTable.LocalPlayer?.Position ?? activeCandidateResolvedPosition, "local refinement arrival");
                refinementMoveDeadlineAt = DateTimeOffset.MinValue;
                refinementMoveRecoveryAttemptCount = 0;
                return handoffResult != CandidateHandoffResult.DangerousTransitionStarted;
            case MovementState.Failed:
                TryRecoverFromRefinementMoveFailure(movementController.LastError.Length == 0
                    ? $"Treasure candidate {activeCandidateKey?.Label} local refinement move failed."
                    : movementController.LastError);
                return false;
            case MovementState.TimedOut:
                movementController.Stop("Treasure refinement move timed out.");
                TryRecoverFromRefinementMoveFailure($"Treasure candidate {activeCandidateKey?.Label} local refinement move timed out.");
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

        var isDangerousCandidate = configuration.UseNinjaForDangerousArea
            ? (IsGeometricSearch() ? IsStaticDangerousCandidate(activeCandidate) : IsDangerousCandidate(activeCandidate))
            : IsAbovePotTreasureAggroLimit(activeCandidate);
        if (isDangerousCandidate)
        {
            if (!configuration.UseNinjaForDangerousArea)
            {
                AdvanceCandidate($"Skipping dangerous treasure refinement for candidate {activeCandidate.Label} because Ninja travel is disabled.");
                return false;
            }

            if (!dangerousTreasureTravelController.Start("TreasureSearch", GetTraversalPreviousCandidate(CurrentCandidateIndex), activeCandidate, resolvedDestination, arrivalTolerance, GetDangerousTravelOptions(targetAlreadyResolved)))
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

    private void TryRecoverFromRefinementMoveFailure(string failureReason)
    {
        if (refinementMoveRecoveryAttemptCount >= MaximumRefinementMoveRecoveryAttempts)
        {
            QueueCandidateAdvance(failureReason);
            return;
        }

        refinementMoveRecoveryAttemptCount++;
        refinementMoveDeadlineAt = DateTimeOffset.MinValue;
        refinementEvent = null;
        refinementProbeDeadlineAt = DateTimeOffset.MinValue;
        refinementProbeLastAttemptAt = DateTimeOffset.MinValue;
        refinementProbeAttemptCount = 0;
        confirmedCofferVerificationUsed = false;
        confirmedCofferVerificationMovePending = false;
        logger.Info($"{BuildLogTag()} op=refine-move-recover candidate={activeCandidateKey?.Label ?? "none"} attempt={refinementMoveRecoveryAttemptCount}/{MaximumRefinementMoveRecoveryAttempts} reason={failureReason} action=probe-current-position");
        logger.ResetThrottle("treasure-search-refine");
        logger.ResetThrottle("treasure-search-refine-probe");
        logger.ResetThrottle("treasure-search-refine-probe-retry");
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

            if (!configuration.UseNinjaForDangerousArea && IsAbovePotTreasureAggroLimit(candidate))
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
        var handoffIsDangerous = configuration.UseNinjaForDangerousArea
            ? !scanner.Snapshot.PlayerForayLevel.HasValue && IsDangerousCandidate(handoffCandidate)
            : IsAbovePotTreasureAggroLimit(handoffCandidate);
        var handoffKey = ToCandidateKey(handoffCandidate);
        var exactCandidatePosition = IsGeometricSearch();
        var overridePosition = Vector3.Zero;
        var usedOverride = !exactCandidatePosition && cofferPositionOverrideStore.TryResolvePosition(handoffKey, out overridePosition);
        var handoffResolvedPosition = usedOverride ? overridePosition : handoffCandidate.Position.ToVector3();
        lock (gate)
        {
            handledCandidateLabels.Add(currentCandidate.Label);
            handledCandidateLabels.Add(handoffCandidate.Label);
            currentCandidateIndex = bestIndex;
            activeCandidateApproachWaypointIndex = -1;
            candidateGeneration++;
            activeCandidateKey = handoffKey;
            activeCandidateUsesOverride = usedOverride;
            activeCandidateResolvedPosition = handoffResolvedPosition;
            mappedPointRetryUsed = false;
            confirmedCofferVerificationUsed = false;
            confirmedCofferVerificationMovePending = false;
            lastHandoffReason = $"Handoff from {currentCandidate.Label} to {handoffCandidate.Label} using {reason}.";
            activeCandidateProbeOperationId = string.Empty;
            activeRefinementProbeOperationId = string.Empty;
        }

        logger.Info($"{BuildLogTag()} op=handoff fromCandidate={currentCandidate.Label} toCandidate={handoffCandidate.Label} reason={reason} currentDistance={currentDistance:0.0}y handoffDistance={bestDistance:0.0}y advantage={(currentDistance - bestDistance):0.0}y");
        if (handoffCandidate.ApproachWaypoints.Count > 0)
        {
            movementController.Stop($"Treasure candidate handoff to {handoffCandidate.Label} approach waypoint.");
            activeCandidateApproachWaypointIndex = 0;
            if (!TryStartCandidateApproachWaypoint(handoffCandidate, activeCandidateApproachWaypointIndex))
            {
                SetFailure($"Failed to start approach waypoint 1 for handoff treasure candidate {handoffCandidate.Label}.");
            }
            else
            {
                ResetCandidateTravelProgressTracking(candidateTravelTarget);
            }

            return CandidateHandoffResult.DangerousTransitionStarted;
        }

        if (!handoffIsDangerous)
        {
            return CandidateHandoffResult.Updated;
        }

        var destination = exactCandidatePosition
            ? handoffResolvedPosition
            : movementController.FindNearestNavigablePoint(handoffResolvedPosition, halfExtentXZ: 5f, halfExtentY: 5f);
        if (!destination.HasValue)
        {
            AdvanceCandidate($"Treasure candidate {handoffCandidate.Label} handoff target has no reliable vnavmesh point near <{handoffResolvedPosition.X:0.0}, {handoffResolvedPosition.Y:0.0}, {handoffResolvedPosition.Z:0.0}>.");
            return CandidateHandoffResult.DangerousTransitionStarted;
        }

        if (!dangerousTreasureTravelController.Start("TreasureSearch", currentCandidate, handoffCandidate, destination.Value, CandidateArrivalTolerance, GetDangerousTravelOptions(exactCandidatePosition)))
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

        logger.Info($"{BuildLogTag()} op=handoff-dangerous fromCandidate={currentCandidate.Label} toCandidate={handoffCandidate.Label} context=handoff-source-as-previous-threshold reason={reason} action=start-dangerous-travel");
        return CandidateHandoffResult.DangerousTransitionStarted;
    }

    private CandidateHandoffResult TryApplyRefinementCandidateHandoff(Vector3 referencePosition, string reason)
    {
        if (refinementCandidateLocked)
        {
            logger.DebugThrottled(
                "treasure-search-refine-lock",
                WaitLogInterval,
                $"Treasure refinement is locked to candidate {activeCandidateKey?.Label}; suppressing handoff after {reason}.");
            return CandidateHandoffResult.None;
        }

        return TryApplyCandidateHandoff(referencePosition, reason);
    }

    private List<TreasureCofferCandidateData> BuildOrderedCandidates(TreasureCofferGroupData group, Vector3 originCenter)
    {
        var safeCandidates = new List<TreasureCofferCandidateData>();
        var dangerousCandidates = new List<TreasureCofferCandidateData>();
        if (!configuration.UseNinjaForDangerousArea)
        {
            logger.Info($"{BuildLogTag()} op=candidate-eligibility cutoff={GetPotTreasureAggroLimit()} source={GetPotTreasureAggroLimitSource()} knowledge={scanner.Snapshot.PlayerForayLevel?.ToString() ?? "unavailable"} offset={configuration.PotTreasureAggroLevelOffset} fallback={configuration.PotTreasureFallbackMaximumAggroLevel}");
        }

        foreach (var candidate in group.Candidates)
        {
            if (!configuration.UseNinjaForDangerousArea && IsAbovePotTreasureAggroLimit(candidate))
            {
                logger.Info($"{BuildLogTag()} op=candidate-ineligible candidate={candidate.Label} candidateAggro={candidate.AggroLevel} cutoff={GetPotTreasureAggroLimit()} source={GetPotTreasureAggroLimitSource()} reason=no-ninja-aggro-cutoff");
                continue;
            }

            if (configuration.UseNinjaForDangerousArea
                && !scanner.Snapshot.PlayerForayLevel.HasValue
                && IsDangerousCandidate(candidate))
            {
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

    private List<TreasureCofferCandidateData> BuildGeometricCandidates(
        TreasureCofferGroupData group,
        IReadOnlyList<TreasureHintObservation> observations,
        Vector3 currentPosition)
    {
        var eligibleCandidates = group.Candidates
            .Where(candidate => configuration.UseNinjaForDangerousArea || !IsAbovePotTreasureAggroLimit(candidate))
            .ToArray();
        if (!configuration.UseNinjaForDangerousArea)
        {
            var skippedCandidates = group.Candidates.Except(eligibleCandidates).Select(candidate => $"{candidate.Label}:{candidate.AggroLevel}");
            logger.Info($"{BuildLogTag()} op=geometric-eligibility cutoff={GetPotTreasureAggroLimit()} source={GetPotTreasureAggroLimitSource()} skipped=[{string.Join(", ", skippedCandidates)}]");
        }
        var maximumAngle = scanner.ActiveTerritoryData?.PotTreasure.GeometricMaximumHintAngleDegrees ?? 95f;
        var ranked = GeometricTreasureCandidatePlanner.Rank(
            eligibleCandidates,
            observations,
            handledCandidateLabels,
            currentPosition,
            maximumAngle);
        PromoteConfirmedAlternateAnchors(ranked);
        logger.Info($"{BuildLogTag()} op=geometric-candidate-order fate=\"{activeFateName}\" observations={observations.Count} eligible={eligibleCandidates.Length} handled={handledCandidateLabels.Count} final=[{string.Join(", ", ranked.Select(candidate => candidate.Label))}]");
        return ranked;
    }

    private static void PromoteConfirmedAlternateAnchors(List<TreasureCofferCandidateData> candidates)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.ConfirmedCofferPosition != null || candidate.CloseAlternateCandidateKeys.Count == 0)
            {
                continue;
            }

            var confirmedAlternateIndex = candidates.FindIndex(
                index + 1,
                alternate => alternate.ConfirmedCofferPosition != null
                    && candidate.CloseAlternateCandidateKeys.Contains(alternate.CandidateKey, StringComparer.OrdinalIgnoreCase));
            if (confirmedAlternateIndex < 0)
            {
                continue;
            }

            var confirmedAlternate = candidates[confirmedAlternateIndex];
            candidates.RemoveAt(confirmedAlternateIndex);
            candidates.Insert(index, confirmedAlternate);
        }
    }

    private bool IsGeometricSearch()
        => scanner.ActiveTerritoryData?.PotTreasure.SearchStrategy == TreasureSearchStrategy.GeometricCandidates;

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
        => candidate.AggroLevel > configuration.PotTreasureFallbackMaximumAggroLevel
            || (candidate.HideThresholdDistance ?? 0) > 0;

    private bool IsAggroDangerousCandidate(TreasureCofferCandidateData candidate)
        => candidate.AggroLevel > configuration.PotTreasureFallbackMaximumAggroLevel;

    private int GetPotTreasureAggroLimit()
        => scanner.Snapshot.PlayerForayLevel is { } knowledgeLevel
            ? Math.Clamp(
                knowledgeLevel + configuration.PotTreasureAggroLevelOffset,
                1,
                scanner.ActiveTerritoryData?.MaximumKnowledgeLevel ?? 28)
            : configuration.PotTreasureFallbackMaximumAggroLevel;

    private string GetPotTreasureAggroLimitSource()
        => scanner.Snapshot.PlayerForayLevel.HasValue ? "knowledge-offset" : "fallback";

    private bool IsAbovePotTreasureAggroLimit(TreasureCofferCandidateData candidate)
        => candidate.AggroLevel > GetPotTreasureAggroLimit();

    private bool IsStaticDangerousCandidate(TreasureCofferCandidateData candidate)
    {
        if (!IsGeometricSearch() || !scanner.Snapshot.PlayerForayLevel.HasValue)
        {
            return IsAggroDangerousCandidate(candidate);
        }

        var hideAtOrAbove = GetPotKnowledgeThreatPolicy().GetHideAtOrAbove(scanner.Snapshot.PlayerForayLevel.Value);
        return candidate.AggroLevel >= hideAtOrAbove;
    }

    private DangerousTreasureTravelOptions GetDangerousTravelOptions(bool destinationAlreadyResolved = false)
    {
        var maximumAggroLevel = configuration.PotTreasureFallbackMaximumAggroLevel;
        if (IsGeometricSearch() && scanner.Snapshot.PlayerForayLevel.HasValue)
        {
            maximumAggroLevel = GetPotKnowledgeThreatPolicy().GetHideAtOrAbove(scanner.Snapshot.PlayerForayLevel.Value) - 1;
        }

        return new(configuration.NinjaGearsetNumber, configuration.HideThresholdDistance, maximumAggroLevel, destinationAlreadyResolved);
    }

    private bool TryHandleCandidateTravelStall()
    {
        if (State != TreasureSearchState.TravelingToCandidate || activeCandidateKey == null)
        {
            return false;
        }

        var playerPosition = Plugin.ObjectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue || candidateTravelTarget == Vector3.Zero)
        {
            return false;
        }

        var distance = CalculateFlatDistance(playerPosition.Value, candidateTravelTarget);
        var now = DateTimeOffset.UtcNow;
        var dangerousTravelRunning = dangerousTreasureTravelController.IsRunning;
        var madeProgress = false;
        var movementProgress = false;
        var bestDistance = distance;
        var progressAge = TimeSpan.Zero;

        lock (gate)
        {
            if (candidateTravelLastProgressAt == DateTimeOffset.MinValue)
            {
                candidateTravelLastProgressAt = now;
                candidateTravelProgressDistance = distance;
                candidateTravelLastObservedPosition = playerPosition.Value;
                hasCandidateTravelObservedPosition = true;
            }
            else if (candidateTravelProgressDistance - distance >= CandidateTravelProgressThreshold)
            {
                candidateTravelLastProgressAt = now;
                candidateTravelProgressDistance = distance;
                candidateTravelLastObservedPosition = playerPosition.Value;
                hasCandidateTravelObservedPosition = true;
                madeProgress = true;
            }
            else if (!hasCandidateTravelObservedPosition || CalculateFlatDistance(candidateTravelLastObservedPosition, playerPosition.Value) >= CandidateTravelProgressThreshold)
            {
                candidateTravelLastProgressAt = now;
                candidateTravelLastObservedPosition = playerPosition.Value;
                hasCandidateTravelObservedPosition = true;
                movementProgress = true;
            }

            bestDistance = candidateTravelProgressDistance;
            progressAge = now - candidateTravelLastProgressAt;
        }

        if (madeProgress || movementProgress)
        {
            logger.DebugThrottled(
                "treasure-search-travel-progress",
                WaitLogInterval,
                $"Treasure search made progress toward candidate {activeCandidateKey.Label}. distance={distance:0.0} bestDistance={bestDistance:0.0} progress={(madeProgress ? "target-distance" : "player-movement")} dangerous={dangerousTravelRunning} dangerousState={(dangerousTravelRunning ? dangerousTreasureTravelController.State.ToString() : "none")} movementState={movementController.State}.");
            return false;
        }

        var stallTimeout = dangerousTravelRunning ? DangerousCandidateTravelStallTimeout : CandidateTravelStallTimeout;
        if (progressAge < stallTimeout)
        {
            return false;
        }

        if (dangerousTravelRunning)
        {
            dangerousTreasureTravelController.Stop("Treasure candidate travel stalled.");
        }

        movementController.Stop("Treasure candidate travel stalled.");
        logger.Warning(
            $"{BuildLogTag()} op=candidate-travel-stalled candidate={activeCandidateKey.Label} dangerous={dangerousTravelRunning} distance={distance:0.0} bestDistance={bestDistance:0.0} lastProgressAgo={progressAge.TotalSeconds:0.0}s dangerousState={(dangerousTravelRunning ? dangerousTreasureTravelController.State.ToString() : "none")} movementState={movementController.State} route={movementController.GetStatusSummary()} step={movementController.GetActiveStepSummary()}");
        AdvanceCandidate($"Stalled while traveling to treasure candidate {activeCandidateKey.Label}.");
        return true;
    }

    private void ClearCandidateTravelProgressTracking()
    {
        candidateTravelLastProgressAt = DateTimeOffset.MinValue;
        candidateTravelProgressDistance = float.MaxValue;
        candidateTravelLastObservedPosition = Vector3.Zero;
        hasCandidateTravelObservedPosition = false;
        candidateTravelTarget = Vector3.Zero;
    }

    private void ResetCandidateTravelProgressTracking(Vector3 target)
    {
        lock (gate)
        {
            candidateTravelTarget = target;
            candidateTravelLastProgressAt = DateTimeOffset.UtcNow;
            candidateTravelProgressDistance = float.MaxValue;
            candidateTravelLastObservedPosition = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            hasCandidateTravelObservedPosition = candidateTravelLastObservedPosition != Vector3.Zero;
        }
    }

    private bool IsHandledCandidate(string label)
        => handledCandidateLabels.Contains(label);

    private TreasureCofferCandidateData? GetTraversalPreviousCandidate(int candidateIndex)
        => candidateIndex > 0 && candidateIndex - 1 < orderedCandidates.Count
            ? orderedCandidates[candidateIndex - 1]
            : null;

    private RefinementMovePlan? ResolveRefinementMove(Vector3 playerPosition, TreasureDirection direction, float baseStep)
    {
        if (GetDirectionVector(direction) == Vector3.Zero)
        {
            lastNavmeshRejectionSummary = string.Empty;
            return null;
        }

        var rejectionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        RefinementMovePlan? bestPlan = null;

        void RecordRejection(string reason)
            => rejectionCounts[reason] = rejectionCounts.GetValueOrDefault(reason, 0) + 1;

        RefinementMovePlan? ValidateMeshTarget(Vector3 rawTarget, Vector3? meshTarget, TreasureDirection refinementDirection, Vector3 directionVector, string method, float radius, float step, float multiplier)
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

            if (targetDistance <= LocalMoveSkipDistance
                && CalculateFlatDistance(playerPosition, rawTarget) > LocalMoveSkipDistance)
            {
                RecordRejection("collapsed_underfoot");
                logger.Debug($"Treasure target <{rawTarget.X:0.0}, {rawTarget.Y:0.0}, {rawTarget.Z:0.0}> resolved via {method}(r={radius:0}) to <{snappedTarget.X:0.0}, {snappedTarget.Y:0.0}, {snappedTarget.Z:0.0}>, but movement collapsed to {targetDistance:0.0}y in the requested {refinementDirection} direction; rejecting fallback mesh point.");
                return null;
            }

            if (snapDistance > maxSnapDistance)
            {
                RecordRejection("horizontal_snap");
                logger.Debug($"Treasure target <{rawTarget.X:0.0}, {rawTarget.Y:0.0}, {rawTarget.Z:0.0}> resolved via {method}(r={radius:0}) to <{snappedTarget.X:0.0}, {snappedTarget.Y:0.0}, {snappedTarget.Z:0.0}>, but snap drift {snapDistance:0.0}y exceeds {maxSnapDistance:0.0}y; rejecting mesh point.");
                return null;
            }

            if (forwardDistance < minimumForwardDistance)
            {
                RecordRejection("insufficient_forward");
                logger.Debug($"Treasure target <{rawTarget.X:0.0}, {rawTarget.Y:0.0}, {rawTarget.Z:0.0}> resolved via {method}(r={radius:0}) to <{snappedTarget.X:0.0}, {snappedTarget.Y:0.0}, {snappedTarget.Z:0.0}>, but it only advances {forwardDistance:0.0}y in the intended {refinementDirection} direction; rejecting mesh point.");
                return null;
            }

            if (lateralDistance > maxLateralDistance)
            {
                RecordRejection("lateral_drift");
                logger.Debug($"Treasure target <{rawTarget.X:0.0}, {rawTarget.Y:0.0}, {rawTarget.Z:0.0}> resolved via {method}(r={radius:0}) to <{snappedTarget.X:0.0}, {snappedTarget.Y:0.0}, {snappedTarget.Z:0.0}>, but lateral drift {lateralDistance:0.0}y exceeds {maxLateralDistance:0.0}y for {refinementDirection}; rejecting mesh point.");
                return null;
            }

            return new RefinementMovePlan(
                rawTarget,
                snappedTarget,
                refinementDirection,
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

        foreach (var refinementDirection in GetRefinementDirections(direction))
        {
            var directionVector = GetDirectionVector(refinementDirection);
            foreach (var multiplier in RefinementStepMultipliers)
            {
                var step = baseStep * multiplier;
                var rawTarget = BuildTreasureTarget(playerPosition, directionVector, step);
                foreach (var radius in RefinementSearchRadii)
                {
                    RefinementMovePlan? radiusBestPlan = null;

                    var nearestPoint = movementController.FindNearestNavigablePoint(rawTarget, radius, MathF.Max(8f, radius * 0.4f));
                    var nearestPlan = ValidateMeshTarget(rawTarget, nearestPoint, refinementDirection, directionVector, "NearestPoint", radius, step, multiplier);
                    if (nearestPlan != null)
                    {
                        radiusBestPlan = nearestPlan;
                    }

                    var floorPoint = movementController.FindPointOnFloor(rawTarget, radius);
                    var floorPlan = ValidateMeshTarget(rawTarget, floorPoint, refinementDirection, directionVector, "PointOnFloor", radius, step, multiplier);
                    if (floorPlan != null && (radiusBestPlan == null || floorPlan.Score + PointOnFloorOverrideScoreAdvantage < radiusBestPlan.Score))
                    {
                        radiusBestPlan = floorPlan;
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

            if (bestPlan != null)
            {
                break;
            }
        }

        lastNavmeshRejectionSummary = rejectionCounts.Count == 0
            ? string.Empty
            : string.Join(", ", rejectionCounts.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}={entry.Value}"));
        return bestPlan;
    }

    private static TreasureDirection[] GetRefinementDirections(TreasureDirection direction)
        => direction switch
        {
            TreasureDirection.Northeast => [direction, TreasureDirection.East, TreasureDirection.North],
            TreasureDirection.Southeast => [direction, TreasureDirection.East, TreasureDirection.South],
            TreasureDirection.Southwest => [direction, TreasureDirection.West, TreasureDirection.South],
            TreasureDirection.Northwest => [direction, TreasureDirection.West, TreasureDirection.North],
            _ => [direction],
        };

    private float GetRefinementStepSize(string distanceBucket)
        => distanceBucket switch
        {
            "beyond_far" => 100f,
            "far" => 40f,
            "immediate" => 8f,
            _ => 20f,
        };

    private TimeSpan GetRefinementMoveTimeout(float step)
    {
        var mountedTravelSpeed = scanner.ActiveTerritoryData?.MountedTravelSpeed > 0 ? scanner.ActiveTerritoryData.MountedTravelSpeed : 14.13f;
        var timeoutSeconds = ((step / Math.Max(0.1f, mountedTravelSpeed)) * 2f) + 15f;
        return TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 12f, 45f));
    }

    private Dictionary<uint, Dictionary<string, TreasureCofferGroupData>> GetGroupsByFateId()
        => scanner.ActiveTerritoryData?.TreasureCofferGroups
            .GroupBy(group => group.FateId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(entry => entry.GroupKey, StringComparer.OrdinalIgnoreCase))
           ?? [];

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
    {
        lock (gate)
        {
            ClearCandidateTravelProgressTracking();
        }

        TransitionTo(TreasureSearchState.Failed, reason, error: reason, result: TreasureSearchRunResult.Failed);
    }

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
            refinementCandidateLocked = false;
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
        string probeOperationId;
        lock (gate)
        {
            candidateProbeAttemptCount++;
            probeOperationId = $"probe-{++probeOperationSequence}";
            activeCandidateProbeOperationId = probeOperationId;
            candidateProbeLastAttemptAt = now;
            candidateProbeDeadlineAt = now + CandidateProbeTimeout;
            candidateProbeBaselineSessionId = hintSnapshot.SessionId;
            candidateProbeBaselineRevision = hintSnapshot.Revision;
            candidateArrivedAt = DateTimeOffset.MinValue;
        }

        logger.ResetThrottle("treasure-search-probe");
        logger.ResetThrottle("treasure-search-probe-retry");
        logger.Info($"{BuildLogTag()} op=probe-attempt candidate={activeCandidateKey?.Label ?? "none"} probeOperation={probeOperationId} attempt={candidateProbeAttemptCount}/{MaximumCandidateProbeAttempts} inventoryUseAccepted={used} baselineSession={candidateProbeBaselineSessionId} baselineRevision={candidateProbeBaselineRevision} probeDeadline={candidateProbeDeadlineAt:O} reason={reason}");
        TransitionTo(TreasureSearchState.ProbingCandidate, $"{reason} Waiting for a new treasure event after baseline revision {candidateProbeBaselineRevision} in session {candidateProbeBaselineSessionId} for candidate {activeCandidateKey?.Label}.");
        return true;
    }

    private bool TryStartRefinementProbe(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (refinementProbeAttemptCount >= MaximumCandidateRefinementSteps)
        {
            AdvanceCandidate($"Treasure candidate {activeCandidateKey?.Label} reached the {MaximumCandidateRefinementSteps}-attempt refinement probe limit; trying the next candidate.");
            return false;
        }

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
        string probeOperationId;
        lock (gate)
        {
            refinementProbeAttemptCount++;
            probeOperationId = $"refine-probe-{++refinementOperationSequence}";
            activeRefinementProbeOperationId = probeOperationId;
            refinementProbeLastAttemptAt = now;
            refinementProbeDeadlineAt = now + CandidateProbeTimeout;
            refinementProbeBaselineSessionId = hintSnapshot.SessionId;
            refinementProbeBaselineRevision = hintSnapshot.Revision;
        }

        logger.ResetThrottle("treasure-search-refine-probe");
        logger.ResetThrottle("treasure-search-refine-probe-retry");
        logger.Info($"{BuildLogTag()} op=refine-probe-attempt candidate={activeCandidateKey?.Label ?? "none"} probeOperation={probeOperationId} attempt={refinementProbeAttemptCount} inventoryUseAccepted={used} baselineSession={refinementProbeBaselineSessionId} baselineRevision={refinementProbeBaselineRevision} probeDeadline={refinementProbeDeadlineAt:O} step={refinementStepIndex}/{MaximumCandidateRefinementSteps} reason={reason}");
        return true;
    }

    private bool EnsureRefinementProbeReady(TreasureCofferCandidateData activeCandidate)
    {
        if (!configuration.UseNinjaForDangerousArea)
        {
            if (IsAbovePotTreasureAggroLimit(activeCandidate))
            {
                AdvanceCandidate($"Skipping treasure refinement probe at candidate {activeCandidate.Label} because aggro level {activeCandidate.AggroLevel} exceeds the current no-Ninja cutoff {GetPotTreasureAggroLimit()} ({GetPotTreasureAggroLimitSource()}).");
                return false;
            }

            return true;
        }

        var isDangerousCandidate = IsGeometricSearch()
            ? IsStaticDangerousCandidate(activeCandidate)
            : IsDangerousCandidate(activeCandidate);
        if (!isDangerousCandidate)
        {
            return true;
        }

        if (!gameActionController.IsStealthed)
        {
            if (!gameActionController.CanUseHide())
            {
                logger.DebugThrottled(
                    "treasure-search-refine-hide-ready",
                    TimeSpan.FromMilliseconds(250),
                    $"Treasure refinement is waiting for Hide before probing dangerous candidate {activeCandidateKey?.Label}. currentClassJob={gameActionController.CurrentClassJobId}.");
                return false;
            }

            if (!gameActionController.TryExecuteAction(GameActionController.HideActionId, $"treasure refinement probe prep {activeCandidateKey?.Label}"))
            {
                logger.DebugThrottled(
                    "treasure-search-refine-hide-request",
                    TimeSpan.FromMilliseconds(250),
                    $"Treasure refinement could not dispatch Hide yet for dangerous candidate {activeCandidateKey?.Label}; retrying.");
                return false;
            }

            logger.Info($"{BuildLogTag()} op=refine-hide-request candidate={activeCandidateKey?.Label ?? "none"} action=wait-for-stealth");
            return false;
        }

        if (!gameActionController.CanUseHide())
        {
            logger.DebugThrottled(
                "treasure-search-refine-hide-recast",
                TimeSpan.FromMilliseconds(250),
                $"Treasure refinement is waiting for Hide to become reusable before probing dangerous candidate {activeCandidateKey?.Label}.");
            return false;
        }

        logger.DebugThrottled(
            "treasure-search-refine-hide-probe-ready",
            TimeSpan.FromMilliseconds(250),
            $"Treasure refinement probe is ready for dangerous candidate {activeCandidateKey?.Label}; Hide is active and reusable.");
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
                geometricObservations.Clear();
                traversalOriginCenter = Vector3.Zero;
                pendingCandidateAdvanceReason = string.Empty;
                lastNavmeshRejectionSummary = string.Empty;
                refinementCandidateLocked = false;
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

        logger.Info($"{BuildLogTag()} op=transition from={previousState} to={nextState} fate=\"{activeFateName}\" ({activeFateId}) group={activeGroupKey} candidate={activeCandidateKey?.Label ?? "none"} candidateGeneration={candidateGeneration} probeOperation={DescribeProbeOperation()} refinementOperation={DescribeRefinementOperation()} reason={reason}");
    }

    private string BuildLogTag(int? sessionId = null)
    {
        var resolvedSessionId = sessionId ?? treasureHintTracker.Snapshot.SessionId;
        return resolvedSessionId > 0
            ? $"[Treasure run={FormatLogValue(currentSearchRunId)} session={resolvedSessionId} generation={candidateGeneration} candidate={activeCandidateKey?.Label ?? "none"}]"
            : $"[Treasure run={FormatLogValue(currentSearchRunId)} generation={candidateGeneration} candidate={activeCandidateKey?.Label ?? "none"}]";
    }

    private void LogEventConsume(string consumer, int baselineSessionId, int baselineRevision, TreasureHintEvent latestEvent, string operationId)
        => logger.Info($"{BuildLogTag()} op=event-consume consumer={consumer} treasureSessionId={treasureHintTracker.Snapshot.SessionId} baselineSessionId={baselineSessionId} baselineRevision={baselineRevision} eventRevision={latestEvent.Revision} candidateGeneration={candidateGeneration} candidateKey={activeCandidateKey?.Label ?? "none"} probeOperation={operationId} kind={latestEvent.Kind} direction={latestEvent.Direction} distance={FormatLogValue(latestEvent.DistanceBucket)}");

    private string DescribeProbeOperation()
        => FormatLogValue(activeCandidateProbeOperationId);

    private string DescribeRefinementOperation()
        => FormatLogValue(activeRefinementProbeOperationId);

    private static string FormatLogValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value;

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
            || string.Equals(distanceBucket, "immediate", StringComparison.Ordinal);

    private static bool IsImmediateTreasureDistance(string distanceBucket)
        => string.Equals(distanceBucket, "immediate", StringComparison.Ordinal);

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }
}
