namespace AOCCH.Automation;

public enum BuffRotationState
{
    Idle,
    Checking,
    MovingToBuffZone,
    Dismounting,
    SwitchingJob,
    Verifying,
    RestoringJob,
    Completed,
    Stopped,
    Failed,
    CriticalFailed,
}
