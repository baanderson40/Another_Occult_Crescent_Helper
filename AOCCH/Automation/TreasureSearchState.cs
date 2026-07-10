namespace AOCCH.Automation;

public enum TreasureSearchState
{
    Idle,
    TravelingToCandidate,
    ReadyForInteraction,
    CandidatesExhausted,
    Stopped,
    Failed,
}
