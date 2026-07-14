using System.Collections.Generic;
using System.Numerics;

namespace AOCCH.Data;

public sealed class OccultCrescentData
{
    public uint TerritoryTypeId { get; init; }
    public float AethernetInteractDistanceMin { get; init; }
    public float AethernetInteractDistanceMax { get; init; }
    public float MountedTravelSpeed { get; init; }
    public List<AethernetData> Aethernets { get; init; } = [];
    public List<CriticalEncounterData> CriticalEncounters { get; init; } = [];
    public List<FateData> Fates { get; init; } = [];
    public List<PotFateData> PotFates { get; init; } = [];
    public List<TreasureCofferGroupData> TreasureCofferGroups { get; init; } = [];
    public List<VisibleCofferFarmSpotData> VisibleCofferFarmSpots { get; init; } = [];
    public List<VisibleCofferFarmRouteEntryData> VisibleCofferFarmRoute { get; init; } = [];
    public List<FateAethernetPreference> FateAethernetPreferences { get; init; } = [];
    public List<DropData> Drops { get; init; } = [];
}

public sealed class AethernetData
{
    public string Name { get; init; } = string.Empty;
    public uint PlaceNameId { get; init; }
    public uint BaseId { get; init; }
    public Vector3Data Position { get; init; } = new();
    public Vector3Data Destination { get; init; } = new();
    public float InteractDistanceMin { get; init; }
    public float InteractDistanceMax { get; init; }
}

public sealed class CriticalEncounterData
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint TerritoryTypeId { get; init; }
    public string PreferredAethernet { get; init; } = string.Empty;
    public int Priority { get; init; }
    public float EngageRadius { get; init; }
    public Vector3Data StagingPoint { get; init; } = new();
}

public sealed class FateData
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Demiatma { get; init; }
    public string? Note { get; init; }
    public string? Aethernet { get; init; }
    public Vector3Data StartPosition { get; init; } = new();
}

public sealed class PotFateData
{
    public uint FateId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string PreferredAethernet { get; init; } = string.Empty;
    public Vector3Data CenterPosition { get; init; } = new();
    public Vector3Data? StagingPosition { get; init; }
}

public sealed class TreasureCofferGroupData
{
    public uint FateId { get; init; }
    public string GroupKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public List<TreasureCofferCandidateData> Candidates { get; init; } = [];
}

public sealed class TreasureCofferCandidateData
{
    public uint FateId { get; init; }
    public string GroupKey { get; init; } = string.Empty;
    public string CandidateKey { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public Vector3Data Position { get; init; } = new();
    public int AggroLevel { get; init; }
    public int? HideThresholdDistance { get; init; }
    public string? Notes { get; init; }
}

public sealed class VisibleCofferFarmSpotData
{
    public string Area { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public Vector3Data Position { get; init; } = new();
    public int AggroLevel { get; init; }
    public int? HideThresholdDistance { get; init; }
    public float? ArrivalDistance { get; init; }
    public bool RouteOnly { get; init; }
    public bool ForceHidden { get; init; }
    public bool ForceUnhidden { get; init; }
    public bool HideOnArrival { get; init; }
    public bool DisableExitHideThreshold { get; init; }
    public bool MountOnArrival { get; init; }
    public string? SpecialBranch { get; init; }
    public bool RecheckAscentSafetyOnArrival { get; init; }
    public bool RainSensitive { get; init; }
    public string? Note { get; init; }
}

public sealed class VisibleCofferFarmRouteEntryData
{
    public string Area { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

public sealed class FateAethernetPreference
{
    public uint FateId { get; init; }
    public string Aethernet { get; init; } = string.Empty;
}

public sealed class DropData
{
    public string SourceType { get; init; } = string.Empty;
    public uint SourceId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public uint? ItemId { get; init; }
    public string? Notes { get; init; }
}

public sealed class Vector3Data
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }

    public Vector3 ToVector3() => new(X, Y, Z);
}
