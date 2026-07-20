using Dalamud.Game.ClientState.Objects.Types;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using TreasureFlags = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags;

namespace AOCCH.Scanning;

public static class TreasureObjectState
{
    public static unsafe bool TryReadTreasureFlags(IGameObject gameObject, out TreasureFlags flags)
    {
        flags = TreasureFlags.None;
        if (gameObject == null || gameObject.Address == nint.Zero)
        {
            return false;
        }

        var objectPointer = (GameObjectStruct*)(void*)gameObject.Address;
        if (objectPointer == null)
        {
            return false;
        }

        var treasurePointer = (FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure*)objectPointer;
        flags = treasurePointer->Flags;
        return true;
    }
}
