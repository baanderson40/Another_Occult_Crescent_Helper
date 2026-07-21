using System;
using System.Collections.Generic;

namespace AOCCH.Shopping;

public sealed class LiveShopExchangeItemEntry
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public uint RowIndex { get; init; }
    public uint Quantity { get; init; }
    public uint CategoryIconId { get; init; }
    public IReadOnlyList<LiveRequiredExchangeItem> RequiredItems { get; init; } = Array.Empty<LiveRequiredExchangeItem>();
}
