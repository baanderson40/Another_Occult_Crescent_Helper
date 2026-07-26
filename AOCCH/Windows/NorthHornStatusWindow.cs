using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public sealed class NorthHornStatusWindow : Window, IDisposable
{
    public const int CurrentStatusRevision = 1;

    private const int PotRevealCofferProgress = 0;
    private const int PotRevealCofferTotal = 30;
    private const int OverworldCofferProgress = 0;
    private const int OverworldCofferTotal = 71;

    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private bool isDebugPreview;

    public NorthHornStatusWindow(Plugin plugin, Configuration configuration)
        : base("AOCCH: North Horn Status###AOCCHNorthHornStatus")
    {
        this.plugin = plugin;
        this.configuration = configuration;
        Size = new Vector2(520, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags |= ImGuiWindowFlags.AlwaysAutoResize;
    }

    public bool IsDebugPreview => isDebugPreview;

    public bool ShouldOpenAutomatically
        => configuration.NorthHornStatusDismissedRevision != CurrentStatusRevision;

    public void Open(bool debugPreview)
    {
        isDebugPreview = debugPreview;
        IsOpen = true;
    }

    public void Close()
    {
        isDebugPreview = false;
        IsOpen = false;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (isDebugPreview)
        {
            ImGui.TextDisabled("Debug preview: North Horn");
            ImGui.Separator();
        }

        ImGui.TextWrapped("North Horn contains new content. AOCCH support is being added in priority order.");
        ImGui.Spacing();

        if (ImGui.BeginTable("NorthHornStatusTable", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Feature");
            ImGui.TableSetupColumn("Support", ImGuiTableColumnFlags.WidthFixed, 105f);
            ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthFixed, 105f);
            ImGui.TableHeadersRow();

            DrawStatusRow("Critical Engagements", "Unsupported", "In progress");
            DrawStatusRow("FATE Farming", "Unsupported", "In progress");
            DrawStatusRow("Shopping", "Unsupported", "Planned");
            DrawStatusRow("Pot / Reveal Coffers", "Unsupported", $"{PotRevealCofferProgress}/{PotRevealCofferTotal}");
            DrawStatusRow("Overworld Coffers", "Unsupported", $"{OverworldCofferProgress}/{OverworldCofferTotal}");
            DrawStatusRow("Overworld Treasure Guide", "Supported", "Available");
            DrawStatusRow("Coffer Position Reporting", "Supported", "Available");

            ImGui.EndTable();
        }

        ImGui.Separator();
        var enableTreasureGuide = configuration.EnableOverworldTreasureGuide;
        if (ImGui.Checkbox("Enable Overworld Treasure Guide", ref enableTreasureGuide))
        {
            plugin.Logger.Info($"[NorthHornStatus] op=setting-change key=EnableOverworldTreasureGuide old={configuration.EnableOverworldTreasureGuide} new={enableTreasureGuide}");
            configuration.EnableOverworldTreasureGuide = enableTreasureGuide;
            configuration.Save();
        }
        ImGui.SameLine();
        DrawTooltipMarker("Draws a world-space line and marker to the nearest detected overworld treasure. It provides guidance only and does not start or control coffer automation.");

        var enableCofferReporting = configuration.EnableCofferObservationSubmission;
        if (ImGui.Checkbox("Enable anonymous coffer position reporting", ref enableCofferReporting))
        {
            plugin.Logger.Info($"[NorthHornStatus] op=setting-change key=EnableCofferObservationSubmission old={configuration.EnableCofferObservationSubmission} new={enableCofferReporting}");
            configuration.EnableCofferObservationSubmission = enableCofferReporting;
            configuration.Save();
        }
        ImGui.SameLine();
        DrawTooltipMarker("When enabled, AOCCH reports confirmed coffer observations to help build shared coffer position data for future North Horn support. Reports include the zone, coffer type, position, plugin version, and timestamp. No character name, account name, chat, inventory, or other personal information is transmitted. Reports are queued locally and sent when possible. You can disable reporting at any time.");

        ImGui.Separator();
        var doNotShowAgain = configuration.NorthHornStatusDismissedRevision == CurrentStatusRevision;
        if (ImGui.Checkbox("Do not show this update again", ref doNotShowAgain))
        {
            configuration.NorthHornStatusDismissedRevision = doNotShowAgain ? CurrentStatusRevision : 0;
            configuration.Save();
            plugin.Logger.Info($"[NorthHornStatus] op=dismissal-change revision={CurrentStatusRevision} dismissed={doNotShowAgain}");
        }
    }

    private static void DrawStatusRow(string feature, string support, string progress)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(feature);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(support);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(progress);
    }

    private static void DrawTooltipMarker(string text)
    {
        ImGui.TextDisabled("(?)");
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.SetTooltip(text);
    }
}
