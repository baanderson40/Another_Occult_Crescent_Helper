using System.Numerics;

namespace AOCCH.Movement;

public sealed class RouteStep
{
    public RouteStepKind Kind { get; init; }
    public string Description { get; init; } = string.Empty;
    public Vector3 Destination { get; init; }
    public float ArrivalTolerance { get; init; }
    public uint GeneralActionId { get; init; }
    public string AethernetName { get; init; } = string.Empty;
    public uint AethernetPlaceNameId { get; init; }
    public int AethernetCallbackValue { get; init; } = -1;
    public Vector3 InteractionCenter { get; init; }
    public float InteractDistanceMin { get; init; }
    public float InteractDistanceMax { get; init; }
    public bool ShouldMountBeforeStep { get; init; } = true;
    public bool ShouldDismountOnArrival { get; init; }
    public Vector3 EarlyDismountTarget { get; init; }
    public float EarlyDismountDistance { get; init; }
}
