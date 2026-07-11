using System;
using System.Threading;
using AOCCH.Logging;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AOCCH.Movement;

public sealed class GameActionController
{
    public readonly record struct GearsetInfo(int RequestedGearsetNumber, int RequestedGearsetIndex, string Name, uint ClassJobId);

    public readonly record struct GearsetEquipAttemptResult(bool Success, string Error, GearsetInfo? Gearset, uint CurrentClassJobId, int? EquipReturnCode)
    {
        public uint? TargetClassJobId => Gearset?.ClassJobId;
    }

    public enum MagicalElixirUseMethod
    {
        Slot,
        Inventory,
        Command,
    }

    public const uint ReturnActionId = 8;
    public const uint MountActionId = 9;
    public const uint DismountActionId = 23;
    public const uint HideActionId = 2245;
    public const uint NinjaClassJobId = 30;
    public const uint MagicalElixirEventItemId = 2003296;
    public const string MagicalElixirKeyItemName = "Magical Elixir";

    private static readonly TimeSpan ReliableGearsetReadyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReliableGearsetPollInterval = TimeSpan.FromMilliseconds(100);

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

    public bool IsPlayerInChangeableState()
        => playerState.IsLoaded
            && !condition[ConditionFlag.InCombat]
            && !condition[ConditionFlag.Casting]
            && !condition[ConditionFlag.Mounted]
            && !condition[ConditionFlag.BetweenAreas]
            && !condition[ConditionFlag.Occupied]
            && !condition[ConditionFlag.OccupiedInQuestEvent];

    public string GetChangeableStateSummary()
        => $"playerLoaded={playerState.IsLoaded} currentClassJob={CurrentClassJobId} inCombat={condition[ConditionFlag.InCombat]} casting={condition[ConditionFlag.Casting]} mounted={condition[ConditionFlag.Mounted]} betweenAreas={condition[ConditionFlag.BetweenAreas]} occupied={condition[ConditionFlag.Occupied]} occupiedInQuestEvent={condition[ConditionFlag.OccupiedInQuestEvent]}";

