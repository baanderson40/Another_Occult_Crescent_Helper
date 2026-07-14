namespace AOCCH.Automation;

public enum TreasureCofferFarmState
{
    Idle,
    Starting,
    TravelingToSpot,
    WaitingForVisibleCoffer,
    WaitingForInteractionHandoff,
    InteractingWithCoffer,
    AdvancingRoute,
    ReturningToBase,
    Completed,
    Stopped,
    Failed,
}
