namespace AOCCH.Movement;

public enum MovementState
{
    Idle,
    Planning,
    Pathfinding,
    UsingAethernet,
    WaitingForArrival,
    Arrived,
    Stopped,
    TimedOut,
    Failed,
}
