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
    AwaitingBonusOffer,
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
