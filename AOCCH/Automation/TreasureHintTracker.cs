using System;
using System.Collections.Generic;
using System.Text;

using AOCCH.Logging;
using AOCCH.Scanning;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class TreasureHintTracker : IDisposable
{
    private static readonly string[] DirectionScanOrder =
    [
        "north-east", "north-west", "south-east", "south-west",
        "northeast", "northwest", "southeast", "southwest",
        "north", "south", "east", "west",
    ];

    private static readonly Dictionary<string, TreasureDirection> DirectionAliases = new(StringComparer.Ordinal)
    {
        ["north"] = TreasureDirection.North,
        ["north-east"] = TreasureDirection.Northeast,
        ["northeast"] = TreasureDirection.Northeast,
        ["east"] = TreasureDirection.East,
        ["south-east"] = TreasureDirection.Southeast,
        ["southeast"] = TreasureDirection.Southeast,
        ["south"] = TreasureDirection.South,
        ["south-west"] = TreasureDirection.Southwest,
        ["southwest"] = TreasureDirection.Southwest,
        ["west"] = TreasureDirection.West,
        ["north-west"] = TreasureDirection.Northwest,
        ["northwest"] = TreasureDirection.Northwest,
    };

    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly OccultCrescentScanner scanner;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private TreasureHintSnapshot snapshot = new()
    {
        LastTransition = "Idle",
    };

    private bool? lastTreasureBuffState;
    private DateTimeOffset lastProcessedScannerUpdate = DateTimeOffset.MinValue;

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
        chatGui.ChatMessage += OnChatMessage;
        logger.Info("Treasure hint tracker initialized.");
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

    public void Dispose()
    {
        chatGui.ChatMessage -= OnChatMessage;
        framework.Update -= OnFrameworkUpdate;

        if (Snapshot.HasActiveSession)
        {
            CompleteCurrentTreasureSession("Treasure hint tracker disposal.", TreasureSessionState.Abandoned);
        }

        logger.Info("Treasure hint tracker stopped.");
    }

    public bool TryGetLatestHint(out TreasureHintEvent? hint)
    {
        var currentSnapshot = Snapshot;
        hint = currentSnapshot.LastHintEvent ?? currentSnapshot.InitialHintEvent;
        return hint != null;
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

        logger.Info($"Treasure session {next.SessionId} started: {reason}");
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

        logger.Info($"Treasure session {completedSnapshot.SessionId} ended: {reason}");
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

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!Snapshot.HasActiveSession)
        {
            return;
        }

        var chatText = message.Message.TextValue;
        if (chatText.Length == 0)
        {
            return;
        }

        var parsedEvent = ClassifyMessage(chatText);
        if (parsedEvent == null)
        {
            return;
        }

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

        logger.Info($"Treasure session {updatedSnapshot.SessionId}: {updatedSnapshot.LastTransition}");
    }

    private static TreasureHintEvent? ClassifyMessage(string message)
    {
        var normalized = NormalizeMessage(message);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Contains("guide you to another treasure coffer", StringComparison.Ordinal)
            || normalized.Contains("willing to guide you to another treasure coffer", StringComparison.Ordinal)
            || normalized.Contains("find another treasure", StringComparison.Ordinal))
        {
            return new TreasureHintEvent
            {
                Kind = TreasureHintKind.BonusOffer,
                RawText = message,
                NormalizedText = normalized,
            };
        }

        if (normalized.Contains("seems to be thirsty for elixir", StringComparison.Ordinal)
            || normalized.Contains("use a magical elixir", StringComparison.Ordinal))
        {
            return new TreasureHintEvent
            {
                Kind = TreasureHintKind.ElixirPrompt,
                RawText = message,
                NormalizedText = normalized,
            };
        }

        if (normalized.Contains("you discover a treasure coffer", StringComparison.Ordinal))
        {
            return new TreasureHintEvent
            {
                Kind = TreasureHintKind.CofferReveal,
                RawText = message,
                NormalizedText = normalized,
            };
        }

        if (TryParseHint(normalized, out var direction, out var distanceBucket, out var distanceText))
        {
            return new TreasureHintEvent
            {
                Kind = TreasureHintKind.Hint,
                RawText = message,
                NormalizedText = normalized,
                Direction = direction,
                DistanceBucket = distanceBucket,
                DistanceText = distanceText,
            };
        }

        if (normalized.Contains("treasure coffer", StringComparison.Ordinal))
        {
            return new TreasureHintEvent
            {
                Kind = TreasureHintKind.CofferMessage,
                RawText = message,
                NormalizedText = normalized,
            };
        }

        return null;
    }

    private static bool TryParseHint(string normalizedMessage, out TreasureDirection direction, out string distanceBucket, out string distanceText)
    {
        const string fullPrefix = "you sense something ";
        const string directionOnlyPrefix = "you sense something to the ";

        direction = TreasureDirection.Unknown;
        distanceBucket = string.Empty;
        distanceText = string.Empty;

        if (normalizedMessage.StartsWith(directionOnlyPrefix, StringComparison.Ordinal))
        {
            var directionText = normalizedMessage[directionOnlyPrefix.Length..];
            direction = FindDirection(directionText);
            if (direction == TreasureDirection.Unknown)
            {
                return false;
            }

            distanceBucket = "close";
            return true;
        }

        if (!normalizedMessage.StartsWith(fullPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var toTheIndex = normalizedMessage.IndexOf(" to the ", fullPrefix.Length, StringComparison.Ordinal);
        if (toTheIndex < 0)
        {
            return false;
        }

        distanceText = normalizedMessage[fullPrefix.Length..toTheIndex].Trim();
        if (distanceText.Length == 0)
        {
            return false;
        }

        var directionTextRaw = normalizedMessage[(toTheIndex + " to the ".Length)..].Trim();
        direction = FindDirection(directionTextRaw);
        if (direction == TreasureDirection.Unknown)
        {
            return false;
        }

        distanceBucket = NormalizeDistanceBucket(distanceText);
        return true;
    }

    private static string NormalizeDistanceBucket(string distanceText)
    {
        var parts = distanceText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && string.Equals(parts[0], parts[1], StringComparison.Ordinal)
            ? $"beyond_{parts[0]}"
            : distanceText;
    }

    private static TreasureDirection FindDirection(string message)
    {
        foreach (var rawDirection in DirectionScanOrder)
        {
            if (!message.Contains(rawDirection, StringComparison.Ordinal))
            {
                continue;
            }

            return DirectionAliases.GetValueOrDefault(rawDirection, TreasureDirection.Unknown);
        }

        return TreasureDirection.Unknown;
    }

    private static string NormalizeMessage(string message)
    {
        var builder = new StringBuilder(message.Length);
        var previousWasWhitespace = true;
        foreach (var character in message)
        {
            var normalizedCharacter = char.ToLowerInvariant(character);
            var isAllowed = char.IsLetterOrDigit(normalizedCharacter) || normalizedCharacter == '-' || normalizedCharacter == '\'';
            if (isAllowed)
            {
                builder.Append(normalizedCharacter);
                previousWasWhitespace = false;
                continue;
            }

            if (previousWasWhitespace)
            {
                continue;
            }

            builder.Append(' ');
            previousWasWhitespace = true;
        }

        return builder.ToString().Trim();
    }

    private static string BuildTransitionMessage(TreasureHintEvent treasureEvent)
        => treasureEvent.Kind switch
        {
            TreasureHintKind.Hint => $"Treasure hint direction={FormatDirection(treasureEvent.Direction)} distance={FormatValue(treasureEvent.DistanceBucket)} raw=\"{treasureEvent.RawText}\".",
            TreasureHintKind.CofferReveal => $"Treasure coffer reveal: \"{treasureEvent.RawText}\".",
            TreasureHintKind.CofferMessage => $"Treasure coffer message: \"{treasureEvent.RawText}\".",
            TreasureHintKind.ElixirPrompt => $"Treasure prompt: \"{treasureEvent.RawText}\".",
            TreasureHintKind.BonusOffer => $"Bonus offer seen but ignored: \"{treasureEvent.RawText}\".",
            _ => $"Treasure event: \"{treasureEvent.RawText}\".",
        };

    private static string FormatDirection(TreasureDirection direction)
        => direction == TreasureDirection.Unknown ? "unknown" : direction.ToString().ToLowerInvariant();

    private static string FormatValue(string value)
        => value.Length == 0 ? "unknown" : value;
}
