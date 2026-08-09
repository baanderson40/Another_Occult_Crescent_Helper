namespace AOCCH.Automation;

public enum PostActivityRevivalState
{
    Idle,
    Scanning,
    SwitchingJob,
    WaitingForAction,
    MovingToTarget,
    Casting,
    WaitingForRaiseStatus,
    RestoringJob,
    Completed,
    Skipped,
    Failed,
}
