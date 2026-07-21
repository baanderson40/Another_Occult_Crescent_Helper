using System.Numerics;

namespace AOCCH.Scanning;

public sealed class ActiveCriticalEncounter
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public int StateCode { get; init; }
    public byte Progress { get; init; }
    public long StartTimestamp { get; init; }
    public bool HasKnownMetadata { get; init; }
    public string PreferredAethernet { get; init; } = string.Empty;
    public int Priority { get; init; }
    public float EngageRadius { get; init; }
    public Vector3 StagingPoint { get; init; }
    public bool IsCandidate { get; init; }
}
