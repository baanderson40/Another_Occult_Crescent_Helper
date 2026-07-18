using System;
using System.Linq;

namespace AOCCH.Scanning;

public readonly record struct KnowledgeThreatPolicy(int HideOffset, float EnterDistance, float ExitDistance)
{
    public int GetHideAtOrAbove(int playerKnowledgeLevel)
        => Math.Clamp(playerKnowledgeLevel + HideOffset, 1, 28);
}

public static class KnowledgeThreatEvaluator
{
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
            .Where(entity => entity.DistanceToPlayer <= radius && entity.KnowledgeLevel >= threshold)
            .OrderByDescending(entity => entity.KnowledgeLevel)
            .ThenBy(entity => entity.DistanceToPlayer)
            .FirstOrDefault();
        return threat != null;
    }
}
