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
    RunningCe,
    RunningFate,
    WaitingForDeathRecovery,
    IdleWaiting,
    Stopping,
    Stopped,
    Failed,
}
