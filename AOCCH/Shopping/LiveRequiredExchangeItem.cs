namespace AOCCH.Shopping;

public sealed class LiveRequiredExchangeItem
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public uint RequiredAmount { get; init; }
}
