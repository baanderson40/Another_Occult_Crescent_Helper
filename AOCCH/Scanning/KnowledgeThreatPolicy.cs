using System;
using System.Linq;

namespace AOCCH.Scanning;

public readonly record struct KnowledgeThreatPolicy(int HideOffset, float EnterDistance, float ExitDistance, int MaximumKnowledgeLevel = 28)
{
    public int GetHideAtOrAbove(int playerKnowledgeLevel)
        => Math.Clamp(playerKnowledgeLevel + HideOffset, 1, MaximumKnowledgeLevel);
}

public static class KnowledgeThreatEvaluator
{
    public const uint OccultIsleblazerBaseId = 17900;
    public const float OccultIsleblazerUnhideDistance = 5f;

    public static bool TryFindThreat(
        ScannerSnapshot snapshot,
        KnowledgeThreatPolicy policy,
        float radius,
        out ForayThreatEntity? threat,
        out int hideAtOrAbove)
    {
        threat = null;
        hideAtOrAbove = 0;
        if (!snapshot.PlayerForayLevel.HasValue)
        {
            return false;
        }

        hideAtOrAbove = policy.GetHideAtOrAbove(snapshot.PlayerForayLevel.Value);
        var threshold = hideAtOrAbove;
        threat = snapshot.NearbyForayEntities
            .Where(entity => entity.DistanceToPlayer <= radius
                && entity.KnowledgeLevel >= threshold
                && !IsHideException(entity))
            .OrderByDescending(entity => entity.KnowledgeLevel)
            .ThenBy(entity => entity.DistanceToPlayer)
            .FirstOrDefault();
        return threat != null;
    }

    public static bool TryFindHideException(ScannerSnapshot snapshot, float radius, out ForayThreatEntity? entity)
    {
        entity = snapshot.NearbyForayEntities
            .Where(candidate => candidate.DistanceToPlayer <= radius && IsHideException(candidate))
            .OrderBy(candidate => candidate.DistanceToPlayer)
            .FirstOrDefault();
        return entity != null;
    }

    private static bool IsHideException(ForayThreatEntity entity)
        => entity.BaseId == OccultIsleblazerBaseId;
}
