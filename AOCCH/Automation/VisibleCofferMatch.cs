using AOCCH.Scanning;

namespace AOCCH.Automation;

public sealed class VisibleCofferMatch
{
    public TreasureCandidateKey CandidateKey { get; init; } = new();
    public VisibleCoffer Coffer { get; init; } = new();
    public float MatchDistance { get; init; }
    public string AttributionReason { get; init; } = string.Empty;
}
