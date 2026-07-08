namespace AOCCH.Automation;

public enum FateAutomationState
{
    Idle,
    PlanningRoute,
    TravelingToFate,
    Participating,
    Recovering,
    Completed,
    Stopped,
    Failed,
}
