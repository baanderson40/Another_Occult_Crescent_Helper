namespace AOCCH.Automation;

public sealed class TreasureCandidateKey
{
    public uint FateId { get; init; }
    public string GroupKey { get; init; } = string.Empty;
    public string CandidateKey { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;

    public override string ToString()
        => $"{FateId}:{GroupKey}:{CandidateKey}";
}
