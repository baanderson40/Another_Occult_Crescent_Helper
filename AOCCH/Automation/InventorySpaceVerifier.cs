using FFXIVClientStructs.FFXIV.Client.Game;

namespace AOCCH.Automation;

internal static class InventorySpaceVerifier
{
    private static readonly InventoryType[] NormalInventoryContainers = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    public static unsafe bool TryGetFreeNormalInventorySlots(out int freeSlots, out string error)
    {
        freeSlots = 0;
        error = string.Empty;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            error = "inventory-manager-unavailable";
            return false;
        }

        var totalSlots = 0;
        var nonEmptySlots = 0;
        foreach (var containerType in NormalInventoryContainers)
        {
            var container = inventoryManager->GetInventoryContainer(containerType);
            if (container == null)
            {
                error = $"container-unavailable:{containerType}";
                return false;
            }

            if (!container->IsLoaded || container->Size <= 0 || container->Items == null)
            {
                error = $"container-not-ready:{containerType}:loaded={container->IsLoaded}:size={container->Size}";
                return false;
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

        freeSlots = totalSlots - nonEmptySlots;
        return true;
    }

}
