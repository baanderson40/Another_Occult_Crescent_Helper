using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using AOCCH.Automation;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AOCCH.Windows;

public sealed class DebugWindow : Window, IDisposable
{
    private enum DebugSection
    {
        Overview,
        NorthHornPreview,
        Safety,
        AutomationTestReadiness,
        SelectedTarget,
        FarmSession,
        PotControl,
        VisibleCofferFarm,
        DangerousTreasureTravel,
        CriticalEngagementAutomation,
        FateAutomation,
        Autorotation,
        BuffRotation,
        DeathRecovery,
        ShopInspector,
        Movement,
        CriticalEngagements,
        Fates,
        Territory,
    }

    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly GameActionController gameActionController;
    private readonly AutorotationController autorotationController;
    private readonly BuffRotationController buffRotationController;
    private readonly CriticalEngagementAutomationController criticalEngagementAutomationController;
    private readonly FateAutomationController fateAutomationController;
    private readonly DeathRecoveryController deathRecoveryController;
    private readonly PotFarmController potFarmController;
    private readonly DangerousTreasureTravelController dangerousTreasureTravelController;
    private readonly FarmSessionController farmSessionController;
    private readonly TreasureCofferFarmController treasureCofferFarmController;
    private DebugSection selectedSection = DebugSection.Overview;
    private int shopAtkValueStartIndex;
    private int shopAtkValueCount = 160;
    private int shopMenuIndex;
    private int shopTestPurchaseQuantity = 2;
    private int selectedVisibleCofferRouteStartIndex;

    // We give this window a hidden ID using ##.
    // The user will see "Another Occult Crescent Helper" as window title,
    // but for ImGui the ID is "Another Occult Crescent Helper##Main".
    public DebugWindow(
        Plugin plugin,
        Configuration configuration,
        OccultCrescentScanner scanner,
        MovementController movementController,
        GameActionController gameActionController,
        AutorotationController autorotationController,
        BuffRotationController buffRotationController,
        CriticalEngagementAutomationController criticalEngagementAutomationController,
        FateAutomationController fateAutomationController,
        DeathRecoveryController deathRecoveryController,
        PotFarmController potFarmController,
        DangerousTreasureTravelController dangerousTreasureTravelController,
        FarmSessionController farmSessionController,
        TreasureCofferFarmController treasureCofferFarmController)
        : base("AOCCH Debug###AOCCHDebug")
    {
        this.plugin = plugin;
        this.configuration = configuration;
        this.scanner = scanner;
        this.movementController = movementController;
        this.gameActionController = gameActionController;
        this.autorotationController = autorotationController;
        this.buffRotationController = buffRotationController;
        this.criticalEngagementAutomationController = criticalEngagementAutomationController;
        this.fateAutomationController = fateAutomationController;
        this.deathRecoveryController = deathRecoveryController;
        this.potFarmController = potFarmController;
        this.dangerousTreasureTravelController = dangerousTreasureTravelController;
        this.farmSessionController = farmSessionController;
        this.treasureCofferFarmController = treasureCofferFarmController;

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

        var navWidth = MathF.Max(180f, ImGui.GetContentRegionAvail().X * 0.28f);
        var childHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y);

