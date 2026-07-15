using System;
using System.Collections.Generic;
using AOCCH.Logging;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AOCCH.Shopping;

public sealed class ShopInspectorController : IDisposable
{
    private const int SelectIconStringIndex = 1;
    private const int ShopExchangeCurrencyIndex = 1;
    private const int ShopExchangeCurrencyNumEntriesOffset = 4;
    private const int ShopExchangeCurrencyCurrencyAmountOffset = 86;
    private const int ShopExchangeCurrencyCurrencyIconOffset = 87;
    private const int ShopExchangeCurrencyCostOffset = 456;
    private const int ShopExchangeCurrencyItemIdOffset = 1066;
    private const int ShopExchangeCurrencyRowIndexOffset = 1310;

    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly AocchLogger logger;
    private readonly object gate = new();
    private readonly Dictionary<uint, uint> itemIdByIconId;
    private readonly Dictionary<uint, string> itemNameById;

    private LiveShopSnapshot snapshot = new();

    public ShopInspectorController(
        IFramework framework,
        IGameGui gameGui,
        IDataManager dataManager,
        AocchLogger logger)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.logger = logger;
        BuildItemCaches(dataManager, out itemIdByIconId, out itemNameById);

        framework.Update += OnFrameworkUpdate;
        logger.Info($"[ShopInspector] op=init iconMapCount={itemIdByIconId.Count} itemNameMapCount={itemNameById.Count}");
    }

    public LiveShopSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }

    public void LogCurrentSnapshot()
    {
        var currentSnapshot = Snapshot;
        logger.Info($"[ShopInspector] op=snapshot-log capturedAt={FormatTimestamp(currentSnapshot.CapturedAt)} selectIconStringOpen={currentSnapshot.IsSelectIconStringOpen} menuEntries={currentSnapshot.MenuEntries.Count} shopExchangeCurrencyOpen={currentSnapshot.IsShopExchangeCurrencyOpen} currencyItemId={currentSnapshot.CurrencyItemId} currencyAmount={currentSnapshot.CurrencyAmount} shopEntries={currentSnapshot.ShopEntries.Count} error={FormatValue(currentSnapshot.LastError)}");

        foreach (var menuEntry in currentSnapshot.MenuEntries)
        {
            logger.Info($"[ShopInspector] op=snapshot-menu-entry index={menuEntry.Index} label={FormatValue(menuEntry.Label)}");
        }

        foreach (var shopEntry in currentSnapshot.ShopEntries)
        {
            logger.Info($"[ShopInspector] op=snapshot-shop-entry itemId={shopEntry.ItemId} itemName={FormatValue(shopEntry.ItemName)} currencyItemId={shopEntry.CurrencyItemId} currencyName={FormatValue(shopEntry.CurrencyName)} cost={shopEntry.Cost} rowIndex={shopEntry.RowIndex}");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var nextSnapshot = CaptureSnapshot();

        LiveShopSnapshot previousSnapshot;
        lock (gate)
        {
            previousSnapshot = snapshot;
            snapshot = nextSnapshot;
        }

        LogStateChanges(previousSnapshot, nextSnapshot);
    }

    private LiveShopSnapshot CaptureSnapshot()
    {
        try
        {
            var menuEntries = TryReadSelectIconStringEntries(out var isSelectIconStringOpen);
            var shopReadResult = TryReadShopExchangeCurrencyEntries();

            return new LiveShopSnapshot
            {
                CapturedAt = DateTimeOffset.UtcNow,
                IsSelectIconStringOpen = isSelectIconStringOpen,
                IsShopExchangeCurrencyOpen = shopReadResult.IsOpen,
                CurrencyName = shopReadResult.CurrencyName,
                CurrencyItemId = shopReadResult.CurrencyItemId,
                CurrencyAmount = shopReadResult.CurrencyAmount,
                LastError = shopReadResult.LastError,
                MenuEntries = menuEntries,
                ShopEntries = shopReadResult.Entries,
            };
        }
        catch (Exception ex)
        {
            logger.Error($"[ShopInspector] op=capture-exception message={FormatValue(ex.Message)} type={ex.GetType().Name}");
            return new LiveShopSnapshot
            {
                CapturedAt = DateTimeOffset.UtcNow,
                LastError = ex.Message,
            };
        }
    }

    private unsafe IReadOnlyList<LiveShopMenuEntry> TryReadSelectIconStringEntries(out bool isOpen)
    {
        var addon = (AddonSelectIconString*)gameGui.GetAddonByName("SelectIconString", SelectIconStringIndex).Address;
        if (addon == null || !addon->AtkUnitBase.IsReady)
        {
            isOpen = false;
            return Array.Empty<LiveShopMenuEntry>();
        }

        isOpen = true;
        var menuEntries = new List<LiveShopMenuEntry>();
        var entryCount = addon->PopupMenu.PopupMenu.EntryCount;
        for (var i = 0; i < entryCount; i++)
        {
            var entryPointer = addon->PopupMenu.PopupMenu.EntryNames[i].Value;
            var label = entryPointer == null
                ? string.Empty
                : MemoryHelper.ReadSeStringNullTerminated((nint)entryPointer).ToString()?.Trim() ?? string.Empty;

            menuEntries.Add(new LiveShopMenuEntry
            {
                Index = i,
                Label = label,
            });
        }

        return menuEntries;
    }

    private unsafe ShopExchangeCurrencyReadResult TryReadShopExchangeCurrencyEntries()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("ShopExchangeCurrency", ShopExchangeCurrencyIndex).Address;
        if (addon == null || !addon->IsReady)
        {
            return ShopExchangeCurrencyReadResult.Closed;
        }

        var entries = new List<LiveShopEntry>();
        var numEntries = (int)addon->AtkValues[ShopExchangeCurrencyNumEntriesOffset].UInt;
        var currencyAmount = addon->AtkValues[ShopExchangeCurrencyCurrencyAmountOffset].UInt;
        var currencyIconId = addon->AtkValues[ShopExchangeCurrencyCurrencyIconOffset].UInt;
        var currencyItemId = ResolveItemIdByIconId(currencyIconId);
        var currencyName = ResolveItemName(currencyItemId);

        for (var i = 0; i < numEntries; i++)
        {
            var itemId = addon->AtkValues[ShopExchangeCurrencyItemIdOffset + i].UInt;
            if (itemId == 0)
            {
                continue;
            }

            entries.Add(new LiveShopEntry
            {
                ItemId = itemId,
                ItemName = ResolveItemName(itemId),
                CurrencyItemId = currencyItemId,
                CurrencyName = currencyName,
                Cost = addon->AtkValues[ShopExchangeCurrencyCostOffset + i].UInt,
                RowIndex = addon->AtkValues[ShopExchangeCurrencyRowIndexOffset + i].UInt,
            });
        }

        return new ShopExchangeCurrencyReadResult(true, currencyName, currencyItemId, currencyAmount, string.Empty, entries);
    }

    private void LogStateChanges(LiveShopSnapshot previousSnapshot, LiveShopSnapshot nextSnapshot)
    {
        if (previousSnapshot.IsSelectIconStringOpen != nextSnapshot.IsSelectIconStringOpen)
        {
            logger.Info($"[ShopInspector] op=select-icon-string-state open={nextSnapshot.IsSelectIconStringOpen} entries={nextSnapshot.MenuEntries.Count}");
        }

        if (previousSnapshot.IsShopExchangeCurrencyOpen != nextSnapshot.IsShopExchangeCurrencyOpen)
        {
            logger.Info($"[ShopInspector] op=shop-exchange-currency-state open={nextSnapshot.IsShopExchangeCurrencyOpen} entries={nextSnapshot.ShopEntries.Count} currencyItemId={nextSnapshot.CurrencyItemId} currencyAmount={nextSnapshot.CurrencyAmount}");
        }

        if (string.Equals(previousSnapshot.LastError, nextSnapshot.LastError, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.IsNullOrEmpty(nextSnapshot.LastError))
        {
            logger.Warning($"[ShopInspector] op=read-warning message={FormatValue(nextSnapshot.LastError)}");
        }
    }

    private static void BuildItemCaches(IDataManager dataManager, out Dictionary<uint, uint> itemIdByIconId, out Dictionary<uint, string> itemNameById)
    {
        itemIdByIconId = new Dictionary<uint, uint>();
        itemNameById = new Dictionary<uint, string>();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            return;
        }

        foreach (var item in itemSheet)
        {
            if (item.RowId == 0)
            {
                continue;
            }

            itemNameById[item.RowId] = item.Name.ToString();
            if (item.Icon != 0 && !itemIdByIconId.ContainsKey(item.Icon))
            {
                itemIdByIconId[item.Icon] = item.RowId;
            }
        }
    }

    private uint ResolveItemIdByIconId(uint iconId)
        => itemIdByIconId.TryGetValue(iconId, out var itemId)
            ? itemId
            : 0;

    private string ResolveItemName(uint itemId)
    {
        if (itemId == 0)
        {
            return string.Empty;
        }

        if (itemNameById.TryGetValue(itemId, out var itemName))
        {
            return itemName;
        }

        return $"Unknown Item {itemId}";
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp == DateTimeOffset.MinValue
            ? "none"
            : timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string FormatValue(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "\"\""
            : $"\"{value.Replace("\"", "'")}\"";

    private readonly record struct ShopExchangeCurrencyReadResult(
        bool IsOpen,
        string CurrencyName,
        uint CurrencyItemId,
        uint CurrencyAmount,
        string LastError,
        IReadOnlyList<LiveShopEntry> Entries)
    {
        public static ShopExchangeCurrencyReadResult Closed { get; } = new(false, string.Empty, 0, 0, string.Empty, Array.Empty<LiveShopEntry>());
    }
}
