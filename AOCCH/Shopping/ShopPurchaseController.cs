using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using AOCCH.Logging;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AOCCH.Shopping;

public sealed class ShopPurchaseController : IDisposable
{
    private static readonly TimeSpan PurchaseTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ConfirmationRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly InventoryType[] NormalInventoryContainers = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
    private const int AddonIndex = 1;
    private const int SelectYesnoIndex = 1;
    private const int ShopExchangeItemDialogIndex = 1;

    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly IGameGui gameGui;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private PurchaseAttempt? activeAttempt;
    private string lastStatus = "Idle";
    private PurchaseCompletionKind lastCompletionKind;

    public ShopPurchaseController(IFramework framework, IChatGui chatGui, IGameGui gameGui, AocchLogger logger)
    {
        this.framework = framework;
        this.chatGui = chatGui;
        this.gameGui = gameGui;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
        chatGui.LogMessage += OnLogMessage;
        logger.Info("[ShopPurchase] op=init");
    }

    public bool IsBusy
    {
        get
        {
            lock (gate)
            {
                return activeAttempt != null;
            }
        }
    }

    public string LastStatus
    {
        get
        {
            lock (gate)
            {
                return lastStatus;
            }
        }
    }

    public PurchaseCompletionKind LastCompletionKind
    {
        get
        {
            lock (gate)
            {
                return lastCompletionKind;
            }
        }
    }

    public void Dispose()
    {
        chatGui.LogMessage -= OnLogMessage;
        framework.Update -= OnFrameworkUpdate;
    }

    public bool TryBuyCurrencyEntry(LiveShopEntry entry, int quantity)
    {
        if (quantity <= 0)
        {
            SetImmediateOutcome("Failed: quantity must be greater than zero.", PurchaseCompletionKind.StopShopping);
            return false;
        }

        var inventorySpaceState = GetInventorySpaceState(out var inventorySpaceReason);
        if (inventorySpaceState != InventorySpaceState.HasSpace)
        {
            var status = inventorySpaceState == InventorySpaceState.NoSpace
                ? "Failed: insufficient inventory space."
                : "Failed: inventory state unavailable.";
            SetImmediateOutcome(status, PurchaseCompletionKind.StopShopping);
            logger.Warning($"[ShopPurchase] op=preflight-blocked addon=ShopExchangeCurrency itemId={entry.ItemId} itemName=\"{entry.ItemName}\" reason={inventorySpaceReason}");
            return false;
        }

        if (!TryBeginAttempt(new PurchaseAttempt
        {
            AddonName = "ShopExchangeCurrency",
            ConfirmationAddonName = "SelectYesno",
            ItemId = entry.ItemId,
            ItemName = entry.ItemName,
            RowIndex = entry.RowIndex,
            Quantity = quantity,
            CurrencyItemId = entry.CurrencyItemId,
            ExpectedCurrencyDelta = entry.Cost * (uint)quantity,
            ExpectedRequiredItems = Array.Empty<ExpectedRequiredItem>(),
            StartedAt = DateTimeOffset.UtcNow,
            DeadlineAt = DateTimeOffset.UtcNow + PurchaseTimeout,
        }))
        {
            return false;
        }

        logger.Info($"[ShopPurchase] op=begin addon=ShopExchangeCurrency itemId={entry.ItemId} itemName=\"{entry.ItemName}\" rowIndex={entry.RowIndex} quantity={quantity} currencyItemId={entry.CurrencyItemId} expectedCurrencyDelta={entry.Cost * (uint)quantity}");
        return true;
    }

