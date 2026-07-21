using System.Numerics;

namespace AOCCH.Scanning;

public sealed class ActiveFate
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public int StateCode { get; init; }
    public bool IsInFate { get; init; }
    public byte Progress { get; init; }
    public float Radius { get; init; }
    public Vector3 Position { get; init; }
    public float DistanceToPlayer { get; init; }
    public bool HasKnownMetadata { get; init; }
    public string Demiatma { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string PreferredAethernet { get; init; } = string.Empty;
    public bool IsExcluded { get; init; }
    public bool IsCandidate { get; init; }
    public bool HasLiveTarget { get; init; }
    public ulong LiveTargetObjectId { get; init; }
    public string LiveTargetName { get; init; } = string.Empty;
    public Vector3 LiveTargetPosition { get; init; }
}
