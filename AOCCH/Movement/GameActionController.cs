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
            logger.Warning($"[GameAction] op=set-target-failed reason=invalid-object description=\"{description}\"");
            return false;
        }

        targetManager.Target = gameObject;
        if (targetManager.Target?.GameObjectId != gameObject.GameObjectId)
        {
            logger.Warning($"[GameAction] op=set-target-failed target=\"{gameObject.Name.TextValue}\" ({gameObject.GameObjectId:X}) description=\"{description}\" reason=target-manager-mismatch");
            return false;
        }

        logger.Info($"[GameAction] op=set-target target=\"{gameObject.Name.TextValue}\" ({gameObject.GameObjectId:X}) description=\"{description}\"");
        return true;
    }

    public unsafe bool TryInteractWithObject(IGameObject gameObject, string description, bool checkLineOfSight = true)
    {
        if (gameObject == null || !gameObject.IsValid())
        {
            logger.Warning($"[GameAction] op=interact-failed reason=invalid-object description=\"{description}\"");
            return false;
        }

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
        {
            logger.Warning($"[GameAction] op=interact-failed reason=target-system-unavailable description=\"{description}\"");
            return false;
        }

        var gameObjectPointer = (GameObjectStruct*)gameObject.Address;
        if (gameObjectPointer == null)
        {
            logger.Warning($"[GameAction] op=interact-failed reason=object-address-unavailable description=\"{description}\"");
            return false;
        }

        var interactionResult = targetSystem->InteractWithObject(gameObjectPointer, checkLineOfSight);
        if (interactionResult == 0)
        {
            logger.Warning($"[GameAction] op=interact-failed target=\"{gameObject.Name.TextValue}\" ({gameObject.GameObjectId:X}) description=\"{description}\" reason=return-code-0");
            return false;
        }

        logger.Info($"[GameAction] op=interact target=\"{gameObject.Name.TextValue}\" ({gameObject.GameObjectId:X}) description=\"{description}\"");
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
            logger.Warning($"[GameAction] op=general-action-failed actionId={actionId} description=\"{description}\" reason=unavailable");
            return false;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.GeneralAction, actionId);
        if (!used)
        {
            logger.Warning($"[GameAction] op=general-action-failed actionId={actionId} description=\"{description}\" reason=dispatch-failed");
            return false;
        }

        logger.Info($"[GameAction] op=general-action actionId={actionId} description=\"{description}\"");
        return true;
    }

    public unsafe bool TryExecuteAction(uint actionId, string description)
    {
        if (!CanUseAction(actionId))
        {
            logger.Warning($"[GameAction] op=action-failed actionId={actionId} description=\"{description}\" reason=unavailable");
            return false;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId);
        if (!used)
        {
            logger.Warning($"[GameAction] op=action-failed actionId={actionId} description=\"{description}\" reason=dispatch-failed");
            return false;
        }

        logger.Info($"[GameAction] op=action actionId={actionId} description=\"{description}\"");
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
            logger.Warning($"[GameAction] op=gearset-equip-failed description=\"{description}\" reason={error}");
            return new GearsetEquipAttemptResult(false, error, null, CurrentClassJobId, null);
        }

        if (!IsPlayerInChangeableState())
        {
            var error = $"Cannot equip gearset {gearsetNumber} for {description} because the player is not in a changeable state. {GetChangeableStateSummary()}";
            logger.Warning($"[GameAction] op=gearset-equip-failed description=\"{description}\" reason={error}");
            return new GearsetEquipAttemptResult(false, error, null, CurrentClassJobId, null);
        }

        if (!TryGetGearsetInfo(gearsetNumber, out var gearsetInfo, out var resolveError))
        {
            var error = $"Failed to resolve gearset {gearsetNumber} for {description}: {resolveError}";
            logger.Warning($"[GameAction] op=gearset-equip-failed description=\"{description}\" reason={error}");
            return new GearsetEquipAttemptResult(false, error, null, CurrentClassJobId, null);
        }

        if (CurrentClassJobId == gearsetInfo.ClassJobId)
        {
            logger.Info($"[GameAction] op=gearset-skip gearset={gearsetInfo.RequestedGearsetNumber} gearsetName=\"{gearsetInfo.Name}\" description=\"{description}\" currentClassJob={CurrentClassJobId} reason=already-active");
            return new GearsetEquipAttemptResult(true, string.Empty, gearsetInfo, CurrentClassJobId, null);
        }

        var module = RaptureGearsetModule.Instance();
        if (module == null)
        {
            var error = $"RaptureGearsetModule is unavailable while equipping gearset {gearsetInfo.RequestedGearsetNumber} ({gearsetInfo.Name}) for {description}.";
            logger.Warning($"[GameAction] op=gearset-equip-failed description=\"{description}\" reason={error}");
            return new GearsetEquipAttemptResult(false, error, gearsetInfo, CurrentClassJobId, null);
        }

        logger.Info($"[GameAction] op=gearset-equip gearset={gearsetInfo.RequestedGearsetNumber} gearsetName=\"{gearsetInfo.Name}\" description=\"{description}\" targetClassJob={gearsetInfo.ClassJobId} currentClassJob={CurrentClassJobId} slot={gearsetInfo.RequestedGearsetIndex}");

        var equipResult = module->EquipGearset(gearsetInfo.RequestedGearsetIndex);
        if (equipResult != 0)
        {
            var error = $"EquipGearset returned {equipResult} while equipping gearset {gearsetInfo.RequestedGearsetNumber} ({gearsetInfo.Name}) for {description}. targetClassJob={gearsetInfo.ClassJobId} currentClassJob={CurrentClassJobId}.";
            logger.Warning($"[GameAction] op=gearset-equip-failed description=\"{description}\" reason={error}");
            return new GearsetEquipAttemptResult(false, error, gearsetInfo, CurrentClassJobId, equipResult);
        }

        logger.Info($"[GameAction] op=gearset-equip-accepted gearset={gearsetInfo.RequestedGearsetNumber} gearsetName=\"{gearsetInfo.Name}\" description=\"{description}\" targetClassJob={gearsetInfo.ClassJobId} currentClassJob={CurrentClassJobId}");
        return new GearsetEquipAttemptResult(true, string.Empty, gearsetInfo, CurrentClassJobId, equipResult);
    }

    public GearsetEquipAttemptResult TryEquipGearsetReliably(int gearsetNumber, string description, TimeSpan verifyTimeout, int maxAttempts, TimeSpan retryDelay, TimeSpan? postActionLockDelay = null)
    {
        if (postActionLockDelay is { } delay && delay > TimeSpan.Zero)
        {
            logger.Info($"[GameAction] op=gearset-delay gearset={gearsetNumber} description=\"{description}\" delay={delay.TotalSeconds:0.0}s reason=action-lock-window");
            Thread.Sleep(delay);
        }

        GearsetEquipAttemptResult lastResult = new(false, string.Empty, null, CurrentClassJobId, null);
        for (var attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
        {
            if (!WaitForChangeableState(ReliableGearsetReadyTimeout, out var readyError))
            {
                lastResult = new GearsetEquipAttemptResult(false, $"Gearset equip attempt {attempt}/{Math.Max(1, maxAttempts)} for {description} failed while waiting for a changeable state: {readyError}", null, CurrentClassJobId, null);
                logger.Warning($"[GameAction] op=gearset-equip-failed description=\"{description}\" reason={lastResult.Error}");
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
                    logger.Warning($"[GameAction] op=gearset-equip-failed description=\"{description}\" reason={lastResult.Error}");
                }
            }

            if (attempt < Math.Max(1, maxAttempts))
            {
                logger.Info($"[GameAction] op=gearset-retry gearset={gearsetNumber} description=\"{description}\" retryDelay={retryDelay.TotalSeconds:0.0}s attempt={attempt}/{Math.Max(1, maxAttempts)}");
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
            logger.Warning($"[GameAction] op=keyitem-command-failed description=\"{description}\" reason=unnamed-key-item");
            return false;
        }

        var escapedKeyItemName = keyItemName.Replace("\"", "\\\"", StringComparison.Ordinal);
        var command = $"/keyitem \"{escapedKeyItemName}\"";
        if (!commandManager.ProcessCommand(command))
        {
            logger.Warning($"[GameAction] op=keyitem-command-failed description=\"{description}\" command=\"{command}\" reason=dispatch-failed");
            return false;
        }

        logger.Info($"[GameAction] op=keyitem-command description=\"{description}\" command=\"{command}\"");
        return true;
    }

    public unsafe bool TryUseInventoryItem(uint itemId, bool isHighQuality, string description)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            logger.Warning($"[GameAction] op=inventory-use-failed description=\"{description}\" reason=inventory-manager-unavailable");
            return false;
        }

        var count = inventoryManager->GetInventoryItemCount(itemId, isHighQuality);
        if (count <= 0)
        {
            logger.Warning($"[GameAction] op=inventory-use-failed description=\"{description}\" itemId={itemId} reason=item-unavailable");
            return false;
        }

        var agent = AgentInventoryContext.Instance();
        if (agent == null)
        {
            logger.Warning($"[GameAction] op=inventory-use-failed description=\"{description}\" reason=agent-context-unavailable");
            return false;
        }

        var itemToUse = isHighQuality ? itemId + 1_000_000u : itemId;
        var result = agent->UseItem(itemToUse);
        if (!IsAcceptedUseItemResult(result))
        {
            logger.Warning($"[GameAction] op=inventory-use-failed description=\"{description}\" itemId={itemToUse} result={result}");
            return false;
        }

        logger.Info($"[GameAction] op=inventory-use description=\"{description}\" itemId={itemToUse}");
        return true;
    }

    public unsafe bool TryUseKeyInventoryItem(uint itemId, string itemName, string description)
    {
        if (itemId == 0)
        {
            logger.Warning($"[GameAction] op=keyitem-use-failed description=\"{description}\" reason=key-item-zero");
            return false;
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            logger.Warning($"[GameAction] op=keyitem-use-failed description=\"{description}\" reason=inventory-manager-unavailable");
            return false;
        }

        var keyItemContainer = inventoryManager->GetInventoryContainer(InventoryType.KeyItems);
        if (keyItemContainer == null)
        {
            logger.Warning($"[GameAction] op=keyitem-use-failed description=\"{description}\" reason=keyitem-container-unavailable");
            return false;
        }

        if (!keyItemContainer->IsLoaded || keyItemContainer->Size <= 0 || keyItemContainer->Items == null)
        {
            logger.Warning($"[GameAction] op=keyitem-use-failed description=\"{description}\" reason=keyitem-container-not-ready loaded={keyItemContainer->IsLoaded} size={keyItemContainer->Size}");
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
            logger.Warning($"[GameAction] op=keyitem-use-failed description=\"{description}\" itemId={itemId} itemName=\"{itemName}\" container={InventoryType.KeyItems} reason=not-found");
            return false;
        }

        var agent = AgentInventoryContext.Instance();
        if (agent == null)
        {
            logger.Warning($"[GameAction] op=keyitem-use-failed description=\"{description}\" reason=agent-context-unavailable");
            return false;
        }

        var result = agent->UseItem(itemSlot->ItemId, InventoryType.KeyItems, (uint)itemSlot->Slot);
        if (!IsAcceptedUseItemResult(result))
        {
            logger.Warning($"[GameAction] op=keyitem-use-failed description=\"{description}\" itemId={itemSlot->ItemId} container={InventoryType.KeyItems} slot={itemSlot->Slot} result={result}");
            return false;
        }

        logger.Info($"[GameAction] op=keyitem-use description=\"{description}\" itemId={itemSlot->ItemId} itemName=\"{itemName}\" container={InventoryType.KeyItems} slot={itemSlot->Slot}");
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

    private static bool IsAcceptedUseItemResult(long result)
        => result is 0 or 1;

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
