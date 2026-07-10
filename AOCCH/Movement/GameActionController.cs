using AOCCH.Logging;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AOCCH.Movement;

public sealed class GameActionController
{
    public const uint ReturnActionId = 8;
    public const uint MountActionId = 9;
    public const uint DismountActionId = 23;

    private readonly ITargetManager targetManager;
    private readonly AocchLogger logger;

    public GameActionController(ITargetManager targetManager, AocchLogger logger)
    {
        this.targetManager = targetManager;
        this.logger = logger;
    }

    public bool TrySetTarget(IGameObject gameObject, string description)
    {
        if (gameObject == null || !gameObject.IsValid())
        {
            logger.Warning($"Cannot target an invalid object for {description}.");
            return false;
        }

        targetManager.Target = gameObject;
        if (targetManager.Target?.GameObjectId != gameObject.GameObjectId)
        {
            logger.Warning($"Failed to set target to {gameObject.Name.TextValue} ({gameObject.GameObjectId:X}) for {description}.");
            return false;
        }

        logger.Info($"Set target to {gameObject.Name.TextValue} ({gameObject.GameObjectId:X}) for {description}.");
        return true;
    }

    public unsafe bool TryInteractWithObject(IGameObject gameObject, string description, bool checkLineOfSight = true)
    {
        if (gameObject == null || !gameObject.IsValid())
        {
            logger.Warning($"Cannot interact with an invalid object for {description}.");
            return false;
        }

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
        {
            logger.Warning($"Target system is unavailable for {description}.");
            return false;
        }

        var gameObjectPointer = (GameObjectStruct*)gameObject.Address;
        if (gameObjectPointer == null)
        {
            logger.Warning($"Failed to resolve object address for {description}.");
            return false;
        }

        var interactionResult = targetSystem->InteractWithObject(gameObjectPointer, checkLineOfSight);
        if (interactionResult == 0)
        {
            logger.Warning($"InteractWithObject returned 0 for {gameObject.Name.TextValue} ({gameObject.GameObjectId:X}) during {description}.");
            return false;
        }

        logger.Info($"Interacted with {gameObject.Name.TextValue} ({gameObject.GameObjectId:X}) for {description}.");
        return true;
    }

    public unsafe bool CanUseGeneralAction(uint actionId)
        => ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, actionId) == 0;

    public unsafe bool TryExecuteGeneralAction(uint actionId, string description)
    {
        if (!CanUseGeneralAction(actionId))
        {
            logger.Warning($"General action {actionId} is unavailable for {description}.");
            return false;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.GeneralAction, actionId);
        if (!used)
        {
            logger.Warning($"Failed to execute general action {actionId} for {description}.");
            return false;
        }

        logger.Info($"Executed general action {actionId} for {description}.");
        return true;
    }
}