    public bool WaitForChangeableState(TimeSpan timeout, out string error)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!IsPlayerInChangeableState())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                error = $"Timed out waiting for a changeable state. {GetChangeableStateSummary()}";
                return false;
            }

            Thread.Sleep(ReliableGearsetPollInterval);
        }

        error = string.Empty;
        return true;
    }

    public unsafe bool TryGetGearsetInfo(int gearsetNumber, out GearsetInfo info, out string error)
    {
        info = default;
        if (!TryResolveGearset(gearsetNumber, out var gearsetIndex, out error))
        {
            return false;
        }

        var module = RaptureGearsetModule.Instance();
        if (module == null)
        {
            error = "RaptureGearsetModule is unavailable.";
            return false;
        }

        var gearset = module->GetGearset(gearsetIndex);
        if (gearset == null)
        {
            error = $"Gearset number {gearsetNumber} resolved to slot {gearsetIndex}, but the gearset entry pointer was null.";
            return false;
        }

        var classJobId = gearset->ClassJob;
        if (classJobId == 0)
        {
            error = $"Gearset number {gearsetNumber} resolved to slot {gearsetIndex}, but the gearset has no readable ClassJob id.";
            return false;
        }

        var name = string.IsNullOrWhiteSpace(gearset->NameString)
            ? $"Gearset {gearsetNumber}"
            : gearset->NameString;

        info = new GearsetInfo(gearsetNumber, gearsetIndex, name, classJobId);
        error = string.Empty;
        return true;
    }

    public unsafe GearsetEquipAttemptResult TryEquipGearset(int gearsetNumber, string description)
    {
        if (gearsetNumber <= 0)
        {
            var error = $"Cannot equip an unconfigured gearset for {description}.";
            logger.Warning(error);
            return new GearsetEquipAttemptResult(false, error, null, CurrentClassJobId, null);
        }

        if (!IsPlayerInChangeableState())
        {
            var error = $"Cannot equip gearset {gearsetNumber} for {description} because the player is not in a changeable state. {GetChangeableStateSummary()}";
            logger.Warning(error);
            return new GearsetEquipAttemptResult(false, error, null, CurrentClassJobId, null);
        }

        if (!TryGetGearsetInfo(gearsetNumber, out var gearsetInfo, out var resolveError))
        {
            var error = $"Failed to resolve gearset {gearsetNumber} for {description}: {resolveError}";
            logger.Warning(error);
            return new GearsetEquipAttemptResult(false, error, null, CurrentClassJobId, null);
        }

        if (CurrentClassJobId == gearsetInfo.ClassJobId)
        {
            logger.Info($"Gearset {gearsetInfo.RequestedGearsetNumber} ({gearsetInfo.Name}) is already active for {description}. currentClassJob={CurrentClassJobId}.");
            return new GearsetEquipAttemptResult(true, string.Empty, gearsetInfo, CurrentClassJobId, null);
        }

        var module = RaptureGearsetModule.Instance();
        if (module == null)
        {
            var error = $"RaptureGearsetModule is unavailable while equipping gearset {gearsetInfo.RequestedGearsetNumber} ({gearsetInfo.Name}) for {description}.";
            logger.Warning(error);
            return new GearsetEquipAttemptResult(false, error, gearsetInfo, CurrentClassJobId, null);
        }

        logger.Info($"Equipping gearset {gearsetInfo.RequestedGearsetNumber} ({gearsetInfo.Name}) for {description}. targetClassJob={gearsetInfo.ClassJobId} currentClassJob={CurrentClassJobId} slot={gearsetInfo.RequestedGearsetIndex}.");

        var equipResult = module->EquipGearset(gearsetInfo.RequestedGearsetIndex);
        if (equipResult != 0)
        {
            var error = $"EquipGearset returned {equipResult} while equipping gearset {gearsetInfo.RequestedGearsetNumber} ({gearsetInfo.Name}) for {description}. targetClassJob={gearsetInfo.ClassJobId} currentClassJob={CurrentClassJobId}.";
            logger.Warning(error);
            return new GearsetEquipAttemptResult(false, error, gearsetInfo, CurrentClassJobId, equipResult);
        }

        logger.Info($"EquipGearset accepted gearset {gearsetInfo.RequestedGearsetNumber} ({gearsetInfo.Name}) for {description}. targetClassJob={gearsetInfo.ClassJobId} currentClassJob={CurrentClassJobId}." );
        return new GearsetEquipAttemptResult(true, string.Empty, gearsetInfo, CurrentClassJobId, equipResult);
    }

    public GearsetEquipAttemptResult TryEquipGearsetReliably(int gearsetNumber, string description, TimeSpan verifyTimeout, int maxAttempts, TimeSpan retryDelay, TimeSpan? postActionLockDelay = null)
    {
        if (postActionLockDelay is { } delay && delay > TimeSpan.Zero)
        {
            logger.Info($"Waiting {delay.TotalSeconds:0.0}s before equipping gearset {gearsetNumber} for {description} to respect the action-lock window.");
            Thread.Sleep(delay);
        }

        GearsetEquipAttemptResult lastResult = new(false, string.Empty, null, CurrentClassJobId, null);
        for (var attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
        {
            if (!WaitForChangeableState(ReliableGearsetReadyTimeout, out var readyError))
            {
                lastResult = new GearsetEquipAttemptResult(false, $"Gearset equip attempt {attempt}/{Math.Max(1, maxAttempts)} for {description} failed while waiting for a changeable state: {readyError}", null, CurrentClassJobId, null);
                logger.Warning(lastResult.Error);
            }
            else
            {
                lastResult = TryEquipGearset(gearsetNumber, description);
                if (lastResult.Success)
                {
                    var targetClassJobId = lastResult.TargetClassJobId;
                    if (!targetClassJobId.HasValue || CurrentClassJobId == targetClassJobId.Value)
                    {
                        return lastResult;
                    }

                    var verifyDeadline = DateTimeOffset.UtcNow + verifyTimeout;
                    while (DateTimeOffset.UtcNow < verifyDeadline)
                    {
                        if (CurrentClassJobId == targetClassJobId.Value)
                        {
                            return lastResult with { CurrentClassJobId = CurrentClassJobId };
                        }

                        Thread.Sleep(ReliableGearsetPollInterval);
                    }

                    lastResult = lastResult with
                    {
                        Success = false,
                        Error = $"Gearset equip attempt {attempt}/{Math.Max(1, maxAttempts)} for {description} did not activate ClassJob {targetClassJobId.Value} within {verifyTimeout.TotalSeconds:0.0}s. currentClassJob={CurrentClassJobId}."
                    };
                    logger.Warning(lastResult.Error);
                }
            }

            if (attempt < Math.Max(1, maxAttempts))
            {
                logger.Info($"Retrying gearset {gearsetNumber} for {description} in {retryDelay.TotalSeconds:0.0}s after attempt {attempt}/{Math.Max(1, maxAttempts)} failed.");
                Thread.Sleep(retryDelay);
            }
        }

        return lastResult;
    }

    private unsafe bool TryResolveGearset(int gearsetNumber, out int gearsetIndex, out string error)
    {
        gearsetIndex = -1;
        if (gearsetNumber <= 0)
        {
            error = "Gearset number must be greater than zero.";
            return false;
        }

        var module = RaptureGearsetModule.Instance();
        if (module == null)
        {
            error = "RaptureGearsetModule is unavailable.";
            return false;
        }

        gearsetIndex = gearsetNumber - 1;
        if (!module->IsValidGearset(gearsetIndex))
        {
            error = $"Gearset number {gearsetNumber} resolved to slot {gearsetIndex}, but the slot is invalid or unavailable.";
            return false;
        }

        error = string.Empty;
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

    public unsafe bool HasInventoryItem(uint itemId, bool isHighQuality = false)
    {
        var inventoryManager = InventoryManager.Instance();
        return inventoryManager != null && inventoryManager->GetInventoryItemCount(itemId, isHighQuality) > 0;
    }

    public bool HasMagicalElixir()
        => HasInventoryItem(MagicalElixirEventItemId);

    public bool TryUseMagicalElixirViaInventory(string description)
        => TryUseInventoryItem(MagicalElixirEventItemId, isHighQuality: false, description);

    public bool TryUseMagicalElixirViaCommand(string description)
        => TryUseKeyItem(MagicalElixirKeyItemName, description);

    public bool TryUseMagicalElixir(MagicalElixirUseMethod method, string description)
        => method switch
        {
            MagicalElixirUseMethod.Slot => TryUseMagicalElixir(description),
            MagicalElixirUseMethod.Inventory => TryUseMagicalElixirViaInventory(description),
            MagicalElixirUseMethod.Command => TryUseMagicalElixirViaCommand(description),
            _ => false,
        };

    public unsafe string DescribeMagicalElixirState()
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return "InventoryManager unavailable.";
        }

        var generalCount = inventoryManager->GetInventoryItemCount(MagicalElixirEventItemId, false);
        var keyItemContainer = inventoryManager->GetInventoryContainer(InventoryType.KeyItems);
        if (keyItemContainer == null)
        {
            return $"generalCount={generalCount} keyItemContainer=null";
        }

        InventoryItem* itemSlot = null;
        for (var i = 0; i < keyItemContainer->Size; i++)
        {
            var candidate = keyItemContainer->GetInventorySlot(i);
            if (candidate == null || candidate->IsEmpty() || candidate->ItemId != MagicalElixirEventItemId)
            {
                continue;
            }

            itemSlot = candidate;
            break;
        }

        var slotSummary = itemSlot == null
            ? "missing"
            : $"itemId={itemSlot->ItemId} container={InventoryType.KeyItems} slot={itemSlot->Slot} quantity={itemSlot->Quantity} spiritbond={itemSlot->SpiritbondOrCollectability} condition={itemSlot->Condition}";

        return $"generalCount={generalCount} keyItemsLoaded={keyItemContainer->IsLoaded} keyItemsSize={keyItemContainer->Size} keyItemsItemsNull={keyItemContainer->Items == null} slotInfo={slotSummary}";
    }
}
