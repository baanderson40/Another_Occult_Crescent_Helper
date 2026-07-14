namespace AOCCH.Automation;

public enum TreasureCofferFarmState
{
    Idle,
    Starting,
    TravelingToSpot,
    TravelingToDangerousSpot,
    WaitingForVisibleCoffer,
    WaitingForInteractionHandoff,
    InteractingWithCoffer,
    AdvancingRoute,
    ReturningToBase,
    Completed,
    Stopped,
    Failed,
}
