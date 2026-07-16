using System;
using System.Collections.Generic;
using System.Linq;
using AOCCH.Data;
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
    private const int ShopExchangeCurrencySelectedTabOffset = 1;
    private const int ShopExchangeCurrencyCurrencyAmountOffset = 86;
    private const int ShopExchangeCurrencyCurrencyIconOffset = 87;
    private const int ShopExchangeCurrencyCostOffset = 456;
    private const int ShopExchangeCurrencyItemIdOffset = 1066;
    private const int ShopExchangeCurrencyMaxStackSizeOffset = 1188;
    private const int ShopExchangeCurrencyRowIndexOffset = 1310;
    private const int ShopExchangeItemIndex = 1;
    private const int ShopExchangeItemNumEntriesOffset = 3;
    private const int ShopExchangeItemCategoryIconOffset = 212;
    private const int ShopExchangeItemQuantityOffset = 700;
    private const int ShopExchangeItemItemIdOffset = 1066;
    private const int ShopExchangeItemRowIndexOffset = 1310;
    private const int ShopExchangeItemRequiredAmountOffset = 2775;
    private const int ShopExchangeItemRequiredItemIdOffset = 3141;
    private const int ShopExchangeItemRequirementsPerEntry = 3;

    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly AocchLogger logger;
    private readonly object gate = new();
    private readonly Dictionary<uint, uint> itemIdByIconId;
    private readonly Dictionary<uint, string> itemNameById;

    private LiveShopSnapshot snapshot = new();
    private CapturedAtkValueSnapshot? capturedShopExchangeCurrencyAtkValues;
    private string atkValueCaptureStatus = "No baseline captured.";
    private int currentMenuIndex = -1;
    private int latchedMenuIndex = -1;
    private string latchedMenuLabel = string.Empty;
    private IReadOnlyList<LiveShopMenuEntry> lastSeenMenuEntries = Array.Empty<LiveShopMenuEntry>();

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

    public string AtkValueCaptureStatus
    {
        get
        {
            lock (gate)
            {
                return atkValueCaptureStatus;
            }
        }
    }

    public int CurrentMenuIndex
    {
        get
        {
            lock (gate)
            {
                return currentMenuIndex;
            }
        }
    }

    public string CurrentMenuLabel
    {
        get
        {
            lock (gate)
            {
                return ResolveMenuLabel(currentMenuIndex, lastSeenMenuEntries);
            }
        }
    }

    public int LatchedMenuIndex
    {
        get
        {
            lock (gate)
            {
                return latchedMenuIndex;
            }
        }
    }

    public string LatchedMenuLabel
    {
        get
        {
            lock (gate)
            {
                return latchedMenuLabel;
            }
        }
    }

    public int EffectiveMenuIndex
    {
        get
        {
            lock (gate)
            {
                return latchedMenuIndex >= 0 ? latchedMenuIndex : currentMenuIndex;
            }
        }
    }

    public string EffectiveMenuLabel
    {
        get
        {
            lock (gate)
            {
                if (!string.IsNullOrEmpty(latchedMenuLabel))
                {
                    return latchedMenuLabel;
                }

                return ResolveMenuLabel(currentMenuIndex, lastSeenMenuEntries);
            }
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }

    public void SetCurrentMenuIndex(int menuIndex)
    {
        lock (gate)
        {
            currentMenuIndex = menuIndex;
        }
    }

    public void ClearLatchedMenuContext()
    {
        lock (gate)
        {
            latchedMenuIndex = -1;
            latchedMenuLabel = string.Empty;
        }

        logger.Info("[ShopInspector] op=menu-context-cleared");
    }

    public void CaptureManualMenuContext()
    {
        int menuIndex;
        string menuLabel;
        lock (gate)
        {
            menuIndex = currentMenuIndex;
            menuLabel = ResolveMenuLabel(currentMenuIndex, lastSeenMenuEntries);
            if (string.IsNullOrEmpty(menuLabel))
            {
                menuLabel = ResolveStaticMenuLabel(currentMenuIndex);
            }

            latchedMenuIndex = menuIndex;
            latchedMenuLabel = menuLabel;
        }

        logger.Info($"[ShopInspector] op=menu-context-manually-captured menuIndex={menuIndex} menuLabel={FormatValue(menuLabel)}");
    }

    public void LogCurrentSnapshot()
    {
        var currentSnapshot = Snapshot;
        var menuIndex = EffectiveMenuIndex;
        var menuLabel = EffectiveMenuLabel;
        var tabLabel = ResolveTabLabel(currentSnapshot.SelectedTabId);
        logger.Info($"[ShopInspector] op=snapshot-log capturedAt={FormatTimestamp(currentSnapshot.CapturedAt)} menuIndex={menuIndex} menuLabel={FormatValue(menuLabel)} selectIconStringOpen={currentSnapshot.IsSelectIconStringOpen} menuEntries={currentSnapshot.MenuEntries.Count} shopExchangeCurrencyOpen={currentSnapshot.IsShopExchangeCurrencyOpen} selectedTabId={currentSnapshot.SelectedTabId} selectedTabLabel={FormatValue(tabLabel)} currencyItemId={currentSnapshot.CurrencyItemId} currencyName={FormatValue(currentSnapshot.CurrencyName)} currencyAmount={currentSnapshot.CurrencyAmount} shopEntries={currentSnapshot.ShopEntries.Count} shopExchangeItemOpen={currentSnapshot.IsShopExchangeItemOpen} itemExchangeEntries={currentSnapshot.ItemExchangeEntries.Count} error={FormatValue(currentSnapshot.LastError)}");

        foreach (var menuEntry in currentSnapshot.MenuEntries)
        {
            logger.Info($"[ShopInspector] op=snapshot-menu-entry index={menuEntry.Index} label={FormatValue(menuEntry.Label)}");
        }

        foreach (var shopEntry in currentSnapshot.ShopEntries)
        {
            logger.Info($"[ShopInspector] op=snapshot-shop-entry menuIndex={menuIndex} selectedTabId={currentSnapshot.SelectedTabId} selectedTabLabel={FormatValue(tabLabel)} itemId={shopEntry.ItemId} itemName={FormatValue(shopEntry.ItemName)} currencyItemId={shopEntry.CurrencyItemId} currencyName={FormatValue(shopEntry.CurrencyName)} cost={shopEntry.Cost} rowIndex={shopEntry.RowIndex} maxStackSize={FormatNullableUInt(shopEntry.MaxStackSize)}");
        }

        foreach (var itemExchangeEntry in currentSnapshot.ItemExchangeEntries)
        {
            var requirements = itemExchangeEntry.RequiredItems.Count == 0
                ? "none"
                : string.Join(", ", itemExchangeEntry.RequiredItems.Select(requiredItem => $"{requiredItem.ItemName} ({requiredItem.ItemId}) x{requiredItem.RequiredAmount}"));

            logger.Info($"[ShopInspector] op=snapshot-item-exchange-entry itemId={itemExchangeEntry.ItemId} itemName={FormatValue(itemExchangeEntry.ItemName)} quantity={itemExchangeEntry.Quantity} rowIndex={itemExchangeEntry.RowIndex} categoryIconId={itemExchangeEntry.CategoryIconId} requirements={FormatValue(requirements)}");
        }
    }

    public void LogCurrentCurrencyCatalogCapture()
    {
        var currentSnapshot = Snapshot;
        var menuIndex = EffectiveMenuIndex;
        var menuLabel = EffectiveMenuLabel;
        var tabLabel = ResolveTabLabel(currentSnapshot.SelectedTabId);

        if (!currentSnapshot.IsShopExchangeCurrencyOpen)
        {
            logger.Warning("[ShopInspector] op=currency-catalog-capture-failed reason=\"ShopExchangeCurrency is not open.\"");
            return;
        }

        logger.Info($"[ShopInspector] op=currency-catalog-capture menuIndex={menuIndex} menuLabel={FormatValue(menuLabel)} currencyItemId={currentSnapshot.CurrencyItemId} currencyName={FormatValue(currentSnapshot.CurrencyName)} selectedTabId={currentSnapshot.SelectedTabId} selectedTabLabel={FormatValue(tabLabel)} entryCount={currentSnapshot.ShopEntries.Count}");
        foreach (var shopEntry in currentSnapshot.ShopEntries)
        {
            logger.Info($"[ShopInspector] op=currency-catalog-item menuIndex={menuIndex} selectedTabId={currentSnapshot.SelectedTabId} itemId={shopEntry.ItemId} itemName={FormatValue(shopEntry.ItemName)} cost={shopEntry.Cost} rowIndex={shopEntry.RowIndex} maxStackSize={FormatNullableUInt(shopEntry.MaxStackSize)}");
        }
    }

    public bool CaptureShopExchangeCurrencyAtkValues(int startIndex, int count)
    {
        if (!TryReadShopExchangeCurrencyAtkValues(startIndex, count, out var capturedSnapshot, out var reason))
        {
            lock (gate)
            {
                atkValueCaptureStatus = reason;
            }

            logger.Warning($"[ShopInspector] op=atkvalue-capture-failed reason={FormatValue(reason)}");
            return false;
        }

        lock (gate)
        {
            capturedShopExchangeCurrencyAtkValues = capturedSnapshot;
            atkValueCaptureStatus = $"Baseline captured for range {capturedSnapshot.StartIndex}-{capturedSnapshot.EndIndex}.";
        }

        logger.Info($"[ShopInspector] op=atkvalue-capture startIndex={capturedSnapshot.StartIndex} endIndex={capturedSnapshot.EndIndex} capturedCount={capturedSnapshot.Values.Count} atkValuesCount={capturedSnapshot.AtkValuesCount}");
        return true;
    }

    public bool CompareShopExchangeCurrencyAtkValues(int startIndex, int count)
    {
        CapturedAtkValueSnapshot? baseline;
        lock (gate)
        {
            baseline = capturedShopExchangeCurrencyAtkValues;
        }

        if (baseline == null)
        {
            lock (gate)
            {
                atkValueCaptureStatus = "No baseline captured.";
            }

            logger.Warning("[ShopInspector] op=atkvalue-compare-failed reason=\"No baseline captured.\"");
            return false;
        }

        var baselineSnapshot = baseline.Value;

        if (baselineSnapshot.StartIndex != startIndex || baselineSnapshot.Count != count)
        {
            var reason = $"Range mismatch. Baseline uses {baselineSnapshot.StartIndex}-{baselineSnapshot.EndIndex}; requested {startIndex}-{startIndex + Math.Max(0, count) - 1}.";
            lock (gate)
            {
                atkValueCaptureStatus = reason;
            }

            logger.Warning($"[ShopInspector] op=atkvalue-compare-failed reason={FormatValue(reason)}");
            return false;
        }

        if (!TryReadShopExchangeCurrencyAtkValues(startIndex, count, out var currentSnapshot, out var readReason))
        {
            lock (gate)
            {
                atkValueCaptureStatus = readReason;
            }

            logger.Warning($"[ShopInspector] op=atkvalue-compare-failed reason={FormatValue(readReason)}");
            return false;
        }

        var changedValues = new List<(CapturedAtkValue OldValue, CapturedAtkValue NewValue)>();
        for (var i = 0; i < baselineSnapshot.Values.Count && i < currentSnapshot.Values.Count; i++)
        {
            var oldValue = baselineSnapshot.Values[i];
            var newValue = currentSnapshot.Values[i];
            if (!oldValue.Equals(newValue))
            {
                changedValues.Add((oldValue, newValue));
            }
        }

        lock (gate)
        {
            atkValueCaptureStatus = $"Compare found {changedValues.Count} changed values in range {baselineSnapshot.StartIndex}-{baselineSnapshot.EndIndex}.";
        }

        logger.Info($"[ShopInspector] op=atkvalue-compare startIndex={baselineSnapshot.StartIndex} endIndex={baselineSnapshot.EndIndex} changedCount={changedValues.Count} atkValuesCount={currentSnapshot.AtkValuesCount}");
        foreach (var changedValue in changedValues)
        {
            logger.Info($"[ShopInspector] op=atkvalue-diff index={changedValue.OldValue.Index} oldType={changedValue.OldValue.Type} oldInt={changedValue.OldValue.IntValue} oldUInt={changedValue.OldValue.UIntValue} oldFloat={changedValue.OldValue.FloatValue} oldByte={changedValue.OldValue.ByteValue} newType={changedValue.NewValue.Type} newInt={changedValue.NewValue.IntValue} newUInt={changedValue.NewValue.UIntValue} newFloat={changedValue.NewValue.FloatValue} newByte={changedValue.NewValue.ByteValue}");
        }

        return true;
    }

    public void ClearCapturedShopExchangeCurrencyAtkValues()
    {
        lock (gate)
        {
            capturedShopExchangeCurrencyAtkValues = null;
            atkValueCaptureStatus = "No baseline captured.";
        }

        logger.Info("[ShopInspector] op=atkvalue-capture-cleared");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var nextSnapshot = CaptureSnapshot();

        LiveShopSnapshot previousSnapshot;
        lock (gate)
        {
            previousSnapshot = snapshot;
            if (nextSnapshot.MenuEntries.Count > 0)
            {
                lastSeenMenuEntries = nextSnapshot.MenuEntries;
            }
            snapshot = nextSnapshot;
        }

        TryLatchMenuContext(previousSnapshot, nextSnapshot);

        LogStateChanges(previousSnapshot, nextSnapshot);
    }

    private void TryLatchMenuContext(LiveShopSnapshot previousSnapshot, LiveShopSnapshot nextSnapshot)
    {
        if (!previousSnapshot.IsSelectIconStringOpen || !nextSnapshot.IsShopExchangeCurrencyOpen)
        {
            return;
        }

        int selectedMenuIndex;
        lock (gate)
        {
            selectedMenuIndex = currentMenuIndex;
        }

        if (selectedMenuIndex < 0)
        {
            return;
        }

        var menuLabel = ResolveMenuLabel(selectedMenuIndex, previousSnapshot.MenuEntries);
        if (string.IsNullOrEmpty(menuLabel))
        {
            menuLabel = ResolveMenuLabel(selectedMenuIndex, lastSeenMenuEntries);
        }

        if (string.IsNullOrEmpty(menuLabel))
        {
            menuLabel = ResolveStaticMenuLabel(selectedMenuIndex);
        }

        if (string.IsNullOrEmpty(menuLabel))
        {
            return;
        }

        lock (gate)
        {
            latchedMenuIndex = selectedMenuIndex;
            latchedMenuLabel = menuLabel;
        }

        logger.Info($"[ShopInspector] op=menu-context-latched menuIndex={selectedMenuIndex} menuLabel={FormatValue(menuLabel)}");
    }

    private LiveShopSnapshot CaptureSnapshot()
    {
        try
        {
            var menuEntries = TryReadSelectIconStringEntries(out var isSelectIconStringOpen);
            var shopReadResult = TryReadShopExchangeCurrencyEntries();
            var itemExchangeReadResult = TryReadShopExchangeItemEntries();

            return new LiveShopSnapshot
            {
                CapturedAt = DateTimeOffset.UtcNow,
                IsSelectIconStringOpen = isSelectIconStringOpen,
                IsShopExchangeCurrencyOpen = shopReadResult.IsOpen,
                IsShopExchangeItemOpen = itemExchangeReadResult.IsOpen,
                SelectedTabId = shopReadResult.SelectedTabId,
                CurrencyName = shopReadResult.CurrencyName,
                CurrencyItemId = shopReadResult.CurrencyItemId,
                CurrencyAmount = shopReadResult.CurrencyAmount,
                LastError = string.IsNullOrEmpty(shopReadResult.LastError)
                    ? itemExchangeReadResult.LastError
                    : shopReadResult.LastError,
                MenuEntries = menuEntries,
                ShopEntries = shopReadResult.Entries,
                ItemExchangeEntries = itemExchangeReadResult.Entries,
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
                MaxStackSize = addon->AtkValues[ShopExchangeCurrencyMaxStackSizeOffset + i].UInt,
                RowIndex = addon->AtkValues[ShopExchangeCurrencyRowIndexOffset + i].UInt,
            });
        }

        var selectedTabId = addon->AtkValues[ShopExchangeCurrencySelectedTabOffset].Int;
        return new ShopExchangeCurrencyReadResult(true, selectedTabId, currencyName, currencyItemId, currencyAmount, string.Empty, entries);
    }

    private unsafe bool TryReadShopExchangeCurrencyAtkValues(int startIndex, int count, out CapturedAtkValueSnapshot snapshotResult, out string reason)
    {
        snapshotResult = default;
        reason = string.Empty;

        var addon = (AtkUnitBase*)gameGui.GetAddonByName("ShopExchangeCurrency", ShopExchangeCurrencyIndex).Address;
        if (addon == null || !addon->IsReady)
        {
            reason = "ShopExchangeCurrency is not open.";
            return false;
        }

        if (count <= 0)
        {
            reason = "Count must be greater than zero.";
            return false;
        }

        var atkValuesCount = addon->AtkValuesCount;
        if (atkValuesCount == 0 || addon->AtkValues == null)
        {
            reason = "ShopExchangeCurrency has no readable AtkValues.";
            return false;
        }

        var clampedStartIndex = Math.Clamp(startIndex, 0, atkValuesCount - 1);
        var maxCount = atkValuesCount - clampedStartIndex;
        var clampedCount = Math.Clamp(count, 1, maxCount);
        var values = new List<CapturedAtkValue>(clampedCount);
        for (var i = 0; i < clampedCount; i++)
        {
            var index = clampedStartIndex + i;
            var atkValue = addon->AtkValues[index];
            values.Add(new CapturedAtkValue(
                index,
                atkValue.Type.ToString(),
                atkValue.Int,
                atkValue.UInt,
                atkValue.Float,
                atkValue.Byte));
        }

        snapshotResult = new CapturedAtkValueSnapshot(clampedStartIndex, clampedCount, atkValuesCount, values);
        return true;
    }

    private unsafe ShopExchangeItemReadResult TryReadShopExchangeItemEntries()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("ShopExchangeItem", ShopExchangeItemIndex).Address;
        if (addon == null || !addon->IsReady)
        {
            return ShopExchangeItemReadResult.Closed;
        }

        var entries = new List<LiveShopExchangeItemEntry>();
        var numEntries = (int)addon->AtkValues[ShopExchangeItemNumEntriesOffset].UInt;
        for (var i = 0; i < numEntries; i++)
        {
            var itemId = addon->AtkValues[ShopExchangeItemItemIdOffset + i].UInt;
            if (itemId == 0)
            {
                continue;
            }

            var requiredItems = new List<LiveRequiredExchangeItem>();
            for (var x = 0; x < ShopExchangeItemRequirementsPerEntry; x++)
            {
                var location = (i * ShopExchangeItemRequirementsPerEntry) + x;
                var requiredItemId = addon->AtkValues[ShopExchangeItemRequiredItemIdOffset + location].UInt;
                if (requiredItemId == 0)
                {
                    continue;
                }

                requiredItems.Add(new LiveRequiredExchangeItem
                {
                    ItemId = requiredItemId,
                    ItemName = ResolveItemName(requiredItemId),
                    RequiredAmount = addon->AtkValues[ShopExchangeItemRequiredAmountOffset + location].UInt,
                });
            }

            entries.Add(new LiveShopExchangeItemEntry
            {
                ItemId = itemId,
                ItemName = ResolveItemName(itemId),
                CategoryIconId = addon->AtkValues[ShopExchangeItemCategoryIconOffset + i].UInt,
                Quantity = addon->AtkValues[ShopExchangeItemQuantityOffset + i].UInt,
                RowIndex = addon->AtkValues[ShopExchangeItemRowIndexOffset + i].UInt,
                RequiredItems = requiredItems,
            });
        }

        return new ShopExchangeItemReadResult(true, string.Empty, entries);
    }

    private void LogStateChanges(LiveShopSnapshot previousSnapshot, LiveShopSnapshot nextSnapshot)
    {
        if (previousSnapshot.IsSelectIconStringOpen != nextSnapshot.IsSelectIconStringOpen)
        {
            logger.Info($"[ShopInspector] op=select-icon-string-state open={nextSnapshot.IsSelectIconStringOpen} entries={nextSnapshot.MenuEntries.Count}");
        }

        if (previousSnapshot.IsShopExchangeCurrencyOpen != nextSnapshot.IsShopExchangeCurrencyOpen)
        {
            logger.Info($"[ShopInspector] op=shop-exchange-currency-state open={nextSnapshot.IsShopExchangeCurrencyOpen} entries={nextSnapshot.ShopEntries.Count} selectedTabId={nextSnapshot.SelectedTabId} currencyItemId={nextSnapshot.CurrencyItemId} currencyAmount={nextSnapshot.CurrencyAmount}");
        }

        if (previousSnapshot.IsShopExchangeItemOpen != nextSnapshot.IsShopExchangeItemOpen)
        {
            logger.Info($"[ShopInspector] op=shop-exchange-item-state open={nextSnapshot.IsShopExchangeItemOpen} entries={nextSnapshot.ItemExchangeEntries.Count}");
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

            itemNameById[item.RowId] = ExcelTextResolver.ResolvePropertyText(item, "Name");
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

    private static string ResolveTabLabel(int tabId)
        => tabId switch
        {
            0 => "Weapons",
            1 => "Armor",
            2 => "Accessories",
            3 => "Other",
            _ => string.Empty,
        };

    private static string ResolveMenuLabel(int menuIndex, IReadOnlyList<LiveShopMenuEntry> menuEntries)
    {
        var menuEntry = menuEntries.FirstOrDefault(entry => entry.Index == menuIndex);
        return menuEntry?.Label ?? ResolveStaticMenuLabel(menuIndex);
    }

    private static string ResolveStaticMenuLabel(int menuIndex)
        => menuIndex switch
        {
            0 => "Enlightenment Silver Piece Exchange (IL 745)",
            1 => "Enlightenment Silver Piece Exchange (Battlecraft Items)",
            2 => "Enlightenment Silver Piece Exchange (Other)",
            3 => "Enlightenment Gold Piece Exchange (Battlecraft Items)",
            4 => "Enlightenment Gold Piece Exchange (Other)",
            5 => "Sanguinite Exchange",
            6 => "Cipher Exchange",
            7 => "Nothing",
            _ => string.Empty,
        };

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

    private static string FormatNullableUInt(uint? value)
        => value?.ToString() ?? "null";

    private readonly record struct ShopExchangeCurrencyReadResult(
        bool IsOpen,
        int SelectedTabId,
        string CurrencyName,
        uint CurrencyItemId,
        uint CurrencyAmount,
        string LastError,
        IReadOnlyList<LiveShopEntry> Entries)
    {
        public static ShopExchangeCurrencyReadResult Closed { get; } = new(false, -1, string.Empty, 0, 0, string.Empty, Array.Empty<LiveShopEntry>());
    }

    private readonly record struct ShopExchangeItemReadResult(
        bool IsOpen,
        string LastError,
        IReadOnlyList<LiveShopExchangeItemEntry> Entries)
    {
        public static ShopExchangeItemReadResult Closed { get; } = new(false, string.Empty, Array.Empty<LiveShopExchangeItemEntry>());
    }

    private readonly record struct CapturedAtkValue(
        int Index,
        string Type,
        int IntValue,
        uint UIntValue,
        float FloatValue,
        byte ByteValue);

    private readonly record struct CapturedAtkValueSnapshot(
        int StartIndex,
        int Count,
        int AtkValuesCount,
        IReadOnlyList<CapturedAtkValue> Values)
    {
        public int EndIndex => StartIndex + Count - 1;
    }
}
