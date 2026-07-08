using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using AOCCH.Automation;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly AutorotationController autorotationController;
    private readonly CriticalEngagementAutomationController criticalEngagementAutomationController;

    // We give this window a hidden ID using ##.
    // The user will see "Another Occult Crescent Helper" as window title,
    // but for ImGui the ID is "Another Occult Crescent Helper##Main".
    public MainWindow(
        Configuration configuration,
        OccultCrescentScanner scanner,
        MovementController movementController,
        AutorotationController autorotationController,
        CriticalEngagementAutomationController criticalEngagementAutomationController)
        : base("Another Occult Crescent Helper##Main")
    {
        this.configuration = configuration;
        this.scanner = scanner;
        this.movementController = movementController;
        this.autorotationController = autorotationController;
        this.criticalEngagementAutomationController = criticalEngagementAutomationController;

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
        DrawSelectedTarget(snapshot);

        ImGui.Separator();
        DrawCriticalEngagementAutomation(snapshot);

        ImGui.Separator();
        DrawAutorotation();

        ImGui.Separator();
        DrawMovement(snapshot);

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
                var targetLabel = GetCeTargetLabel(snapshot, encounter);
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

                if (encounter.IsCandidate)
                {
                    details = $"{details} | Priority: {encounter.Priority}";
                }

                ImGui.TextWrapped($"- {targetLabel}{encounter.Name} ({encounter.Id})");
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
            var targetLabel = GetFateTargetLabel(snapshot, fate);
            var metadata = new[]
            {
                string.IsNullOrEmpty(fate.Demiatma) ? null : $"Demiatma: {fate.Demiatma}",
                string.IsNullOrEmpty(fate.Note) ? null : $"Note: {fate.Note}",
                string.IsNullOrEmpty(fate.PreferredAethernet) ? null : $"Aethernet: {fate.PreferredAethernet}",
                fate.IsExcluded ? "Excluded" : null,
            }.Where(value => !string.IsNullOrEmpty(value)).Cast<string>();

            ImGui.TextWrapped($"- {targetLabel}{fate.Name} ({fate.Id})");
            ImGui.TextWrapped($"  [{fate.State}] {fate.Progress}% | Distance: {FormatDistance(fate.DistanceToPlayer)} | Radius: {fate.Radius:0.0} | Pos: {FormatVector3(fate.Position)}");

            var metadataText = string.Join(" | ", metadata);
            if (!string.IsNullOrEmpty(metadataText))
            {
                ImGui.TextWrapped($"  {metadataText}");
            }
        }
    }

    private static void DrawSelectedTarget(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("Selected Target");

        if (!snapshot.IsInSouthHorn)
        {
            ImGui.TextUnformatted("No target selection outside South Horn.");
            return;
        }

        if (snapshot.SelectedCriticalEncounter != null)
        {
            ImGui.TextWrapped($"Best CE: {snapshot.SelectedCriticalEncounter.Name} ({snapshot.SelectedCriticalEncounter.Id}) | Priority: {snapshot.SelectedCriticalEncounter.Priority} | State: {snapshot.SelectedCriticalEncounter.State}");
        }
        else
        {
            ImGui.TextUnformatted("Best CE: None");
        }

        if (snapshot.SelectedFate != null)
        {
            ImGui.TextWrapped($"Best FATE: {snapshot.SelectedFate.Name} ({snapshot.SelectedFate.Id}) | Progress: {snapshot.SelectedFate.Progress}% | Distance: {FormatDistance(snapshot.SelectedFate.DistanceToPlayer)}");
        }
        else
        {
            ImGui.TextUnformatted("Best FATE: None");
        }

        var target = snapshot.EffectiveTarget;
        if (target.Kind == SelectedTargetKind.None)
        {
            if (snapshot.CurrentCriticalEncounter != null)
            {
                ImGui.TextWrapped($"Current CE: {snapshot.CurrentCriticalEncounter.Name} ({snapshot.CurrentCriticalEncounter.Id}) | State: {snapshot.CurrentCriticalEncounter.State}");
            }
            else if (snapshot.IsInCriticalEncounter)
            {
                ImGui.TextWrapped($"Current CE ID: {snapshot.CurrentCriticalEncounterId}");
            }

            ImGui.TextUnformatted("Effective Target: None");
            return;
        }

        switch (target.Kind)
        {
            case SelectedTargetKind.CriticalEncounter when target.CriticalEncounter != null:
                ImGui.TextWrapped($"Effective Target: CE {target.CriticalEncounter.Name} ({target.CriticalEncounter.Id}) | Reason: {target.Reason}");
                break;
            case SelectedTargetKind.Fate when target.Fate != null:
                ImGui.TextWrapped($"Effective Target: FATE {target.Fate.Name} ({target.Fate.Id}) | Reason: {target.Reason}");
                break;
        }

        if (target.WouldPreemptFate)
        {
            ImGui.TextUnformatted("CE would preempt the current FATE target.");
        }

        if (snapshot.CurrentCriticalEncounter != null)
        {
            ImGui.TextWrapped($"Current CE: {snapshot.CurrentCriticalEncounter.Name} ({snapshot.CurrentCriticalEncounter.Id}) | State: {snapshot.CurrentCriticalEncounter.State}");
        }
        else if (snapshot.IsInCriticalEncounter)
        {
            ImGui.TextWrapped($"Current CE ID: {snapshot.CurrentCriticalEncounterId}");
        }
    }

    private void DrawCriticalEngagementAutomation(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("Critical Engagement Automation");
        ImGui.TextUnformatted($"State: {criticalEngagementAutomationController.State}");

        if (criticalEngagementAutomationController.TargetCeId != 0)
        {
            ImGui.TextWrapped($"Locked Target: {criticalEngagementAutomationController.TargetCeName} ({criticalEngagementAutomationController.TargetCeId})");
        }
        else
        {
            ImGui.TextUnformatted("Locked Target: None");
        }

        ImGui.TextWrapped($"Last Transition: {criticalEngagementAutomationController.LastTransition}");
        if (!string.IsNullOrEmpty(criticalEngagementAutomationController.LastError))
        {
            ImGui.TextWrapped($"Last Error: {criticalEngagementAutomationController.LastError}");
        }

        var canStart = snapshot.IsInSouthHorn && snapshot.EffectiveTarget.Kind == SelectedTargetKind.CriticalEncounter;
        if (ImGui.Button("Start CE Automation") && canStart)
        {
            criticalEngagementAutomationController.Start();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop CE Automation"))
        {
            criticalEngagementAutomationController.Stop("Manual CE automation stop requested.");
        }

        if (!canStart)
        {
            ImGui.TextUnformatted("Start CE Automation requires South Horn and a CE effective target.");
        }
    }

    private void DrawAutorotation()
    {
        ImGui.TextUnformatted("Autorotation");
        ImGui.TextUnformatted($"BossMod: {(autorotationController.BossModAvailable ? "Available" : "Unavailable")}");
        ImGui.TextWrapped($"Configured Preset: {FormatPreset(autorotationController.ConfiguredPreset)}");
        ImGui.TextWrapped($"Active Preset: {FormatPreset(autorotationController.LastKnownActivePreset)}");
        ImGui.TextWrapped($"Owned Preset: {FormatPreset(autorotationController.OwnedPreset)}");
        ImGui.TextUnformatted($"Owns Active Preset: {(autorotationController.HasOwnership ? "Yes" : "No")}");

        if (!string.IsNullOrEmpty(autorotationController.InitialPreset))
        {
            ImGui.TextWrapped($"Captured Initial Preset: {autorotationController.InitialPreset}");
        }

        ImGui.TextWrapped($"Status: {autorotationController.LastStatus}");
        if (!string.IsNullOrEmpty(autorotationController.LastError))
        {
            ImGui.TextWrapped($"Last Error: {autorotationController.LastError}");
        }
    }

    private void DrawMovement(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("Movement");
        ImGui.TextUnformatted($"vnavmesh: {movementController.VNavmeshStatusText}");
        ImGui.TextUnformatted($"Lifestream: {movementController.LifestreamStatusText}");
        ImGui.TextUnformatted($"State: {movementController.State}");
        ImGui.TextWrapped($"Route: {movementController.GetStatusSummary()}");
        ImGui.TextWrapped($"Step: {movementController.GetActiveStepSummary()}");
        ImGui.TextUnformatted($"Distance Remaining: {FormatDistance(movementController.DistanceRemaining)}");
        ImGui.TextUnformatted($"Elapsed: {movementController.GetElapsedSummary()}");

        if (!string.IsNullOrEmpty(movementController.LastError))
        {
            ImGui.TextWrapped($"Last Error: {movementController.LastError}");
        }

        var hasSelectedTarget = snapshot.EffectiveTarget.Kind != SelectedTargetKind.None;
        if (ImGui.Button("Plan Route") && hasSelectedTarget)
        {
            movementController.PlanRouteToSelectedTarget();
        }

        ImGui.SameLine();
        if (ImGui.Button("Start Route"))
        {
            movementController.StartPlannedRoute();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop Movement"))
        {
            movementController.Stop("Manual stop requested.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Recover To Base Camp"))
        {
            movementController.RecoverToBaseCamp();
        }

        if (!hasSelectedTarget)
        {
            ImGui.TextUnformatted("Plan Route requires a selected CE or FATE target.");
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

    private static string FormatDistance(float distance)
        => float.IsFinite(distance) ? $"{distance:0.0}" : "Unknown";

    private static string FormatPreset(string preset)
        => string.IsNullOrEmpty(preset) ? "None" : preset;

    private static string GetCeTargetLabel(ScannerSnapshot snapshot, ActiveCriticalEncounter encounter)
    {
        if (snapshot.EffectiveTarget.Kind == SelectedTargetKind.CriticalEncounter
            && snapshot.EffectiveTarget.CriticalEncounter?.Id == encounter.Id)
        {
            return "[Target] ";
        }

        if (snapshot.SelectedCriticalEncounter?.Id == encounter.Id)
        {
            return "[Candidate] ";
        }

        return string.Empty;
    }

    private static string GetFateTargetLabel(ScannerSnapshot snapshot, ActiveFate fate)
    {
        if (snapshot.EffectiveTarget.Kind == SelectedTargetKind.Fate
            && snapshot.EffectiveTarget.Fate?.Id == fate.Id)
        {
            return "[Target] ";
        }

        if (snapshot.SelectedFate?.Id == fate.Id)
        {
            return "[Candidate] ";
        }

        return string.Empty;
    }
}
