namespace AOCCH.Automation;

public enum TreasureCofferFarmState
{
    Idle,
    Starting,
    TravelingToSpot,
    WaitingForVisibleCoffer,
    InteractingWithCoffer,
    AdvancingRoute,
    ReturningToBase,
    Completed,
    Stopped,
    Failed,
}