    public bool TryBuyItemExchangeEntry(LiveShopExchangeItemEntry entry, int quantity)
    {
        if (quantity <= 0)
        {
            SetImmediateOutcome("Failed: quantity must be greater than zero.", PurchaseCompletionKind.StopShopping);
            return false;
        }

        var inventorySpaceState = GetInventorySpaceState(out var inventorySpaceReason);
        if (inventorySpaceState != InventorySpaceState.HasSpace)
        {
            var status = inventorySpaceState == InventorySpaceState.NoSpace
                ? "Failed: insufficient inventory space."
                : "Failed: inventory state unavailable.";
            SetImmediateOutcome(status, PurchaseCompletionKind.StopShopping);
            logger.Warning($"[ShopPurchase] op=preflight-blocked addon=ShopExchangeItem itemId={entry.ItemId} itemName=\"{entry.ItemName}\" reason={inventorySpaceReason}");
            return false;
        }

        var expectedRequiredItems = entry.RequiredItems
            .Select(requiredItem => new ExpectedRequiredItem(requiredItem.ItemId, requiredItem.RequiredAmount * (uint)quantity))
            .ToArray();

        if (!TryBeginAttempt(new PurchaseAttempt
        {
            AddonName = "ShopExchangeItem",
            ConfirmationAddonName = "ShopExchangeItemDialog",
            ItemId = entry.ItemId,
            ItemName = entry.ItemName,
            RowIndex = entry.RowIndex,
            Quantity = quantity,
            CurrencyItemId = 0,
            ExpectedCurrencyDelta = 0,
            ExpectedRequiredItems = expectedRequiredItems,
            StartedAt = DateTimeOffset.UtcNow,
            DeadlineAt = DateTimeOffset.UtcNow + PurchaseTimeout,
        }))
        {
            return false;
        }

        logger.Info($"[ShopPurchase] op=begin addon=ShopExchangeItem itemId={entry.ItemId} itemName=\"{entry.ItemName}\" rowIndex={entry.RowIndex} quantity={quantity} requirements={string.Join(",", expectedRequiredItems.Select(requiredItem => $"{requiredItem.ItemId}:{requiredItem.RequiredAmount}"))}");
        return true;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        PurchaseAttempt? attempt;
        lock (gate)
        {
            attempt = activeAttempt;
        }

        if (attempt == null)
        {
            return;
        }

        if (DateTimeOffset.UtcNow >= attempt.DeadlineAt)
        {
            CompleteAttempt($"Failed: timeout while {attempt.StateDescription}.", PurchaseCompletionKind.StopShopping);
            return;
        }

        switch (attempt.State)
        {
            case PurchaseState.PendingDispatch:
                TickPendingDispatch(attempt);
                break;
            case PurchaseState.PollingForOutcomeOrConfirmation:
                TickPollingForOutcomeOrConfirmation(attempt);
                break;
        }
    }

    private unsafe void TickPendingDispatch(PurchaseAttempt attempt)
    {
        if (!TryGetAddon(attempt.AddonName, out var addon))
        {
            return;
        }

        var callbackResult = FirePurchaseCallback(addon, attempt.RowIndex, attempt.Quantity);

        attempt.State = PurchaseState.PollingForOutcomeOrConfirmation;
        attempt.StateDescription = "polling for outcome or confirmation";
        attempt.CallbackDispatchResult = callbackResult;
        SetStatus($"Polling for purchase outcome or {attempt.ConfirmationAddonName} for {attempt.ItemName}. callbackResult={callbackResult}");
        logger.Info($"[ShopPurchase] op=callback-fired addon={attempt.AddonName} itemId={attempt.ItemId} rowIndex={attempt.RowIndex} quantity={attempt.Quantity} callbackResult={callbackResult}");
    }

