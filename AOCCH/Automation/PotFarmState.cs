namespace AOCCH.Automation;

public enum PotFarmState
{
    Idle,
    Bootstrapping,
    WaitingForPredictedWindow,
    TravelingToSpawn,
    WaitingAtSpawn,
    RunningPotFate,
    WaitingForTreasureBuff,
    MovingNearTreasureCenter,
    TreasurePending,
    RunningTreasureSearch,
    RunningCofferInteraction,
    RecoveringToBase,
    ReturningForSecondChance,
    WaitingForSecondChanceDirection,
    TravelingToSecondChanceArea,
    PreparingSecondChanceWindCurrent,
    RunningSecondChanceSearch,
    Stopped,
    Completed,
    Failed,
}
