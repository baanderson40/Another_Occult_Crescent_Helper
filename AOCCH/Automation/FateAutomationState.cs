namespace AOCCH.Automation;

public enum FateAutomationState
{
    Idle,
    PlanningRoute,
    TravelingToFate,
    Participating,
    AwaitingCombatExit,
    Recovering,
    Completed,
    Stopped,
    Failed,
}