        ImGui.BeginChild("DebugSectionList", new Vector2(navWidth, childHeight), true);
        DrawSectionList();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("DebugSectionContent", new Vector2(0, childHeight), true);
        DrawSectionContent(snapshot);
        ImGui.EndChild();
    }

    private void DrawSectionList()
    {
        DrawSectionButton(DebugSection.Overview, "Overview");
        DrawSectionButton(DebugSection.AutomationTestReadiness, "Automation Test Readiness");
        DrawSectionButton(DebugSection.Autorotation, "Autorotation");
        DrawSectionButton(DebugSection.BuffRotation, "Buff Rotation");
        DrawSectionButton(DebugSection.CriticalEngagementAutomation, "Critical Engagement Automation");
        DrawSectionButton(DebugSection.CriticalEngagements, "Critical Engagements");
        DrawSectionButton(DebugSection.DangerousTreasureTravel, "Dangerous Treasure Travel");
        DrawSectionButton(DebugSection.DeathRecovery, "Death Recovery");
        DrawSectionButton(DebugSection.NorthHornPreview, "North Horn Preview");
        DrawSectionButton(DebugSection.FateAutomation, "FATE Automation");
        DrawSectionButton(DebugSection.Fates, "FATEs");
        DrawSectionButton(DebugSection.FarmSession, "Farm Session");
        DrawSectionButton(DebugSection.Movement, "Movement");
        DrawSectionButton(DebugSection.VisibleCofferFarm, "Overworld Coffer Route");
        DrawSectionButton(DebugSection.PotControl, "Pot Control");
        DrawSectionButton(DebugSection.Safety, "Safety");
        DrawSectionButton(DebugSection.SelectedTarget, "Selected Target");
        DrawSectionButton(DebugSection.ShopInspector, "Shop Inspector");
        DrawSectionButton(DebugSection.Territory, "Territory");
    }

    private void DrawSectionButton(DebugSection section, string label)
    {
        var isSelected = selectedSection == section;
        if (ImGui.Selectable(label, isSelected))
        {
            selectedSection = section;
        }
    }

    private void DrawSectionContent(ScannerSnapshot snapshot)
    {
        switch (selectedSection)
        {
            case DebugSection.Overview:
                DrawOverview(snapshot);
                break;
            case DebugSection.NorthHornPreview:
                DrawNorthHornPreview();
                break;
            case DebugSection.Safety:
                DrawSafety(snapshot);
                break;
            case DebugSection.AutomationTestReadiness:
                DrawTestReadiness(snapshot);
                break;
            case DebugSection.SelectedTarget:
                DrawSelectedTarget(snapshot);
                break;
            case DebugSection.FarmSession:
                DrawFarmSession();
                break;
            case DebugSection.PotControl:
                DrawPotStatus(snapshot);
                break;
            case DebugSection.VisibleCofferFarm:
                DrawVisibleCofferFarm();
                break;
            case DebugSection.DangerousTreasureTravel:
                DrawDangerousTreasureTravel();
                break;
            case DebugSection.CriticalEngagementAutomation:
                DrawCriticalEngagementAutomation(snapshot);
                break;
            case DebugSection.FateAutomation:
                DrawFateAutomation(snapshot);
                break;
            case DebugSection.Autorotation:
                DrawAutorotation();
                break;
            case DebugSection.BuffRotation:
                DrawBuffRotation(snapshot);
                break;
            case DebugSection.DeathRecovery:
                DrawDeathRecovery();
                break;
            case DebugSection.ShopInspector:
                DrawShopInspector();
                break;
            case DebugSection.Movement:
                DrawMovement(snapshot);
                break;
            case DebugSection.CriticalEngagements:
                DrawCriticalEncounters(snapshot);
                break;
            case DebugSection.Fates:
                DrawFates(snapshot);
                break;
            case DebugSection.Territory:
                DrawTerritory(snapshot);
                break;
        }
    }

    private void DrawOverview(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("Overview");
        ImGui.TextUnformatted($"CE Farming: {(configuration.EnableCriticalEngagementFarming ? "Enabled" : "Disabled")}");
        ImGui.TextUnformatted($"FATE Farming: {(configuration.EnableFateFarming ? "Enabled" : "Disabled")}");
        ImGui.TextUnformatted($"FATE Data: {FormatFeatureAvailability(snapshot.CanFarmFates)}");
        ImGui.TextUnformatted($"CE Data: {FormatFeatureAvailability(snapshot.CanFarmCriticalEncounters)}");
        ImGui.TextUnformatted($"Shopping Data: {FormatFeatureAvailability(snapshot.CanUseShopping)}");
        ImGui.TextUnformatted($"Visible Coffer Data: {FormatFeatureAvailability(snapshot.CanRunVisibleCofferRoute)}");
        ImGui.TextUnformatted($"Pot Cycle Tracking: {FormatFeatureAvailability(snapshot.CanTrackPotCycle)}");
        ImGui.TextUnformatted($"Pot Treasure Data: {FormatFeatureAvailability(snapshot.CanRunPotTreasure)}");
        ImGui.TextUnformatted($"Buff Rotation Data: {FormatFeatureAvailability(snapshot.CanRunBuffRotation)}");
        ImGui.TextUnformatted($"Last Scan: {FormatTimestamp(snapshot.LastUpdated)}");
    }

    private static void DrawTerritory(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("Territory");
        ImGui.TextUnformatted($"Territory: {snapshot.TerritoryTypeId}");
        ImGui.TextUnformatted($"In Supported Territory: {(snapshot.IsInSupportedTerritory ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Territory Key: {(snapshot.TerritoryKey.Length == 0 ? "Unsupported" : snapshot.TerritoryKey)}");
        ImGui.TextUnformatted($"Territory Name: {(snapshot.TerritoryDisplayName.Length == 0 ? "Unsupported" : snapshot.TerritoryDisplayName)}");
    }

    private static string FormatFeatureAvailability(bool available)
        => available ? "Ready" : "Unavailable";

    private void DrawNorthHornPreview()
    {
        ImGui.TextUnformatted("North Horn Status Preview");
        ImGui.TextWrapped("Preview the North Horn feature status window without entering North Horn.");
        if (ImGui.Button("Open North Horn Status Preview"))
        {
            plugin.OpenNorthHornStatusPreview();
        }
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp == DateTimeOffset.MinValue
            ? "Waiting for first scan"
            : timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private void DrawShopInspector()
    {
        var snapshot = plugin.ShopInspectorController.Snapshot;

        ImGui.TextUnformatted("Shop Inspector");
        ImGui.TextUnformatted($"Captured: {FormatTimestamp(snapshot.CapturedAt)}");
        ImGui.TextUnformatted($"SelectIconString Open: {(snapshot.IsSelectIconStringOpen ? "Yes" : "No")}");
        ImGui.TextUnformatted($"ShopExchangeCurrency Open: {(snapshot.IsShopExchangeCurrencyOpen ? "Yes" : "No")}");
        ImGui.TextUnformatted($"ShopExchangeItem Open: {(snapshot.IsShopExchangeItemOpen ? "Yes" : "No")}");

        if (snapshot.IsShopExchangeCurrencyOpen)
        {
            var currencyLabel = string.IsNullOrEmpty(snapshot.CurrencyName)
                ? $"Currency Item ID {snapshot.CurrencyItemId}"
                : $"{snapshot.CurrencyName} ({snapshot.CurrencyItemId})";
            ImGui.TextUnformatted($"Currency: {currencyLabel}");
            ImGui.TextUnformatted($"Currency Amount: {snapshot.CurrencyAmount}");
            ImGui.TextUnformatted($"Selected Tab: {snapshot.SelectedTabId}");
        }

        if (!string.IsNullOrEmpty(snapshot.LastError))
        {
            ImGui.TextWrapped($"Last Error: {snapshot.LastError}");
        }

        ImGui.TextWrapped($"Purchase Status: {plugin.ShopPurchaseController.LastStatus}");
        ImGui.TextWrapped($"AtkValue Capture: {plugin.ShopInspectorController.AtkValueCaptureStatus}");

        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt("Test Purchase Quantity", ref shopTestPurchaseQuantity))
        {
            shopTestPurchaseQuantity = Math.Max(1, shopTestPurchaseQuantity);
        }

        shopMenuIndex = plugin.ShopInspectorController.CurrentMenuIndex < 0 ? shopMenuIndex : plugin.ShopInspectorController.CurrentMenuIndex;
        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt("Current Menu Index", ref shopMenuIndex))
        {
            plugin.ShopInspectorController.SetCurrentMenuIndex(shopMenuIndex);
        }

        var currentMenuLabel = plugin.ShopInspectorController.CurrentMenuLabel;
        ImGui.TextWrapped($"Current Menu Label: {(string.IsNullOrEmpty(currentMenuLabel) ? "Unknown" : currentMenuLabel)}");
        ImGui.TextWrapped($"Latched Menu: {(plugin.ShopInspectorController.LatchedMenuIndex < 0 ? "None" : $"{plugin.ShopInspectorController.LatchedMenuIndex} / {plugin.ShopInspectorController.LatchedMenuLabel}")}");
        ImGui.TextWrapped($"Effective Menu: {(plugin.ShopInspectorController.EffectiveMenuIndex < 0 ? "Unknown" : $"{plugin.ShopInspectorController.EffectiveMenuIndex} / {plugin.ShopInspectorController.EffectiveMenuLabel}")}");

        if (ImGui.Button("Log Current Shop Snapshot"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=log-shop-snapshot");
            plugin.ShopInspectorController.LogCurrentSnapshot();
        }

        ImGui.SameLine();
        if (ImGui.Button("Log Static Catalog Capture"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=log-currency-catalog-capture");
            plugin.ShopInspectorController.LogCurrentCurrencyCatalogCapture();
        }

        ImGui.SameLine();
        if (ImGui.Button("Capture Menu Context"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=capture-menu-context");
            plugin.ShopInspectorController.CaptureManualMenuContext();
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear Latched Menu"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=clear-latched-menu");
            plugin.ShopInspectorController.ClearLatchedMenuContext();
        }

        ImGui.SetNextItemWidth(90f);
        ImGui.InputInt("AtkValue Start", ref shopAtkValueStartIndex);
        ImGui.SetNextItemWidth(90f);
        ImGui.InputInt("AtkValue Count", ref shopAtkValueCount);

        if (ImGui.Button("Capture Baseline AtkValues"))
        {
            plugin.Logger.Info($"[DebugWindow] op=ui-action action=capture-shop-atkvalues startIndex={shopAtkValueStartIndex} count={shopAtkValueCount}");
            plugin.ShopInspectorController.CaptureShopExchangeCurrencyAtkValues(shopAtkValueStartIndex, shopAtkValueCount);
        }

        ImGui.SameLine();
        if (ImGui.Button("Compare Current AtkValues"))
        {
            plugin.Logger.Info($"[DebugWindow] op=ui-action action=compare-shop-atkvalues startIndex={shopAtkValueStartIndex} count={shopAtkValueCount}");
            plugin.ShopInspectorController.CompareShopExchangeCurrencyAtkValues(shopAtkValueStartIndex, shopAtkValueCount);
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear AtkValue Baseline"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=clear-shop-atkvalue-baseline");
            plugin.ShopInspectorController.ClearCapturedShopExchangeCurrencyAtkValues();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Select Menu Entries");
        if (snapshot.MenuEntries.Count == 0)
        {
            ImGui.TextUnformatted("No SelectIconString entries detected.");
        }
        else
        {
            foreach (var entry in snapshot.MenuEntries)
            {
                ImGui.TextWrapped($"- [{entry.Index}] {entry.Label}");
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("ShopExchangeCurrency Entries");
        if (snapshot.ShopEntries.Count == 0)
        {
            ImGui.TextUnformatted("No ShopExchangeCurrency entries detected.");
        }
        else if (ImGui.BeginTable("ShopInspectorEntries", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Item ID", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Currency", ImGuiTableColumnFlags.WidthFixed, 140f);
            ImGui.TableSetupColumn("Max Stack", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("Row Index", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableHeadersRow();

            foreach (var entry in snapshot.ShopEntries)
            {
                ImGui.TableNextRow();
                ImGui.PushID($"currency_{entry.ItemId}_{entry.RowIndex}");
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(entry.ItemId.ToString(CultureInfo.InvariantCulture));

                ImGui.TableNextColumn();
                ImGui.TextWrapped(entry.ItemName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Cost.ToString(CultureInfo.InvariantCulture));

                ImGui.TableNextColumn();
                ImGui.TextWrapped(string.IsNullOrEmpty(entry.CurrencyName)
                    ? entry.CurrencyItemId.ToString(CultureInfo.InvariantCulture)
                    : entry.CurrencyName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.MaxStackSize?.ToString(CultureInfo.InvariantCulture) ?? "null");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.RowIndex.ToString(CultureInfo.InvariantCulture));

                ImGui.TableNextColumn();
                ImGui.BeginDisabled(plugin.ShopPurchaseController.IsBusy);
                if (ImGui.Button("Buy 1"))
                {
                    plugin.Logger.Info($"[DebugWindow] op=ui-action action=buy-shop-exchange-currency itemId={entry.ItemId} rowIndex={entry.RowIndex}");
                    plugin.ShopPurchaseController.TryBuyCurrencyEntry(entry, 1);
                }

                if (ImGui.Button("Buy Test"))
                {
                    plugin.Logger.Info($"[DebugWindow] op=ui-action action=buy-shop-exchange-currency-test itemId={entry.ItemId} rowIndex={entry.RowIndex} quantity={shopTestPurchaseQuantity}");
                    plugin.ShopPurchaseController.TryBuyCurrencyEntry(entry, shopTestPurchaseQuantity);
                }

                ImGui.EndDisabled();
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("ShopExchangeItem Entries");
        if (snapshot.ItemExchangeEntries.Count == 0)
        {
            ImGui.TextUnformatted("No ShopExchangeItem entries detected.");
            return;
        }

        if (!ImGui.BeginTable("ShopInspectorItemExchangeEntries", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("Item ID", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Requirements");
        ImGui.TableSetupColumn("Row Index", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableHeadersRow();

        foreach (var entry in snapshot.ItemExchangeEntries)
        {
            ImGui.TableNextRow();
            ImGui.PushID($"exchange_{entry.ItemId}_{entry.RowIndex}");
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(entry.ItemId.ToString(CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();
            ImGui.TextWrapped(entry.ItemName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Quantity.ToString(CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();
            var requirementsText = entry.RequiredItems.Count == 0
                ? "None"
                : string.Join(", ", entry.RequiredItems.Select(requiredItem => $"{requiredItem.ItemName} x{requiredItem.RequiredAmount}"));
            ImGui.TextWrapped(requirementsText);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.RowIndex.ToString(CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();
            ImGui.BeginDisabled(plugin.ShopPurchaseController.IsBusy);
            if (ImGui.Button("Buy 1"))
            {
                plugin.Logger.Info($"[DebugWindow] op=ui-action action=buy-shop-exchange-item itemId={entry.ItemId} rowIndex={entry.RowIndex}");
                plugin.ShopPurchaseController.TryBuyItemExchangeEntry(entry, 1);
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawCriticalEncounters(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("Critical Engagements");

        if (!snapshot.IsInSupportedTerritory)
        {
            ImGui.TextUnformatted("Not in a supported Occult Crescent territory.");
            return;
        }

        if (ImGui.Button("Dump CE Boss Candidates"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=dump-ce-boss-candidates");
            DumpCeBossCandidates(snapshot);
        }

        if (ImGui.Button("Dump Live CE States"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=dump-live-ce-states");
            DumpLiveCeStates(snapshot);
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

    private unsafe void DumpCeBossCandidates(ScannerSnapshot snapshot)
    {
        var currentCe = snapshot.CurrentCriticalEncounter;
        var selectedCe = snapshot.SelectedCriticalEncounter;
        var player = Plugin.ObjectTable.LocalPlayer;
        var playerPosition = player?.Position;
        var currentTarget = Plugin.TargetManager.Target;

        plugin.Logger.Info(
            $"[DebugWindow] op=ce-boss-dump territory={snapshot.TerritoryTypeId} " +
            $"currentCeId={snapshot.CurrentCriticalEncounterId} currentCe=\"{currentCe?.Name ?? "none"}\" " +
            $"currentCeState=\"{currentCe?.State ?? "none"}\" selectedCe=\"{selectedCe?.Name ?? "none"}\"");

        if (currentTarget == null)
        {
            plugin.Logger.Info("[DebugWindow] op=ce-boss-dump-current-target available=false reason=no-target");
        }
        else
        {
            plugin.Logger.Info($"[DebugWindow] op=ce-boss-dump-current-target {FormatCeCandidate(currentTarget, playerPosition)}");
        }

        var candidates = Plugin.ObjectTable
            .Where(gameObject => gameObject is IBattleNpc battleNpc
                && battleNpc is ICharacter character
                && character.IsValid()
                && character.IsTargetable
                && character.CurrentHp > 0
                && playerPosition.HasValue
                && CalculateFlatDistance(playerPosition.Value, character.Position) <= 100f)
            .OrderBy(gameObject => playerPosition.HasValue
                ? CalculateFlatDistance(playerPosition.Value, gameObject.Position)
                : float.MaxValue)
            .ToArray();

        plugin.Logger.Info($"[DebugWindow] op=ce-boss-dump-candidates count={candidates.Length} radius=100");
        foreach (var candidate in candidates)
        {
            plugin.Logger.Info($"[DebugWindow] op=ce-boss-dump-candidate {FormatCeCandidate(candidate, playerPosition)}");
        }

        Plugin.ChatGui.Print($"CE boss candidate dump logged ({candidates.Length} nearby candidates).");
    }

    private void DumpLiveCeStates(ScannerSnapshot snapshot)
    {
        var selectedCeId = snapshot.SelectedCriticalEncounter?.Id ?? 0;
        var encounters = snapshot.CriticalEncounters
            .Concat(snapshot.UnknownCriticalEncounters)
            .OrderBy(encounter => encounter.Id)
            .ThenBy(encounter => encounter.Name, StringComparer.Ordinal);

        plugin.Logger.Info(
            $"[DebugWindow] op=live-ce-state-dump territory={snapshot.TerritoryTypeId} " +
            $"snapshotAt={snapshot.LastUpdated:O} currentCeId={snapshot.CurrentCriticalEncounterId} " +
            $"automationState={criticalEngagementAutomationController.State} " +
            $"lockedCeId={criticalEngagementAutomationController.TargetCeId}");

        var count = 0;
        foreach (var encounter in encounters)
        {
            count++;
            plugin.Logger.Info(
                $"[DebugWindow] op=live-ce-state-dump-entry id={encounter.Id} " +
                $"name=\"{encounter.Name}\" state=\"{encounter.State}\" stateCode={encounter.StateCode} " +
                $"progress={encounter.Progress} candidate={encounter.IsCandidate} " +
                $"knownMetadata={encounter.HasKnownMetadata} current={encounter.Id == snapshot.CurrentCriticalEncounterId} " +
                $"selected={encounter.Id == selectedCeId}");
        }

        if (count == 0)
        {
            plugin.Logger.Info("[DebugWindow] op=live-ce-state-dump-entry count=0");
        }
    }

    private static unsafe string FormatCeCandidate(IGameObject gameObject, Vector3? playerPosition)
    {
        var character = gameObject as ICharacter;
        var characterPointer = character != null ? (Character*)character.Address : null;
        var isCharacter = characterPointer != null && characterPointer->VirtualTable != null;
        var battleCharaPointer = gameObject is IBattleNpc && isCharacter ? (BattleChara*)characterPointer : null;
        var fateId = isCharacter ? characterPointer->FateId : (ushort)0;
        var normalLevel = isCharacter ? characterPointer->Level : -1;
        var forayInfoAvailable = false;
        var forayLevel = -1;
        var forayElement = -1;

        if (isCharacter)
        {
            var forayInfo = characterPointer->GetForayInfo();
            if (forayInfo != null)
            {
                forayInfoAvailable = true;
                forayLevel = forayInfo->Level;
                forayElement = forayInfo->Element;
            }
            else if (battleCharaPointer != null)
            {
                forayInfoAvailable = true;
                forayLevel = battleCharaPointer->ForayInfo.Level;
                forayElement = battleCharaPointer->ForayInfo.Element;
            }
        }

        var distance = playerPosition.HasValue
            ? CalculateFlatDistance(playerPosition.Value, gameObject.Position)
            : float.NaN;
        var hp = character?.CurrentHp ?? 0;
        var maxHp = character?.MaxHp ?? 0;
        var hostile = character != null && IsHostile(character);

        return $"name=\"{gameObject.Name}\" kind={gameObject.ObjectKind} objectId={gameObject.GameObjectId:X} " +
            $"dataId={gameObject.BaseId} objectIndex={gameObject.ObjectIndex} hp={hp}/{maxHp} " +
            $"pos=<{gameObject.Position.X:0.00},{gameObject.Position.Y:0.00},{gameObject.Position.Z:0.00}> " +
            $"distance={(float.IsNaN(distance) ? "n/a" : distance.ToString("0.00", CultureInfo.InvariantCulture))} " +
            $"valid={gameObject.IsValid()} targetable={gameObject.IsTargetable} hostile={hostile} " +
            $"isCharacter={isCharacter} isBattleNpc={gameObject is IBattleNpc} normalLevel={normalLevel} fateId={fateId} " +
            $"forayInfo={forayInfoAvailable} forayLevel={forayLevel} forayElement={forayElement}";
    }

    private static unsafe bool IsHostile(ICharacter character)
    {
        var nativeCharacter = (Character*)character.Address;
        return nativeCharacter != null && nativeCharacter->IsHostile;
    }

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private void DrawFates(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("FATEs");

        if (!snapshot.IsInSupportedTerritory)
        {
            ImGui.TextUnformatted("Not in a supported Occult Crescent territory.");
            return;
        }

        if (snapshot.Fates.Count == 0 && snapshot.PotFates.Count == 0)
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

        foreach (var fate in snapshot.PotFates)
        {
            ImGui.TextWrapped($"- [Pot] {fate.Name} ({fate.Id})");
            ImGui.TextWrapped($"  [{fate.State}] {fate.Progress}% | Distance: {FormatDistance(fate.DistanceToPlayer)} | Radius: {fate.Radius:0.0} | Pos: {FormatVector3(fate.Position)}");
        }
    }

    private static void DrawSelectedTarget(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("Selected Target");

        if (!snapshot.IsInSupportedTerritory)
        {
            ImGui.TextUnformatted("No target selection outside a supported Occult Crescent territory.");
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
        var dependencyBlocked = !plugin.GetNormalAutomationDependencyReport().IsReady;
        var canStart = snapshot.IsInSupportedTerritory
            && snapshot.CanFarmCriticalEncounters
            && snapshot.EffectiveTarget.Kind == SelectedTargetKind.CriticalEncounter
            && !otherAutomationRunning
            && !configuration.ScannerOnlyMode
            && !dependencyBlocked;

        if (DrawDependencyAwareStartButton("Start CE Automation", canStart, dependencyBlocked))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=start-ce-automation");
            criticalEngagementAutomationController.Start();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop CE Automation"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=stop-ce-automation");
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
        else if (dependencyBlocked)
        {
            ImGui.TextWrapped(plugin.GetNormalAutomationDependencyReport().FailureSummary);
        }
        else if (!canStart)
        {
            ImGui.TextUnformatted(snapshot.IsInSupportedTerritory && !snapshot.CanFarmCriticalEncounters
                ? $"CE data is unavailable in {snapshot.TerritoryDisplayName}."
                : "Start CE Automation requires a CE-capable territory and a CE effective target.");
        }
    }

    private void DrawAutorotation()
    {
        ImGui.TextUnformatted("Autorotation");
        ImGui.TextUnformatted($"BossMod: {(autorotationController.BossModAvailable ? "Available" : "Unavailable")}");
        ImGui.TextWrapped($"Configured Provider: {autorotationController.ConfiguredProviderName}");
        ImGui.TextWrapped($"Effective Provider: {autorotationController.EffectiveProviderName}");
        ImGui.TextWrapped($"Override Preset: {FormatPreset(autorotationController.ConfiguredPreset)}");
        ImGui.TextWrapped($"Managed Preset: {FormatPreset(autorotationController.ManagedPreset)}");
        ImGui.TextWrapped($"Passive Preset: {FormatPreset(autorotationController.PassiveManagedPreset)}");
        ImGui.TextWrapped($"Selected Source: {autorotationController.SelectedSource}");
        ImGui.TextWrapped($"Selected Role: {autorotationController.SelectedRole}");
        ImGui.TextWrapped($"Selected Range: {autorotationController.SelectedRange:0.0#}");
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

        if (ImGui.Button("Test Rotation Plugin IPCs"))
        {
            var availableProviders = AutorotationProviderDiscovery.GetAvailable();
            var bossModLoaded = availableProviders.Contains(AutorotationProvider.BossMod)
                || availableProviders.Contains(AutorotationProvider.BossModReborn);
            var rsrLoaded = availableProviders.Contains(AutorotationProvider.RSR);
            var wrathLoaded = availableProviders.Contains(AutorotationProvider.Wrath);
            var bossMod = bossModLoaded && autorotationController.RefreshBossModAvailability();
            var rsr = rsrLoaded ? plugin.RotationSolverReborn.Test().ToString() : "skipped-unloaded";
            var wrath = wrathLoaded ? plugin.WrathCombo.Test().ToString() : "skipped-unloaded";
            plugin.Logger.Info($"[DebugWindow] op=rotation-ipc-test bossMod={(bossModLoaded ? bossMod.ToString() : "skipped-unloaded")} rsr={rsr} wrath={wrath}");
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (configuration.ShowTooltips && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Probes BossMod preset IPC, RSR Test IPC, and Wrath IPCReady/Test without changing rotation state.");
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
        var dependencyBlocked = !plugin.GetNormalAutomationDependencyReport().IsReady;
        var canStart = snapshot.IsInSupportedTerritory
            && snapshot.CanFarmFates
            && snapshot.EffectiveTarget.Kind == SelectedTargetKind.Fate
            && !otherAutomationRunning
            && !configuration.ScannerOnlyMode
            && !dependencyBlocked;

        if (DrawDependencyAwareStartButton("Start FATE Automation", canStart, dependencyBlocked))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=start-fate-automation");
            fateAutomationController.Start();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop FATE Automation"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=stop-fate-automation");
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
        else if (dependencyBlocked)
        {
            ImGui.TextWrapped(plugin.GetNormalAutomationDependencyReport().FailureSummary);
        }
        else if (!canStart)
        {
            ImGui.TextUnformatted(snapshot.IsInSupportedTerritory && !snapshot.CanFarmFates
                ? $"FATE data is unavailable in {snapshot.TerritoryDisplayName}."
                : "Start FATE Automation requires a FATE-capable territory and a FATE effective target.");
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
        var canStart = snapshot.IsInSupportedTerritory && snapshot.CanRunBuffRotation && !otherAutomationRunning && !buffRotationController.IsRunning;
        canStart = canStart && !configuration.ScannerOnlyMode;

        ImGui.BeginDisabled(!canStart);
        if (ImGui.Button("Run Buff Rotation"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=run-buff-rotation");
            buffRotationController.Start("manual UI");
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Stop Buff Rotation"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=stop-buff-rotation");
            buffRotationController.Stop("Manual buff rotation stop requested.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Restore Support Job"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=restore-support-job");
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
        else if (!snapshot.IsInSupportedTerritory)
        {
            ImGui.TextUnformatted("Buff rotation requires a supported Occult Crescent territory.");
        }
        else if (!snapshot.CanRunBuffRotation)
        {
            ImGui.TextUnformatted($"Buff rotation is unavailable in {snapshot.TerritoryDisplayName}.");
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
        var automaticCofferStatus = farmSessionController.AutomaticTreasureCofferStatus;
        var cofferSurveySnapshot = plugin.TreasureHintTracker.CofferSurveySnapshot;

        ImGui.TextUnformatted("Farm Session");
        ImGui.TextUnformatted($"State: {farmSessionController.State}");
        ImGui.TextWrapped($"Activity: {FormatValue(farmSessionController.CurrentActivity)}");
        ImGui.TextWrapped($"Last Transition: {farmSessionController.LastTransition}");
        ImGui.TextWrapped($"Auto Coffer Status: {FormatValue(automaticCofferStatus.LastTransition)}");
        ImGui.TextWrapped($"Auto Coffer Survey: {(cofferSurveySnapshot.HasSurvey ? $"silver={cofferSurveySnapshot.SilverCount} bronze={cofferSurveySnapshot.BronzeCount} @ {FormatTimestamp(cofferSurveySnapshot.ReceivedAt)}" : "None")}");
        ImGui.TextWrapped($"Auto Coffer Rescan: silver={automaticCofferStatus.RemainingSilverCompletionsUntilRescan} bronze={automaticCofferStatus.RemainingBronzeCompletionsUntilRescan}");
        ImGui.TextWrapped($"Auto Coffer Disabled For Run: {(automaticCofferStatus.DisabledForCurrentRun ? "Yes" : "No")}");
        ImGui.TextWrapped($"Auto Coffer Restore Retry: {(automaticCofferStatus.RestoreRetryPending ? "Yes" : "No")}");

        if (!string.IsNullOrEmpty(farmSessionController.LastError))
        {
            ImGui.TextWrapped($"Last Error: {farmSessionController.LastError}");
        }

        var farmStartBlocker = GetFarmStartBlocker();
        var dependencyBlocked = !plugin.GetNormalAutomationDependencyReport().IsReady;
        if (DrawDependencyAwareStartButton("Start Farm", farmStartBlocker == null, dependencyBlocked))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=start-farm");
            farmSessionController.Start();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop Farm"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=stop-farm");
            farmSessionController.Stop("Manual farm session stop requested.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Panic Stop"))
        {
            plugin.Logger.Warning("[DebugWindow] op=ui-action action=panic-stop");
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
        var ceDecision = plugin.PotFallbackWindowEvaluator.EvaluateCeStart(potCycleSnapshot, now, snapshot.CanRunPotTreasure, snapshot.TerritoryKey);
        var fateDecision = plugin.PotFallbackWindowEvaluator.EvaluateFateStart(potCycleSnapshot, now, snapshot.CanRunPotTreasure, snapshot.TerritoryKey);
        var departureAt = GetDepartureAt(potCycleSnapshot);
        var timeUntilDeparture = departureAt == DateTimeOffset.MinValue ? (TimeSpan?)null : departureAt - now;

        ImGui.TextUnformatted("Pot Control");
        ImGui.TextUnformatted($"Territory: {FormatValue(snapshot.TerritoryKey)}");
        ImGui.TextUnformatted($"Enabled: {(configuration.IsPotFarmingEnabled(snapshot.TerritoryKey) ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Treasure Capability: {(snapshot.CanRunPotTreasure ? "Ready" : "Unavailable")}");
        var maximumKnowledgeLevel = plugin.Scanner.ActiveTerritoryData?.MaximumKnowledgeLevel ?? 28;
        var potAggroCutoff = snapshot.PlayerForayLevel.HasValue
            ? Math.Clamp(snapshot.PlayerForayLevel.Value + configuration.PotTreasureAggroLevelOffset, 1, maximumKnowledgeLevel)
            : configuration.PotTreasureFallbackMaximumAggroLevel;
        ImGui.TextUnformatted($"No-Ninja Aggro Cutoff: {potAggroCutoff} ({(snapshot.PlayerForayLevel.HasValue ? "Knowledge offset" : "fallback")})");
        ImGui.TextUnformatted($"Farm State: {potFarmController.State}");
        ImGui.TextWrapped($"Farm Transition: {potFarmController.LastTransition}");
        ImGui.TextWrapped($"Current Pot: {FormatValue(potFarmController.CurrentPotName)}");
        ImGui.TextUnformatted($"Active Pot: {FormatValue(snapshot.ActivePotFate?.Name)}");
        ImGui.TextUnformatted($"Known Anchor: {(potCycleSnapshot.HasKnownAnchor ? "Yes" : "No")}");
        ImGui.TextWrapped($"Last Anchor: {FormatValue(potCycleSnapshot.LastObservedPotFateName)} @ {FormatTimestamp(potCycleSnapshot.LastObservedSpawnAt)}");
        ImGui.TextWrapped($"Predicted Next Pot: {FormatValue(potCycleSnapshot.PredictedNextPotFateName)} @ {FormatTimestamp(potCycleSnapshot.PredictedNextSpawnAt)}");
        ImGui.TextWrapped($"Time Until Departure: {FormatTimeSpan(timeUntilDeparture)}");
        ImGui.TextWrapped($"Spawn Wait Deadline: {FormatTimestamp(potFarmController.WaitDeadlineAt)}");
        var instanceTimeDecision = potFarmController.LastInstanceTimeDecision;
        ImGui.TextUnformatted($"Instance Timer Available: {(instanceTimeDecision.IsContentTimerAvailable ? "Yes" : "No")}");
        ImGui.TextWrapped($"Instance Time Remaining: {FormatTimeSpan(instanceTimeDecision.IsContentTimerAvailable ? TimeSpan.FromSeconds(instanceTimeDecision.RemainingSeconds) : null)}");
        ImGui.TextWrapped($"Instance Time Next Pot Wait: {FormatTimeSpan(instanceTimeDecision.IsContentTimerAvailable ? TimeSpan.FromSeconds(instanceTimeDecision.WaitSecondsUntilNextPot) : null)}");
        ImGui.TextWrapped($"Instance Time Required: {FormatTimeSpan(instanceTimeDecision.IsContentTimerAvailable ? TimeSpan.FromSeconds(instanceTimeDecision.RequiredSeconds) : null)}");
        ImGui.TextWrapped($"Instance Time Source: {FormatValue(instanceTimeDecision.TimingSource)}");
        ImGui.TextUnformatted($"Instance Time Allow Next Cycle: {(instanceTimeDecision.AllowNextPotCycle ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Instance Time Can Leave: {(instanceTimeDecision.CanLeaveCurrentContent ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Leave Pending: {(potFarmController.IsLeavePending ? "Yes" : "No")}");
        ImGui.TextWrapped($"Leave Requested At: {FormatTimestamp(potFarmController.LeaveRequestedAt)}");
        ImGui.TextWrapped($"Instance Time Decision: {FormatValue(instanceTimeDecision.Reason)}");
        ImGui.TextUnformatted($"CE Fallback Allowed: {(ceDecision.AllowStart ? "Yes" : "No")}");
        ImGui.TextWrapped($"CE Fallback: {ceDecision.Reason}");
        ImGui.TextUnformatted($"FATE Fallback Allowed: {(fateDecision.AllowStart ? "Yes" : "No")}");
        ImGui.TextWrapped($"FATE Fallback: {fateDecision.Reason}");

        var treasureSnapshot = plugin.TreasureHintTracker.Snapshot;
        var cofferSurveySnapshot = plugin.TreasureHintTracker.CofferSurveySnapshot;
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
        ImGui.TextWrapped($"Treasure Coffer Survey: {(cofferSurveySnapshot.HasSurvey ? $"revision={cofferSurveySnapshot.Revision} silver={cofferSurveySnapshot.SilverCount} bronze={cofferSurveySnapshot.BronzeCount} @ {FormatTimestamp(cofferSurveySnapshot.ReceivedAt)}" : "None")}");

        var treasureSearch = plugin.TreasureSearchController;
        ImGui.TextUnformatted($"Treasure Search State: {treasureSearch.State}");
        ImGui.TextWrapped($"Treasure Search Transition: {treasureSearch.LastTransition}");
        ImGui.TextWrapped($"Treasure Search Group: {FormatValue(treasureSearch.ActiveGroupKey)}");
        ImGui.TextWrapped($"Treasure Search Candidate: {FormatValue(treasureSearch.ActiveCandidateKey?.Label)}");
        ImGui.TextWrapped($"Treasure Search Candidate Key: {FormatValue(treasureSearch.ActiveCandidateKey?.ToString())}");
        ImGui.TextWrapped($"Treasure Search Candidate Index: {treasureSearch.CurrentCandidateIndex}");
        ImGui.TextWrapped($"Treasure Search Ordered Candidates: {FormatStringList(treasureSearch.OrderedCandidateLabels)}");
        ImGui.TextWrapped($"Treasure Search Handled Candidates: {FormatStringList(treasureSearch.HandledCandidateLabels)}");
        ImGui.TextWrapped($"Treasure Search Position Source: {FormatPositionSource(treasureSearch.ActiveCandidateKey, treasureSearch.ActiveCandidateUsesOverride)}");
        ImGui.TextWrapped($"Treasure Search Resolved Position: {FormatResolvedPosition(treasureSearch.ActiveCandidateKey, treasureSearch.ActiveCandidateResolvedPosition)}");
        ImGui.TextWrapped($"Treasure Search Handoff: {FormatValue(treasureSearch.LastHandoffReason)}");
        ImGui.TextWrapped($"Coffer Override Count: {plugin.CofferPositionOverrideStore.Count}");
        ImGui.TextWrapped($"Last Saved Override: {FormatOverride(plugin.CofferPositionOverrideStore.LastSavedOverride)}");
        var visibleMatch = treasureSearch.ActiveVisibleCofferMatch;
        var visibleMatchText = visibleMatch == null
            ? null
            : $"{visibleMatch.CandidateKey.Label} <- {visibleMatch.Coffer.Name} ({visibleMatch.MatchDistance:0.0}y) | trusted={(visibleMatch.IsTrustworthy ? "yes" : "no")} | nearestOther={(visibleMatch.DistanceToNearestOtherCandidate == float.MaxValue ? "none" : $"{visibleMatch.DistanceToNearestOtherCandidate:0.0}y")} | dataId={visibleMatch.Coffer.DataId} | pos={FormatVector3(visibleMatch.Coffer.Position)} | {visibleMatch.AttributionReason}";
        ImGui.TextWrapped($"Treasure Visible Match: {FormatValue(visibleMatchText)}");

        var cofferInteraction = plugin.CofferInteractionController;
        ImGui.TextUnformatted($"Coffer Interaction State: {cofferInteraction.State}");
        ImGui.TextWrapped($"Coffer Interaction Transition: {cofferInteraction.LastTransition}");
        ImGui.TextWrapped($"Coffer Interaction Attempts: {cofferInteraction.InteractionAttemptCount}");
        ImGui.TextWrapped($"Coffer Interaction Confirmation Deadline: {FormatTimestamp(cofferInteraction.ConfirmationDeadlineAt)}");
        var activeInteractionMatch = cofferInteraction.ActiveMatch;
        var activeInteractionMatchText = activeInteractionMatch == null
            ? null
            : $"{activeInteractionMatch.CandidateKey.Label} <- {activeInteractionMatch.Coffer.Name} ({activeInteractionMatch.Coffer.GameObjectId:X}) | trusted={(activeInteractionMatch.IsTrustworthy ? "yes" : "no")} | dataId={activeInteractionMatch.Coffer.DataId} | pos={FormatVector3(activeInteractionMatch.Coffer.Position)}";
        ImGui.TextWrapped($"Coffer Interaction Match: {FormatValue(activeInteractionMatchText)}");

        if (!string.IsNullOrEmpty(potFarmController.LastError))
        {
            ImGui.TextWrapped($"Last Error: {potFarmController.LastError}");
        }
    }

    private void DrawDangerousTreasureTravel()
    {
        ImGui.TextUnformatted("Dangerous Treasure Travel");
        ImGui.TextUnformatted($"State: {dangerousTreasureTravelController.State}");
        ImGui.TextWrapped($"Active Candidate: {FormatValue(dangerousTreasureTravelController.ActiveCandidateLabel)}");
        ImGui.TextWrapped($"Previous Candidate: {FormatValue(dangerousTreasureTravelController.PreviousCandidateLabel)}");
        ImGui.TextUnformatted($"Ninja Gearset Equipped By Controller: {(dangerousTreasureTravelController.HasEquippedNinjaGearset ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Current Class Job: {gameActionController.CurrentClassJobId}");
        ImGui.TextUnformatted($"Hide Available: {(gameActionController.CanUseHide() ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Stealthed: {(gameActionController.IsStealthed ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Walking Phase: {dangerousTreasureTravelController.ActiveWalkingPhase}");
        ImGui.TextUnformatted($"Pending Hidden Move: {dangerousTreasureTravelController.PendingHiddenMovePhase}");
        ImGui.TextUnformatted($"FATE Gearset Restore Pending: {(dangerousTreasureTravelController.IsFateGearsetRestorePending ? "Yes" : "No")}");
        ImGui.TextUnformatted($"FATE Gearset Restore In Progress: {(dangerousTreasureTravelController.IsFateGearsetRestoreInProgress ? "Yes" : "No")}");
        ImGui.TextUnformatted($"FATE Gearset Restore Attempts: {dangerousTreasureTravelController.FateGearsetRestoreAttemptCount}");
        ImGui.TextWrapped($"FATE Gearset Restore Target: {(dangerousTreasureTravelController.PendingFateGearsetNumber <= 0 ? "None" : $"{dangerousTreasureTravelController.PendingFateGearsetNumber} / {FormatValue(dangerousTreasureTravelController.PendingFateGearsetName)} / ClassJob {dangerousTreasureTravelController.PendingFateGearsetTargetClassJobId}")}");
        ImGui.TextWrapped($"FATE Gearset Restore Reason: {FormatValue(dangerousTreasureTravelController.LastFateGearsetRestoreReason)}");
        ImGui.TextWrapped($"FATE Gearset Restore Requested At: {FormatTimestamp(dangerousTreasureTravelController.FateGearsetRestoreRequestedAt)}");
        ImGui.TextWrapped($"FATE Gearset Restore Next Attempt: {FormatTimestamp(dangerousTreasureTravelController.FateGearsetRestoreAttemptAvailableAt)}");
        ImGui.TextWrapped($"Pending Hidden Destination: {FormatDangerousPendingDestination(dangerousTreasureTravelController.PendingHiddenMovePhase, dangerousTreasureTravelController.PendingHiddenMoveDestination)}");
        ImGui.TextWrapped($"Pending Hidden Arrival Tolerance: {FormatDangerousPendingArrivalTolerance(dangerousTreasureTravelController.PendingHiddenMovePhase, dangerousTreasureTravelController.PendingHiddenMoveArrivalTolerance)}");
        ImGui.TextWrapped($"Last Transition: {dangerousTreasureTravelController.LastTransition}");

        if (!string.IsNullOrEmpty(dangerousTreasureTravelController.LastError))
        {
            ImGui.TextWrapped($"Last Error: {dangerousTreasureTravelController.LastError}");
        }

        if (!string.IsNullOrEmpty(dangerousTreasureTravelController.LastFateGearsetRestoreError))
        {
            ImGui.TextWrapped($"FATE Gearset Restore Error: {dangerousTreasureTravelController.LastFateGearsetRestoreError}");
        }
    }

    private void DrawVisibleCofferFarm()
    {
        var routeEntries = plugin.Scanner.ActiveTerritoryData?.VisibleCofferFarmRoute ?? [];
        var routeSpots = plugin.Scanner.ActiveTerritoryData?.VisibleCofferFarmSpots ?? [];

        ImGui.TextUnformatted("Overworld Coffer Route");
        ImGui.TextUnformatted($"State: {treasureCofferFarmController.State}");
        ImGui.TextWrapped($"Last Transition: {treasureCofferFarmController.LastTransition}");
        ImGui.TextWrapped($"Current Route Index: {treasureCofferFarmController.CurrentRouteIndex}");

        if (routeEntries.Count > 0)
        {
            selectedVisibleCofferRouteStartIndex = Math.Clamp(selectedVisibleCofferRouteStartIndex, 0, routeEntries.Count - 1);
            ImGui.SetNextItemWidth(420f);
            if (ImGui.BeginCombo("Start Candidate", GetVisibleCofferRouteEntryLabel(routeEntries, routeSpots, selectedVisibleCofferRouteStartIndex)))
            {
                for (var index = 0; index < routeEntries.Count; index++)
                {
                    var isSelected = index == selectedVisibleCofferRouteStartIndex;
                    if (ImGui.Selectable(GetVisibleCofferRouteEntryLabel(routeEntries, routeSpots, index), isSelected))
                    {
                        selectedVisibleCofferRouteStartIndex = index;
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            var startBlocker = GetVisibleCofferStartBlocker();
            var dependencyBlocked = !plugin.GetNormalAutomationDependencyReport().IsReady;
            if (DrawDependencyAwareStartButton("Start Route At Candidate", startBlocker == null, dependencyBlocked))
            {
                var selectedRouteEntry = routeEntries[selectedVisibleCofferRouteStartIndex];
                plugin.Logger.Info($"[DebugWindow] op=ui-action action=start-coffer-route startIndex={selectedVisibleCofferRouteStartIndex} startSpot={selectedRouteEntry.Area}:{selectedRouteEntry.Label}");
                treasureCofferFarmController.Start(startRouteIndex: selectedVisibleCofferRouteStartIndex);
            }

            ImGui.SameLine();
            if (ImGui.Button("Stop Route"))
            {
                plugin.Logger.Info("[DebugWindow] op=ui-action action=stop-coffer-route");
                treasureCofferFarmController.Stop("Manual debug overworld coffer route stop requested.");
            }

            if (startBlocker != null)
            {
                ImGui.TextWrapped(startBlocker);
            }
        }
        else
        {
            ImGui.TextWrapped("Overworld coffer route data is missing route entries.");
        }

        var activeSpot = treasureCofferFarmController.ActiveSpot;
        var activeLabel = activeSpot == null ? null : $"{activeSpot.Area}:{activeSpot.Label}";
        ImGui.TextWrapped($"Active Spot: {FormatValue(activeLabel)}");
        ImGui.TextWrapped($"Resolved Position: {(activeSpot == null ? "None" : FormatVector3(treasureCofferFarmController.ActiveResolvedPosition))}");
        ImGui.TextWrapped($"Position Source: {(activeSpot == null ? "None" : (treasureCofferFarmController.ActiveSpotUsesOverride ? "Override" : "Canonical"))}");

        var overrideEntry = activeSpot == null
            ? null
            : plugin.VisibleCofferPositionOverrideStore.TryGetOverride(scanner.Snapshot.TerritoryKey, activeSpot.Area, activeSpot.Label);
        ImGui.TextWrapped($"Active Override: {FormatVisibleCofferOverride(overrideEntry)}");
        ImGui.TextWrapped($"Overworld Override Count: {plugin.VisibleCofferPositionOverrideStore.Count}");
        ImGui.TextWrapped($"Last Saved Override: {FormatVisibleCofferOverride(plugin.VisibleCofferPositionOverrideStore.LastSavedOverride)}");

        var lastMatched = treasureCofferFarmController.LastMatchedCoffer;
        var lastMatchedText = lastMatched == null
            ? null
            : $"{lastMatched.Name} ({lastMatched.DataId}) | pos={FormatVector3(lastMatched.Position)} | object={lastMatched.GameObjectId:X}";
        ImGui.TextWrapped($"Last Matched Coffer: {FormatValue(lastMatchedText)}");

        ImGui.TextUnformatted($"Visible Coffers In Scanner: {scanner.Snapshot.VisibleCoffers.Count}");
        foreach (var visibleCoffer in scanner.Snapshot.VisibleCoffers.Take(8))
        {
            ImGui.TextWrapped($"{visibleCoffer.Name} ({visibleCoffer.GameObjectId:X}) recognition={visibleCoffer.RecognitionSource} kind={visibleCoffer.ObjectKind} baseId={visibleCoffer.DataId} distance={visibleCoffer.DistanceToPlayer:0.0}y targetable={visibleCoffer.IsTargetable} pos={FormatVector3(visibleCoffer.Position)}");
        }

        if (!string.IsNullOrEmpty(treasureCofferFarmController.LastError))
        {
            ImGui.TextWrapped($"Last Error: {treasureCofferFarmController.LastError}");
        }
    }

    private void DrawMovement(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("Movement");
        ImGui.TextUnformatted($"vnavmesh: {movementController.VNavmeshStatusText}");
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
            plugin.Logger.Info("[DebugWindow] op=ui-action action=plan-route");
            movementController.PlanRouteToSelectedTarget();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(scannerOnlyMode);
        if (ImGui.Button("Start Route"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=start-route");
            movementController.StartPlannedRoute();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Stop Movement"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=stop-movement");
            movementController.Stop("Manual stop requested.");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(scannerOnlyMode);
        if (ImGui.Button("Recover To Base Camp"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=recover-to-base-camp");
            movementController.RecoverToBaseCamp();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(scannerOnlyMode || !movementController.CanUseReturnAction);
        if (ImGui.Button("Test Return Recovery"))
        {
            plugin.Logger.Info("[DebugWindow] op=ui-action action=test-return-recovery");
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
        var bossModRequired = plugin.GetNormalAutomationDependencyReport().Statuses.Any(status => status.Key == "rotation" && status.Required);
        var bossModAvailable = autorotationController.RefreshBossModAvailability();
        var scannerOnlyMode = configuration.ScannerOnlyMode;

        ImGui.TextUnformatted("Safety");
        if (ImGui.Checkbox("Scanner-Only Mode", ref scannerOnlyMode))
        {
            plugin.Logger.Info($"[DebugWindow] op=setting-change key=ScannerOnlyMode old={configuration.ScannerOnlyMode} new={scannerOnlyMode}");
            configuration.ScannerOnlyMode = scannerOnlyMode;
            configuration.Save();
        }

        ImGui.TextWrapped("Scanner-only mode keeps scanning and target selection active while blocking movement, combat automation, and buff rotation starts.");
        ImGui.TextUnformatted($"Scanner-Only Mode: {(configuration.ScannerOnlyMode ? "Enabled" : "Disabled")}");
        ImGui.TextUnformatted($"vnavmesh: {movementController.VNavmeshStatusText}");
        ImGui.TextUnformatted($"Return Action: {(movementController.CanUseReturnAction ? "Available" : "Unavailable")}");
        ImGui.TextUnformatted($"BossMod: {FormatBossModStatus(bossModRequired, bossModAvailable)}");
        ImGui.TextUnformatted($"Farm Running: {(farmSessionController.IsRunning ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Movement State: {movementController.State}");

        if (configuration.ScannerOnlyMode)
        {
            ImGui.TextWrapped("Automation start requests are blocked while scanner-only mode is enabled.");
        }

        if (!snapshot.IsInSupportedTerritory)
        {
            ImGui.TextUnformatted("Automation is currently outside a supported Occult Crescent territory.");
        }
    }

    private void DrawTestReadiness(ScannerSnapshot snapshot)
    {
        ImGui.TextUnformatted("Automation Test Readiness");
        ImGui.TextUnformatted($"Supported Territory: {(snapshot.IsInSupportedTerritory ? "Ready" : "Blocked")}");
        ImGui.TextUnformatted($"Return Required: {(configuration.UseReturn ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Return Available: {(movementController.CanUseReturnAction ? "Yes" : "No")}");
        ImGui.TextUnformatted($"Farm Start: {FormatValue(GetFarmStartBlocker() ?? "Ready for full farm test")}");
    }

    private bool DrawDependencyAwareStartButton(string label, bool enabled, bool dependencyBlocked)
    {
        var clicked = false;
        if (!enabled && !dependencyBlocked)
        {
            ImGui.BeginDisabled();
        }

        if (dependencyBlocked)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.FrameBg));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(ImGuiCol.FrameBg));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGui.GetColorU32(ImGuiCol.FrameBg));
        }

        clicked = ImGui.Button(label);

        if (dependencyBlocked)
        {
            ImGui.PopStyleColor(4);
        }

        if (!enabled && !dependencyBlocked)
        {
            ImGui.EndDisabled();
        }

        if (dependencyBlocked && clicked)
        {
            plugin.OpenDependencyUi();
        }

        return enabled && clicked;
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

        var dependencyReport = plugin.GetNormalAutomationDependencyReport();
        return dependencyReport.IsReady ? null : dependencyReport.FailureSummary;
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

        if (criticalEngagementAutomationController.IsRunning || fateAutomationController.IsRunning || buffRotationController.IsRunning || potFarmController.IsRunning || treasureCofferFarmController.IsRunning)
        {
            return "Stop CE/FATE automation, pot control, buff rotation, and overworld coffer routing before starting the farm session.";
        }

        var dependencyReport = plugin.GetNormalAutomationDependencyReport();
        if (!dependencyReport.IsReady)
        {
            return dependencyReport.FailureSummary;
        }

        if (configuration.UseReturn && !movementController.CanUseReturnAction)
        {
            return "Farm session start requires the Return general action when Use Return is enabled.";
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

    private static string FormatTimeSpan(TimeSpan? timeSpan)
    {
        if (!timeSpan.HasValue)
        {
            return "Unknown";
        }

        var value = timeSpan.Value;
        if (value >= TimeSpan.Zero)
        {
            return value.ToString("mm\\:ss", CultureInfo.InvariantCulture);
        }

        return $"-{(-value).ToString("mm\\:ss", CultureInfo.InvariantCulture)}";
    }

    private static string FormatPreset(string preset)
        => string.IsNullOrEmpty(preset) ? "None" : preset;

    private static string FormatValue(string? value)
        => string.IsNullOrEmpty(value) ? "None" : value;

    private static string FormatDangerousPendingDestination(DangerousTreasureWalkingPhase phase, Vector3 destination)
        => phase == DangerousTreasureWalkingPhase.None ? "None" : FormatVector3(destination);

    private static string FormatDangerousPendingArrivalTolerance(DangerousTreasureWalkingPhase phase, float arrivalTolerance)
        => phase == DangerousTreasureWalkingPhase.None ? "None" : arrivalTolerance.ToString("0.0", CultureInfo.InvariantCulture);

    private static string FormatStringList(System.Collections.Generic.IReadOnlyList<string> values)
        => values.Count == 0 ? "None" : string.Join(", ", values);

    private static string FormatResolvedPosition(TreasureCandidateKey? candidateKey, Vector3 position)
        => candidateKey == null ? "None" : FormatVector3(position);

    private static string FormatPositionSource(TreasureCandidateKey? candidateKey, bool usesOverride)
        => candidateKey == null ? "None" : usesOverride ? "Override" : "Canonical";

    private static string FormatOverride(AOCCH.Data.CofferPositionOverride? entry)
    {
        if (entry == null)
        {
            return "None";
        }

        return $"{entry.FateId}:{entry.GroupKey}:{entry.CandidateKey} | dataId={entry.ObservedDataId} | pos={FormatVector3(entry.ObservedPosition.ToVector3())} | {FormatTimestamp(entry.LastConfirmedAt)}";
    }

    private static string FormatVisibleCofferOverride(AOCCH.Data.VisibleCofferPositionOverride? entry)
    {
        if (entry == null)
        {
            return "None";
        }

        return $"{entry.Area}:{entry.Label} | dataId={entry.ObservedDataId} | pos={FormatVector3(entry.ObservedPosition.ToVector3())} | {FormatTimestamp(entry.LastConfirmedAt)}";
    }

    private DateTimeOffset GetDepartureAt(PotCycleSnapshot snapshot)
    {
        if (!snapshot.HasPredictedNextPot)
        {
            return DateTimeOffset.MinValue;
        }

        return snapshot.PredictedNextSpawnAt - TimeSpan.FromMinutes(Math.Max(0, configuration.SpawnLeadMinutes));
    }

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

    private static string GetVisibleCofferRouteEntryLabel(
        System.Collections.Generic.IReadOnlyList<AOCCH.Data.VisibleCofferFarmRouteEntryData> routeEntries,
        System.Collections.Generic.IReadOnlyList<AOCCH.Data.VisibleCofferFarmSpotData> routeSpots,
        int index)
    {
        var routeEntry = routeEntries[index];
        var label = $"#{index} {routeEntry.Area}:{routeEntry.Label}";
        var spot = routeSpots.FirstOrDefault(candidate => string.Equals(candidate.Area, routeEntry.Area, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Label, routeEntry.Label, StringComparison.OrdinalIgnoreCase));
        if (spot == null)
        {
            return $"{label} | missing spot";
        }

        return $"{label} | aggro {spot.AggroLevel}";
    }

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
