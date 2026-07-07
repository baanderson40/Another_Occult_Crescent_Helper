using System;
using System.Linq;
using System.Numerics;
using AOCCH.Logging;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public sealed class LogWindow : Window, IDisposable
{
    private static readonly string[] FilterLabels = ["Info", "Debug", "Verbose"];

    private readonly Plugin plugin;
    private int selectedFilterIndex;
    private bool autoScroll = true;

    public LogWindow(Plugin plugin) : base("AOCCH Log###AOCCHLog")
    {
        Size = new Vector2(700, 400);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.SetNextItemWidth(140);
        ImGui.Combo("Level", ref selectedFilterIndex, FilterLabels, FilterLabels.Length);

        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref autoScroll);

        ImGui.SameLine();
        if (ImGui.Button("Copy Visible"))
        {
            ImGui.SetClipboardText(string.Join(Environment.NewLine, GetVisibleEntries().Select(entry => entry.Format())));
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            plugin.Logger.Clear();
        }

        ImGui.Separator();

        using var child = ImRaii.Child("LogScrollRegion", Vector2.Zero, true);
        if (!child.Success)
        {
            return;
        }

        foreach (var entry in GetVisibleEntries())
        {
            ImGui.TextUnformatted(entry.Format());
        }

        if (autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
        {
            ImGui.SetScrollHereY(1.0f);
        }
    }

    private AocchLogEntry[] GetVisibleEntries()
    {
        var filter = selectedFilterIndex switch
        {
            0 => AocchLogLevel.Info,
            1 => AocchLogLevel.Debug,
            _ => AocchLogLevel.Verbose,
        };

        return plugin.Logger.Entries.Where(entry => IsVisible(entry.Level, filter)).ToArray();
    }

    private static bool IsVisible(AocchLogLevel entryLevel, AocchLogLevel filter)
    {
        if (entryLevel is AocchLogLevel.Warning or AocchLogLevel.Error)
        {
            return true;
        }

        return filter switch
        {
            AocchLogLevel.Info => entryLevel == AocchLogLevel.Info,
            AocchLogLevel.Debug => entryLevel is AocchLogLevel.Info or AocchLogLevel.Debug,
            AocchLogLevel.Verbose => entryLevel is AocchLogLevel.Info or AocchLogLevel.Debug or AocchLogLevel.Verbose,
            _ => false,
        };
    }
}
