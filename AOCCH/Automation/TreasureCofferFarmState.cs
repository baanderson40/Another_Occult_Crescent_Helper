namespace AOCCH.Automation;

public enum TreasureCofferFarmState
{
    Idle,
    Starting,
    TravelingToSpot,
    WaitingForVisibleCoffer,
    InteractingWithCoffer,
    AdvancingRoute,
    Completed,
    Stopped,
    Failed,
}
