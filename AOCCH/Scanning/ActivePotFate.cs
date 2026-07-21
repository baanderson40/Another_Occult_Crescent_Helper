using System.Numerics;

namespace AOCCH.Scanning;

public sealed class ActivePotFate
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
    public string PreferredAethernet { get; init; } = string.Empty;
    public Vector3 CenterPosition { get; init; }
    public Vector3? StagingPosition { get; init; }
}
