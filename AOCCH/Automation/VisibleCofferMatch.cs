using AOCCH.Scanning;

namespace AOCCH.Automation;

public sealed class VisibleCofferMatch
{
    public CofferInteractionFlow Flow { get; init; } = CofferInteractionFlow.VisibleRoute;
    public TreasureCandidateKey CandidateKey { get; init; } = new();
    public VisibleCoffer Coffer { get; init; } = new();
    public float MatchDistance { get; init; }
    public bool IsTrustworthy { get; init; }
    public bool RequiresJumpAssist { get; init; }
    public float DistanceToNearestOtherCandidate { get; init; } = float.MaxValue;
    public string AttributionReason { get; init; } = string.Empty;
}
