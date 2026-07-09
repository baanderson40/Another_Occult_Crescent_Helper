namespace AOCCH.Movement;

public enum MovementState
{
    Idle,
    Planning,
    UsingReturn,
    Pathfinding,
    UsingAethernet,
    WaitingForArrival,
    Arrived,
    Stopped,
    TimedOut,
    Failed,
}
