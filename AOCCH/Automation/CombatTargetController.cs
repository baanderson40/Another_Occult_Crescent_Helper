using System;
using System.Linq;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AOCCH.Automation;

public sealed class CombatTargetController
{
    private const float CeBossSearchRadius = 100f;

    private readonly IObjectTable objectTable;
    private readonly GameActionController gameActionController;
    private readonly AocchLogger logger;
    private ulong ownedTargetObjectId;

    public CombatTargetController(IObjectTable objectTable, GameActionController gameActionController, AocchLogger logger)
    {
        this.objectTable = objectTable;
        this.gameActionController = gameActionController;
        this.logger = logger;
    }

    public bool MaintainFateTarget(FateRunTarget target)
    {
        if (target.IsPotTarget || !target.HasLiveTarget || target.LiveTargetObjectId == 0)
        {
            return false;
        }

        var liveTarget = objectTable.FirstOrDefault(gameObject => gameObject.GameObjectId == target.LiveTargetObjectId);
        return liveTarget != null
            && IsValidFateTarget(liveTarget)
            && EnsureTarget(liveTarget, "FATE combat target");
    }

    public bool MaintainCeTarget(ActiveCriticalEncounter target)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue)
        {
            return false;
        }

        var boss = objectTable
            .Where(gameObject => IsValidCeTarget(gameObject))
            .Where(gameObject => TryGetForayLevel(gameObject) == 1)
            .Where(gameObject => CalculateFlatDistance(playerPosition.Value, gameObject.Position) <= CeBossSearchRadius)
            .OrderBy(gameObject => CalculateFlatDistance(playerPosition.Value, gameObject.Position))
            .FirstOrDefault();

        return boss != null && EnsureTarget(boss, $"CE {target.Name} combat target");
    }

    public void ReleaseOwnedTarget(string reason)
    {
        if (ownedTargetObjectId == 0)
        {
            return;
        }

        var targetObjectId = ownedTargetObjectId;
        ownedTargetObjectId = 0;
        if (gameActionController.TryClearTarget(targetObjectId, reason))
        {
            logger.Info($"[CombatTarget] op=release-target objectId={targetObjectId:X} reason=\"{reason}\"");
        }
    }

    private bool EnsureTarget(IGameObject target, string description)
    {
        if (gameActionController.IsCurrentTarget(target))
        {
            if (ownedTargetObjectId != target.GameObjectId)
            {
                logger.Info($"[CombatTarget] op=target-maintained objectId={target.GameObjectId:X} name=\"{target.Name}\" description=\"{description}\" action=already-current");
            }

            ownedTargetObjectId = target.GameObjectId;
            return true;
        }

        if (!gameActionController.TrySetTarget(target, description))
        {
            return false;
        }

        ownedTargetObjectId = target.GameObjectId;
        logger.Info($"[CombatTarget] op=target-maintained objectId={target.GameObjectId:X} name=\"{target.Name}\" description=\"{description}\"");
        return true;
    }

    private static bool IsValidFateTarget(IGameObject gameObject)
        => gameObject is IBattleNpc
            && gameObject is ICharacter character
            && character.IsValid()
            && character.IsTargetable
            && character.CurrentHp > 0
            && IsHostile(character);

    private static bool IsValidCeTarget(IGameObject gameObject)
        => gameObject is IBattleNpc
            && gameObject is ICharacter character
            && character.IsValid()
            && character.IsTargetable
            && character.CurrentHp > 0;

    private static unsafe int TryGetForayLevel(IGameObject gameObject)
    {
        if (gameObject is not ICharacter character || character.Address == IntPtr.Zero)
        {
            return -1;
        }

        var characterPointer = (Character*)character.Address;
        if (characterPointer == null || characterPointer->VirtualTable == null)
        {
            return -1;
        }

        var forayInfo = characterPointer->GetForayInfo();
        if (forayInfo != null)
        {
            return forayInfo->Level;
        }

        if (gameObject is IBattleNpc)
        {
            return ((BattleChara*)characterPointer)->ForayInfo.Level;
        }

        return -1;
    }

    private static unsafe bool IsHostile(ICharacter character)
    {
        var nativeCharacter = (Character*)character.Address;
        return nativeCharacter != null && nativeCharacter->IsHostile;
    }

    private static float CalculateFlatDistance(System.Numerics.Vector3 left, System.Numerics.Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }
}
