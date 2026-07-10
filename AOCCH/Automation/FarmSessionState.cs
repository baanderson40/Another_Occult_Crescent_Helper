namespace AOCCH.Automation;

public enum FarmSessionState
{
    Idle,
    Starting,
    WaitingForSouthHorn,
    ValidatingDependencies,
    RunningBuffRotation,
    RecoveringToBase,
    SelectingTarget,
    WaitingForPredictedPotWindow,
    WaitingAtPotSpawn,
    RunningPots,
    RunningCe,
    RunningFate,
    WaitingForDeathRecovery,
    IdleWaiting,
    Stopping,
    Stopped,
    Failed,
}