    private unsafe void TickPollingForOutcomeOrConfirmation(PurchaseAttempt attempt)
    {
        if (VerifyAttemptSuccess(attempt, out var confirmationReason))
        {
            CompleteAttempt($"Success: purchased {attempt.ItemName}. {confirmationReason}", PurchaseCompletionKind.Success);
            return;
        }

        if (attempt.ConfirmationAddonName == "ShopExchangeItemDialog"
            && attempt.ConfirmationCount > 0
            && !TryGetConfirmationAddon(attempt.ConfirmationAddonName, out _)
            && TryGetAddon(attempt.AddonName, out _))
        {
            CompleteAttempt($"Skipped: insufficient required items for exchange. item={attempt.ItemName}", PurchaseCompletionKind.SkipTarget);
            return;
        }

        if (!TryGetConfirmationAddon(attempt.ConfirmationAddonName, out var confirmationAddon))
        {
            return;
        }

        if (DateTimeOffset.UtcNow < attempt.NextConfirmationAttemptAt)
        {
            return;
        }

        var confirmResult = ConfirmExchange(confirmationAddon, attempt.ConfirmationAddonName);
        logger.Info($"[ShopPurchase] op=confirm-attempt itemId={attempt.ItemId} itemName=\"{attempt.ItemName}\" confirmationAddon={attempt.ConfirmationAddonName} countNext={attempt.ConfirmationCount + 1} result={confirmResult}");

        if (!confirmResult)
        {
            attempt.NextConfirmationAttemptAt = DateTimeOffset.UtcNow + ConfirmationRetryDelay;
            SetStatus($"Waiting to retry confirmation for {attempt.ItemName}. confirmations={attempt.ConfirmationCount}");
            return;
        }

        attempt.ConfirmationSent = true;
        attempt.ConfirmationCount++;
        attempt.LastConfirmationAt = DateTimeOffset.UtcNow;
        attempt.NextConfirmationAttemptAt = attempt.LastConfirmationAt + ConfirmationRetryDelay;
        SetStatus($"Polling for purchase result for {attempt.ItemName}. confirmations={attempt.ConfirmationCount}");
        logger.Info($"[ShopPurchase] op=confirm-step itemId={attempt.ItemId} itemName=\"{attempt.ItemName}\" confirmationAddon={attempt.ConfirmationAddonName} count={attempt.ConfirmationCount}");
    }

    private bool TryBeginAttempt(PurchaseAttempt attempt)
    {
        lock (gate)
        {
            if (activeAttempt != null)
            {
                lastStatus = $"Failed: already purchasing {activeAttempt.ItemName}.";
                lastCompletionKind = PurchaseCompletionKind.StopShopping;
                return false;
            }

            attempt.PrePurchaseTargetCount = GetItemCount(attempt.ItemId);
            attempt.PrePurchaseCurrencyCount = attempt.CurrencyItemId == 0
                ? 0
                : GetItemCount(attempt.CurrencyItemId);
            attempt.PrePurchaseRequiredItemCounts = attempt.ExpectedRequiredItems.ToDictionary(requiredItem => requiredItem.ItemId, requiredItem => GetItemCount(requiredItem.ItemId));
            attempt.State = PurchaseState.PendingDispatch;
            attempt.StateDescription = "dispatching purchase callback";
            activeAttempt = attempt;
            lastStatus = $"Dispatching purchase for {attempt.ItemName}.";
            lastCompletionKind = PurchaseCompletionKind.None;
        }

        return true;
    }

    private void CompleteAttempt(string status, PurchaseCompletionKind completionKind, uint? logMessageId = null)
    {
        PurchaseAttempt? completedAttempt;
        lock (gate)
        {
            completedAttempt = activeAttempt;
            activeAttempt = null;
            lastStatus = status;
            lastCompletionKind = completionKind;
        }

        if (completedAttempt == null)
        {
            return;
        }

        if (completionKind == PurchaseCompletionKind.Success)
        {
            logger.Info($"[ShopPurchase] op=complete-success itemId={completedAttempt.ItemId} itemName=\"{completedAttempt.ItemName}\" status=\"{status}\"");
        }
        else
        {
            logger.Warning($"[ShopPurchase] op=complete-failed itemId={completedAttempt.ItemId} itemName=\"{completedAttempt.ItemName}\" addon={completedAttempt.AddonName} outcome={completionKind} logMessageId={logMessageId ?? 0} status=\"{status}\"");
        }
    }

    private void SetStatus(string status)
    {
        lock (gate)
        {
            lastStatus = status;
        }
    }

    private void SetImmediateOutcome(string status, PurchaseCompletionKind completionKind)
    {
        lock (gate)
        {
            lastStatus = status;
            lastCompletionKind = completionKind;
        }
    }

