namespace AOCCH.Automation;

public sealed class TreasureCofferAutomaticModeStatus
{
    public bool DisabledForCurrentRun { get; init; }
    public bool RestoreRetryPending { get; init; }
    public int RemainingSilverCompletionsUntilRescan { get; init; }
    public int RemainingBronzeCompletionsUntilRescan { get; init; }
    public string LastTransition { get; init; } = "Idle";
}
