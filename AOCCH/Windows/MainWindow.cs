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
    private readonly BuffRotationController buffRotationController;
    private readonly CriticalEngagementAutomationController criticalEngagementAutomationController;
    private readonly FateAutomationController fateAutomationController;
    private readonly FarmSessionController farmSessionController;
    private readonly TreasureCofferFarmController treasureCofferFarmController;

    public MainWindow(
        Plugin plugin,
        Configuration configuration,
        OccultCrescentScanner scanner,
        MovementController movementController,
        BuffRotationController buffRotationController,
        CriticalEngagementAutomationController criticalEngagementAutomationController,
        FateAutomationController fateAutomationController,
        FarmSessionController farmSessionController,
        TreasureCofferFarmController treasureCofferFarmController)
        : base("Another Occult Crescent Helper##Main")
    {
        this.plugin = plugin;
        this.configuration = configuration;
        this.scanner = scanner;
        this.movementController = movementController;
        this.buffRotationController = buffRotationController;
        this.criticalEngagementAutomationController = criticalEngagementAutomationController;
        this.fateAutomationController = fateAutomationController;
        this.farmSessionController = farmSessionController;
        this.treasureCofferFarmController = treasureCofferFarmController;

        Size = new Vector2(460, 215);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 135),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        TitleBarButtons = [];
    }

    public void Dispose() { }

    public override void Draw()
    {
        var snapshot = scanner.Snapshot;
        var potCycleSnapshot = plugin.PotCycleTracker.Snapshot;
        var treasureSnapshot = plugin.TreasureHintTracker.Snapshot;
        var cofferSurveySnapshot = plugin.TreasureHintTracker.CofferSurveySnapshot;
        var automaticCofferStatus = farmSessionController.AutomaticTreasureCofferStatus;
        var instanceTimeDecision = plugin.PotFarmController.LastInstanceTimeDecision;
        var statusTextScale = configuration.MainWindowStatusTextScalePercent / 100f;

        ImGui.SetWindowFontScale(statusTextScale);
        ImGui.TextWrapped($"Farm: {GetFarmSummary(snapshot, potCycleSnapshot, treasureSnapshot, cofferSurveySnapshot, automaticCofferStatus)}");
        var potSummary = GetPotSummary(snapshot, potCycleSnapshot);
        if (potSummary != null)
        {
            ImGui.TextWrapped($"Pot: {potSummary}");
        }

        if (plugin.PotFarmController.IsLeavePending || (instanceTimeDecision.ManageInstanceTimeEnabled && instanceTimeDecision.IsContentTimerAvailable && !instanceTimeDecision.AllowNextPotCycle))
        {
            ImGui.TextWrapped($"Instance: {FormatValue(instanceTimeDecision.Reason)}");
        }
        ImGui.SetWindowFontScale(1f);

        var farmStartBlocker = GetFarmStartBlocker();
        var farmDependencyBlocked = !plugin.GetNormalAutomationDependencyReport().IsReady;
        if (farmSessionController.IsRunning)
        {
            if (DrawIconButton(FontAwesomeIcon.Stop, "farm-toggle", "Stop Farm"))
            {
                plugin.Logger.Info("[MainWindow] op=ui-action action=stop-farm");
                farmSessionController.Stop("Manual farm session stop requested.");
            }
        }
        else if (DrawIconButton(FontAwesomeIcon.Play, "farm-toggle", "Start Farm", farmStartBlocker == null, farmStartBlocker, disabledClick: farmDependencyBlocked ? plugin.OpenDependencyUi : null))
        {
            plugin.Logger.Info("[MainWindow] op=ui-action action=start-farm");
            farmSessionController.Start();
        }

        if (!configuration.EnableAutomaticTreasureCofferRoute)
        {
            var cofferStartBlocker = GetVisibleCofferStartBlocker();
            var cofferDependencyBlocked = !plugin.GetNormalAutomationDependencyReport().IsReady;
            ImGui.SameLine();
            if (treasureCofferFarmController.IsRunning)
            {
                if (DrawIconButton(FontAwesomeIcon.Stop, "coffer-toggle", "Stop Coffer Route"))
                {
                    plugin.Logger.Info("[MainWindow] op=ui-action action=stop-coffer-route");
                    treasureCofferFarmController.Stop("Manual overworld coffer route stop requested.");
                }
            }
            else if (DrawIconButton(FontAwesomeIcon.StepForward, "coffer-toggle", "Start Coffer Route", cofferStartBlocker == null, cofferStartBlocker, disabledClick: cofferDependencyBlocked ? plugin.OpenDependencyUi : null))
            {
                plugin.Logger.Info("[MainWindow] op=ui-action action=start-coffer-route");
                treasureCofferFarmController.Start();
            }
        }

        ImGui.SameLine();
        var panicStopBlocker = GetPanicStopBlocker();
        if (DrawIconButton(FontAwesomeIcon.Skull, "panic-stop", "Panic Stop", panicStopBlocker == null, panicStopBlocker))
        {
            PanicStop();
        }

        ImGui.SameLine();
        if (DrawIconButton(FontAwesomeIcon.Store, "toggle-shopping", "Open Shopping", iconOffset: new Vector2(0f, -0.5f)))
        {
            plugin.Logger.Info("[MainWindow] op=ui-action action=open-shopping");
            plugin.OpenShoppingUi();
        }

        ImGui.SameLine();
        if (DrawIconButton(FontAwesomeIcon.Scroll, "toggle-log", "Open Log"))
        {
            plugin.ToggleLogUi();
        }

        ImGui.SameLine();
        if (DrawIconButton(FontAwesomeIcon.Cog, "toggle-config", "Open Configuration"))
        {
            plugin.ToggleConfigUi();
        }
    }

    private void PanicStop()
    {
        plugin.Logger.Warning("[MainWindow] op=ui-action action=panic-stop");
        plugin.PanicStopAll();
    }

    private string? GetPanicStopBlocker()
        => scanner.Snapshot.IsInSupportedTerritory ? null : "Panic Stop requires a supported territory.";

    private static bool DrawIconButton(FontAwesomeIcon icon, string id, string tooltip, bool enabled = true, string? disabledTooltip = null, Vector2? iconOffset = null, Action? disabledClick = null)
    {
        const float IconScale = 0.85f;
        var buttonSize = new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight());
        var clicked = false;
        if (!enabled && disabledClick == null)
        {
            ImGui.BeginDisabled();
        }

        if (!enabled && disabledClick != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.FrameBg));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(ImGuiCol.FrameBg));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGui.GetColorU32(ImGuiCol.FrameBg));
        }

        if (ImGui.Button($"##{id}", buttonSize))
        {
            clicked = true;
        }

        if (!enabled && disabledClick == null)
        {
            ImGui.EndDisabled();
        }

        if (!enabled && disabledClick != null)
        {
            ImGui.PopStyleColor(4);
        }

        if (!enabled && disabledClick != null && clicked)
        {
            disabledClick();
        }

        var rectMin = ImGui.GetItemRectMin();
        var rectMax = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var iconColor = ImGui.GetColorU32(enabled ? ImGuiCol.Text : ImGuiCol.TextDisabled);
        var iconText = icon.ToIconString();
        var iconFontSize = UiBuilder.IconFont.FontSize * IconScale;

        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconText) * IconScale;
        ImGui.PopFont();

        var iconPosition = new Vector2(
            rectMin.X + ((rectMax.X - rectMin.X) - iconSize.X) * 0.5f,
            rectMin.Y + ((rectMax.Y - rectMin.Y) - iconSize.Y) * 0.5f);
        if (iconOffset.HasValue)
        {
            iconPosition += iconOffset.Value;
        }

        iconPosition = new Vector2(MathF.Round(iconPosition.X), MathF.Round(iconPosition.Y));
        drawList.AddText(UiBuilder.IconFont, iconFontSize, iconPosition, iconColor, iconText);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(enabled || string.IsNullOrEmpty(disabledTooltip) ? tooltip : disabledTooltip);
        }

        return enabled && clicked;
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

        if (!scanner.Snapshot.IsInSupportedTerritory)
        {
            return "Farm session requires a supported Occult Crescent territory.";
        }

        var snapshot = scanner.Snapshot;
        if (!CanRunAnyAutomation(snapshot))
        {
            return $"No automation features are available in {snapshot.TerritoryDisplayName}.";
        }

        var dependencyReport = plugin.GetNormalAutomationDependencyReport();
        if (!dependencyReport.IsReady)
        {
            return dependencyReport.FailureSummary;
        }

        if (criticalEngagementAutomationController.IsRunning || fateAutomationController.IsRunning || buffRotationController.IsRunning || plugin.PotFarmController.IsRunning || treasureCofferFarmController.IsRunning)
        {
            return "Stop CE/FATE automation, pot control, buff rotation, and overworld coffer routing before starting the farm session.";
        }

        if (configuration.UseReturn && !movementController.CanUseReturnAction)
        {
            return "Farm session start requires the Return general action when Use Return is enabled.";
        }

        return null;
    }

    private string GetFarmSummary(
        ScannerSnapshot snapshot,
        PotCycleSnapshot potCycleSnapshot,
        TreasureHintSnapshot treasureSnapshot,
        TreasureCofferSurveySnapshot cofferSurveySnapshot,
        TreasureCofferAutomaticModeStatus automaticCofferStatus)
    {
        if (treasureCofferFarmController.IsRunning)
        {
            return $"Overworld coffer route | {GetVisibleCofferSummary()}";
        }

        var treasureSearchSummary = GetTreasureSearchSummary(treasureSnapshot);
        if (treasureSearchSummary != null)
        {
            return treasureSearchSummary;
        }

        var baseActivity = GetFriendlyFarmActivity();
        if (IsPotFlowActivity(baseActivity))
        {
            return baseActivity;
        }

        var pendingPotDepartureSummary = GetPendingPotDepartureSummary(baseActivity, potCycleSnapshot);
        if (pendingPotDepartureSummary != null)
        {
            return pendingPotDepartureSummary;
        }

        var targetSummary = GetActivityTargetSummary(snapshot);
        if (targetSummary != null)
        {
            return targetSummary;
        }

        var idleAutoCofferSummary = GetIdleAutoCofferSummary(cofferSurveySnapshot, automaticCofferStatus);
        if (idleAutoCofferSummary != null)
        {
            return idleAutoCofferSummary;
        }

        var fallbackDetail = GetFarmFallbackDetail();
        return fallbackDetail == null
            ? baseActivity
            : $"{baseActivity} | {fallbackDetail}";
    }

    private string? GetActivityTargetSummary(ScannerSnapshot snapshot)
    {
        var activity = GetFriendlyFarmActivity();
        var cePrefix = activity == "Running CE" ? "Running CE" : $"{activity} | CE";
        var fatePrefix = activity == "Running FATE" ? "Running FATE" : $"{activity} | FATE";

        switch (snapshot.EffectiveTarget.Kind)
        {
            case SelectedTargetKind.CriticalEncounter when snapshot.EffectiveTarget.CriticalEncounter != null:
                return FormattableString.Invariant($"{cePrefix} | {snapshot.EffectiveTarget.CriticalEncounter.Name} ({snapshot.EffectiveTarget.CriticalEncounter.Id})");
            case SelectedTargetKind.Fate when snapshot.EffectiveTarget.Fate != null:
                return FormattableString.Invariant($"{fatePrefix} | {snapshot.EffectiveTarget.Fate.Name} ({snapshot.EffectiveTarget.Fate.Id})");
        }

        if (snapshot.CurrentCriticalEncounter != null)
        {
            return FormattableString.Invariant($"Running CE | {snapshot.CurrentCriticalEncounter.Name} ({snapshot.CurrentCriticalEncounter.Id})");
        }

        if (snapshot.IsInCriticalEncounter)
        {
            return FormattableString.Invariant($"Running CE | {snapshot.CurrentCriticalEncounterId.ToString(CultureInfo.InvariantCulture)}");
        }

        return null;
    }

    private string? GetTreasureSearchSummary(TreasureHintSnapshot treasureSnapshot)
    {
        var treasureSearch = plugin.TreasureSearchController;
        if (!treasureSearch.IsRunning)
        {
            return null;
        }

        var groupKey = treasureSearch.ActiveGroupKey;
        if (groupKey.Length == 0)
        {
            return GetFriendlyFarmActivity();
        }

        var summary = $"Checking {groupKey} group";
        var candidateLabel = treasureSearch.ActiveCandidateKey?.Label;
        if (!string.IsNullOrEmpty(candidateLabel))
        {
            summary += $" | {candidateLabel}";
        }

        var distanceBucket = treasureSnapshot.LastHintEvent?.DistanceBucket;
        if (!string.IsNullOrEmpty(distanceBucket))
        {
            summary += $" | {distanceBucket}";
        }

        return summary;
    }

    private string GetFriendlyFarmActivity()
        => farmSessionController.CurrentActivity switch
        {
            "Critical Engagement" => "Running CE",
            "FATE" => "Running FATE",
            _ => farmSessionController.CurrentActivity,
        };

    private string? GetFarmFallbackDetail()
    {
        var activity = GetFriendlyFarmActivity();
        var transition = FormatValue(farmSessionController.LastTransition);
        if (string.Equals(transition, "None", StringComparison.Ordinal)
            || string.Equals(transition, activity, StringComparison.Ordinal))
        {
            return null;
        }

        return IsIdleLikeFarmActivity(activity) ? null : transition;
    }

    private string? GetPendingPotDepartureSummary(string activity, PotCycleSnapshot potCycleSnapshot)
    {
        if (!IsIdleLikeFarmActivity(activity))
        {
            return null;
        }

        var transition = farmSessionController.LastTransition;
        if (transition.Length == 0
            || (!transition.StartsWith("FATE fallback start blocked: pot departure in ", StringComparison.Ordinal)
                && !transition.StartsWith("CE fallback start blocked: pot departure in ", StringComparison.Ordinal)))
        {
            return null;
        }

        if (!potCycleSnapshot.HasPredictedNextPot)
        {
            return "Waiting to depart for pots";
        }

        var departureAt = potCycleSnapshot.PredictedNextSpawnAt
            - TimeSpan.FromMinutes(Math.Max(0, configuration.SpawnLeadMinutes));
        var timeUntilDeparture = departureAt - DateTimeOffset.UtcNow;
        var countdown = timeUntilDeparture > TimeSpan.Zero
            ? timeUntilDeparture.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
            : "00:00";
        return $"Waiting to depart for pots | {countdown}";
    }

    private string? GetIdleAutoCofferSummary(TreasureCofferSurveySnapshot cofferSurveySnapshot, TreasureCofferAutomaticModeStatus automaticCofferStatus)
    {
        var activity = GetFriendlyFarmActivity();
        if (!IsIdleLikeFarmActivity(activity)
            || !configuration.EnableAutomaticTreasureCofferRoute
            || automaticCofferStatus.DisabledForCurrentRun)
        {
            return null;
        }

        var rotationEntries = new System.Collections.Generic.List<string> { activity };
        if (cofferSurveySnapshot.HasSurvey)
        {
            rotationEntries.Add($"Auto coffers | last scan silver {cofferSurveySnapshot.SilverCount} bronze {cofferSurveySnapshot.BronzeCount}");
        }

        if (automaticCofferStatus.RemainingSilverCompletionsUntilRescan == 0
            && automaticCofferStatus.RemainingBronzeCompletionsUntilRescan == 0)
        {
            rotationEntries.Add("Auto coffers | ready");
        }
        else if (automaticCofferStatus.RemainingSilverCompletionsUntilRescan > 0
            || automaticCofferStatus.RemainingBronzeCompletionsUntilRescan > 0)
        {
            rotationEntries.Add($"Auto coffers | next scan {automaticCofferStatus.RemainingSilverCompletionsUntilRescan + automaticCofferStatus.RemainingBronzeCompletionsUntilRescan}");
        }

        if (rotationEntries.Count == 1)
        {
            return null;
        }

        var rotationIndex = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 2 % rotationEntries.Count);
        return rotationEntries[rotationIndex];
    }

    private static bool IsIdleLikeFarmActivity(string activity)
        => activity is "Idle waiting" or "Selecting target";

    private static bool IsPotFlowActivity(string activity)
        => activity is "Waiting for predicted pot window"
            or "Waiting at pot spawn"
            or "Running pots"
            or "Running treasure hunt";

    private static string? GetPotSummary(ScannerSnapshot snapshot, PotCycleSnapshot potCycleSnapshot)
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

        return null;
    }

    private static string FormatValue(string? value)
        => string.IsNullOrEmpty(value) ? "None" : value;

    private string GetVisibleCofferSummary()
    {
        var activeSpot = treasureCofferFarmController.ActiveSpot;
        if (activeSpot == null)
        {
            return "Idle";
        }

        return $"{activeSpot.Area}:{activeSpot.Label}";
    }

    private string? GetVisibleCofferStartBlocker()
    {
        if (configuration.ScannerOnlyMode)
        {
            return "Scanner-only mode blocks overworld coffer route starts.";
        }

        if (treasureCofferFarmController.IsRunning)
        {
            return "Overworld coffer route is already running.";
        }

        if (farmSessionController.IsRunning)
        {
            return "Overworld coffer route start is blocked while the farm session is running.";
        }

        if (!scanner.Snapshot.IsInSupportedTerritory)
        {
            return "Overworld coffer route requires a supported Occult Crescent territory.";
        }

        if (!scanner.Snapshot.CanRunVisibleCofferRoute)
        {
            return $"Overworld coffer route data is unavailable in {scanner.Snapshot.TerritoryDisplayName}.";
        }

        var dependencyReport = plugin.GetNormalAutomationDependencyReport();
        return dependencyReport.IsReady ? null : dependencyReport.FailureSummary;
    }

    private static bool CanRunAnyAutomation(ScannerSnapshot snapshot)
        => snapshot.CanFarmFates
            || snapshot.CanFarmCriticalEncounters
            || snapshot.CanRunPotTreasure
            || snapshot.CanRunVisibleCofferRoute
            || snapshot.CanUseShopping
            || snapshot.CanRunBuffRotation;
}
