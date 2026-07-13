using System;
using System.Collections.Generic;

using AOCCH.Logging;
using AOCCH.Scanning;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class TreasureHintTracker : IDisposable
{
    private const uint TreasureCofferRevealLogMessageId = 10985u;
    private const uint TreasureHintImmediateLogMessageId = 10986u;
    private const uint TreasureHintCloseLogMessageId = 10987u;
    private const uint TreasureHintFarLogMessageId = 10988u;
    private const uint TreasureHintBeyondFarLogMessageId = 10989u;
    private const uint TreasureElixirPromptLogMessageId = 10990u;
    private const uint TreasureBonusOfferLogMessageId = 10994u;
    private const uint TreasureCofferSurveyCountsLogMessageId = 10965u;
    private const uint TreasureCofferSurveyEmptyLogMessageId = 10966u;
    private static readonly HashSet<uint> DebugTreasureLogMessageIds =
    [
        TreasureCofferSurveyCountsLogMessageId,
        TreasureCofferSurveyEmptyLogMessageId,
        TreasureCofferRevealLogMessageId,
        TreasureHintImmediateLogMessageId,
        TreasureHintCloseLogMessageId,
        TreasureHintFarLogMessageId,
        TreasureHintBeyondFarLogMessageId,
        TreasureElixirPromptLogMessageId,
        TreasureBonusOfferLogMessageId,
    ];

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

        logger.Info($"[TreasureHintTracker] op=debug-logmessage-capture-arm attempt={attemptId} reason=\"{SanitizeLogText(reason)}\" duration={duration.TotalSeconds:0.0}s ids={string.Join(",", DebugTreasureLogMessageIds)}");
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
                StartedAt = snapshot.StartedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                CompletionReason = reason,
                Revision = snapshot.Revision,
                InitialHintEvent = snapshot.InitialHintEvent,
                LastHintEvent = snapshot.LastHintEvent,
                LastEvent = snapshot.LastEvent,
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

        if (!scannerSnapshot.IsInSouthHorn)
        {
            if (Snapshot.HasActiveSession)
            {
                CompleteCurrentTreasureSession("Left South Horn during treasure tracking.", TreasureSessionState.Abandoned);
            }

            lastTreasureBuffState = false;
            return;
        }

        if (lastTreasureBuffState != true && scannerSnapshot.HasTreasureBuff)
        {
            BeginNewTreasureSession($"Treasure buff detected with {scannerSnapshot.TreasureBuffRemainingSeconds:0}s remaining.");
        }
        else if (lastTreasureBuffState == true && !scannerSnapshot.HasTreasureBuff)
        {
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

            logger.Info($"[TreasureHintTracker] op=treasure-logmessage-debug attempt={attemptId} source=testkeyitem reason=\"{SanitizeLogText(reason)}\" {summary}");
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
        lock (gate)
        {
            if (snapshot.SessionState != TreasureSessionState.Active)
            {
                return;
            }

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
            if (eventWithRevision.Kind == TreasureHintKind.Hint)
            {
                initialHint ??= eventWithRevision;
                lastHint = eventWithRevision;
            }

            updatedSnapshot = new TreasureHintSnapshot
            {
                SessionState = snapshot.SessionState,
                SessionId = snapshot.SessionId,
                StartedAt = snapshot.StartedAt,
                CompletedAt = snapshot.CompletedAt,
                CompletionReason = snapshot.CompletionReason,
                Revision = nextRevision,
                InitialHintEvent = initialHint,
                LastHintEvent = lastHint,
                LastEvent = eventWithRevision,
                LastTransition = BuildTransitionMessage(eventWithRevision),
                LastResetReason = snapshot.LastResetReason,
            };

            snapshot = updatedSnapshot;
        }

        logger.Info($"[TreasureHintTracker session={updatedSnapshot.SessionId}] op=event revision={updatedSnapshot.Revision} transition=\"{updatedSnapshot.LastTransition}\"");
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

            if (!DebugTreasureLogMessageIds.Contains(message.LogMessageId))
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

        switch (message.LogMessageId)
        {
            case TreasureCofferRevealLogMessageId:
                parsedEvent = new TreasureHintEvent
                {
                    Kind = TreasureHintKind.CofferReveal,
                    RawText = $"LogMessageId={message.LogMessageId}",
                    NormalizedText = $"logmessage:{message.LogMessageId}",
                };
                return true;
            case TreasureElixirPromptLogMessageId:
                parsedEvent = new TreasureHintEvent
                {
                    Kind = TreasureHintKind.ElixirPrompt,
                    RawText = $"LogMessageId={message.LogMessageId}",
                    NormalizedText = $"logmessage:{message.LogMessageId}",
                };
                return true;
            case TreasureBonusOfferLogMessageId:
                parsedEvent = new TreasureHintEvent
                {
                    Kind = TreasureHintKind.BonusOffer,
                    RawText = $"LogMessageId={message.LogMessageId}",
                    NormalizedText = $"logmessage:{message.LogMessageId}",
                };
                return true;
            case TreasureHintImmediateLogMessageId:
            case TreasureHintCloseLogMessageId:
            case TreasureHintFarLogMessageId:
            case TreasureHintBeyondFarLogMessageId:
                if (!message.TryGetIntParameter(0, out var directionValue))
                {
                    logger.Warning($"[TreasureHintTracker] op=hint-logmessage-parse-failed id={message.LogMessageId} reason=missing-direction-param");
                    return false;
                }

                var direction = MapDirection(directionValue);
                if (direction == TreasureDirection.Unknown)
                {
                    logger.Warning($"[TreasureHintTracker] op=hint-logmessage-parse-failed id={message.LogMessageId} reason=unknown-direction value={directionValue}");
                }

                parsedEvent = new TreasureHintEvent
                {
                    Kind = TreasureHintKind.Hint,
                    RawText = $"LogMessageId={message.LogMessageId}",
                    NormalizedText = $"logmessage:{message.LogMessageId}",
                    Direction = direction,
                    DistanceBucket = MapDistanceBucket(message.LogMessageId),
                    DistanceText = MapDistanceBucket(message.LogMessageId),
                };
                return true;
            default:
                return false;
        }
    }

    private bool TryParseCofferSurveyLogMessage(ILogMessage message, out TreasureCofferSurveySnapshot parsedSurvey)
    {
        parsedSurvey = null!;

        switch (message.LogMessageId)
        {
            case TreasureCofferSurveyCountsLogMessageId:
                if (TryParseCofferSurveyCounts(message, out var silverCount, out var bronzeCount))
                {
                    parsedSurvey = new TreasureCofferSurveySnapshot
                    {
                        LogMessageId = message.LogMessageId,
                        SilverCount = Math.Max(0, silverCount),
                        BronzeCount = Math.Max(0, bronzeCount),
                    };
                    return true;
                }

                return false;
            case TreasureCofferSurveyEmptyLogMessageId:
                parsedSurvey = new TreasureCofferSurveySnapshot
                {
                    LogMessageId = message.LogMessageId,
                    SilverCount = 0,
                    BronzeCount = 0,
                };
                return true;
            default:
                return false;
        }
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

    private static string MapDistanceBucket(uint logMessageId)
        => logMessageId switch
        {
            TreasureHintImmediateLogMessageId => "immediate",
            TreasureHintCloseLogMessageId => "close",
            TreasureHintFarLogMessageId => "far",
            TreasureHintBeyondFarLogMessageId => "beyond_far",
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
