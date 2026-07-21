using System;
using System.Linq;
using AOCCH.Data;
using Dalamud.Game.ClientState.Objects.Types;

namespace AOCCH.Scanning;

public static class CofferRecognition
{
    public static bool TryRecognize(VisibleCofferData data, IGameObject gameObject, out string source)
    {
        var objectKind = gameObject.ObjectKind.ToString();
        if (!data.ObjectKinds.Any(kind => string.Equals(kind, objectKind, StringComparison.OrdinalIgnoreCase)))
        {
            source = string.Empty;
            return false;
        }

        source = "object-kind";
        return true;
    }

    public static bool TryRecognizePotReveal(VisibleCofferData data, IGameObject gameObject, out string source)
    {
        if (data.BaseIds.Contains(gameObject.BaseId))
        {
            source = "base-id";
            return true;
        }

        var name = gameObject.Name.ToString();
        if (data.LocalizedNames.Any(configuredName => string.Equals(configuredName.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            source = "localized-name";
            return true;
        }

        source = string.Empty;
        return false;
    }
}
