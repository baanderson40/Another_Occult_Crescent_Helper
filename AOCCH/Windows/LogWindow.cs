using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AOCCH.Logging;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public sealed class LogWindow : Window, IDisposable
{
    private static readonly string[] FilterLabels = ["Info", "Debug", "Verbose"];

    private readonly Plugin plugin;
    private readonly HashSet<int> selectedEntryIndexes = [];
    private int selectedFilterIndex;
    private int copyStartIndex = -1;
    private bool autoScroll = true;
    private bool copyMode;

    public LogWindow(Plugin plugin) : base("AOCCH Log###AOCCHLog")
    {
        Size = new Vector2(700, 400);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var visibleEntries = GetVisibleEntries();
        selectedEntryIndexes.RemoveWhere(index => index >= visibleEntries.Length);
        if (copyStartIndex >= visibleEntries.Length)
        {
            copyStartIndex = -1;
        }

        ImGui.SetNextItemWidth(140);
        ImGui.Combo("Level", ref selectedFilterIndex, FilterLabels, FilterLabels.Length);

        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref autoScroll);

        ImGui.SameLine();
        if (copyMode)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, 0x4000AA00);
        }

        if (ImGui.Button("Copy Mode"))
        {
            copyMode = !copyMode;
            if (!copyMode)
            {
                ClearSelection();
            }
        }

        if (copyMode)
        {
            ImGui.PopStyleColor();
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            CopyEntries(visibleEntries.Select(entry => entry.Format()));
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            plugin.Logger.Clear();
            ClearSelection();
        }

        ImGui.Separator();

        if (!ImGui.BeginChild("LogScrollRegion", Vector2.Zero, true))
        {
            ImGui.EndChild();
            return;
        }

        var shouldScrollToBottom = autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY();

        for (var i = 0; i < visibleEntries.Length; i++)
        {
            var isSelected = selectedEntryIndexes.Contains(i);

            var rowColor = isSelected ? 0x80404040u : GetRowColor(visibleEntries[i].Level);
            ImGui.PushStyleColor(ImGuiCol.Header, rowColor);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, rowColor);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, rowColor);

            ImGui.Selectable($"##LogEntry{i}", true, ImGuiSelectableFlags.AllowItemOverlap | ImGuiSelectableFlags.SpanAllColumns);
            HandleCopyMode(i, visibleEntries);
            ImGui.PopStyleColor(3);

            ImGui.SameLine();
            ImGui.TextUnformatted(visibleEntries[i].Format());
        }

        if (shouldScrollToBottom)
        {
            ImGui.SetScrollHereY(1.0f);
        }

        ImGui.EndChild();
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

    private static uint GetRowColor(AocchLogLevel level) => level switch
    {
        AocchLogLevel.Warning => 0x8A0070EE,
        AocchLogLevel.Error => 0x800000EE,
        _ => 0x00000000,
    };

    private void HandleCopyMode(int index, IReadOnlyList<AocchLogEntry> visibleEntries)
    {
        var selectionChanged = false;

        if (copyMode && copyStartIndex == -1 && ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            copyStartIndex = index;
            selectedEntryIndexes.Clear();
            selectedEntryIndexes.Add(index);

            selectionChanged = true;
        }

        if (copyMode && copyStartIndex != -1 && ImGui.IsItemHovered() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            UpdateSelectionRange(index, visibleEntries.Count);
            selectionChanged = true;
        }

        if (copyMode && copyStartIndex != -1 && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            copyStartIndex = -1;
        }

        if (selectionChanged)
        {
            CopyEntries(selectedEntryIndexes.OrderBy(selectedIndex => selectedIndex).Select(selectedIndex => visibleEntries[selectedIndex].Format()));
        }
    }

    private void UpdateSelectionRange(int currentIndex, int entryCount)
    {
        selectedEntryIndexes.Clear();

        var start = Math.Min(copyStartIndex, currentIndex);
        var end = Math.Max(copyStartIndex, currentIndex);

        for (var i = 0; i < entryCount; i++)
        {
            if (i >= start && i <= end)
            {
                selectedEntryIndexes.Add(i);
            }
        }
    }

    private void ClearSelection()
    {
        selectedEntryIndexes.Clear();
        copyStartIndex = -1;
    }

    private static void CopyEntries(IEnumerable<string> entries)
    {
        var copiedText = string.Join(Environment.NewLine, entries);
        if (copiedText.Length > 0)
        {
            ImGui.SetClipboardText(copiedText);
        }
    }
}