    private void OnLogMessage(ILogMessage message)
    {
        if (!TryClassifyExchangeFailure(message.LogMessageId, out var failure))
        {
            return;
        }

        PurchaseAttempt? attempt;
        lock (gate)
        {
            attempt = activeAttempt;
        }

        if (attempt == null)
        {
            return;
        }

        CompleteAttempt($"{failure.StatusPrefix} logMessageId={message.LogMessageId}", failure.Outcome, message.LogMessageId);
    }

    private static bool TryClassifyExchangeFailure(uint logMessageId, out ExchangeFailure failure)
    {
        failure = logMessageId switch
        {
            1939u => new ExchangeFailure(PurchaseCompletionKind.SkipTarget, "Skipped: cannot carry any more of this item."),
            1940u or 3737u or 3974u or 3978u or 5283u => new ExchangeFailure(PurchaseCompletionKind.StopShopping, "Failed: insufficient inventory space."),
            1941u or 1942u or 5282u => new ExchangeFailure(PurchaseCompletionKind.SkipTarget, "Skipped: insufficient required items for exchange."),
            1943u or 3739u or 3740u or 3976u or 3977u or 3979u => new ExchangeFailure(PurchaseCompletionKind.SkipTarget, "Skipped: unique-item restriction blocked exchange."),
            3736u or 3738u => new ExchangeFailure(PurchaseCompletionKind.SkipTarget, "Skipped: exchange blocked because a required item is equipped."),
            3975u => new ExchangeFailure(PurchaseCompletionKind.SkipTarget, "Skipped: insufficient required currency for exchange."),
            1947u or 1949u => new ExchangeFailure(PurchaseCompletionKind.StopShopping, "Failed: exchange was rejected by the game."),
            _ => default,
        };

        return failure != default;
    }

    private bool VerifyAttemptSuccess(PurchaseAttempt attempt, out string confirmationReason)
    {
        var currentTargetCount = GetItemCount(attempt.ItemId);
        if (currentTargetCount > attempt.PrePurchaseTargetCount)
        {
            confirmationReason = $"Target count increased from {attempt.PrePurchaseTargetCount} to {currentTargetCount}.";
            return true;
        }

        if (attempt.CurrencyItemId != 0)
        {
            var currentCurrencyCount = GetItemCount(attempt.CurrencyItemId);
            if (currentCurrencyCount < attempt.PrePurchaseCurrencyCount)
            {
                confirmationReason = $"Currency count decreased from {attempt.PrePurchaseCurrencyCount} to {currentCurrencyCount}.";
                return true;
            }
        }

        foreach (var requiredItem in attempt.ExpectedRequiredItems)
        {
            if (!attempt.PrePurchaseRequiredItemCounts.TryGetValue(requiredItem.ItemId, out var prePurchaseCount))
            {
                continue;
            }

            var currentRequiredCount = GetItemCount(requiredItem.ItemId);
            if (currentRequiredCount < prePurchaseCount)
            {
                confirmationReason = $"Required item {requiredItem.ItemId} count decreased from {prePurchaseCount} to {currentRequiredCount}.";
                return true;
            }
        }

        confirmationReason = string.Empty;
        return false;
    }

