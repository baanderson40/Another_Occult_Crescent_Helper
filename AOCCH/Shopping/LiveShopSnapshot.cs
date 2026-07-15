using System;
using System.Collections.Generic;

namespace AOCCH.Shopping;

public sealed class LiveShopSnapshot
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.MinValue;
    public bool IsSelectIconStringOpen { get; init; }
    public bool IsShopExchangeCurrencyOpen { get; init; }
    public bool IsShopExchangeItemOpen { get; init; }
    public int SelectedTabId { get; init; } = -1;
    public string CurrencyName { get; init; } = string.Empty;
    public uint CurrencyItemId { get; init; }
    public uint CurrencyAmount { get; init; }
    public string LastError { get; init; } = string.Empty;
    public IReadOnlyList<LiveShopMenuEntry> MenuEntries { get; init; } = Array.Empty<LiveShopMenuEntry>();
    public IReadOnlyList<LiveShopEntry> ShopEntries { get; init; } = Array.Empty<LiveShopEntry>();
    public IReadOnlyList<LiveShopExchangeItemEntry> ItemExchangeEntries { get; init; } = Array.Empty<LiveShopExchangeItemEntry>();
}
