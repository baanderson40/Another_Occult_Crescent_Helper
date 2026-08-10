namespace AOCCH.Automation;

public enum CofferInteractionState
{
    Idle,
    ApproachingCoffer,
    TargetingCoffer,
    InteractingWithCoffer,
    WaitingForOpenConfirmation,
    RepositioningPastCoffer,
    ReturningToCoffer,
    Opened,
    LostCoffer,
    TimedOut,
    Stopped,
    Failed,
}
