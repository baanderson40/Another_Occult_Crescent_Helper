namespace AOCCH.Automation;

public enum CriticalEngagementAutomationState
{
    Idle,
    PlanningRoute,
    TravelingToStaging,
    WaitingForEngage,
    InBattle,
    AwaitingCombatExit,
    Recovering,
    Completed,
    Stopped,
    Failed,
}
