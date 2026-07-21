namespace AOCCH.Scanning;

public enum SelectedTargetKind
{
    None,
    CriticalEncounter,
    Fate,
}

public sealed class TargetSelection
{
    public static TargetSelection None { get; } = new();

    public SelectedTargetKind Kind { get; init; }
    public ActiveCriticalEncounter? CriticalEncounter { get; init; }
    public ActiveFate? Fate { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool WouldPreemptFate { get; init; }
}
