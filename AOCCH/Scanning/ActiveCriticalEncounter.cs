namespace AOCCH.Scanning;

public sealed class ActiveCriticalEncounter
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public byte Progress { get; init; }
    public long StartTimestamp { get; init; }
    public bool HasKnownMetadata { get; init; }
    public string PreferredAethernet { get; init; } = string.Empty;
    public int Priority { get; init; }
}
