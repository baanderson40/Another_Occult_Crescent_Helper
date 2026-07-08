using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public class ConfigWindow : Window, IDisposable
{
    private static readonly string[] FarmingModeLabels = ["CE & FATE", "CE Only", "FATE Only"];
    private static readonly string[] FatePriorityLabels = ["Lowest Progress", "Nearest"];

    private readonly Configuration configuration;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Configuration configuration) : base("AOCCH Configuration###AOCCHConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(620, 360);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.configuration = configuration;
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
            configuration.AutorotationPresetName = autorotationPresetName;
            configuration.Save();
        }

        var farmingMode = (int)configuration.FarmingMode;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Farming Mode", ref farmingMode, FarmingModeLabels, FarmingModeLabels.Length))
        {
            configuration.FarmingMode = (FarmingMode)farmingMode;
            configuration.Save();
        }

        var prioritizeCe = configuration.PrioritizeCe;
        if (ImGui.Checkbox("Prioritize CE", ref prioritizeCe))
        {
            configuration.PrioritizeCe = prioritizeCe;
            configuration.Save();
        }

        var useReturn = configuration.UseReturn;
        if (ImGui.Checkbox("Use Return", ref useReturn))
        {
            configuration.UseReturn = useReturn;
            configuration.Save();
        }

        var enableBuffRotation = configuration.EnableBuffRotation;
        if (ImGui.Checkbox("Enable Buff Rotation", ref enableBuffRotation))
        {
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
            configuration.FatePriority = (FatePriority)fatePriority;
            configuration.Save();
        }

        var excludedFates = configuration.ExcludedFates;
        ImGui.SetNextItemWidth(360);
        if (ImGui.InputText("Excluded FATEs", ref excludedFates, 512))
        {
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

    private static void DrawSettingsTab()
    {
        ImGui.TextUnformatted("General plugin settings will be added later.");
    }
}
