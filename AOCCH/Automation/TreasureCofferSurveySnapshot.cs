using System;

namespace AOCCH.Automation;

public sealed class TreasureCofferSurveySnapshot
{
    public int Revision { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public uint LogMessageId { get; init; }
    public int SilverCount { get; init; }
    public int BronzeCount { get; init; }
    public string LastTransition { get; init; } = "None";

    public bool HasSurvey
        => Revision > 0;
}
