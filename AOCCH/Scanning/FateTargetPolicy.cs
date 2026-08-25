namespace AOCCH.Scanning;

public static class FateTargetPolicy
{
    // Persistent Pot BaseIds from the North Horn and South Horn pot FATEs.
    public static bool IsExcludedObjectiveBaseId(uint baseId)
        => baseId is 18280 or 18281 or 18282 or 18287 or 19849 or 19852;
}
