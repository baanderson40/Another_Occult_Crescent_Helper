using System;

namespace AOCCH.Automation;

public sealed class PotFallbackStartDecision
{
    public bool AllowStart { get; init; }
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset DepartureAt { get; init; }
    public TimeSpan TimeUntilDeparture { get; init; }
}
