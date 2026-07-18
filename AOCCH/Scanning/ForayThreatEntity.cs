using System.Numerics;

namespace AOCCH.Scanning;

public sealed class ForayThreatEntity
{
    public ulong ObjectId { get; init; }
    public string Name { get; init; } = string.Empty;
    public Vector3 Position { get; init; }
    public int KnowledgeLevel { get; init; }
    public float DistanceToPlayer { get; init; }
}
