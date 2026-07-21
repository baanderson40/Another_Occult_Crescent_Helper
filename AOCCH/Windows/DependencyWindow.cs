using System;
using System.Numerics;
using AOCCH.Automation;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public sealed class DependencyWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public DependencyWindow(Plugin plugin) : base("AOCCH Dependencies###AOCCHDependencies")
    {
        this.plugin = plugin;
        Size = new Vector2(440, 180);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags |= ImGuiWindowFlags.AlwaysAutoResize;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.TextWrapped("Normal automation requires every dependency below to be available.");
        ImGui.Separator();

        foreach (var dependency in plugin.GetNormalAutomationDependencyReport().Statuses)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, dependency.IsUsable ? 0xFF55CC55 : 0xFF5555FF);
            ImGui.TextUnformatted($"{dependency.Name}: {(dependency.IsUsable ? "Available" : dependency.Detail)}");
            ImGui.PopStyleColor();
        }
    }
}
