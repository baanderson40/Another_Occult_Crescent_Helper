using System.Numerics;

namespace AOCCH.Scanning;

public sealed class ActiveCriticalEncounter
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public int StateCode { get; init; }
    public byte Progress { get; init; }
    public long StartTimestamp { get; init; }
    public bool HasKnownMetadata { get; init; }
    public string PreferredAethernet { get; init; } = string.Empty;
    public int Priority { get; init; }
    public float EngageRadius { get; init; }
    public Vector3 StagingPoint { get; init; }
    public bool IsCandidate { get; init; }

    public bool IsJoinable => IsJoinableState(StateCode);
    public bool IsWarmup => IsWarmupState(StateCode);
    public bool IsBattle => IsBattleState(StateCode);

    public static bool IsJoinableState(int stateCode)
        => stateCode == 1;

    public static bool IsWarmupState(int stateCode)
        => stateCode == 2;

    public static bool IsBattleState(int stateCode)
        => stateCode >= 3;
}
