using System;
using System.Collections.Generic;
using System.Linq;
using AOCCH.IPC;

namespace AOCCH.Automation;

public sealed record AutomationDependencyStatus(
    string Key,
    string Name,
    bool Installed,
    bool Available,
    string Detail)
{
    public bool IsUsable => Installed && Available;
}

public sealed class AutomationDependencyReport
{
    public AutomationDependencyReport(IReadOnlyList<AutomationDependencyStatus> statuses)
    {
        Statuses = statuses;
    }

    public IReadOnlyList<AutomationDependencyStatus> Statuses { get; }

    public bool IsReady => Statuses.All(status => status.IsUsable);

    public string FailureSummary
        => string.Join(" ", Statuses.Where(status => !status.IsUsable).Select(status => status.Detail));
}

public sealed class NormalAutomationDependencyChecker
{
    private const string VNavmeshName = "vnavmesh";
    private const string BossModName = "BossMod";
    private const string BossModRebornName = "BossModReborn";

    private readonly VNavmeshIpc vnavmesh;
    private readonly BossModIpc bossMod;

    public NormalAutomationDependencyChecker(VNavmeshIpc vnavmesh, BossModIpc bossMod)
    {
        this.vnavmesh = vnavmesh;
        this.bossMod = bossMod;
    }

    public AutomationDependencyReport Evaluate()
    {
        var installedPlugins = Plugin.PluginInterface.InstalledPlugins;
        var vnavmeshInstalled = installedPlugins.Any(plugin => string.Equals(plugin.InternalName, VNavmeshName, StringComparison.OrdinalIgnoreCase));
        var rotationProvider = installedPlugins.FirstOrDefault(plugin =>
            plugin.IsLoaded
            && (string.Equals(plugin.InternalName, BossModName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(plugin.InternalName, BossModRebornName, StringComparison.OrdinalIgnoreCase)));
        var rotationProviderName = rotationProvider?.InternalName;
        var bossModAvailable = rotationProvider != null && bossMod.IsAvailable();

        return new AutomationDependencyReport(
        [
            new AutomationDependencyStatus(
                "vnavmesh",
                VNavmeshName,
                vnavmeshInstalled,
                vnavmesh.IsReady(),
                vnavmeshInstalled ? "vnavmesh is installed but unavailable." : "vnavmesh is not installed."),
            new AutomationDependencyStatus(
                "rotation",
                rotationProviderName ?? "BossMod / BossModReborn",
                rotationProviderName != null,
                bossModAvailable,
                rotationProviderName == null
                    ? "BossMod or BossModReborn is not installed or enabled."
                    : $"BossMod Presets IPC is unavailable from {rotationProviderName}.")
        ]);
    }
}
