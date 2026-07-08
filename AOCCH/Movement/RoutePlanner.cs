using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Scanning;

namespace AOCCH.Movement;

public sealed class RoutePlanner
{
    private const float TargetArrivalTolerance = 5f;
    private const float AethernetSavingsThreshold = 120f;

    private readonly OccultCrescentData data;
    private readonly AocchLogger logger;

    public RoutePlanner(OccultCrescentData data, AocchLogger logger)
    {
        this.data = data;
        this.logger = logger;
    }

    public bool TryPlan(TargetSelection selection, Vector3 playerPosition, out PlannedRoute route, out string failureReason)
    {
        route = new PlannedRoute();
        failureReason = string.Empty;

        if (selection.Kind == SelectedTargetKind.None)
        {
            failureReason = "No CE or FATE target is currently selected.";
            return false;
        }

        var destination = GetDestination(selection);
        var targetDescription = GetTargetDescription(selection);
        if (destination == null)
        {
            failureReason = "The selected target does not have a valid destination.";
            return false;
        }

        var directDistance = CalculateFlatDistance(playerPosition, destination.Value);
        var preferredAethernet = GetPreferredAethernet(selection, destination.Value);
        if (preferredAethernet == null)
        {
            route = CreateDirectRoute(targetDescription, destination.Value, directDistance);
            return true;
        }

        var sourceAethernet = GetClosestAethernet(playerPosition);
        var sourceDistance = CalculateFlatDistance(playerPosition, sourceAethernet.Position.ToVector3());
        var destinationDistance = CalculateFlatDistance(preferredAethernet.Destination.ToVector3(), destination.Value);
        var aethernetDistance = sourceDistance + destinationDistance;
        var shouldUseAethernet = aethernetDistance + AethernetSavingsThreshold < directDistance;

        route = shouldUseAethernet
            ? CreateAethernetRoute(targetDescription, playerPosition, destination.Value, sourceAethernet, preferredAethernet, aethernetDistance)
            : CreateDirectRoute(targetDescription, destination.Value, directDistance);

        logger.Debug($"Planned {route.RouteType} route for {targetDescription}: direct={directDistance:0.0} aethernet={aethernetDistance:0.0}.");
        return true;
    }

    public PlannedRoute PlanBaseCampRecovery(Vector3 playerPosition)
    {
        var baseCamp = data.Aethernets.First(aethernet => string.Equals(aethernet.Name, "BaseCamp", StringComparison.OrdinalIgnoreCase));
        var destination = baseCamp.Position.ToVector3();
        var distance = CalculateFlatDistance(playerPosition, destination);

        return new PlannedRoute
        {
            TargetDescription = "Base Camp recovery",
            RouteType = "Recovery",
            FinalDestination = destination,
            EstimatedDistance = distance,
            Steps =
            [
                new RouteStep
                {
                    Kind = RouteStepKind.RecoverToBaseCamp,
                    Description = "Path to Base Camp aethernet",
                    Destination = destination,
                    ArrivalTolerance = baseCamp.InteractDistanceMax,
                },
            ],
        };
    }

    private PlannedRoute CreateDirectRoute(string targetDescription, Vector3 destination, float directDistance)
        => new()
        {
            TargetDescription = targetDescription,
            RouteType = "Direct",
            FinalDestination = destination,
            EstimatedDistance = directDistance,
            Steps =
            [
                new RouteStep
                {
                    Kind = RouteStepKind.PathToPoint,
                    Description = $"Path directly to {targetDescription}",
                    Destination = destination,
                    ArrivalTolerance = TargetArrivalTolerance,
                },
            ],
        };

    private PlannedRoute CreateAethernetRoute(
        string targetDescription,
        Vector3 playerPosition,
        Vector3 destination,
        AethernetData sourceAethernet,
        AethernetData destinationAethernet,
        float estimatedDistance)
    {
        var steps = new List<RouteStep>();
        var sourcePosition = sourceAethernet.Position.ToVector3();
        if (CalculateFlatDistance(playerPosition, sourcePosition) > sourceAethernet.InteractDistanceMax)
        {
            steps.Add(new RouteStep
            {
                Kind = RouteStepKind.PathToAethernet,
                Description = $"Move to {FormatAethernetName(sourceAethernet.Name)} aethernet",
                Destination = sourcePosition,
                ArrivalTolerance = sourceAethernet.InteractDistanceMax,
                AethernetName = sourceAethernet.Name,
                AethernetPlaceNameId = sourceAethernet.PlaceNameId,
            });
        }

        steps.Add(new RouteStep
        {
            Kind = RouteStepKind.AethernetTeleport,
            Description = $"Teleport to {FormatAethernetName(destinationAethernet.Name)}",
            Destination = destinationAethernet.Destination.ToVector3(),
            ArrivalTolerance = TargetArrivalTolerance,
            AethernetName = destinationAethernet.Name,
            AethernetPlaceNameId = destinationAethernet.PlaceNameId,
        });

        steps.Add(new RouteStep
        {
            Kind = RouteStepKind.PathToPoint,
            Description = $"Path from {FormatAethernetName(destinationAethernet.Name)} to {targetDescription}",
            Destination = destination,
            ArrivalTolerance = TargetArrivalTolerance,
        });

        return new PlannedRoute
        {
            TargetDescription = targetDescription,
            RouteType = "Aethernet",
            FinalDestination = destination,
            EstimatedDistance = estimatedDistance,
            Steps = steps,
        };
    }

    private AethernetData GetClosestAethernet(Vector3 position)
        => data.Aethernets
            .OrderBy(aethernet => CalculateFlatDistance(position, aethernet.Position.ToVector3()))
            .First();

    private AethernetData? GetPreferredAethernet(TargetSelection selection, Vector3 destination)
    {
        var preferredName = selection.Kind switch
        {
            SelectedTargetKind.CriticalEncounter => selection.CriticalEncounter?.PreferredAethernet,
            SelectedTargetKind.Fate => selection.Fate?.PreferredAethernet,
            _ => string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var explicitMatch = data.Aethernets.FirstOrDefault(aethernet => string.Equals(aethernet.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (explicitMatch != null)
            {
                return explicitMatch;
            }
        }

        return data.Aethernets
            .OrderBy(aethernet => CalculateFlatDistance(destination, aethernet.Destination.ToVector3()))
            .FirstOrDefault();
    }

    private static Vector3? GetDestination(TargetSelection selection)
        => selection.Kind switch
        {
            SelectedTargetKind.CriticalEncounter => selection.CriticalEncounter?.StagingPoint,
            SelectedTargetKind.Fate => selection.Fate?.Position,
            _ => null,
        };

    private static string GetTargetDescription(TargetSelection selection)
        => selection.Kind switch
        {
            SelectedTargetKind.CriticalEncounter => $"CE {selection.CriticalEncounter?.Name} ({selection.CriticalEncounter?.Id})",
            SelectedTargetKind.Fate => $"FATE {selection.Fate?.Name} ({selection.Fate?.Id})",
            _ => "Unknown target",
        };

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private static string FormatAethernetName(string name)
        => name switch
        {
            "BaseCamp" => "Base Camp",
            "TheWanderersHaven" => "The Wanderer's Haven",
            "CrystallizedCaverns" => "Crystallized Caverns",
            _ => name,
        };
}
