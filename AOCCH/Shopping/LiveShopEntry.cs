namespace AOCCH.Shopping;

public sealed class LiveShopEntry
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public uint CurrencyItemId { get; init; }
    public string CurrencyName { get; init; } = string.Empty;
    public uint Cost { get; init; }
    public uint RowIndex { get; init; }
    public int? TabIndex { get; init; }
    public uint? MaxStackSize { get; init; }
    public bool IsVisible { get; init; } = true;
}
