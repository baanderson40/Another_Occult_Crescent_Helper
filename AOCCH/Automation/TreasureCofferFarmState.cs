namespace AOCCH.Automation;

public enum TreasureCofferFarmState
{
    Idle,
    Starting,
    TravelingToSpot,
    ClearingPreviousHideThreshold,
    TravelingToDangerousSpot,
    TravelingToThreatenedCoffer,
    ReturningToBaseBetweenAreas,
    TravelingToNextArea,
    WaitingForVisibleCoffer,
    WaitingForInteractionHandoff,
    InteractingWithCoffer,
    AdvancingRoute,
    ReturningToBase,
    Completed,
    Stopped,
    Failed,
}
