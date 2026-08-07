using System;
using System.Collections.Generic;
using System.Linq;

namespace AOCCH.Automation;

public enum AutorotationProvider
{
    BossMod,
    BossModReborn,
    RSR,
    Wrath,
}

public static class AutorotationProviderDiscovery
{
    public static IReadOnlyList<AutorotationProvider> GetAvailable()
    {
        var plugins = Plugin.PluginInterface.InstalledPlugins
            .Where(plugin => plugin.IsLoaded)
            .Select(plugin => plugin.InternalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<AutorotationProvider>();
        if (plugins.Contains("BossMod")) result.Add(AutorotationProvider.BossMod);
        if (plugins.Contains("BossModReborn")) result.Add(AutorotationProvider.BossModReborn);
        if (plugins.Contains("RotationSolver")) result.Add(AutorotationProvider.RSR);
        if (plugins.Contains("WrathCombo")) result.Add(AutorotationProvider.Wrath);
        return result;
    }

    public static AutorotationProvider? GetDefault(IReadOnlyList<AutorotationProvider> available)
    {
        if (available.Contains(AutorotationProvider.BossModReborn)) return AutorotationProvider.BossModReborn;
        if (available.Contains(AutorotationProvider.BossMod)) return AutorotationProvider.BossMod;
        return null;
    }

    public static string GetDisplayName(AutorotationProvider provider)
        => provider switch
        {
            AutorotationProvider.BossMod => "BossMod",
            AutorotationProvider.BossModReborn => "BossModReborn",
            AutorotationProvider.RSR => "Rotation Solver Reborn",
            AutorotationProvider.Wrath => "Wrath",
            _ => provider.ToString(),
        };
}
