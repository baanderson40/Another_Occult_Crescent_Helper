using System.Numerics;

namespace AOCCH.Scanning;

public sealed class DetectedTreasure
{
    public ulong GameObjectId { get; init; }
    public uint DataId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ObjectKind { get; init; } = string.Empty;
    public Vector3 Position { get; init; }
    public float DistanceToPlayer { get; init; }
    public bool IsTargetable { get; init; }
}
