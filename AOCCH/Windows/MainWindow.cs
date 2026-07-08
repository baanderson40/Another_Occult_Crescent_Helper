using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using AOCCH.Scanning;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly OccultCrescentScanner scanner;

    // We give this window a hidden ID using ##.
    // The user will see "Another Occult Crescent Helper" as window title,
    // but for ImGui the ID is "Another Occult Crescent Helper##Main".
    public MainWindow(Configuration configuration, OccultCrescentScanner scanner)
        : base("Another Occult Crescent Helper##Main")
    {
        this.configuration = configuration;
        this.scanner = scanner;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(540, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var snapshot = scanner.Snapshot;

        ImGui.TextUnformatted($"Selected Mode: {FormatFarmingMode(configuration.FarmingMode)}");
        ImGui.TextUnformatted($"Territory: {snapshot.TerritoryTypeId}");
        ImGui.TextUnformatted($"In South Horn: {(snapshot.IsInSouthHorn ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Last Scan: {FormatTimestamp(snapshot.LastUpdated)}");

        ImGui.Separator();
        DrawCriticalEncounters(snapshot);

        ImGui.Separator();
        DrawFates(snapshot);
    }

    private static string FormatFarmingMode(FarmingMode farmingMode)
        => farmingMode switch
        {
            FarmingMode.CeAndFate => "CE & FATE",
            FarmingMode.CeOnly => "CE Only",
            FarmingMode.FateOnly => "FATE Only",
            _ => farmingMode.ToString(),
        };

    private static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp == DateTimeOffset.MinValue
            ? "Waiting for first scan"
            : timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private void DrawCriticalEncounters(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("Critical Engagements");

        if (!snapshot.IsInSouthHorn)
        {
            ImGui.TextUnformatted("Not in South Horn.");
            return;
        }

        if (snapshot.CriticalEncounters.Count == 0)
        {
            ImGui.TextUnformatted("No active Critical Engagements detected.");
        }
        else
        {
            foreach (var encounter in snapshot.CriticalEncounters)
            {
                var details = $"[{encounter.State}] {encounter.Progress}%";
                var eta = FormatCeTime(encounter.StartTimestamp);
                if (!string.IsNullOrEmpty(eta))
                {
                    details = $"{details} | {eta}";
                }

                if (!string.IsNullOrEmpty(encounter.PreferredAethernet))
                {
                    details = $"{details} | Aethernet: {encounter.PreferredAethernet}";
                }

                ImGui.TextWrapped($"- {encounter.Name} ({encounter.Id})");
                ImGui.TextWrapped($"  {details}");
            }
        }

        if (snapshot.UnknownCriticalEncounters.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Unknown Dynamic Events");
            foreach (var encounter in snapshot.UnknownCriticalEncounters)
            {
                var details = $"[{encounter.State}] {encounter.Progress}%";
                var eta = FormatCeTime(encounter.StartTimestamp);
                if (!string.IsNullOrEmpty(eta))
                {
                    details = $"{details} | {eta}";
                }

                ImGui.TextWrapped($"- {encounter.Name} ({encounter.Id})");
                ImGui.TextWrapped($"  {details}");
            }
        }
    }

    private void DrawFates(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("FATEs");

        if (!snapshot.IsInSouthHorn)
        {
            ImGui.TextUnformatted("Not in South Horn.");
            return;
        }

        if (snapshot.Fates.Count == 0)
        {
            ImGui.TextUnformatted("No active FATEs detected.");
            return;
        }

        foreach (var fate in snapshot.Fates)
        {
            var metadata = new[]
            {
                string.IsNullOrEmpty(fate.Demiatma) ? null : $"Demiatma: {fate.Demiatma}",
                string.IsNullOrEmpty(fate.Note) ? null : $"Note: {fate.Note}",
                string.IsNullOrEmpty(fate.PreferredAethernet) ? null : $"Aethernet: {fate.PreferredAethernet}",
            }.Where(value => !string.IsNullOrEmpty(value)).Cast<string>();

            ImGui.TextWrapped($"- {fate.Name} ({fate.Id})");
            ImGui.TextWrapped($"  {fate.Progress}% | Radius: {fate.Radius:0.0} | Pos: {FormatVector3(fate.Position)}");

            var metadataText = string.Join(" | ", metadata);
            if (!string.IsNullOrEmpty(metadataText))
            {
                ImGui.TextWrapped($"  {metadataText}");
            }
        }
    }

    private static string FormatCeTime(long startTimestamp)
    {
        if (startTimestamp <= 0)
        {
            return string.Empty;
        }

        var timeUntilStart = DateTimeOffset.FromUnixTimeSeconds(startTimestamp) - DateTimeOffset.UtcNow;
        if (timeUntilStart.TotalSeconds > 0)
        {
            return $"Starts in {timeUntilStart:mm\\:ss}";
        }

        return $"Started {(-timeUntilStart):mm\\:ss} ago";
    }

    private static string FormatVector3(Vector3 position)
        => $"{position.X:0.0}, {position.Y:0.0}, {position.Z:0.0}";
}
