using AOCCH.Data;
using Lumina.Excel.Sheets;

namespace AOCCH.Shopping;

internal static class ShoppingItemNameResolver
{
    public static string ResolveItemName(uint itemId, string? fallbackName = null)
    {
        if (itemId != 0)
        {
            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            if (itemSheet != null && itemSheet.TryGetRow(itemId, out var itemRow))
            {
                var resolvedName = ExcelTextResolver.ResolvePropertyText(itemRow, "Name");
                if (!string.IsNullOrEmpty(resolvedName))
                {
                    return resolvedName;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackName))
        {
            return fallbackName;
        }

        return itemId == 0 ? string.Empty : $"Item {itemId}";
    }
}
