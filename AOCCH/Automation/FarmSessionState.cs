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
    RunningTreasureHunt,
    RunningCe,
    RunningFate,
    WaitingForDeathRecovery,
    ResumingInterruptedCe,
    ResumingInterruptedFate,
    ResumingInterruptedPotFate,
    IdleWaiting,
    Stopping,
    Stopped,
    Failed,
}
