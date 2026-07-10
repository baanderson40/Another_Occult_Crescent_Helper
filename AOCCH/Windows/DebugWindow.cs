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

public sealed class DebugWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly AutorotationController autorotationController;
    private readonly BuffRotationController buffRotationController;
    private readonly CriticalEngagementAutomationController criticalEngagementAutomationController;
    private readonly FateAutomationController fateAutomationController;
    private readonly DeathRecoveryController deathRecoveryController;
    private readonly PotFarmController potFarmController;
    private readonly FarmSessionController farmSessionController;

    // We give this window a hidden ID using ##.
    // The user will see "Another Occult Crescent Helper" as window title,
    // but for ImGui the ID is "Another Occult Crescent Helper##Main".
    public DebugWindow(
        Plugin plugin,
        Configuration configuration,
        OccultCrescentScanner scanner,
        MovementController movementController,
        AutorotationController autorotationController,
        BuffRotationController buffRotationController,
        CriticalEngagementAutomationController criticalEngagementAutomationController,
        FateAutomationController fateAutomationController,
        DeathRecoveryController deathRecoveryController,
        PotFarmController potFarmController,
        FarmSessionController farmSessionController)
        : base("AOCCH Debug###AOCCHDebug")
    {
        this.plugin = plugin;
        this.configuration = configuration;
        this.scanner = scanner;
        this.movementController = movementController;
        this.autorotationController = autorotationController;
        this.buffRotationController = buffRotationController;
        this.criticalEngagementAutomationController = criticalEngagementAutomationController;
        this.fateAutomationController = fateAutomationController;
        this.deathRecoveryController = deathRecoveryController;
        this.potFarmController = potFarmController;
        this.farmSessionController = farmSessionController;

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

        ImGui.TextUnformatted($"CE Farming: {(configuration.EnableCriticalEngagementFarming ? "Enabled" : "Disabled")}");
        ImGui.TextUnformatted($"FATE Farming: {(configuration.EnableFateFarming ? "Enabled" : "Disabled")}");
        ImGui.TextUnformatted($"Territory: {snapshot.TerritoryTypeId}");
        ImGui.TextUnformatted($"In South Horn: {(snapshot.IsInSouthHorn ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Last Scan: {FormatTimestamp(snapshot.LastUpdated)}");

        ImGui.Separator();
        DrawSafety(snapshot);

        ImGui.Separator();
        DrawTestReadiness(snapshot);

        ImGui.Separator();
        DrawSelectedTarget(snapshot);

        ImGui.Separator();
        DrawFarmSession();

        ImGui.Separator();
        DrawPotStatus(snapshot);

        ImGui.Separator();
        DrawCriticalEngagementAutomation(snapshot);

        ImGui.Separator();
        DrawFateAutomation(snapshot);

        ImGui.Separator();
        DrawAutorotation();

        ImGui.Separator();
        DrawBuffRotation(snapshot);

        ImGui.Separator();
        DrawDeathRecovery();

        ImGui.Separator();
        DrawMovement(snapshot);

        ImGui.Separator();
        DrawCriticalEncounters(snapshot);

        ImGui.Separator();
        DrawFates(snapshot);
    }

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

        var otherAutomationRunning = fateAutomationController.IsRunning || farmSessionController.IsRunning;
        var canStart = snapshot.IsInSouthHorn
            && snapshot.EffectiveTarget.Kind == SelectedTargetKind.CriticalEncounter
            && !otherAutomationRunning
            && !configuration.ScannerOnlyMode;

        ImGui.BeginDisabled(!canStart);
        if (ImGui.Button("Start CE Automation"))
        {
            plugin.Logger.Info("Manual UI action: Start CE Automation.");
            criticalEngagementAutomationController.Start();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Stop CE Automation"))
        {
            plugin.Logger.Info("Manual UI action: Stop CE Automation.");
            criticalEngagementAutomationController.Stop("Manual CE automation stop requested.");
        }

        if (otherAutomationRunning)
        {
            ImGui.TextUnformatted("Stop the farm session/FATE automation before starting CE automation.");
        }
        else if (configuration.ScannerOnlyMode)
        {
            ImGui.TextUnformatted("Scanner-only mode blocks CE automation starts.");
        }
        else if (!canStart)
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

    private void DrawFateAutomation(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("FATE Automation");
        ImGui.TextUnformatted($"State: {fateAutomationController.State}");

        if (fateAutomationController.TargetFateId != 0)
        {
            ImGui.TextWrapped($"Locked Target: {fateAutomationController.TargetFateName} ({fateAutomationController.TargetFateId})");
        }
        else
        {
            ImGui.TextUnformatted("Locked Target: None");
        }

        ImGui.TextWrapped($"Last Transition: {fateAutomationController.LastTransition}");
        if (!string.IsNullOrEmpty(fateAutomationController.LastError))
        {
            ImGui.TextWrapped($"Last Error: {fateAutomationController.LastError}");
        }

        var otherAutomationRunning = criticalEngagementAutomationController.IsRunning || farmSessionController.IsRunning;
        var canStart = snapshot.IsInSouthHorn
            && snapshot.EffectiveTarget.Kind == SelectedTargetKind.Fate
            && !otherAutomationRunning
            && !configuration.ScannerOnlyMode;

        ImGui.BeginDisabled(!canStart);
        if (ImGui.Button("Start FATE Automation"))
        {
            plugin.Logger.Info("Manual UI action: Start FATE Automation.");
            fateAutomationController.Start();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Stop FATE Automation"))
        {
            plugin.Logger.Info("Manual UI action: Stop FATE Automation.");
            fateAutomationController.Stop("Manual FATE automation stop requested.");
        }

        if (otherAutomationRunning)
        {
            ImGui.TextUnformatted("Stop the farm session/CE automation before starting FATE automation.");
        }
        else if (configuration.ScannerOnlyMode)
        {
            ImGui.TextUnformatted("Scanner-only mode blocks FATE automation starts.");
        }
        else if (!canStart)
        {
            ImGui.TextUnformatted("Start FATE Automation requires South Horn and a FATE effective target.");
        }
    }

    private void DrawBuffRotation(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("Buff Rotation");
        ImGui.TextUnformatted($"Enabled: {(configuration.EnableBuffRotation ? "Yes" : "No")}");
        ImGui.TextUnformatted($"State: {buffRotationController.State}");
        ImGui.TextWrapped($"Context: {FormatValue(buffRotationController.LastContext)}");
        ImGui.TextWrapped($"Action: {FormatValue(buffRotationController.CurrentAction)}");
        ImGui.TextWrapped($"Original Support Job: {FormatSupportJob(buffRotationController.OriginalSupportJob)}");
        ImGui.TextWrapped($"Current Support Job: {FormatSupportJob(buffRotationController.CurrentSupportJob)}");
        ImGui.TextWrapped($"Pending Restore: {FormatSupportJob(buffRotationController.PendingSupportJobRestore)}");
        ImGui.TextWrapped($"Missing Required Statuses: {FormatValue(buffRotationController.MissingRequiredStatuses)}");
        ImGui.TextWrapped($"Last Transition: {buffRotationController.LastTransition}");

        if (!string.IsNullOrEmpty(buffRotationController.LastError))
        {
            ImGui.TextWrapped($"Last Error: {buffRotationController.LastError}");
        }

        var otherAutomationRunning = criticalEngagementAutomationController.IsRunning || fateAutomationController.IsRunning || farmSessionController.IsRunning;
        var canStart = snapshot.IsInSouthHorn && !otherAutomationRunning && !buffRotationController.IsRunning;
        canStart = canStart && !configuration.ScannerOnlyMode;

        ImGui.BeginDisabled(!canStart);
        if (ImGui.Button("Run Buff Rotation"))
        {
            plugin.Logger.Info("Manual UI action: Run Buff Rotation.");
            buffRotationController.Start("manual UI");
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Stop Buff Rotation"))
        {
            plugin.Logger.Info("Manual UI action: Stop Buff Rotation.");
            buffRotationController.Stop("Manual buff rotation stop requested.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Restore Support Job"))
        {
            plugin.Logger.Info("Manual UI action: Restore Support Job.");
            buffRotationController.RestorePendingSupportJob("manual UI restore");
        }

        if (otherAutomationRunning)
        {
            ImGui.TextUnformatted("Stop CE/FATE automation before running buff rotation.");
        }
        else if (configuration.ScannerOnlyMode)
        {
            ImGui.TextUnformatted("Scanner-only mode blocks buff rotation starts.");
        }
        else if (!snapshot.IsInSouthHorn)
        {
            ImGui.TextUnformatted("Buff rotation requires South Horn.");
        }
        else if (buffRotationController.IsRunning)
        {
            ImGui.TextUnformatted("Buff rotation is already running.");
        }
    }

    private void DrawDeathRecovery()
    {
        ImGui.TextUnformatted("Death Recovery");
        ImGui.TextUnformatted($"State: {deathRecoveryController.State}");
        ImGui.TextUnformatted($"Raise Detected: {(deathRecoveryController.RaiseDetected ? "Yes" : "No")}");
        ImGui.TextWrapped($"Elapsed: {deathRecoveryController.Elapsed:mm\\:ss}");
        ImGui.TextWrapped($"Last Transition: {deathRecoveryController.LastTransition}");

        if (!string.IsNullOrEmpty(deathRecoveryController.LastError))
        {
            ImGui.TextWrapped($"Last Error: {deathRecoveryController.LastError}");
        }
    }

    private void DrawFarmSession()
    {
        ImGui.TextUnformatted("Farm Session");
        ImGui.TextUnformatted($"State: {farmSessionController.State}");
        ImGui.TextWrapped($"Activity: {FormatValue(farmSessionController.CurrentActivity)}");
        ImGui.TextWrapped($"Last Transition: {farmSessionController.LastTransition}");

        if (!string.IsNullOrEmpty(farmSessionController.LastError))
        {
            ImGui.TextWrapped($"Last Error: {farmSessionController.LastError}");
        }

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

    private void DrawPotStatus(ScannerSnapshot snapshot)
    {
        var potCycleSnapshot = plugin.PotCycleTracker.Snapshot;
        var now = DateTimeOffset.UtcNow;
        var ceDecision = plugin.PotFallbackWindowEvaluator.EvaluateCeStart(potCycleSnapshot, now);
        var fateDecision = plugin.PotFallbackWindowEvaluator.EvaluateFateStart(potCycleSnapshot, now);

        ImGui.TextUnformatted("Pot Control");
        ImGui.TextUnformatted($"Enabled: {(configuration.EnablePotFarming ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Farm State: {potFarmController.State}");
        ImGui.TextWrapped($"Farm Transition: {potFarmController.LastTransition}");
        ImGui.TextWrapped($"Current Pot: {FormatValue(potFarmController.CurrentPotName)}");
        ImGui.TextUnformatted($"Active Pot: {FormatValue(snapshot.ActivePotFate?.Name)}");
        ImGui.TextUnformatted($"Known Anchor: {(potCycleSnapshot.HasKnownAnchor ? "Yes" : "No")}");
        ImGui.TextWrapped($"Last Anchor: {FormatValue(potCycleSnapshot.LastObservedPotFateName)} @ {FormatTimestamp(potCycleSnapshot.LastObservedSpawnAt)}");
        ImGui.TextWrapped($"Predicted Next Pot: {FormatValue(potCycleSnapshot.PredictedNextPotFateName)} @ {FormatTimestamp(potCycleSnapshot.PredictedNextSpawnAt)}");
        ImGui.TextWrapped($"Spawn Wait Deadline: {FormatTimestamp(potFarmController.WaitDeadlineAt)}");
        ImGui.TextWrapped($"CE Fallback: {ceDecision.Reason}");
        ImGui.TextWrapped($"FATE Fallback: {fateDecision.Reason}");

        var treasureSnapshot = plugin.TreasureHintTracker.Snapshot;
        ImGui.TextUnformatted($"Treasure Session: {treasureSnapshot.SessionState}");
        ImGui.TextUnformatted($"Treasure Session ID: {treasureSnapshot.SessionId}");
        ImGui.TextWrapped($"Treasure Started: {FormatTimestamp(treasureSnapshot.StartedAt)}");
        ImGui.TextWrapped($"Treasure Completed: {FormatTimestamp(treasureSnapshot.CompletedAt)}");
        ImGui.TextWrapped($"Treasure Completion: {FormatValue(treasureSnapshot.CompletionReason)}");
        ImGui.TextUnformatted($"Treasure Revision: {treasureSnapshot.Revision}");
        ImGui.TextWrapped($"Treasure Initial Hint: {FormatTreasureHint(treasureSnapshot.InitialHintEvent)}");
        ImGui.TextWrapped($"Treasure Last Hint: {FormatTreasureHint(treasureSnapshot.LastHintEvent)}");
        ImGui.TextWrapped($"Treasure Last Event: {FormatTreasureEvent(treasureSnapshot.LastEvent)}");
        ImGui.TextWrapped($"Treasure Transition: {treasureSnapshot.LastTransition}");
        ImGui.TextWrapped($"Treasure Reset Reason: {FormatValue(treasureSnapshot.LastResetReason)}");

        var treasureSearch = plugin.TreasureSearchController;
        ImGui.TextUnformatted($"Treasure Search State: {treasureSearch.State}");
        ImGui.TextWrapped($"Treasure Search Transition: {treasureSearch.LastTransition}");
        ImGui.TextWrapped($"Treasure Search Group: {FormatValue(treasureSearch.ActiveGroupKey)}");
        ImGui.TextWrapped($"Treasure Search Candidate: {FormatValue(treasureSearch.ActiveCandidateKey?.Label)}");
        ImGui.TextWrapped($"Treasure Search Candidate Index: {treasureSearch.CurrentCandidateIndex}");
        ImGui.TextWrapped($"Treasure Search Handoff: {FormatValue(treasureSearch.LastHandoffReason)}");
        var visibleMatch = treasureSearch.ActiveVisibleCofferMatch;
        var visibleMatchText = visibleMatch == null
            ? null
            : $"{visibleMatch.CandidateKey.Label} <- {visibleMatch.Coffer.Name} ({visibleMatch.MatchDistance:0.0}y) | {visibleMatch.AttributionReason}";
        ImGui.TextWrapped($"Treasure Visible Match: {FormatValue(visibleMatchText)}");

        if (!string.IsNullOrEmpty(potFarmController.LastError))
        {
            ImGui.TextWrapped($"Last Error: {potFarmController.LastError}");
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
        var scannerOnlyMode = configuration.ScannerOnlyMode;
        if (ImGui.Button("Plan Route") && hasSelectedTarget)
        {
            plugin.Logger.Info("Manual UI action: Plan Route.");
            movementController.PlanRouteToSelectedTarget();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(scannerOnlyMode);
        if (ImGui.Button("Start Route"))
        {
            plugin.Logger.Info("Manual UI action: Start Route.");
            movementController.StartPlannedRoute();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Stop Movement"))
        {
            plugin.Logger.Info("Manual UI action: Stop Movement.");
            movementController.Stop("Manual stop requested.");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(scannerOnlyMode);
        if (ImGui.Button("Recover To Base Camp"))
        {
            plugin.Logger.Info("Manual UI action: Recover To Base Camp.");
            movementController.RecoverToBaseCamp();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(scannerOnlyMode || !movementController.CanUseReturnAction);
        if (ImGui.Button("Test Return Recovery"))
        {
            plugin.Logger.Info("Manual UI action: Test Return Recovery.");
            movementController.RecoverToBaseCamp();
        }
        ImGui.EndDisabled();

        if (!hasSelectedTarget)
        {
            ImGui.TextUnformatted("Plan Route requires a selected CE or FATE target.");
        }

        if (scannerOnlyMode)
        {
            ImGui.TextUnformatted("Scanner-only mode blocks movement starts and Base Camp recovery.");
            return;
        }

        if (!movementController.CanUseReturnAction)
        {
            ImGui.TextUnformatted("Return recovery testing is blocked because the Return action is unavailable.");
        }

        ImGui.TextUnformatted("Use Recover To Base Camp to test the Return recovery flow.");
    }

    private void DrawSafety(ScannerSnapshot snapshot)
    {
        var bossModRequired = autorotationController.ConfiguredPreset.Length > 0;
        var bossModAvailable = autorotationController.RefreshBossModAvailability();

        ImGui.TextUnformatted("Safety");
        ImGui.TextUnformatted($"Scanner-Only Mode: {(configuration.ScannerOnlyMode ? "Enabled" : "Disabled")}");
        ImGui.TextUnformatted($"vnavmesh: {movementController.VNavmeshStatusText}");
        ImGui.TextUnformatted($"Lifestream: {movementController.LifestreamStatusText}");
        ImGui.TextUnformatted($"Return Action: {(movementController.CanUseReturnAction ? "Available" : "Unavailable")}");
        ImGui.TextUnformatted($"BossMod: {FormatBossModStatus(bossModRequired, bossModAvailable)}");
        ImGui.TextUnformatted($"Farm Running: {(farmSessionController.IsRunning ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Movement State: {movementController.State}");

        if (configuration.ScannerOnlyMode)
        {
            ImGui.TextWrapped("Automation start requests are blocked while scanner-only mode is enabled.");
        }

        if (!snapshot.IsInSouthHorn)
        {
            ImGui.TextUnformatted("Automation is currently outside South Horn.");
        }
    }

    private void DrawTestReadiness(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("Automation Test Readiness");
        ImGui.TextUnformatted($"South Horn: {(snapshot.IsInSouthHorn ? "Ready" : "Blocked")}");
        ImGui.TextUnformatted($"Return Required: {(configuration.UseReturn ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Return Available: {(movementController.CanUseReturnAction ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Farm Start: {FormatValue(GetFarmStartBlocker() ?? "Ready for full farm test")}");
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

        if (criticalEngagementAutomationController.IsRunning || fateAutomationController.IsRunning || buffRotationController.IsRunning || potFarmController.IsRunning)
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

    private static string FormatBossModStatus(bool required, bool available)
    {
        if (!required)
        {
            return available ? "Available (Not Required)" : "Not Required";
        }

        return available ? "Available" : "Unavailable";
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

    private static string FormatValue(string? value)
        => string.IsNullOrEmpty(value) ? "None" : value;

    private static string FormatTreasureHint(TreasureHintEvent? hint)
    {
        if (hint == null)
        {
            return "None";
        }

        var direction = hint.Direction == TreasureDirection.Unknown ? "unknown" : hint.Direction.ToString().ToLowerInvariant();
        var distance = string.IsNullOrWhiteSpace(hint.DistanceBucket) ? "unknown" : hint.DistanceBucket;
        return $"{direction} / {distance} @ {FormatTimestamp(hint.ReceivedAt)}";
    }

    private static string FormatTreasureEvent(TreasureHintEvent? treasureEvent)
    {
        if (treasureEvent == null)
        {
            return "None";
        }

        return $"{treasureEvent.Kind} @ {FormatTimestamp(treasureEvent.ReceivedAt)} | {treasureEvent.RawText}";
    }

    private static string FormatSupportJob(byte supportJob)
        => supportJob switch
        {
            0 => "0 (Freelancer)",
            1 => "1 (Knight)",
            3 => "3 (Monk)",
            6 => "6 (Bard)",
            15 => "15 (Dancer)",
            _ => supportJob.ToString(CultureInfo.InvariantCulture),
        };

    private static string FormatSupportJob(byte? supportJob)
        => supportJob.HasValue ? FormatSupportJob(supportJob.Value) : "None";

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
