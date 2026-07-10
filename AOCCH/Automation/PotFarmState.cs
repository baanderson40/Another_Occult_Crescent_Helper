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
    RecoveringToBase,
    Stopped,
    Completed,
    Failed,
}
