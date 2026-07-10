using System;

namespace AOCCH.Automation;

public sealed class TreasureHintEvent
{
    public TreasureHintKind Kind { get; init; }
    public string RawText { get; init; } = string.Empty;
    public string NormalizedText { get; init; } = string.Empty;
    public TreasureDirection Direction { get; init; }
    public string DistanceBucket { get; init; } = string.Empty;
    public string DistanceText { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; init; }
    public int Revision { get; init; }
}
