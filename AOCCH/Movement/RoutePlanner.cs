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
    private const float TransitionCompletionDistance = 25f;
    private const float TargetArrivalTolerance = 5f;
    private const float RouteSavingsThreshold = 0f;
    private const float ReturnPenaltySeconds = 7f;
    private const float AethernetTransitionPenaltySeconds = 3f;
    private const float BaseDirectThreshold = 120f;

    private readonly Configuration configuration;
    private readonly AocchLogger logger;

    public RoutePlanner(Configuration configuration, AocchLogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    public bool TryPlan(
        OccultCrescentTerritoryData territory,
        TargetSelection selection,
        Vector3 playerPosition,
        out PlannedRoute route,
        out string failureReason,
        bool allowReturn = true,
        Vector3? finalDestinationOverride = null,
        float? finalArrivalToleranceOverride = null)
    {
        route = new PlannedRoute();
        failureReason = string.Empty;

        if (selection.Kind == SelectedTargetKind.None)
        {
            failureReason = "No CE or FATE target is currently selected.";
            return false;
        }

        var destination = finalDestinationOverride ?? GetDestination(selection);
        var targetDescription = GetTargetDescription(selection);
        if (destination == null)
        {
            failureReason = "The selected target does not have a valid destination.";
            return false;
        }

        var directDistance = CalculateFlatDistance(playerPosition, destination.Value);
        var travelSpeed = MathF.Max(territory.MountedTravelSpeed, 1f);
        if (territory.Aethernets.Count == 0)
        {
            route = CreateDirectRoute(targetDescription, destination.Value, directDistance, finalArrivalToleranceOverride, earlyDismountDistance: null, earlyDismountTarget: null);
            logger.Warning($"[RoutePlanner] op=direct-fallback target=\"{targetDescription}\" reason=no-aethernet-data");
            return true;
        }

        var preferredAethernet = GetPreferredAethernet(territory, selection, destination.Value);
        if (preferredAethernet == null)
        {
            route = CreateDirectRoute(targetDescription, destination.Value, directDistance, finalArrivalToleranceOverride, earlyDismountDistance: null, earlyDismountTarget: null);
            return true;
        }

        var sourceAethernet = GetClosestAethernet(territory, playerPosition);
        if (sourceAethernet == null)
        {
            route = CreateDirectRoute(targetDescription, destination.Value, directDistance, finalArrivalToleranceOverride, earlyDismountDistance: null, earlyDismountTarget: null);
            logger.Warning($"[RoutePlanner] op=direct-fallback target=\"{targetDescription}\" reason=no-source-aethernet");
            return true;
        }

        var sourceDistance = CalculateFlatDistance(playerPosition, sourceAethernet.Position.ToVector3());
        var destinationDistance = CalculateFlatDistance(preferredAethernet.Destination.ToVector3(), destination.Value);
        var directTime = directDistance / travelSpeed;
        var aethernetTime = (sourceDistance / travelSpeed) + AethernetTransitionPenaltySeconds + (destinationDistance / travelSpeed);
        var returnTime = float.MaxValue;

        if (allowReturn && configuration.UseReturn && (selection.Kind == SelectedTargetKind.CriticalEncounter || selection.Kind == SelectedTargetKind.Fate))
        {
            returnTime = CalculateReturnTime(preferredAethernet, destination.Value, travelSpeed);
        }

        route = ChooseRoute(territory, targetDescription, playerPosition, destination.Value, directDistance, directTime, sourceAethernet, preferredAethernet, aethernetTime, returnTime, finalArrivalToleranceOverride, earlyDismountDistance: null, earlyDismountTarget: null, shouldMountBeforeStep: true);

        logger.Info($"[RoutePlanner] op=route-selected target=\"{targetDescription}\" routeType={route.RouteType} selectionReason={route.SelectionReason} direct={directTime:0.0}s aethernet={aethernetTime:0.0}s return={(float.IsFinite(returnTime) ? $"{returnTime:0.0}s" : "disabled")}");
        return true;
    }

    public bool TryPlan(
        OccultCrescentTerritoryData territory,
        FateRunTarget target,
        Vector3 playerPosition,
        out PlannedRoute route,
        out string failureReason,
        bool allowReturn = true,
        Vector3? finalDestinationOverride = null,
        float? finalArrivalToleranceOverride = null,
        float? earlyDismountDistance = null)
    {
        var earlyDismountTarget = target.Destination;
        var destination = finalDestinationOverride ?? target.Destination;
        var planned = TryPlanToLocation(
            territory,
            $"FATE {target.Name} ({target.Id})",
            target.PreferredAethernet,
            destination,
            playerPosition,
            out route,
            out failureReason,
            allowReturn,
            finalArrivalToleranceOverride,
            earlyDismountDistance,
            earlyDismountTarget);
        if (planned && earlyDismountDistance.HasValue)
        {
            var source = target.HasLiveTarget ? $"live-target:{target.LiveTargetName}" : "fate-center";
            logger.Info($"[RoutePlanner] op=fate-early-dismount target=\"{target.Name}\" ({target.Id}) source={source} distance={earlyDismountDistance.Value:0.0} targetPos={FormatVector(earlyDismountTarget)}");
        }

        if (planned)
        {
            var source = finalDestinationOverride.HasValue ? "override" : target.HasLiveTarget ? $"live-target:{target.LiveTargetName}" : "fate-center";
            logger.Info($"[RoutePlanner] op=fate-destination target=\"{target.Name}\" ({target.Id}) source={source} destination={FormatVector(destination)}");
        }

        return planned;
    }

    public bool TryPlanToLocation(
        OccultCrescentTerritoryData territory,
        string targetDescription,
        string preferredAethernetName,
        Vector3 destination,
        Vector3 playerPosition,
        out PlannedRoute route,
        out string failureReason,
        bool allowReturn = true,
        float? finalArrivalToleranceOverride = null,
        float? earlyDismountDistance = null,
        Vector3? earlyDismountTarget = null,
        bool shouldMountBeforeStep = true,
        bool forceAethernet = false)
    {
        route = new PlannedRoute();
        failureReason = string.Empty;

        if (IsZeroVector(destination))
        {
            failureReason = $"Destination is invalid for {targetDescription}: {FormatVector(destination)}.";
            return false;
        }

        var directDistance = CalculateFlatDistance(playerPosition, destination);
        var travelSpeed = MathF.Max(territory.MountedTravelSpeed, 1f);
        if (territory.Aethernets.Count == 0)
        {
            route = CreateDirectRoute(targetDescription, destination, directDistance, finalArrivalToleranceOverride, earlyDismountDistance, earlyDismountTarget, shouldMountBeforeStep);
            logger.Warning($"[RoutePlanner] op=direct-fallback target=\"{targetDescription}\" reason=no-aethernet-data");
            return true;
        }

        var preferredAethernet = GetPreferredAethernet(territory, preferredAethernetName, destination);
        if (preferredAethernet == null)
        {
            route = CreateDirectRoute(targetDescription, destination, directDistance, finalArrivalToleranceOverride, earlyDismountDistance, earlyDismountTarget, shouldMountBeforeStep);
            return true;
        }

        var sourceAethernet = GetClosestAethernet(territory, playerPosition);
        if (sourceAethernet == null)
        {
            route = CreateDirectRoute(targetDescription, destination, directDistance, finalArrivalToleranceOverride, earlyDismountDistance, earlyDismountTarget, shouldMountBeforeStep);
            logger.Warning($"[RoutePlanner] op=direct-fallback target=\"{targetDescription}\" reason=no-source-aethernet");
            return true;
        }

        var sourceDistance = CalculateFlatDistance(playerPosition, sourceAethernet.Position.ToVector3());
        var destinationDistance = CalculateFlatDistance(preferredAethernet.Destination.ToVector3(), destination);
        var directTime = directDistance / travelSpeed;
        var aethernetTime = (sourceDistance / travelSpeed) + AethernetTransitionPenaltySeconds + (destinationDistance / travelSpeed);
        var returnTime = allowReturn && configuration.UseReturn
            ? CalculateReturnTime(preferredAethernet, destination, travelSpeed)
            : float.MaxValue;

        route = forceAethernet
            ? CreateAethernetRoute(targetDescription, playerPosition, destination, sourceAethernet, preferredAethernet, directDistance, finalArrivalToleranceOverride, earlyDismountDistance, earlyDismountTarget, shouldMountBeforeStep)
            : ChooseRoute(territory, targetDescription, playerPosition, destination, directDistance, directTime, sourceAethernet, preferredAethernet, aethernetTime, returnTime, finalArrivalToleranceOverride, earlyDismountDistance, earlyDismountTarget, shouldMountBeforeStep);

        logger.Info($"[RoutePlanner] op=route-selected target=\"{targetDescription}\" routeType={route.RouteType} selectionReason={route.SelectionReason} direct={directTime:0.0}s aethernet={aethernetTime:0.0}s return={(float.IsFinite(returnTime) ? $"{returnTime:0.0}s" : "disabled")}");
        return true;
    }

    public bool TryPlanBaseCampRecovery(OccultCrescentTerritoryData territory, Vector3 playerPosition, out PlannedRoute route, out string failureReason, bool allowReturn = true)
    {
        route = new PlannedRoute();
        failureReason = string.Empty;

        var baseCamp = territory.GetBaseCampAethernet();
        if (baseCamp == null)
        {
            failureReason = "Base Camp aethernet data is unavailable.";
            return false;
        }

        var destination = baseCamp.Position.ToVector3();
        var distance = CalculateFlatDistance(playerPosition, destination);

        if (allowReturn && configuration.UseReturn && distance > BaseDirectThreshold)
        {
            route = new PlannedRoute
            {
                TargetDescription = "Base Camp recovery",
                RouteType = "Return",
                SelectionReason = "return_recovery",
                FinalDestination = destination,
                EstimatedDistance = distance,
                Steps =
                [
                    new RouteStep
                    {
                        Kind = RouteStepKind.Return,
                        Description = "Return to Base Camp",
                        Destination = baseCamp.Destination.ToVector3(),
                        ArrivalTolerance = TransitionCompletionDistance,
                        GeneralActionId = GameActionController.ReturnActionId,
                        AethernetName = baseCamp.Name,
                    },
                    new RouteStep
                    {
                        Kind = RouteStepKind.RecoverToBaseCamp,
                        Description = "Path to Base Camp aethernet",
                        Destination = destination,
                        ArrivalTolerance = baseCamp.InteractDistanceMax,
                        AethernetName = baseCamp.Name,
                        InteractionCenter = destination,
                        InteractDistanceMin = baseCamp.InteractDistanceMin,
                        InteractDistanceMax = baseCamp.InteractDistanceMax,
                        ShouldDismountOnArrival = true,
                        ShouldMountBeforeStep = false,
                    },
                ],
            };
            return true;
        }

        route = new PlannedRoute
        {
            TargetDescription = "Base Camp recovery",
            RouteType = "Recovery",
            SelectionReason = allowReturn && configuration.UseReturn ? "direct_recovery_near_base" : "direct_recovery_return_disabled",
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
                    AethernetName = baseCamp.Name,
                    InteractionCenter = destination,
                    InteractDistanceMin = baseCamp.InteractDistanceMin,
                    InteractDistanceMax = baseCamp.InteractDistanceMax,
                    ShouldDismountOnArrival = true,
                    ShouldMountBeforeStep = false,
                },
            ],
        };
        return true;
    }

    private PlannedRoute ChooseRoute(
        OccultCrescentTerritoryData territory,
        string targetDescription,
        Vector3 playerPosition,
        Vector3 destination,
        float directDistance,
        float directTime,
        AethernetData sourceAethernet,
        AethernetData preferredAethernet,
        float aethernetTime,
        float returnTime,
        float? finalArrivalToleranceOverride,
        float? earlyDismountDistance,
        Vector3? earlyDismountTarget,
        bool shouldMountBeforeStep)
    {
        var baseCamp = territory.GetBaseCampAethernet();
        var closeToBaseCamp = baseCamp != null
            && string.Equals(preferredAethernet.Name, baseCamp.Name, StringComparison.OrdinalIgnoreCase)
            && CalculateFlatDistance(playerPosition, baseCamp.Position.ToVector3()) <= BaseDirectThreshold;

        if (closeToBaseCamp)
        {
            return CreateDirectRoute(targetDescription, destination, directDistance, finalArrivalToleranceOverride, earlyDismountDistance, earlyDismountTarget, shouldMountBeforeStep);
        }

        if ((directTime + RouteSavingsThreshold) <= aethernetTime
            && (directTime + RouteSavingsThreshold) <= returnTime)
        {
            return CreateDirectRoute(targetDescription, destination, directDistance, finalArrivalToleranceOverride, earlyDismountDistance, earlyDismountTarget, shouldMountBeforeStep);
        }

        if (baseCamp != null && returnTime + RouteSavingsThreshold < aethernetTime)
        {
            return CreateReturnRoute(targetDescription, playerPosition, destination, baseCamp, preferredAethernet, directDistance, finalArrivalToleranceOverride, earlyDismountDistance, earlyDismountTarget, shouldMountBeforeStep);
        }

        return CreateAethernetRoute(targetDescription, playerPosition, destination, sourceAethernet, preferredAethernet, directDistance, finalArrivalToleranceOverride, earlyDismountDistance, earlyDismountTarget, shouldMountBeforeStep);
    }

    private PlannedRoute CreateDirectRoute(string targetDescription, Vector3 destination, float directDistance, float? finalArrivalToleranceOverride, float? earlyDismountDistance, Vector3? earlyDismountTarget, bool shouldMountBeforeStep = true)
        => new()
        {
            TargetDescription = targetDescription,
            RouteType = "Direct",
            SelectionReason = "direct_route",
            FinalDestination = destination,
            EstimatedDistance = directDistance,
            Steps =
            [
                new RouteStep
                {
                    Kind = RouteStepKind.PathToPoint,
                    Description = $"Path directly to {targetDescription}",
                    Destination = destination,
                    ArrivalTolerance = finalArrivalToleranceOverride ?? TargetArrivalTolerance,
                    ShouldMountBeforeStep = shouldMountBeforeStep,
                    EarlyDismountDistance = earlyDismountDistance ?? 0f,
                    EarlyDismountTarget = earlyDismountTarget ?? Vector3.Zero,
                },
            ],
        };

    private PlannedRoute CreateAethernetRoute(
        string targetDescription,
        Vector3 playerPosition,
        Vector3 destination,
        AethernetData sourceAethernet,
        AethernetData destinationAethernet,
        float estimatedDistance,
        float? finalArrivalToleranceOverride,
        float? earlyDismountDistance,
        Vector3? earlyDismountTarget,
        bool shouldMountBeforeStep)
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
                InteractionCenter = sourcePosition,
                InteractDistanceMin = sourceAethernet.InteractDistanceMin,
                InteractDistanceMax = sourceAethernet.InteractDistanceMax,
                ShouldMountBeforeStep = shouldMountBeforeStep,
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
            AethernetCallbackValue = destinationAethernet.CallbackValue ?? -1,
        });

        steps.Add(new RouteStep
        {
            Kind = RouteStepKind.PathToPoint,
            Description = $"Path from {FormatAethernetName(destinationAethernet.Name)} to {targetDescription}",
            Destination = destination,
            ArrivalTolerance = finalArrivalToleranceOverride ?? TargetArrivalTolerance,
            ShouldMountBeforeStep = shouldMountBeforeStep,
            EarlyDismountDistance = earlyDismountDistance ?? 0f,
            EarlyDismountTarget = earlyDismountTarget ?? Vector3.Zero,
        });

        return new PlannedRoute
        {
            TargetDescription = targetDescription,
            RouteType = "Aethernet",
            SelectionReason = "aethernet_route",
            FinalDestination = destination,
            EstimatedDistance = estimatedDistance,
            Steps = steps,
        };
    }

    private PlannedRoute CreateReturnRoute(
        string targetDescription,
        Vector3 playerPosition,
        Vector3 destination,
        AethernetData baseCamp,
        AethernetData destinationAethernet,
        float estimatedDistance,
        float? finalArrivalToleranceOverride,
        float? earlyDismountDistance,
        Vector3? earlyDismountTarget,
        bool shouldMountBeforeStep)
    {
        var steps = new List<RouteStep>
        {
            new()
            {
                Kind = RouteStepKind.Return,
                Description = $"Return before traveling to {targetDescription}",
                Destination = baseCamp.Destination.ToVector3(),
                ArrivalTolerance = TransitionCompletionDistance,
                GeneralActionId = GameActionController.ReturnActionId,
                AethernetName = baseCamp.Name,
            },
        };

        if (!string.Equals(destinationAethernet.Name, baseCamp.Name, StringComparison.OrdinalIgnoreCase))
        {
            steps.Add(new RouteStep
            {
                Kind = RouteStepKind.PathToAethernet,
                Description = $"Move to {FormatAethernetName(baseCamp.Name)} aethernet after Return",
                Destination = baseCamp.Position.ToVector3(),
                ArrivalTolerance = baseCamp.InteractDistanceMax,
                AethernetName = baseCamp.Name,
                AethernetPlaceNameId = baseCamp.PlaceNameId,
                InteractionCenter = baseCamp.Position.ToVector3(),
                InteractDistanceMin = baseCamp.InteractDistanceMin,
                InteractDistanceMax = baseCamp.InteractDistanceMax,
                ShouldMountBeforeStep = false,
            });

            steps.Add(new RouteStep
            {
                Kind = RouteStepKind.AethernetTeleport,
                Description = $"Teleport to {FormatAethernetName(destinationAethernet.Name)} after Return",
                Destination = destinationAethernet.Destination.ToVector3(),
                ArrivalTolerance = TargetArrivalTolerance,
                AethernetName = destinationAethernet.Name,
                AethernetPlaceNameId = destinationAethernet.PlaceNameId,
                AethernetCallbackValue = destinationAethernet.CallbackValue ?? -1,
            });
        }

        steps.Add(new RouteStep
        {
            Kind = RouteStepKind.PathToPoint,
            Description = $"Path from Return route to {targetDescription}",
            Destination = destination,
            ArrivalTolerance = finalArrivalToleranceOverride ?? TargetArrivalTolerance,
            ShouldMountBeforeStep = shouldMountBeforeStep,
            EarlyDismountDistance = earlyDismountDistance ?? 0f,
            EarlyDismountTarget = earlyDismountTarget ?? Vector3.Zero,
        });

        return new PlannedRoute
        {
            TargetDescription = targetDescription,
            RouteType = "Return",
            SelectionReason = "return_route",
            FinalDestination = destination,
            EstimatedDistance = estimatedDistance,
            Steps = steps,
        };
    }

    private AethernetData? GetClosestAethernet(OccultCrescentTerritoryData territory, Vector3 position)
        => territory.Aethernets
            .OrderBy(aethernet => CalculateFlatDistance(position, aethernet.Position.ToVector3()))
            .FirstOrDefault();

    private AethernetData? GetPreferredAethernet(OccultCrescentTerritoryData territory, TargetSelection selection, Vector3 destination)
    {
        var preferredName = selection.Kind switch
        {
            SelectedTargetKind.CriticalEncounter => selection.CriticalEncounter?.PreferredAethernet,
            SelectedTargetKind.Fate => selection.Fate?.PreferredAethernet,
            _ => string.Empty,
        };

        return GetPreferredAethernet(territory, preferredName, destination);
    }

    private AethernetData? GetPreferredAethernet(OccultCrescentTerritoryData territory, string? preferredName, Vector3 destination)
    {
        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var explicitMatch = territory.Aethernets.FirstOrDefault(aethernet => string.Equals(aethernet.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (explicitMatch != null)
            {
                return explicitMatch;
            }
        }

        return territory.Aethernets
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

    private static bool IsZeroVector(Vector3 value)
        => value.X == 0f && value.Y == 0f && value.Z == 0f;

    private static string FormatVector(Vector3 value)
        => $"<{value.X:0.000}, {value.Y:0.000}, {value.Z:0.000}>";

    private static string FormatAethernetName(string name)
        => name switch
        {
            "BaseCamp" => "Base Camp",
            "TheWanderersHaven" => "The Wanderer's Haven",
            "CrystallizedCaverns" => "Crystallized Caverns",
            _ => name,
        };

    private float CalculateReturnTime(AethernetData preferredAethernet, Vector3 destination, float travelSpeed)
        => ReturnPenaltySeconds
            + (preferredAethernet.IsBaseCamp ? 0f : AethernetTransitionPenaltySeconds)
            + (CalculateFlatDistance(preferredAethernet.Destination.ToVector3(), destination) / travelSpeed);

}
