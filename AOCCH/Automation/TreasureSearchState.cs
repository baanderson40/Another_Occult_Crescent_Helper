namespace AOCCH.Automation;

public enum TreasureSearchState
{
    Idle,
    TravelingToCandidate,
    ProbingCandidate,
    RefiningCandidate,
    AcquiringRevealedCoffer,
    ReadyForInteraction,
    CandidatesExhausted,
    Stopped,
    Failed,
}
