using System;

using AOCCH.Logging;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AOCCH.Movement;

public sealed class GameActionController
{
    public const uint ReturnActionId = 8;
    public const uint MountActionId = 9;
    public const uint DismountActionId = 23;
    public const uint HideActionId = 2245;
    public const uint NinjaClassJobId = 30;
    public const uint MagicalElixirEventItemId = 2003296;
    public const string MagicalElixirKeyItemName = "Magical Elixir";

    private readonly ICommandManager commandManager;
    private readonly ICondition condition;
    private readonly IPlayerState playerState;
    private readonly ITargetManager targetManager;
    private readonly AocchLogger logger;

    public GameActionController(ICommandManager commandManager, ICondition condition, IPlayerState playerState, ITargetManager targetManager, AocchLogger logger)
    {
        this.commandManager = commandManager;
        this.condition = condition;
        this.playerState = playerState;
        this.targetManager = targetManager;
        this.logger = logger;
    }

    public bool IsStealthed
        => condition[ConditionFlag.Stealthed];

    public uint CurrentClassJobId
        => playerState.ClassJob.RowId;

    public bool IsOnClassJob(uint classJobId)
        => CurrentClassJobId == classJobId;

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

    public unsafe bool CanUseAction(uint actionId)
        => ActionManager.Instance()->GetActionStatus(ActionType.Action, actionId) == 0;

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

    public unsafe bool TryExecuteAction(uint actionId, string description)
    {
        if (!CanUseAction(actionId))
        {
            logger.Warning($"Action {actionId} is unavailable for {description}.");
            return false;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId);
        if (!used)
        {
            logger.Warning($"Failed to execute action {actionId} for {description}.");
            return false;
        }

        logger.Info($"Executed action {actionId} for {description}.");
        return true;
    }

    public bool CanUseHide()
        => CanUseAction(HideActionId);

    public bool TryEquipGearset(int gearsetNumber, string description)
    {
        if (gearsetNumber <= 0)
        {
            logger.Warning($"Cannot equip an unconfigured gearset for {description}.");
            return false;
        }

        var command = $"/gearset change {gearsetNumber}";
        if (!commandManager.ProcessCommand(command))
        {
            logger.Warning($"Failed to dispatch gearset command '{command}' for {description}.");
            return false;
        }

        logger.Info($"Dispatched gearset command '{command}' for {description}.");
        return true;
    }

    public bool TryUseKeyItem(string keyItemName, string description)
    {
        if (string.IsNullOrWhiteSpace(keyItemName))
        {
            logger.Warning($"Cannot use an unnamed key item for {description}.");
            return false;
        }

        var escapedKeyItemName = keyItemName.Replace("\"", "\\\"", StringComparison.Ordinal);
        var command = $"/keyitem \"{escapedKeyItemName}\"";
        if (!commandManager.ProcessCommand(command))
        {
            logger.Warning($"Failed to dispatch key item command '{command}' for {description}.");
            return false;
        }

        logger.Info($"Dispatched key item command '{command}' for {description}.");
        return true;
    }

    public unsafe bool TryUseInventoryItem(uint itemId, bool isHighQuality, string description)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            logger.Warning($"InventoryManager is unavailable for {description}.");
            return false;
        }

        var count = inventoryManager->GetInventoryItemCount(itemId, isHighQuality);
        if (count <= 0)
        {
            logger.Warning($"Item {itemId} is unavailable for {description}.");
            return false;
        }

        var agent = AgentInventoryContext.Instance();
        if (agent == null)
        {
            logger.Warning($"AgentInventoryContext is unavailable for {description}.");
            return false;
        }

        var itemToUse = isHighQuality ? itemId + 1_000_000u : itemId;
        var result = agent->UseItem(itemToUse);
        if (result != 0)
        {
            logger.Warning($"UseItem({itemToUse}) returned {result} for {description}.");
            return false;
        }

        logger.Info($"Used inventory item {itemToUse} for {description}.");
        return true;
    }

    public unsafe bool TryUseKeyInventoryItem(uint itemId, string itemName, string description)
    {
        if (itemId == 0)
        {
            logger.Warning($"Cannot use key item 0 for {description}.");
            return false;
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            logger.Warning($"InventoryManager is unavailable for {description}.");
            return false;
        }

        var keyItemContainer = inventoryManager->GetInventoryContainer(InventoryType.KeyItems);
        if (keyItemContainer == null)
        {
            logger.Warning($"Key item container is unavailable for {description}.");
            return false;
        }

        if (!keyItemContainer->IsLoaded || keyItemContainer->Size <= 0 || keyItemContainer->Items == null)
        {
            logger.Warning($"Key item container is not ready for {description}. loaded={keyItemContainer->IsLoaded} size={keyItemContainer->Size}.");
            return false;
        }

        InventoryItem* itemSlot = null;
        for (var i = 0; i < keyItemContainer->Size; i++)
        {
            var candidate = keyItemContainer->GetInventorySlot(i);
            if (candidate == null || candidate->IsEmpty())
            {
                continue;
            }

            if (candidate->ItemId != itemId)
            {
                continue;
            }

            itemSlot = candidate;
            break;
        }

        if (itemSlot == null)
        {
            logger.Warning($"Key item {itemId} ({itemName}) was not found in {InventoryType.KeyItems} for {description}.");
            return false;
        }

        var agent = AgentInventoryContext.Instance();
        if (agent == null)
        {
            logger.Warning($"AgentInventoryContext is unavailable for {description}.");
            return false;
        }

        var result = agent->UseItem(itemSlot->ItemId, InventoryType.KeyItems, (uint)itemSlot->Slot);
        if (result != 0)
        {
            logger.Warning($"UseItem({itemSlot->ItemId}, {InventoryType.KeyItems}, slot={itemSlot->Slot}) returned {result} for {description}.");
            return false;
        }

        logger.Info($"Used key item {itemSlot->ItemId} ({itemName}) from {InventoryType.KeyItems} slot {itemSlot->Slot} for {description}.");
        return true;
    }

    public bool TryUseMagicalElixir(string description)
        => TryUseKeyInventoryItem(MagicalElixirEventItemId, MagicalElixirKeyItemName, description);
}
