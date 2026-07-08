using System.Numerics;

namespace AOCCH.Movement;

public sealed class RouteStep
{
    public RouteStepKind Kind { get; init; }
    public string Description { get; init; } = string.Empty;
    public Vector3 Destination { get; init; }
    public float ArrivalTolerance { get; init; }
    public string AethernetName { get; init; } = string.Empty;
    public uint AethernetPlaceNameId { get; init; }
}