    private unsafe bool TryGetAddon(string addonName, out AtkUnitBase* addon)
    {
        addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName, AddonIndex).Address;
        return addon != null && addon->IsReady;
    }

    private unsafe bool TryGetConfirmationAddon(string addonName, out AtkUnitBase* addon)
    {
        var addonIndex = addonName == "ShopExchangeItemDialog"
            ? ShopExchangeItemDialogIndex
            : SelectYesnoIndex;
        addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName, addonIndex).Address;
        return addon != null && addon->IsReady;
    }

    private static unsafe bool ConfirmExchange(AtkUnitBase* addon, string addonName)
    {
        if (addonName == "SelectYesno")
        {
            return addon->FireCallbackInt(0);
        }

        var values = (AtkValue*)Marshal.AllocHGlobal(sizeof(AtkValue));
        if (values == null)
        {
            return false;
        }

        try
        {
            values[0] = default;
            values[0].Type = AtkValueType.Int;
            values[0].Int = 0;
            return addon->FireCallback(1, values, true);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)values);
        }
    }

    private static unsafe bool FirePurchaseCallback(AtkUnitBase* addon, uint rowIndex, int quantity)
    {
        var values = (AtkValue*)Marshal.AllocHGlobal(4 * sizeof(AtkValue));
        if (values == null)
        {
            return false;
        }

        try
        {
            values[0] = default;
            values[1] = default;
            values[2] = default;
            values[3] = default;
            values[0].Type = AtkValueType.Int;
            values[0].Int = 0;
            values[1].Type = AtkValueType.UInt;
            values[1].UInt = rowIndex;
            values[2].Type = AtkValueType.Int;
            values[2].Int = quantity;
            return addon->FireCallback(4, values, true);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)values);
        }
    }

    private static unsafe uint GetItemCount(uint itemId)
    {
        if (itemId == 0)
        {
            return 0;
        }

        var inventoryManager = InventoryManager.Instance();
        return inventoryManager == null
            ? 0
            : (uint)inventoryManager->GetInventoryItemCount(itemId, false);
    }

    private static unsafe InventorySpaceState GetInventorySpaceState(out string reason)
    {
        reason = string.Empty;
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            reason = "inventory-manager-unavailable";
            return InventorySpaceState.Unknown;
        }

        var totalSlots = 0;
        var nonEmptySlots = 0;
        foreach (var containerType in NormalInventoryContainers)
        {
            var container = inventoryManager->GetInventoryContainer(containerType);
            if (container == null)
            {
                reason = $"container-unavailable:{containerType}";
                return InventorySpaceState.Unknown;
            }

            if (!container->IsLoaded || container->Size <= 0 || container->Items == null)
            {
                reason = $"container-not-ready:{containerType}:loaded={container->IsLoaded}:size={container->Size}";
                return InventorySpaceState.Unknown;
            }

            totalSlots += container->Size;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->IsEmpty())
                {
                    continue;
                }

                nonEmptySlots++;
            }
        }

        reason = totalSlots > nonEmptySlots ? "space-available" : "inventory-full";
        return totalSlots > nonEmptySlots
            ? InventorySpaceState.HasSpace
            : InventorySpaceState.NoSpace;
    }

    private sealed class PurchaseAttempt
    {
        public string AddonName { get; init; } = string.Empty;
        public string ConfirmationAddonName { get; init; } = string.Empty;
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public uint RowIndex { get; init; }
        public int Quantity { get; init; }
        public uint CurrencyItemId { get; init; }
        public uint ExpectedCurrencyDelta { get; init; }
        public IReadOnlyList<ExpectedRequiredItem> ExpectedRequiredItems { get; init; } = Array.Empty<ExpectedRequiredItem>();
        public uint PrePurchaseTargetCount { get; set; }
        public uint PrePurchaseCurrencyCount { get; set; }
        public Dictionary<uint, uint> PrePurchaseRequiredItemCounts { get; set; } = new();
        public bool ConfirmationSent { get; set; }
        public int ConfirmationCount { get; set; }
        public DateTimeOffset LastConfirmationAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset NextConfirmationAttemptAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset DeadlineAt { get; init; }
        public bool CallbackDispatchResult { get; set; }
        public PurchaseState State { get; set; }
        public string StateDescription { get; set; } = string.Empty;
    }

    private enum PurchaseState
    {
        PendingDispatch,
        PollingForOutcomeOrConfirmation,
    }

    private readonly record struct ExpectedRequiredItem(uint ItemId, uint RequiredAmount);
    private readonly record struct ExchangeFailure(PurchaseCompletionKind Outcome, string StatusPrefix);

    public enum PurchaseCompletionKind
    {
        None,
        Success,
        SkipTarget,
        StopShopping,
    }

    private enum InventorySpaceState
    {
        HasSpace,
        NoSpace,
        Unknown,
    }
}
