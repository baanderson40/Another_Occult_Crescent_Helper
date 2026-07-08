namespace AOCCH.Automation;

public enum CriticalEngagementAutomationState
{
    Idle,
    PlanningRoute,
    TravelingToStaging,
    WaitingForEngage,
    InBattle,
    Recovering,
    Completed,
    Stopped,
    Failed,
}
