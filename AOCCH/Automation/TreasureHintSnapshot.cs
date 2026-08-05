using System;

namespace AOCCH.Automation;

public sealed class TreasureHintSnapshot
{
    public TreasureSessionState SessionState { get; init; }
    public int SessionId { get; init; }
    public string TerritoryKey { get; init; } = string.Empty;
    public uint TerritoryTypeId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public string CompletionReason { get; init; } = string.Empty;
    public int Revision { get; init; }
    public TreasureHintEvent? InitialHintEvent { get; init; }
    public TreasureHintEvent? LastHintEvent { get; init; }
    public TreasureHintEvent? LastEvent { get; init; }
    public TreasureHintEvent? RevealLatchedEvent { get; init; }
    public TreasureHintEvent? BonusOfferLatchedEvent { get; init; }
    public DateTimeOffset PostBuffGraceDeadlineAt { get; init; }
    public string LastTransition { get; init; } = "Idle";
    public string LastResetReason { get; init; } = string.Empty;

    public bool HasActiveSession
        => SessionState == TreasureSessionState.Active;

    public bool HasInitialHint
        => InitialHintEvent != null;

    public bool HasRevealLatched
        => RevealLatchedEvent != null;

    public bool HasBonusOfferLatched
        => BonusOfferLatchedEvent != null;

    public bool IsPostBuffGraceActive
        => PostBuffGraceDeadlineAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow < PostBuffGraceDeadlineAt;

    public string GetHintSummary()
    {
        var hint = LastHintEvent ?? InitialHintEvent;
        if (hint == null)
        {
            return "No hint captured";
        }

        var directionText = hint.Direction == TreasureDirection.Unknown ? "unknown" : hint.Direction.ToString().ToLowerInvariant();
        var distanceText = hint.DistanceBucket.Length == 0 ? "unknown" : hint.DistanceBucket;
        return $"{directionText} / {distanceText}";
    }
}
