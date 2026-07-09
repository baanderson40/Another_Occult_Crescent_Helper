using System.Collections.Generic;
using System.Numerics;

namespace AOCCH.Movement;

public sealed class PlannedRoute
{
    public string TargetDescription { get; init; } = string.Empty;
    public string RouteType { get; init; } = string.Empty;
    public string SelectionReason { get; init; } = string.Empty;
    public Vector3 FinalDestination { get; init; }
    public float EstimatedDistance { get; init; }
    public IReadOnlyList<RouteStep> Steps { get; init; } = [];
}
