namespace AOCCH.Automation;

public enum TreasureCofferFarmState
{
    Idle,
    Starting,
    TravelingToSpot,
    ClearingPreviousHideThreshold,
    TravelingToDangerousSpot,
    TravelingToThreatenedCoffer,
    WaitingForVisibleCoffer,
    WaitingForInteractionHandoff,
    InteractingWithCoffer,
    AdvancingRoute,
    ReturningToBase,
    Completed,
    Stopped,
    Failed,
}
