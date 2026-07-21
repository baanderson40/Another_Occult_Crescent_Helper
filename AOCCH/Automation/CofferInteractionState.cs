namespace AOCCH.Automation;

public enum CofferInteractionState
{
    Idle,
    ApproachingCoffer,
    TargetingCoffer,
    InteractingWithCoffer,
    WaitingForOpenConfirmation,
    Opened,
    LostCoffer,
    TimedOut,
    Stopped,
    Failed,
}
