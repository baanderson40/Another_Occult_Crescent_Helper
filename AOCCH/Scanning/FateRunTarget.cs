using System.Numerics;

namespace AOCCH.Scanning;

public sealed class FateRunTarget
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public int StateCode { get; init; }
    public bool IsInFate { get; init; }
    public byte Progress { get; init; }
    public float Radius { get; init; }
    public Vector3 Position { get; init; }
    public string PreferredAethernet { get; init; } = string.Empty;
    public bool IsPotTarget { get; init; }
    public bool HasLiveTarget { get; init; }
    public ulong LiveTargetObjectId { get; init; }
    public string LiveTargetName { get; init; } = string.Empty;
    public Vector3 LiveTargetPosition { get; init; }
    public Vector3 Destination => HasLiveTarget ? LiveTargetPosition : Position;
}

public static class FateRunTargetExtensions
{
    public static FateRunTarget ToFateRunTarget(this ActiveFate fate)
        => new()
        {
            Id = fate.Id,
            Name = fate.Name,
            State = fate.State,
            StateCode = fate.StateCode,
            IsInFate = fate.IsInFate,
            Progress = fate.Progress,
            Radius = fate.Radius,
            Position = fate.Position,
            PreferredAethernet = fate.PreferredAethernet,
            IsPotTarget = false,
            HasLiveTarget = fate.HasLiveTarget,
            LiveTargetObjectId = fate.LiveTargetObjectId,
            LiveTargetName = fate.LiveTargetName,
            LiveTargetPosition = fate.LiveTargetPosition,
        };

    public static FateRunTarget ToFateRunTarget(this ActivePotFate fate)
        => new()
        {
            Id = fate.Id,
            Name = fate.Name,
            State = fate.State,
            StateCode = fate.StateCode,
            IsInFate = fate.IsInFate,
            Progress = fate.Progress,
            Radius = fate.Radius,
            Position = fate.Position,
            PreferredAethernet = fate.PreferredAethernet,
            IsPotTarget = true,
            HasLiveTarget = fate.HasLiveTarget,
            LiveTargetObjectId = fate.LiveTargetObjectId,
            LiveTargetName = fate.LiveTargetName,
            LiveTargetPosition = fate.LiveTargetPosition,
        };
}
