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
    string Detail,
    bool Required = true)
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

    public bool IsReady => Statuses.Where(status => status.Required).All(status => status.IsUsable);

    public string FailureSummary
        => string.Join(" ", Statuses.Where(status => status.Required && !status.IsUsable).Select(status => status.Detail));
}

public sealed class NormalAutomationDependencyChecker
{
    private const string VNavmeshName = "vnavmesh";
    private const string BossModName = "BossMod";
    private const string BossModRebornName = "BossModReborn";

    private readonly VNavmeshIpc vnavmesh;
    private readonly BossModIpc bossMod;
    private readonly WrathComboIpc wrath;

    public NormalAutomationDependencyChecker(VNavmeshIpc vnavmesh, BossModIpc bossMod, WrathComboIpc wrath)
    {
        this.vnavmesh = vnavmesh;
        this.bossMod = bossMod;
        this.wrath = wrath;
    }

    public AutomationDependencyReport Evaluate()
    {
        var installedPlugins = Plugin.PluginInterface.InstalledPlugins;
        var vnavmeshInstalled = installedPlugins.Any(plugin => string.Equals(plugin.InternalName, VNavmeshName, StringComparison.OrdinalIgnoreCase));
        var bossModInstalled = installedPlugins.Any(plugin => plugin.IsLoaded && string.Equals(plugin.InternalName, BossModName, StringComparison.OrdinalIgnoreCase));
        var bossModRebornInstalled = installedPlugins.Any(plugin => plugin.IsLoaded && string.Equals(plugin.InternalName, BossModRebornName, StringComparison.OrdinalIgnoreCase));
        var rotationSolverInstalled = installedPlugins.Any(plugin => plugin.IsLoaded && string.Equals(plugin.InternalName, "RotationSolver", StringComparison.OrdinalIgnoreCase));
        var wrathInstalled = installedPlugins.Any(plugin => plugin.IsLoaded && string.Equals(plugin.InternalName, "WrathCombo", StringComparison.OrdinalIgnoreCase));
        var bossModAvailable = (bossModInstalled || bossModRebornInstalled) && bossMod.IsAvailable();

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
                bossModRebornInstalled ? "BossModReborn" : bossModInstalled ? "BossMod" : "BossMod / BossModReborn",
                bossModInstalled || bossModRebornInstalled,
                bossModAvailable,
                !bossModInstalled && !bossModRebornInstalled
                    ? "BossMod or BossModReborn is not installed or enabled."
                    : "BossMod Presets IPC is unavailable.") ,
            new AutomationDependencyStatus("rotation-solver-reborn", "RotationSolver", rotationSolverInstalled, rotationSolverInstalled, rotationSolverInstalled ? "Optional solver is available." : "Optional solver is not enabled.", Required: false),
            new AutomationDependencyStatus("wrath", "WrathCombo", wrathInstalled, wrathInstalled && wrath.IsAvailable(), wrathInstalled ? "Optional solver is available." : "Optional solver is not enabled.", Required: false)
        ]);
    }
}
