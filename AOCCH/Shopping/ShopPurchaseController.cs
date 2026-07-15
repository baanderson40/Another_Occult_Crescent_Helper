using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using AOCCH.Logging;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AOCCH.Shopping;

public sealed class ShopPurchaseController : IDisposable
{
    private static readonly TimeSpan PurchaseTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ConfirmationRetryDelay = TimeSpan.FromMilliseconds(500);
    private const int AddonIndex = 1;
    private const int SelectYesnoIndex = 1;
    private const int ShopExchangeItemDialogIndex = 1;

    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private PurchaseAttempt? activeAttempt;
    private string lastStatus = "Idle";

    public ShopPurchaseController(IFramework framework, IGameGui gameGui, AocchLogger logger)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
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

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }

    public bool TryBuyCurrencyEntry(LiveShopEntry entry, int quantity)
    {
        if (quantity <= 0)
        {
            SetStatus("Failed: quantity must be greater than zero.");
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
            SetStatus("Failed: quantity must be greater than zero.");
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
            CompleteAttempt($"Failed: timeout while {attempt.StateDescription}.", success: false);
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
            CompleteAttempt($"Success: purchased {attempt.ItemName}. {confirmationReason}", success: true);
            return;
        }

        if (attempt.ConfirmationAddonName == "ShopExchangeItemDialog"
            && attempt.ConfirmationCount > 0
            && !TryGetConfirmationAddon(attempt.ConfirmationAddonName, out _)
            && TryGetAddon(attempt.AddonName, out _))
        {
            CompleteAttempt($"Failed: exchange did not complete for {attempt.ItemName}. Likely insufficient required items.", success: false);
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
        }

        return true;
    }

    private void CompleteAttempt(string status, bool success)
    {
        PurchaseAttempt? completedAttempt;
        lock (gate)
        {
            completedAttempt = activeAttempt;
            activeAttempt = null;
            lastStatus = status;
        }

        if (completedAttempt == null)
        {
            return;
        }

        if (success)
        {
            logger.Info($"[ShopPurchase] op=complete-success itemId={completedAttempt.ItemId} itemName=\"{completedAttempt.ItemName}\" status=\"{status}\"");
        }
        else
        {
            logger.Warning($"[ShopPurchase] op=complete-failed itemId={completedAttempt.ItemId} itemName=\"{completedAttempt.ItemName}\" status=\"{status}\"");
        }
    }

    private void SetStatus(string status)
    {
        lock (gate)
        {
            lastStatus = status;
        }
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
}
