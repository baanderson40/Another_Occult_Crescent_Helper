using System.Numerics;

namespace AOCCH.Scanning;

public sealed class ActiveFate
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public byte Progress { get; init; }
    public float Radius { get; init; }
    public Vector3 Position { get; init; }
    public bool HasKnownMetadata { get; init; }
    public string Demiatma { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string PreferredAethernet { get; init; } = string.Empty;
}
