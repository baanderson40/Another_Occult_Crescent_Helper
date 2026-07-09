using System;
using System.Numerics;
using AOCCH.Logging;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public class ConfigWindow : Window, IDisposable
{
    private static readonly string[] FarmingModeLabels = ["CE & FATE", "CE Only", "FATE Only"];
    private static readonly string[] FatePriorityLabels = ["Lowest Progress", "Nearest"];
    private static readonly TimeSpan SettingTextLogInterval = TimeSpan.FromSeconds(10);

    private readonly Configuration configuration;
    private readonly AocchLogger logger;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Configuration configuration, AocchLogger logger) : base("AOCCH Configuration###AOCCHConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(620, 360);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.configuration = configuration;
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
        var autorotationPresetName = configuration.AutorotationPresetName;
        ImGui.SetNextItemWidth(240);
        if (ImGui.InputText("Autorotation Preset Name", ref autorotationPresetName, 128))
        {
            logger.InfoThrottled("setting-autorotation-preset-name", SettingTextLogInterval, $"Setting changed: AutorotationPresetName: '{configuration.AutorotationPresetName}' -> '{autorotationPresetName}'.");
            configuration.AutorotationPresetName = autorotationPresetName;
            configuration.Save();
        }

        var farmingMode = (int)configuration.FarmingMode;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Farming Mode", ref farmingMode, FarmingModeLabels, FarmingModeLabels.Length))
        {
            logger.Info($"Setting changed: FarmingMode: {configuration.FarmingMode} -> {(FarmingMode)farmingMode}.");
            configuration.FarmingMode = (FarmingMode)farmingMode;
            configuration.Save();
        }

        var prioritizeCe = configuration.PrioritizeCe;
        if (ImGui.Checkbox("Prioritize CE", ref prioritizeCe))
        {
            logger.Info($"Setting changed: PrioritizeCe: {configuration.PrioritizeCe} -> {prioritizeCe}.");
            configuration.PrioritizeCe = prioritizeCe;
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
    }

    private void DrawFatesTab()
    {
        var fatePriority = (int)configuration.FatePriority;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("FATE Priority", ref fatePriority, FatePriorityLabels, FatePriorityLabels.Length))
        {
            logger.Info($"Setting changed: FatePriority: {configuration.FatePriority} -> {(FatePriority)fatePriority}.");
            configuration.FatePriority = (FatePriority)fatePriority;
            configuration.Save();
        }

        var excludedFates = configuration.ExcludedFates;
        ImGui.SetNextItemWidth(360);
        if (ImGui.InputText("Excluded FATEs", ref excludedFates, 512))
        {
            logger.InfoThrottled("setting-excluded-fates", SettingTextLogInterval, $"Setting changed: ExcludedFates: '{configuration.ExcludedFates}' -> '{excludedFates}'.");
            configuration.ExcludedFates = excludedFates;
            configuration.Save();
        }
    }

    private static void DrawPotsTab()
    {
        ImGui.TextUnformatted("Pot farming settings will be added later.");
    }

    private static void DrawTreasureCoffersTab()
    {
        ImGui.TextUnformatted("Treasure coffer settings will be added later.");
    }

    private void DrawSettingsTab()
    {
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
}
