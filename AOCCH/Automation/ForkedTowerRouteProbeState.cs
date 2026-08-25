namespace AOCCH.Automation;

public enum ForkedTowerRouteProbeState
{
    Idle,
    Moving,
    WaitingForManualAdvance,
    WaitingForTransition,
    PausedForDeath,
    Completed,
    Stopped,
    Failed,
}
