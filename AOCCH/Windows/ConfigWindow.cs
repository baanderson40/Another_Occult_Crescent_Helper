using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AOCCH.Data;
using AOCCH.Logging;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public class ConfigWindow : Window, IDisposable
{
    private static readonly string[] FatePriorityLabels = ["Lowest Progress", "Nearest"];
    private static readonly string[] StartingPotFateLabels = ["Auto", "Persistent Pots (South)", "Pleading Pots (North)"];
    private static readonly TimeSpan SettingTextLogInterval = TimeSpan.FromSeconds(10);

    private readonly Configuration configuration;
    private readonly OccultCrescentData data;
    private readonly OccultCrescentNameResolver nameResolver;
    private readonly AocchLogger logger;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(
        Configuration configuration,
        OccultCrescentData data,
        OccultCrescentNameResolver nameResolver,
        AocchLogger logger) : base("AOCCH Configuration###AOCCHConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(620, 360);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.configuration = configuration;
        this.data = data;
        this.nameResolver = nameResolver;
        this.logger = logger;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("AOCCHConfigTabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Critical Engagements"))
        {
            DrawCriticalEngagementsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("FATEs"))
        {
            DrawFatesTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Pots"))
        {
            DrawPotsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Treasure Coffers"))
        {
            DrawTreasureCoffersTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Settings"))
        {
            DrawSettingsTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawCriticalEngagementsTab()
    {
        var enableCeFarming = configuration.EnableCriticalEngagementFarming;
        if (ImGui.Checkbox("Enable CE Farming", ref enableCeFarming))
        {
            logger.Info($"Setting changed: EnableCriticalEngagementFarming: {configuration.EnableCriticalEngagementFarming} -> {enableCeFarming}.");
            configuration.EnableCriticalEngagementFarming = enableCeFarming;
            configuration.Save();
        }

        var prioritizeCe = configuration.PrioritizeCe;
        if (ImGui.Checkbox("Prioritize CE", ref prioritizeCe))
        {
            logger.Info($"Setting changed: PrioritizeCe: {configuration.PrioritizeCe} -> {prioritizeCe}.");
            configuration.PrioritizeCe = prioritizeCe;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Enabled Critical Engagements");
        DrawCriticalEncounterCheckboxList();
    }

    private void DrawFatesTab()
    {
        var enableFateFarming = configuration.EnableFateFarming;
        if (ImGui.Checkbox("Enable FATE Farming", ref enableFateFarming))
        {
            logger.Info($"Setting changed: EnableFateFarming: {configuration.EnableFateFarming} -> {enableFateFarming}.");
            configuration.EnableFateFarming = enableFateFarming;
            configuration.Save();
        }

        var fatePriority = (int)configuration.FatePriority;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("FATE Priority", ref fatePriority, FatePriorityLabels, FatePriorityLabels.Length))
        {
            logger.Info($"Setting changed: FatePriority: {configuration.FatePriority} -> {(FatePriority)fatePriority}.");
            configuration.FatePriority = (FatePriority)fatePriority;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Enabled FATEs");
        DrawFateCheckboxList();
    }

    private void DrawPotsTab()
    {
        ImGui.TextUnformatted("Pot Cycle");

        var startingPotFate = (int)configuration.StartingPotFate;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Starting Pot FATE", ref startingPotFate, StartingPotFateLabels, StartingPotFateLabels.Length))
        {
            logger.Info($"Setting changed: StartingPotFate: {configuration.StartingPotFate} -> {(StartingPotFateMode)startingPotFate}.");
            configuration.StartingPotFate = (StartingPotFateMode)startingPotFate;
            configuration.Save();
        }

        DrawClampedIntSetting(
            "Spawn Lead Minutes",
            configuration.SpawnLeadMinutes,
            0,
            30,
            value => configuration.SpawnLeadMinutes = value,
            nameof(configuration.SpawnLeadMinutes));

        var manageInstanceTime = configuration.ManageInstanceTime;
        if (ImGui.Checkbox("Manage Instance Time", ref manageInstanceTime))
        {
            logger.Info($"Setting changed: ManageInstanceTime: {configuration.ManageInstanceTime} -> {manageInstanceTime}.");
            configuration.ManageInstanceTime = manageInstanceTime;
            configuration.Save();
        }

        ImGui.TextWrapped("When enabled, pot timing can respect the remaining instance window and exit buffer.");

        DrawClampedIntSetting(
            "FATE Completion Budget Minutes",
            configuration.FateCompletionBudgetMinutes,
            0,
            60,
            value => configuration.FateCompletionBudgetMinutes = value,
            nameof(configuration.FateCompletionBudgetMinutes));
        DrawClampedIntSetting(
            "Treasure Hunt Budget Minutes",
            configuration.TreasureHuntBudgetMinutes,
            0,
            60,
            value => configuration.TreasureHuntBudgetMinutes = value,
            nameof(configuration.TreasureHuntBudgetMinutes));
        DrawClampedIntSetting(
            "Instance Exit Buffer Minutes",
            configuration.InstanceExitBufferMinutes,
            0,
            30,
            value => configuration.InstanceExitBufferMinutes = value,
            nameof(configuration.InstanceExitBufferMinutes));

        ImGui.Separator();
        ImGui.TextUnformatted("Treasure Travel");

        DrawClampedIntSetting(
            "Spawn Arrival Radius",
            configuration.SpawnArrivalRadius,
            0,
            100,
            value => configuration.SpawnArrivalRadius = value,
            nameof(configuration.SpawnArrivalRadius));
        DrawClampedIntSetting(
            "Maximum Aggro Level",
            configuration.MaximumAggroLevel,
            0,
            20,
            value => configuration.MaximumAggroLevel = value,
            nameof(configuration.MaximumAggroLevel));

        var useNinjaForDangerousArea = configuration.UseNinjaForDangerousArea;
        if (ImGui.Checkbox("Use Ninja For Dangerous Area", ref useNinjaForDangerousArea))
        {
            logger.Info($"Setting changed: UseNinjaForDangerousArea: {configuration.UseNinjaForDangerousArea} -> {useNinjaForDangerousArea}.");
            configuration.UseNinjaForDangerousArea = useNinjaForDangerousArea;
            configuration.Save();
        }

        ImGui.TextWrapped("When enabled, dangerous treasure candidates can switch to the configured Ninja gearset, use Hide, and finish the last stretch on foot.");

        using var disabled = ImRaii.Disabled(!configuration.UseNinjaForDangerousArea);
        {
            DrawClampedIntSetting(
                "Hide Threshold Distance",
                configuration.HideThresholdDistance,
                0,
                500,
                value => configuration.HideThresholdDistance = value,
                nameof(configuration.HideThresholdDistance));
            DrawClampedIntSetting(
                "Ninja Gearset Number",
                configuration.NinjaGearsetNumber,
                0,
                100,
                value => configuration.NinjaGearsetNumber = value,
                nameof(configuration.NinjaGearsetNumber));
        }

        DrawClampedIntSetting(
            "FATE Gearset Number",
            configuration.FateGearsetNumber,
            0,
            100,
            value => configuration.FateGearsetNumber = value,
            nameof(configuration.FateGearsetNumber));

        ImGui.Separator();
        ImGui.TextUnformatted("Fallback Gating");

        DrawClampedIntSetting(
            "CE Fallback Cutoff Minutes",
            configuration.CeFallbackCutoffMinutes,
            0,
            30,
            value => configuration.CeFallbackCutoffMinutes = value,
            nameof(configuration.CeFallbackCutoffMinutes));
        DrawClampedIntSetting(
            "FATE Fallback Cutoff Minutes",
            configuration.FateFallbackCutoffMinutes,
            0,
            30,
            value => configuration.FateFallbackCutoffMinutes = value,
            nameof(configuration.FateFallbackCutoffMinutes));

        ImGui.TextWrapped("New fallback CE or non-pot FATE starts are held once the predicted pot departure is inside the configured cutoff window.");
    }

    private static void DrawTreasureCoffersTab()
    {
        ImGui.TextUnformatted("Treasure coffer settings will be added later.");
    }

    private void DrawSettingsTab()
    {
        var autorotationPresetName = configuration.AutorotationPresetName;
        ImGui.SetNextItemWidth(240);
        if (ImGui.InputText("Autorotation Preset Name", ref autorotationPresetName, 128))
        {
            logger.InfoThrottled("setting-autorotation-preset-name", SettingTextLogInterval, $"Setting changed: AutorotationPresetName: '{configuration.AutorotationPresetName}' -> '{autorotationPresetName}'.");
            configuration.AutorotationPresetName = autorotationPresetName;
            configuration.Save();
        }

        var useReturn = configuration.UseReturn;
        if (ImGui.Checkbox("Use Return", ref useReturn))
        {
            logger.Info($"Setting changed: UseReturn: {configuration.UseReturn} -> {useReturn}.");
            configuration.UseReturn = useReturn;
            configuration.Save();
        }

        var enableBuffRotation = configuration.EnableBuffRotation;
        if (ImGui.Checkbox("Enable Buff Rotation", ref enableBuffRotation))
        {
            logger.Info($"Setting changed: EnableBuffRotation: {configuration.EnableBuffRotation} -> {enableBuffRotation}.");
            configuration.EnableBuffRotation = enableBuffRotation;
            configuration.Save();
        }

        var minimumMountingRange = configuration.MinimumMountingRange;
        if (ImGui.InputInt("Minimum Mounting Range", ref minimumMountingRange))
        {
            var nextValue = Math.Clamp(minimumMountingRange, 0, 100);
            logger.InfoThrottled("setting-minimum-mounting-range", SettingTextLogInterval, $"Setting changed: MinimumMountingRange: {configuration.MinimumMountingRange} -> {nextValue}.");
            configuration.MinimumMountingRange = nextValue;
            configuration.Save();
        }

        ImGui.TextWrapped("Movement stays on foot when the current pathing step starts within this many yalms of its destination.");

        var scannerOnlyMode = configuration.ScannerOnlyMode;
        if (ImGui.Checkbox("Scanner-Only Mode", ref scannerOnlyMode))
        {
            logger.Info($"Setting changed: ScannerOnlyMode: {configuration.ScannerOnlyMode} -> {scannerOnlyMode}.");
            configuration.ScannerOnlyMode = scannerOnlyMode;
            configuration.Save();
        }

        ImGui.TextWrapped("Scanner-only mode keeps scanning and target selection active while blocking movement, combat automation, and buff rotation starts.");
    }

    private void DrawCriticalEncounterCheckboxList()
    {
        DrawScrollableCheckboxList(
            "AOCCHCeCheckboxes",
            GetCriticalEncounterEntries(),
            configuration.IsCriticalEncounterEnabled,
            (id, enabled) => configuration.SetCriticalEncounterEnabled(id, enabled),
            "CriticalEncounter");
    }

    private void DrawFateCheckboxList()
    {
        DrawScrollableCheckboxList(
            "AOCCHFateCheckboxes",
            GetDirectFarmFateEntries(),
            configuration.IsFateEnabled,
            (id, enabled) => configuration.SetFateEnabled(id, enabled),
            "FATE");
    }

    private void DrawScrollableCheckboxList(
        string childId,
        IReadOnlyList<(uint Id, string Label)> entries,
        Func<uint, bool> isEnabled,
        Func<uint, bool, bool> setEnabled,
        string logLabel)
    {
        ImGui.BeginChild(childId, new Vector2(0, 170), true);
        foreach (var entry in entries)
        {
            var enabled = isEnabled(entry.Id);
            if (!ImGui.Checkbox($"{entry.Label}##{childId}-{entry.Id}", ref enabled) || !setEnabled(entry.Id, enabled))
            {
                continue;
            }

            logger.Info($"Setting changed: {logLabel} {entry.Id} enabled={enabled}.");
            configuration.Save();
        }

        ImGui.EndChild();
    }

    private void DrawClampedIntSetting(string label, int currentValue, int minValue, int maxValue, Action<int> applyValue, string logName)
    {
        var value = currentValue;
        if (!ImGui.InputInt(label, ref value))
        {
            return;
        }

        var nextValue = Math.Clamp(value, minValue, maxValue);
        if (nextValue == currentValue)
        {
            return;
        }

        logger.InfoThrottled($"setting-{logName}", SettingTextLogInterval, $"Setting changed: {logName}: {currentValue} -> {nextValue}.");
        applyValue(nextValue);
        configuration.Save();
    }

    private List<(uint Id, string Label)> GetCriticalEncounterEntries()
        => data.CriticalEncounters
            .Select(criticalEncounter => (
                criticalEncounter.Id,
                nameResolver.GetCriticalEncounterName(criticalEncounter.Id, criticalEncounter.Name)))
            .OrderBy(entry => entry.Item2, StringComparer.Ordinal)
            .ToList();

    private List<(uint Id, string Label)> GetDirectFarmFateEntries()
        => data.Fates
            .Where(fate => !IsPotFate(fate))
            .Select(fate => (
                fate.Id,
                nameResolver.GetFateName(fate.Id, fate.Name)))
            .OrderBy(entry => entry.Item2, StringComparer.Ordinal)
            .ToList();

    private static bool IsPotFate(FateData fate)
        => string.Equals(fate.Note, "PersistentPots", StringComparison.Ordinal);
}
