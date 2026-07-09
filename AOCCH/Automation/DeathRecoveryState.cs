namespace AOCCH.Automation;

public enum DeathRecoveryState
{
    Idle,
    DetectedDead,
    WaitingForRaise,
    WaitingForRaiseDialog,
    AcceptingRaise,
    Releasing,
    Recovered,
    Failed,
    Stopped,
}
