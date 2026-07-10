using System;
using System.Globalization;
using System.Numerics;
using AOCCH.Automation;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly AutorotationController autorotationController;
    private readonly BuffRotationController buffRotationController;
    private readonly CriticalEngagementAutomationController criticalEngagementAutomationController;
    private readonly FateAutomationController fateAutomationController;
    private readonly FarmSessionController farmSessionController;

    public MainWindow(
        Plugin plugin,
        Configuration configuration,
        OccultCrescentScanner scanner,
        MovementController movementController,
        AutorotationController autorotationController,
        BuffRotationController buffRotationController,
        CriticalEngagementAutomationController criticalEngagementAutomationController,
        FateAutomationController fateAutomationController,
        FarmSessionController farmSessionController)
        : base("Another Occult Crescent Helper##Main")
    {
        this.plugin = plugin;
        this.configuration = configuration;
        this.scanner = scanner;
        this.movementController = movementController;
        this.autorotationController = autorotationController;
        this.buffRotationController = buffRotationController;
        this.criticalEngagementAutomationController = criticalEngagementAutomationController;
        this.fateAutomationController = fateAutomationController;
        this.farmSessionController = farmSessionController;

        Size = new Vector2(460, 190);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 170),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        TitleBarButtons =
        [
            new TitleBarButton
            {
                Icon = FontAwesomeIcon.Cog,
                Click = _ => plugin.ToggleConfigUi(),
                ShowTooltip = () => ImGui.SetTooltip("Open Configuration"),
            }
            ,
            new TitleBarButton
            {
                Icon = FontAwesomeIcon.Scroll,
                Click = _ => plugin.ToggleLogUi(),
                ShowTooltip = () => ImGui.SetTooltip("Open Log"),
            }
        ];
    }

    public void Dispose() { }

    public override void Draw()
    {
        var snapshot = scanner.Snapshot;
        var potCycleSnapshot = plugin.PotCycleTracker.Snapshot;
        var treasureSnapshot = plugin.TreasureHintTracker.Snapshot;

        ImGui.TextWrapped($"Farm: {farmSessionController.State} | {farmSessionController.CurrentActivity}");
        ImGui.TextWrapped($"Activity: {GetActivityLabel(snapshot)}");
        ImGui.TextWrapped($"Pot: {GetPotSummary(snapshot, potCycleSnapshot)}");
        ImGui.TextWrapped($"Treasure: {GetTreasureSummary(treasureSnapshot)}");

        var farmStartBlocker = GetFarmStartBlocker();
        ImGui.BeginDisabled(farmStartBlocker != null);
        if (ImGui.Button("Start Farm"))
        {
            plugin.Logger.Info("Manual UI action: Start Farm.");
            farmSessionController.Start();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Stop Farm"))
        {
            plugin.Logger.Info("Manual UI action: Stop Farm.");
            farmSessionController.Stop("Manual farm session stop requested.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Panic Stop"))
        {
            plugin.Logger.Warning("Manual UI action: Panic Stop.");
            plugin.PanicStopAll();
        }

        if (farmStartBlocker != null)
        {
            ImGui.TextWrapped(farmStartBlocker);
        }
    }

    private string? GetFarmStartBlocker()
    {
        if (configuration.ScannerOnlyMode)
        {
            return "Scanner-only mode blocks farm session starts.";
        }

        if (farmSessionController.IsRunning)
        {
            return "Farm session is already running.";
        }

        if (criticalEngagementAutomationController.IsRunning || fateAutomationController.IsRunning || buffRotationController.IsRunning || plugin.PotFarmController.IsRunning)
        {
            return "Stop CE/FATE automation, pot control, and buff rotation before starting the farm session.";
        }

        if (!movementController.IsVNavmeshReady)
        {
            return "Farm session start requires vnavmesh IPC.";
        }

        if (!movementController.IsLifestreamAvailable)
        {
            return "Farm session start requires Lifestream IPC.";
        }

        if (configuration.UseReturn && !movementController.CanUseReturnAction)
        {
            return "Farm session start requires the Return general action when Use Return is enabled.";
        }

        if (autorotationController.ConfiguredPreset.Length > 0 && !autorotationController.RefreshBossModAvailability())
        {
            return "Farm session start requires BossMod IPC when an autorotation preset is configured.";
        }

        return null;
    }

    private static string GetActivityLabel(ScannerSnapshot snapshot)
    {
        switch (snapshot.EffectiveTarget.Kind)
        {
            case SelectedTargetKind.CriticalEncounter when snapshot.EffectiveTarget.CriticalEncounter != null:
                return FormattableString.Invariant($"CE {snapshot.EffectiveTarget.CriticalEncounter.Name} ({snapshot.EffectiveTarget.CriticalEncounter.Id})");
            case SelectedTargetKind.Fate when snapshot.EffectiveTarget.Fate != null:
                return FormattableString.Invariant($"FATE {snapshot.EffectiveTarget.Fate.Name} ({snapshot.EffectiveTarget.Fate.Id})");
        }

        if (snapshot.CurrentCriticalEncounter != null)
        {
            return FormattableString.Invariant($"CE {snapshot.CurrentCriticalEncounter.Name} ({snapshot.CurrentCriticalEncounter.Id})");
        }

        if (snapshot.IsInCriticalEncounter)
        {
            return FormattableString.Invariant($"CE {snapshot.CurrentCriticalEncounterId.ToString(CultureInfo.InvariantCulture)}");
        }

        return "None";
    }

    private static string GetPotSummary(ScannerSnapshot snapshot, PotCycleSnapshot potCycleSnapshot)
    {
        if (snapshot.ActivePotFate != null)
        {
            return $"Active {snapshot.ActivePotFate.Name} ({snapshot.ActivePotFate.Id})";
        }

        if (potCycleSnapshot.HasPredictedNextPot)
        {
            var timeUntil = potCycleSnapshot.PredictedNextSpawnAt - DateTimeOffset.UtcNow;
            var eta = timeUntil > TimeSpan.Zero
                ? $"in {timeUntil:mm\\:ss}"
                : $"{(-timeUntil):mm\\:ss} late";
            return $"Next {potCycleSnapshot.PredictedNextPotFateName} {eta}";
        }

        if (potCycleSnapshot.HasKnownAnchor)
        {
            return $"Anchor {potCycleSnapshot.LastObservedPotFateName} @ {potCycleSnapshot.LastObservedSpawnAt.ToLocalTime():HH:mm:ss}";
        }

        return "No anchor yet";
    }

    private static string GetTreasureSummary(TreasureHintSnapshot treasureSnapshot)
    {
        if (treasureSnapshot.SessionState == TreasureSessionState.Active)
        {
            return $"{treasureSnapshot.SessionState} | {treasureSnapshot.GetHintSummary()}";
        }

        return treasureSnapshot.SessionState == TreasureSessionState.Idle
            ? "Idle"
            : $"{treasureSnapshot.SessionState} | {FormatValue(treasureSnapshot.CompletionReason)}";
    }

    private static string FormatValue(string? value)
        => string.IsNullOrEmpty(value) ? "None" : value;
}
