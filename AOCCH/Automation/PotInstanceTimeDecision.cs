namespace AOCCH.Automation;

public sealed class PotInstanceTimeDecision
{
    public bool ManageInstanceTimeEnabled { get; init; }
    public bool IsContentTimerAvailable { get; init; }
    public bool AllowNextPotCycle { get; init; } = true;
    public bool ShouldAttemptLeave { get; init; }
    public bool CanLeaveCurrentContent { get; set; }
    public float RemainingSeconds { get; init; }
    public float WaitSecondsUntilNextPot { get; init; }
    public float RequiredSeconds { get; init; }
    public string TimingSource { get; init; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
