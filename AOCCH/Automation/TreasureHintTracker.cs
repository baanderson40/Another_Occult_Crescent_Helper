using System;
using System.Collections.Generic;

using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Scanning;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class TreasureHintTracker : IDisposable
{
    private static readonly TimeSpan PostBuffGraceDuration = TimeSpan.FromSeconds(1.5);

    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly OccultCrescentScanner scanner;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private TreasureHintSnapshot snapshot = new()
    {
        LastTransition = "Idle",
    };
    private TreasureCofferSurveySnapshot cofferSurveySnapshot = new();

    private bool? lastTreasureBuffState;
    private DateTimeOffset lastProcessedScannerUpdate = DateTimeOffset.MinValue;
    private DateTimeOffset debugLogCaptureDeadlineAt = DateTimeOffset.MinValue;
    private string debugLogCaptureReason = string.Empty;
    private int debugLogCaptureAttemptId;
    private readonly List<string> debugLogCaptureEntries = [];
    private string latestDebugLogMessageSummary = string.Empty;

    public TreasureHintTracker(
        IFramework framework,
        IChatGui chatGui,
        OccultCrescentScanner scanner,
        AocchLogger logger)
    {
        this.framework = framework;
        this.chatGui = chatGui;
        this.scanner = scanner;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
        chatGui.LogMessage += OnLogMessage;
        logger.Info("[TreasureHintTracker] op=init");
    }

    public TreasureHintSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public bool HasActiveSession
        => Snapshot.HasActiveSession;

    public bool HasInitialHint
        => Snapshot.HasInitialHint;

    public TreasureCofferSurveySnapshot CofferSurveySnapshot
    {
        get
        {
            lock (gate)
            {
                return cofferSurveySnapshot;
            }
        }
    }

    public int ArmDebugLogMessageCapture(string reason, TimeSpan duration)
    {
        int attemptId;
        lock (gate)
        {
            debugLogCaptureAttemptId++;
            attemptId = debugLogCaptureAttemptId;
            debugLogCaptureReason = reason;
            debugLogCaptureDeadlineAt = DateTimeOffset.UtcNow + duration;
            debugLogCaptureEntries.Clear();
            latestDebugLogMessageSummary = string.Empty;
        }

        logger.Info($"[TreasureHintTracker] op=debug-logmessage-capture-arm attempt={attemptId} reason=\"{SanitizeLogText(reason)}\" duration={duration.TotalSeconds:0.0}s ids={string.Join(",", GetDebugTreasureLogMessageIds())}");
        return attemptId;
    }

    public void ClearDebugLogMessageCapture()
    {
        lock (gate)
        {
            debugLogCaptureDeadlineAt = DateTimeOffset.MinValue;
            debugLogCaptureReason = string.Empty;
            debugLogCaptureEntries.Clear();
            latestDebugLogMessageSummary = string.Empty;
        }
    }

    public string GetDebugLogMessageCaptureSummary()
    {
        lock (gate)
        {
            if (debugLogCaptureEntries.Count == 0)
            {
                if (debugLogCaptureDeadlineAt > DateTimeOffset.UtcNow)
                {
                    return $"attempt={debugLogCaptureAttemptId} armed reason=\"{debugLogCaptureReason}\" entries=0 latestLog=none";
                }

                return string.IsNullOrEmpty(debugLogCaptureReason)
                    ? "none"
                    : $"attempt={debugLogCaptureAttemptId} reason=\"{debugLogCaptureReason}\" entries=0 latestLog=none";
            }

            return $"attempt={debugLogCaptureAttemptId} reason=\"{debugLogCaptureReason}\" entries={debugLogCaptureEntries.Count} latestLog={FormatDebugValue(latestDebugLogMessageSummary)}";
        }
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            snapshot = new TreasureHintSnapshot
            {
                LastTransition = "Idle",
                LastResetReason = reason,
            };
            cofferSurveySnapshot = new TreasureCofferSurveySnapshot
            {
                LastTransition = reason,
            };
            lastTreasureBuffState = false;
            lastProcessedScannerUpdate = DateTimeOffset.MinValue;
        }

        logger.Info($"[TreasureHintTracker] op=reset reason={reason}");
    }

    public void Dispose()
    {
        chatGui.LogMessage -= OnLogMessage;
        framework.Update -= OnFrameworkUpdate;

        if (Snapshot.HasActiveSession)
        {
            CompleteCurrentTreasureSession("Treasure hint tracker disposal.", TreasureSessionState.Abandoned);
        }

        logger.Info("[TreasureHintTracker] op=stop");
    }

    public bool TryGetLatestHint(out TreasureHintEvent? hint)
    {
        var currentSnapshot = Snapshot;
        hint = currentSnapshot.LastHintEvent ?? currentSnapshot.InitialHintEvent;
        return hint != null;
    }

    public bool TryGetLatestEventSince(int sessionId, int revision, out TreasureHintEvent? treasureEvent)
    {
        var currentSnapshot = Snapshot;
        if (currentSnapshot.SessionId != sessionId
            || currentSnapshot.Revision <= revision
            || currentSnapshot.LastEvent == null)
        {
            treasureEvent = null;
            return false;
        }

        treasureEvent = currentSnapshot.LastEvent;
        return true;
    }

    public bool TryGetLatestCofferSurveySince(int revision, out TreasureCofferSurveySnapshot? survey)
    {
        var currentSurvey = CofferSurveySnapshot;
        if (currentSurvey.Revision <= revision)
        {
            survey = null;
            return false;
        }

        survey = currentSurvey;
        return true;
    }

    public void BeginNewTreasureSession(string reason)
    {
        TreasureHintSnapshot previous;
        TreasureHintSnapshot next;
        lock (gate)
        {
            previous = snapshot;
            next = new TreasureHintSnapshot
            {
                SessionState = TreasureSessionState.Active,
                SessionId = previous.SessionId + 1,
                TerritoryKey = scanner.Snapshot.TerritoryKey,
                TerritoryTypeId = scanner.Snapshot.TerritoryTypeId,
                StartedAt = DateTimeOffset.UtcNow,
                LastTransition = reason,
                LastResetReason = reason,
            };
            snapshot = next;
        }

        logger.Info($"[TreasureHintTracker session={next.SessionId}] op=session-start reason={reason}");
    }

    public void CompleteCurrentTreasureSession(string reason, TreasureSessionState terminalState)
    {
        TreasureHintSnapshot? completedSnapshot = null;

        lock (gate)
        {
            if (snapshot.SessionState != TreasureSessionState.Active)
            {
                return;
            }

            completedSnapshot = new TreasureHintSnapshot
            {
                SessionState = terminalState,
                SessionId = snapshot.SessionId,
                TerritoryKey = snapshot.TerritoryKey,
                TerritoryTypeId = snapshot.TerritoryTypeId,
                StartedAt = snapshot.StartedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                CompletionReason = reason,
                Revision = snapshot.Revision,
                InitialHintEvent = snapshot.InitialHintEvent,
                LastHintEvent = snapshot.LastHintEvent,
                LastEvent = snapshot.LastEvent,
                RevealLatchedEvent = snapshot.RevealLatchedEvent,
                LastTransition = reason,
                LastResetReason = snapshot.LastResetReason,
            };

            snapshot = completedSnapshot;
        }

        logger.Info($"[TreasureHintTracker session={completedSnapshot.SessionId}] op=session-end state={terminalState} reason={reason}");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var scannerSnapshot = scanner.Snapshot;
        if (scannerSnapshot.LastUpdated == DateTimeOffset.MinValue)
        {
            return;
        }

        if (scannerSnapshot.LastUpdated <= lastProcessedScannerUpdate)
        {
            return;
        }

        lastProcessedScannerUpdate = scannerSnapshot.LastUpdated;

        if (!scannerSnapshot.IsInSupportedTerritory || !scannerSnapshot.CanRunPotTreasure)
        {
            if (Snapshot.HasActiveSession)
            {
                CompleteCurrentTreasureSession("Pot treasure became unavailable during treasure tracking.", TreasureSessionState.Abandoned);
            }

            lastTreasureBuffState = false;
            return;
        }

        if (Snapshot.HasActiveSession
            && (!string.Equals(Snapshot.TerritoryKey, scannerSnapshot.TerritoryKey, StringComparison.OrdinalIgnoreCase)
                || Snapshot.TerritoryTypeId != scannerSnapshot.TerritoryTypeId))
        {
            CompleteCurrentTreasureSession("Treasure tracking territory changed.", TreasureSessionState.Abandoned);
            lastTreasureBuffState = false;
            return;
        }

        if (lastTreasureBuffState != true && scannerSnapshot.HasTreasureBuff)
        {
            BeginNewTreasureSession($"Treasure buff detected with {scannerSnapshot.TreasureBuffRemainingSeconds:0}s remaining.");
        }
        else if (lastTreasureBuffState == true && !scannerSnapshot.HasTreasureBuff)
        {
            if (Snapshot.HasRevealLatched)
            {
                logger.Info($"[TreasureHintTracker session={Snapshot.SessionId}] op=reveal-latched-buff-cleared revision={Snapshot.RevealLatchedEvent?.Revision ?? 0} kind={Snapshot.RevealLatchedEvent?.Kind}");
            }
            else if (!Snapshot.IsPostBuffGraceActive)
            {
                BeginPostBuffGrace();
            }
        }

        if (Snapshot.PostBuffGraceDeadlineAt != DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow >= Snapshot.PostBuffGraceDeadlineAt)
        {
            logger.Info($"[TreasureHintTracker session={Snapshot.SessionId}] op=post-buff-grace-expired deadline={Snapshot.PostBuffGraceDeadlineAt:O}");
            CompleteCurrentTreasureSession("Treasure buff expired or cleared.", TreasureSessionState.Expired);
        }

        lastTreasureBuffState = scannerSnapshot.HasTreasureBuff;
    }

    private void OnLogMessage(ILogMessage message)
    {
        var summary = CaptureDebugLogMessageSummary(message);
        if (summary.Length > 0)
        {
            int attemptId;
            string reason;
            lock (gate)
            {
                attemptId = debugLogCaptureAttemptId;
                reason = debugLogCaptureReason;
            }

            logger.Info($"[TreasureHintTracker] op=treasure-logmessage-debug attempt={attemptId} source=debug-window reason=\"{SanitizeLogText(reason)}\" {summary}");
        }

        if (TryParseCofferSurveyLogMessage(message, out var surveySnapshot))
        {
            ApplyCofferSurveySnapshot(surveySnapshot);
        }

        if (!TryClassifyTreasureLogMessage(message, out var parsedEvent))
        {
            return;
        }

        ApplyParsedTreasureEvent(parsedEvent);
    }

    private void ApplyParsedTreasureEvent(TreasureHintEvent parsedEvent)
    {
        TreasureHintSnapshot updatedSnapshot;
        var acceptedDuringPostBuffGrace = false;
        lock (gate)
        {
            if (snapshot.SessionState != TreasureSessionState.Active)
            {
                logger.Info($"[TreasureHintTracker] op=event-rejected reason=no-active-session kind={parsedEvent.Kind} text=\"{SanitizeLogText(parsedEvent.RawText)}\"");
                return;
            }

            if (snapshot.IsPostBuffGraceActive && parsedEvent.Kind is not TreasureHintKind.CofferReveal and not TreasureHintKind.CofferMessage)
            {
                logger.Info($"[TreasureHintTracker session={snapshot.SessionId}] op=event-rejected reason=post-buff-grace kind={parsedEvent.Kind} text=\"{SanitizeLogText(parsedEvent.RawText)}\" postBuffGraceDeadline={snapshot.PostBuffGraceDeadlineAt:O}");
                return;
            }

            acceptedDuringPostBuffGrace = snapshot.IsPostBuffGraceActive;

            var nextRevision = snapshot.Revision + 1;
            var eventWithRevision = new TreasureHintEvent
            {
                Kind = parsedEvent.Kind,
                RawText = parsedEvent.RawText,
                NormalizedText = parsedEvent.NormalizedText,
                Direction = parsedEvent.Direction,
                DistanceBucket = parsedEvent.DistanceBucket,
                DistanceText = parsedEvent.DistanceText,
                ReceivedAt = DateTimeOffset.UtcNow,
                Revision = nextRevision,
            };

            var initialHint = snapshot.InitialHintEvent;
            var lastHint = snapshot.LastHintEvent;
            var revealLatchedEvent = snapshot.RevealLatchedEvent;
            if (eventWithRevision.Kind == TreasureHintKind.Hint)
            {
                initialHint ??= eventWithRevision;
                lastHint = eventWithRevision;
            }

            if (eventWithRevision.Kind is TreasureHintKind.CofferReveal or TreasureHintKind.CofferMessage)
            {
                revealLatchedEvent = eventWithRevision;
            }

            updatedSnapshot = new TreasureHintSnapshot
            {
                SessionState = snapshot.SessionState,
                SessionId = snapshot.SessionId,
                TerritoryKey = snapshot.TerritoryKey,
                TerritoryTypeId = snapshot.TerritoryTypeId,
                StartedAt = snapshot.StartedAt,
                CompletedAt = snapshot.CompletedAt,
                CompletionReason = snapshot.CompletionReason,
                Revision = nextRevision,
                InitialHintEvent = initialHint,
                LastHintEvent = lastHint,
                LastEvent = eventWithRevision,
                RevealLatchedEvent = revealLatchedEvent,
                PostBuffGraceDeadlineAt = eventWithRevision.Kind is TreasureHintKind.CofferReveal or TreasureHintKind.CofferMessage
                    ? DateTimeOffset.MinValue
                    : snapshot.PostBuffGraceDeadlineAt,
                LastTransition = BuildTransitionMessage(eventWithRevision),
                LastResetReason = snapshot.LastResetReason,
            };

            snapshot = updatedSnapshot;
        }

        logger.Info($"[TreasureHintTracker session={updatedSnapshot.SessionId}] op=event-accepted revision={updatedSnapshot.Revision} kind={updatedSnapshot.LastEvent?.Kind} direction={updatedSnapshot.LastEvent?.Direction ?? TreasureDirection.Unknown} distance={FormatValue(updatedSnapshot.LastEvent?.DistanceBucket ?? string.Empty)} acceptedDuringPostBuffGrace={acceptedDuringPostBuffGrace} text=\"{SanitizeLogText(updatedSnapshot.LastEvent?.RawText ?? string.Empty)}\"");

        if (parsedEvent.Kind is TreasureHintKind.CofferReveal or TreasureHintKind.CofferMessage)
        {
            if (acceptedDuringPostBuffGrace)
            {
                logger.Info($"[TreasureHintTracker session={updatedSnapshot.SessionId}] op=post-buff-grace-reveal-accepted revision={updatedSnapshot.RevealLatchedEvent?.Revision ?? 0} kind={updatedSnapshot.RevealLatchedEvent?.Kind}");
            }

            logger.Info($"[TreasureHintTracker session={updatedSnapshot.SessionId}] op=reveal-latched revision={updatedSnapshot.RevealLatchedEvent?.Revision ?? 0} kind={updatedSnapshot.RevealLatchedEvent?.Kind}");
        }

        logger.Info($"[TreasureHintTracker session={updatedSnapshot.SessionId}] op=event revision={updatedSnapshot.Revision} transition=\"{updatedSnapshot.LastTransition}\"");
    }

    private void BeginPostBuffGrace()
    {
        TreasureHintSnapshot next;
        lock (gate)
        {
            if (snapshot.SessionState != TreasureSessionState.Active || snapshot.HasRevealLatched || snapshot.IsPostBuffGraceActive)
            {
                return;
            }

            next = new TreasureHintSnapshot
            {
                SessionState = snapshot.SessionState,
                SessionId = snapshot.SessionId,
                TerritoryKey = snapshot.TerritoryKey,
                TerritoryTypeId = snapshot.TerritoryTypeId,
                StartedAt = snapshot.StartedAt,
                CompletedAt = snapshot.CompletedAt,
                CompletionReason = snapshot.CompletionReason,
                Revision = snapshot.Revision,
                InitialHintEvent = snapshot.InitialHintEvent,
                LastHintEvent = snapshot.LastHintEvent,
                LastEvent = snapshot.LastEvent,
                RevealLatchedEvent = snapshot.RevealLatchedEvent,
                PostBuffGraceDeadlineAt = DateTimeOffset.UtcNow + PostBuffGraceDuration,
                LastTransition = snapshot.LastTransition,
                LastResetReason = snapshot.LastResetReason,
            };
            snapshot = next;
        }

        logger.Info($"[TreasureHintTracker session={next.SessionId}] op=post-buff-grace-start deadline={next.PostBuffGraceDeadlineAt:O}");
    }

    private void ApplyCofferSurveySnapshot(TreasureCofferSurveySnapshot parsedSurvey)
    {
        TreasureCofferSurveySnapshot updatedSurvey;
        lock (gate)
        {
            updatedSurvey = new TreasureCofferSurveySnapshot
            {
                Revision = cofferSurveySnapshot.Revision + 1,
                ReceivedAt = DateTimeOffset.UtcNow,
                LogMessageId = parsedSurvey.LogMessageId,
                SilverCount = parsedSurvey.SilverCount,
                BronzeCount = parsedSurvey.BronzeCount,
                LastTransition = $"Survey silver={parsedSurvey.SilverCount} bronze={parsedSurvey.BronzeCount} via logmessage:{parsedSurvey.LogMessageId}.",
            };
            cofferSurveySnapshot = updatedSurvey;
        }

        logger.Info($"[TreasureHintTracker] op=coffer-survey revision={updatedSurvey.Revision} id={updatedSurvey.LogMessageId} silver={updatedSurvey.SilverCount} bronze={updatedSurvey.BronzeCount}");
    }

    private string CaptureDebugLogMessageSummary(ILogMessage message)
    {
        lock (gate)
        {
            if (debugLogCaptureDeadlineAt == DateTimeOffset.MinValue || DateTimeOffset.UtcNow > debugLogCaptureDeadlineAt)
            {
                return string.Empty;
            }

            if (!GetDebugTreasureLogMessageIds().Contains(message.LogMessageId))
            {
                return string.Empty;
            }

            var summary = BuildDebugLogMessageSummary(message);
            debugLogCaptureEntries.Add(summary);
            latestDebugLogMessageSummary = summary;
            return summary;
        }
    }

    private bool TryClassifyTreasureLogMessage(ILogMessage message, out TreasureHintEvent parsedEvent)
    {
        parsedEvent = null!;
        var behavior = scanner.ActiveTerritoryData?.PotTreasure;
        if (behavior == null)
        {
            return false;
        }

        if (message.LogMessageId == behavior.CofferRevealLogMessageId)
        {
            parsedEvent = CreateEvent(TreasureHintKind.CofferReveal, message.LogMessageId);
            return LogClassifiedEvent(message, parsedEvent);
        }

        if (message.LogMessageId == behavior.ElixirPromptLogMessageId || message.LogMessageId == behavior.BonusOfferLogMessageId)
        {
            parsedEvent = CreateEvent(message.LogMessageId == behavior.ElixirPromptLogMessageId ? TreasureHintKind.ElixirPrompt : TreasureHintKind.BonusOffer, message.LogMessageId);
            return LogClassifiedEvent(message, parsedEvent);
        }

        var distanceBucket = MapDistanceBucket(message.LogMessageId, behavior);
        if (distanceBucket.Length == 0)
        {
            return false;
        }

        if (!message.TryGetIntParameter(0, out var directionValue))
        {
            logger.Warning($"[TreasureHintTracker] op=hint-logmessage-parse-failed id={message.LogMessageId} reason=missing-direction-param");
            return false;
        }

        var direction = MapDirection(directionValue);
        if (direction == TreasureDirection.Unknown)
        {
            logger.Warning($"[TreasureHintTracker] op=hint-logmessage-parse-failed id={message.LogMessageId} reason=unknown-direction value={directionValue}");
            return false;
        }

        parsedEvent = new TreasureHintEvent
        {
            Kind = TreasureHintKind.Hint,
            RawText = $"LogMessageId={message.LogMessageId}",
            NormalizedText = $"logmessage:{message.LogMessageId}",
            Direction = direction,
            DistanceBucket = distanceBucket,
            DistanceText = distanceBucket,
        };
        return LogClassifiedEvent(message, parsedEvent);
    }

    private static TreasureHintEvent CreateEvent(TreasureHintKind kind, uint logMessageId)
        => new()
        {
            Kind = kind,
            RawText = $"LogMessageId={logMessageId}",
            NormalizedText = $"logmessage:{logMessageId}",
        };

    private bool LogClassifiedEvent(ILogMessage message, TreasureHintEvent parsedEvent)
    {
        logger.Info($"[TreasureHintTracker] op=event-classified logMessageId={message.LogMessageId} kind={parsedEvent.Kind} direction={parsedEvent.Direction} distance={parsedEvent.DistanceBucket} summary={BuildDebugLogMessageSummary(message)}");
        return true;
    }

    private HashSet<uint> GetDebugTreasureLogMessageIds()
    {
        var behavior = scanner.ActiveTerritoryData?.PotTreasure;
        var ids = new HashSet<uint>();
        if (behavior != null)
        {
            ids.UnionWith(
            [
                behavior.CofferRevealLogMessageId,
                behavior.HintImmediateLogMessageId,
                behavior.HintCloseLogMessageId,
                behavior.HintFarLogMessageId,
                behavior.HintBeyondFarLogMessageId,
                behavior.ElixirPromptLogMessageId,
                behavior.BonusOfferLogMessageId,
                behavior.CofferSurveyCountsLogMessageId,
                behavior.CofferSurveyEmptyLogMessageId,
            ]);
        }

        ids.Remove(0);
        return ids;
    }

    private bool TryParseCofferSurveyLogMessage(ILogMessage message, out TreasureCofferSurveySnapshot parsedSurvey)
    {
        parsedSurvey = null!;
        var behavior = scanner.ActiveTerritoryData?.PotTreasure;
        if (behavior == null)
        {
            return false;
        }

        if (message.LogMessageId == behavior.CofferSurveyCountsLogMessageId)
        {
            if (!TryParseCofferSurveyCounts(message, out var silverCount, out var bronzeCount))
            {
                return false;
            }

            parsedSurvey = new TreasureCofferSurveySnapshot
            {
                LogMessageId = message.LogMessageId,
                SilverCount = Math.Max(0, silverCount),
                BronzeCount = Math.Max(0, bronzeCount),
            };
            return true;
        }

        if (message.LogMessageId != behavior.CofferSurveyEmptyLogMessageId)
        {
            return false;
        }

        parsedSurvey = new TreasureCofferSurveySnapshot
        {
            LogMessageId = message.LogMessageId,
            SilverCount = 0,
            BronzeCount = 0,
        };
        return true;
    }

    private bool TryParseCofferSurveyCounts(ILogMessage message, out int silverCount, out int bronzeCount)
    {
        silverCount = 0;
        bronzeCount = 0;

        if (message.TryGetIntParameter(0, out silverCount)
            && message.TryGetIntParameter(1, out bronzeCount)
            && IsValidSurveyCountPair(silverCount, bronzeCount))
        {
            return true;
        }

        var intParameters = new List<(int Index, int Value)>();
        for (var i = 0; i < message.ParameterCount; i++)
        {
            if (message.TryGetIntParameter(i, out var intValue))
            {
                intParameters.Add((i, intValue));
            }
        }

        var candidatePairs = new List<(int SilverIndex, int BronzeIndex, int SilverCount, int BronzeCount)>();
        for (var i = 0; i < intParameters.Count; i++)
        {
            for (var j = i + 1; j < intParameters.Count; j++)
            {
                var silverCandidate = intParameters[i];
                var bronzeCandidate = intParameters[j];
                if (!IsValidSurveyCountPair(silverCandidate.Value, bronzeCandidate.Value))
                {
                    continue;
                }

                candidatePairs.Add((silverCandidate.Index, bronzeCandidate.Index, silverCandidate.Value, bronzeCandidate.Value));
            }
        }

        if (candidatePairs.Count == 1)
        {
            var candidate = candidatePairs[0];
            silverCount = candidate.SilverCount;
            bronzeCount = candidate.BronzeCount;
            logger.Warning($"[TreasureHintTracker] op=coffer-survey-parse-fallback id={message.LogMessageId} silverIndex={candidate.SilverIndex} bronzeIndex={candidate.BronzeIndex} silver={silverCount} bronze={bronzeCount} summary={BuildDebugLogMessageSummary(message)}");
            return true;
        }

        var failureReason = candidatePairs.Count == 0 ? "no-valid-int-pair" : "ambiguous-int-pairs";
        logger.Warning($"[TreasureHintTracker] op=coffer-survey-parse-failed id={message.LogMessageId} reason={failureReason} summary={BuildDebugLogMessageSummary(message)}");
        return false;
    }

    private static bool IsValidSurveyCountPair(int silverCount, int bronzeCount)
        => silverCount is >= 0 and <= 8 && bronzeCount is >= 0 and <= 30;

    private static TreasureDirection MapDirection(int directionValue)
        => directionValue switch
        {
            1 => TreasureDirection.North,
            2 => TreasureDirection.Northeast,
            3 => TreasureDirection.East,
            4 => TreasureDirection.Southeast,
            5 => TreasureDirection.South,
            6 => TreasureDirection.Southwest,
            7 => TreasureDirection.West,
            8 => TreasureDirection.Northwest,
            _ => TreasureDirection.Unknown,
        };

    private static string MapDistanceBucket(uint logMessageId, PotTreasureBehaviorData behavior)
        => logMessageId switch
        {
            var id when id == behavior.HintImmediateLogMessageId => "immediate",
            var id when id == behavior.HintCloseLogMessageId => "close",
            var id when id == behavior.HintFarLogMessageId => "far",
            var id when id == behavior.HintBeyondFarLogMessageId => "beyond_far",
            _ => string.Empty,
        };

    private static string BuildTransitionMessage(TreasureHintEvent treasureEvent)
        => treasureEvent.Kind switch
        {
            TreasureHintKind.Hint => $"Treasure hint direction={FormatDirection(treasureEvent.Direction)} distance={FormatValue(treasureEvent.DistanceBucket)} raw=\"{treasureEvent.RawText}\".",
            TreasureHintKind.CofferReveal => $"Treasure coffer reveal: \"{treasureEvent.RawText}\".",
            TreasureHintKind.CofferMessage => $"Treasure coffer message: \"{treasureEvent.RawText}\".",
            TreasureHintKind.ElixirPrompt => $"Treasure prompt: \"{treasureEvent.RawText}\".",
            TreasureHintKind.BonusOffer => $"Bonus offer detected: \"{treasureEvent.RawText}\".",
            _ => $"Treasure event: \"{treasureEvent.RawText}\".",
        };

    private static string FormatDirection(TreasureDirection direction)
        => direction == TreasureDirection.Unknown ? "unknown" : direction.ToString().ToLowerInvariant();

    private static string FormatValue(string value)
        => value.Length == 0 ? "unknown" : value;

    private static string BuildDebugLogMessageSummary(ILogMessage message)
    {
        var ints = new List<string>();
        var strings = new List<string>();
        for (var i = 0; i < message.ParameterCount; i++)
        {
            if (message.TryGetIntParameter(i, out var intValue))
            {
                ints.Add($"{i}:{intValue}");
            }

            if (message.TryGetStringParameter(i, out var stringValue))
            {
                strings.Add($"{i}:\"{SanitizeLogText(stringValue.ExtractText())}\"");
            }
        }

        return $"id={message.LogMessageId} gameDataRow={message.GameData.RowId} paramCount={message.ParameterCount} ints=[{string.Join(", ", ints)}] strings=[{string.Join(", ", strings)}]";
    }

    private static string SanitizeLogText(string text)
        => text.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\"", "'", StringComparison.Ordinal);

    private static string FormatDebugValue(string value)
        => value.Length == 0 ? "none" : value;
}
