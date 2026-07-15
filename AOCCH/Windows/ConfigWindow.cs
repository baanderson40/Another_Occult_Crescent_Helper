using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Shopping;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public class ConfigWindow : Window, IDisposable
{
    private static readonly string[] CurrencyShopTargetModeLabels = ["Keep", "Buy Amount", "Spend Excess"];
    private static readonly string[] FatePriorityLabels = ["Lowest Progress", "Nearest"];
    private static readonly string[] StartingPotFateLabels = ["Auto", "Persistent Pots (North)", "Pleading Pots (South)"];
    private static readonly TimeSpan SettingTextLogInterval = TimeSpan.FromSeconds(10);
    private const float PotsNumericInputWidth = 60f;
    private const float SettingsNumericInputWidth = 60f;
    private const float SettingsTextInputMinWidth = 120f;
    private const float SettingsTextInputMaxWidth = 240f;

    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly OccultCrescentData data;
    private readonly OccultCrescentNameResolver nameResolver;
    private readonly AocchLogger logger;
    private readonly HashSet<uint> potFateIds;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(
        Plugin plugin,
        Configuration configuration,
        OccultCrescentData data,
        OccultCrescentNameResolver nameResolver,
        AocchLogger logger) : base("AOCCH Configuration###AOCCHConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(620, 360);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        this.configuration = configuration;
        this.data = data;
        this.nameResolver = nameResolver;
        this.logger = logger;
        potFateIds = data.PotFates.Select(potFate => potFate.FateId).ToHashSet();
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("AOCCHConfigTabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Critical Engagements"))
        {
            DrawCriticalEngagementsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("FATEs"))
        {
            DrawFatesTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Pots"))
        {
            DrawPotsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Treasure Coffers"))
        {
            DrawTreasureCoffersTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Settings"))
        {
            DrawSettingsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Shopping"))
        {
            DrawShoppingTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawCriticalEngagementsTab()
    {
        var enableCeFarming = configuration.EnableCriticalEngagementFarming;
        if (ImGui.Checkbox("Enable CE Farming", ref enableCeFarming))
        {
            logger.Info($"[Config] op=setting-change key=EnableCriticalEngagementFarming old={configuration.EnableCriticalEngagementFarming} new={enableCeFarming}");
            configuration.EnableCriticalEngagementFarming = enableCeFarming;
            configuration.Save();
        }

        var prioritizeCe = configuration.PrioritizeCe;
        if (ImGui.Checkbox("Prioritize CE", ref prioritizeCe))
        {
            logger.Info($"[Config] op=setting-change key=PrioritizeCe old={configuration.PrioritizeCe} new={prioritizeCe}");
            configuration.PrioritizeCe = prioritizeCe;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Enabled Critical Engagements");
        DrawCriticalEncounterCheckboxList();
    }

    private void DrawFatesTab()
    {
        var enableFateFarming = configuration.EnableFateFarming;
        if (ImGui.Checkbox("Enable FATE Farming", ref enableFateFarming))
        {
            logger.Info($"[Config] op=setting-change key=EnableFateFarming old={configuration.EnableFateFarming} new={enableFateFarming}");
            configuration.EnableFateFarming = enableFateFarming;
            configuration.Save();
        }

        var fatePriority = (int)configuration.FatePriority;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("FATE Priority", ref fatePriority, FatePriorityLabels, FatePriorityLabels.Length))
        {
            logger.Info($"[Config] op=setting-change key=FatePriority old={configuration.FatePriority} new={(FatePriority)fatePriority}");
            configuration.FatePriority = (FatePriority)fatePriority;
            configuration.Save();
        }

        ImGui.SetNextItemWidth(PotsNumericInputWidth);
        DrawClampedIntSetting(
            "FATE Dismount Distance",
            configuration.FateDismountDistance,
            5,
            50,
            value => configuration.FateDismountDistance = value,
            nameof(configuration.FateDismountDistance));

        ImGui.Separator();
        ImGui.TextUnformatted("Enabled FATEs");
        DrawFateCheckboxList();
    }

    private void DrawPotsTab()
    {
        var enablePotFarming = configuration.EnablePotFarming;
        if (ImGui.Checkbox("Enable Pot Farming", ref enablePotFarming))
        {
            logger.Info($"[Config] op=setting-change key=EnablePotFarming old={configuration.EnablePotFarming} new={enablePotFarming}");
            configuration.EnablePotFarming = enablePotFarming;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Pot Cycle");

        var startingPotFate = (int)configuration.StartingPotFate;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Starting Pot FATE", ref startingPotFate, StartingPotFateLabels, StartingPotFateLabels.Length))
        {
            logger.Info($"[Config] op=setting-change key=StartingPotFate old={configuration.StartingPotFate} new={(StartingPotFateMode)startingPotFate}");
            configuration.StartingPotFate = (StartingPotFateMode)startingPotFate;
            configuration.Save();
        }

        DrawPotsIntSetting(
            "Spawn Lead Minutes",
            configuration.SpawnLeadMinutes,
            0,
            30,
            value => configuration.SpawnLeadMinutes = value,
            nameof(configuration.SpawnLeadMinutes));

        DrawPotsIntSetting(
            "Arrival Radius",
            configuration.SpawnArrivalRadius,
            0,
            100,
            value => configuration.SpawnArrivalRadius = value,
            nameof(configuration.SpawnArrivalRadius));

        ImGui.Separator();
        ImGui.TextUnformatted("Treasure Travel");

        DrawPotsIntSetting(
            "Maximum Aggro Level (Revealed Treasure)",
            configuration.MaximumAggroLevel,
            0,
            20,
            value => configuration.MaximumAggroLevel = value,
            nameof(configuration.MaximumAggroLevel));

        var manageInstanceTime = configuration.ManageInstanceTime;
        var useNinjaForDangerousArea = configuration.UseNinjaForDangerousArea;
        if (ImGui.Checkbox("Use Ninja For Dangerous Area", ref useNinjaForDangerousArea))
        {
            logger.Info($"[Config] op=setting-change key=UseNinjaForDangerousArea old={configuration.UseNinjaForDangerousArea} new={useNinjaForDangerousArea}");
            configuration.UseNinjaForDangerousArea = useNinjaForDangerousArea;
            configuration.Save();
        }
        DrawSettingTooltip("When enabled, dangerous treasure candidates can switch to the configured Ninja gearset, use Hide, and finish the last stretch on foot.");

        using (ImRaii.Disabled(!configuration.UseNinjaForDangerousArea))
        {
            DrawPotsIntSetting(
                "Hide Threshold Distance",
                configuration.HideThresholdDistance,
                0,
                500,
                value => configuration.HideThresholdDistance = value,
                nameof(configuration.HideThresholdDistance));
            DrawPotsIntSetting(
                "Ninja Gearset Number",
                configuration.NinjaGearsetNumber,
                0,
                100,
                value => configuration.NinjaGearsetNumber = value,
                nameof(configuration.NinjaGearsetNumber));
            DrawSettingTooltip("This gearset value is linked with the overworld coffer Ninja gearset setting. Changing either one updates both.");
        }

        DrawPotsIntSetting(
            "FATE Gearset Number",
            configuration.FateGearsetNumber,
            0,
            100,
            value => configuration.FateGearsetNumber = value,
            nameof(configuration.FateGearsetNumber));

        ImGui.Separator();
        ImGui.TextUnformatted("Time Constraints");

        if (ImGui.Checkbox("Manage Instance Time", ref manageInstanceTime))
        {
            logger.Info($"[Config] op=setting-change key=ManageInstanceTime old={configuration.ManageInstanceTime} new={manageInstanceTime}");
            configuration.ManageInstanceTime = manageInstanceTime;
            configuration.Save();
        }
        DrawSettingTooltip("When enabled, pot timing can respect the remaining instance window and exit buffer.");

        DrawPotsIntSetting(
            "FATE Completion Budget Minutes",
            configuration.FateCompletionBudgetMinutes,
            0,
            60,
            value => configuration.FateCompletionBudgetMinutes = value,
            nameof(configuration.FateCompletionBudgetMinutes));
        DrawPotsIntSetting(
            "Treasure Hunt Budget Minutes",
            configuration.TreasureHuntBudgetMinutes,
            0,
            60,
            value => configuration.TreasureHuntBudgetMinutes = value,
            nameof(configuration.TreasureHuntBudgetMinutes));
        DrawPotsIntSetting(
            "Instance Exit Buffer Minutes",
            configuration.InstanceExitBufferMinutes,
            0,
            30,
            value => configuration.InstanceExitBufferMinutes = value,
            nameof(configuration.InstanceExitBufferMinutes));

        DrawPotsIntSetting(
            "CE Fallback Cutoff Minutes",
            configuration.CeFallbackCutoffMinutes,
            0,
            30,
            value => configuration.CeFallbackCutoffMinutes = value,
            nameof(configuration.CeFallbackCutoffMinutes));
        DrawPotsIntSetting(
            "FATE Fallback Cutoff Minutes",
            configuration.FateFallbackCutoffMinutes,
            0,
            30,
            value => configuration.FateFallbackCutoffMinutes = value,
            nameof(configuration.FateFallbackCutoffMinutes));
        DrawSettingTooltip("New fallback CE or non-pot FATE starts are held once the predicted pot departure is inside the configured cutoff window.");
    }

    private void DrawTreasureCoffersTab()
    {
        var enableAutomaticTreasureCofferRoute = configuration.EnableAutomaticTreasureCofferRoute;
        if (ImGui.Checkbox("Enable Automatic Coffer Route", ref enableAutomaticTreasureCofferRoute))
        {
            logger.Info($"[Config] op=setting-change key=EnableAutomaticTreasureCofferRoute old={configuration.EnableAutomaticTreasureCofferRoute} new={enableAutomaticTreasureCofferRoute}");
            configuration.EnableAutomaticTreasureCofferRoute = enableAutomaticTreasureCofferRoute;
            configuration.Save();
        }
        DrawSettingTooltip("When enabled, base-camp recovery can use Occult Treasuresight and automatically start the overworld coffer route once both threshold rules are satisfied.");

        DrawPotsIntSetting(
            "Automatic Silver Threshold",
            configuration.AutomaticTreasureCofferSilverThreshold,
            0,
            8,
            value => configuration.AutomaticTreasureCofferSilverThreshold = value,
            nameof(configuration.AutomaticTreasureCofferSilverThreshold));
        DrawPotsIntSetting(
            "Automatic Bronze Threshold",
            configuration.AutomaticTreasureCofferBronzeThreshold,
            0,
            30,
            value => configuration.AutomaticTreasureCofferBronzeThreshold = value,
            nameof(configuration.AutomaticTreasureCofferBronzeThreshold));
        DrawSettingTooltip("0 means any amount of that type. Both automatic threshold checks must pass. A 0/0 configuration starts the route when the survey finds at least one coffer of either type.");

        ImGui.Separator();
        ImGui.TextUnformatted("Overworld Coffer Route");

        DrawPotsIntSetting(
            "Arrival Distance",
            configuration.ArrivalDistance,
            1,
            50,
            value => configuration.ArrivalDistance = value,
            nameof(configuration.ArrivalDistance));

        DrawPotsIntSetting(
            "Maximum Aggro Level (Overworld Coffers)",
            configuration.VisibleTreasureCofferMaximumAggroLevel,
            0,
            28,
            value => configuration.VisibleTreasureCofferMaximumAggroLevel = value,
            nameof(configuration.VisibleTreasureCofferMaximumAggroLevel));

        ImGui.Separator();
        ImGui.TextUnformatted("Dangerous Travel");

        var useNinjaForDangerousVisibleCoffers = configuration.UseNinjaForDangerousVisibleCoffers;
        if (ImGui.Checkbox("Use Ninja For Dangerous Overworld Coffers", ref useNinjaForDangerousVisibleCoffers))
        {
            logger.Info($"[Config] op=setting-change key=UseNinjaForDangerousVisibleCoffers old={configuration.UseNinjaForDangerousVisibleCoffers} new={useNinjaForDangerousVisibleCoffers}");
            configuration.UseNinjaForDangerousVisibleCoffers = useNinjaForDangerousVisibleCoffers;
            configuration.Save();
        }
        DrawSettingTooltip("When enabled, dangerous overworld coffer route spots can switch to the configured Ninja gearset, use Hide, and finish the last stretch on foot.");

        using (ImRaii.Disabled(!configuration.UseNinjaForDangerousVisibleCoffers))
        {
            DrawPotsIntSetting(
                "Overworld Coffer Hide Threshold Distance",
                configuration.VisibleCofferHideThresholdDistance,
                0,
                500,
                value => configuration.VisibleCofferHideThresholdDistance = value,
                nameof(configuration.VisibleCofferHideThresholdDistance));
            DrawPotsIntSetting(
                "Overworld Coffer Ninja Gearset Number",
                configuration.VisibleCofferNinjaGearsetNumber,
                0,
                100,
                value => configuration.VisibleCofferNinjaGearsetNumber = value,
                nameof(configuration.VisibleCofferNinjaGearsetNumber));
            DrawSettingTooltip("This gearset value is linked with the pots/revealed treasure Ninja gearset setting. Changing either one updates both.");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Route Rules");

        var skipHighLevelCavernsDuringAshkin = configuration.SkipHighLevelCavernsDuringAshkin;
        if (ImGui.Checkbox("Skip High-Level Caverns During Ashkin", ref skipHighLevelCavernsDuringAshkin))
        {
            logger.Info($"[Config] op=setting-change key=SkipHighLevelCavernsDuringAshkin old={configuration.SkipHighLevelCavernsDuringAshkin} new={skipHighLevelCavernsDuringAshkin}");
            configuration.SkipHighLevelCavernsDuringAshkin = skipHighLevelCavernsDuringAshkin;
            configuration.Save();
        }

        DrawSettingTooltip("Reserved for Lua-parity route rules in the overworld coffer route controller.");
    }

    private void DrawSettingsTab()
    {
        var autorotationPresetName = configuration.AutorotationPresetName;
        var presetWidth = ImGui.CalcTextSize(autorotationPresetName).X + 24f;
        presetWidth = Math.Clamp(presetWidth, SettingsTextInputMinWidth, SettingsTextInputMaxWidth);
        ImGui.SetNextItemWidth(presetWidth);
        if (ImGui.InputText("Autorotation Preset Name", ref autorotationPresetName, 120))
        {
            logger.InfoThrottled("setting-autorotation-preset-name", SettingTextLogInterval, $"Setting changed: AutorotationPresetName: '{configuration.AutorotationPresetName}' -> '{autorotationPresetName}'.");
            configuration.AutorotationPresetName = autorotationPresetName;
            configuration.Save();
        }

        var useReturn = configuration.UseReturn;
        if (ImGui.Checkbox("Use Return", ref useReturn))
        {
            logger.Info($"[Config] op=setting-change key=UseReturn old={configuration.UseReturn} new={useReturn}");
            configuration.UseReturn = useReturn;
            configuration.Save();
        }

        var enableBuffRotation = configuration.EnableBuffRotation;
        if (ImGui.Checkbox("Enable Buff Rotation", ref enableBuffRotation))
        {
            logger.Info($"[Config] op=setting-change key=EnableBuffRotation old={configuration.EnableBuffRotation} new={enableBuffRotation}");
            configuration.EnableBuffRotation = enableBuffRotation;
            configuration.Save();
        }

        var minimumMountingRange = configuration.MinimumMountingRange;
        ImGui.SetNextItemWidth(SettingsNumericInputWidth);
        if (ImGui.InputInt("Minimum Mounting Range", ref minimumMountingRange))
        {
            var nextValue = Math.Clamp(minimumMountingRange, 0, 100);
            logger.InfoThrottled("setting-minimum-mounting-range", SettingTextLogInterval, $"Setting changed: MinimumMountingRange: {configuration.MinimumMountingRange} -> {nextValue}.");
            configuration.MinimumMountingRange = nextValue;
            configuration.Save();
        }

        var mainWindowStatusTextScalePercent = configuration.MainWindowStatusTextScalePercent;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderInt("Main Window Status Text Size", ref mainWindowStatusTextScalePercent, 85, 150, "%d%%"))
        {
            var nextValue = Math.Clamp(mainWindowStatusTextScalePercent, 85, 150);
            logger.Info($"[Config] op=setting-change key=MainWindowStatusTextScalePercent old={configuration.MainWindowStatusTextScalePercent} new={nextValue}");
            configuration.MainWindowStatusTextScalePercent = nextValue;
            configuration.Save();
        }

        var scannerOnlyMode = configuration.ScannerOnlyMode;
        if (ImGui.Checkbox("Scanner-Only Mode", ref scannerOnlyMode))
        {
            logger.Info($"[Config] op=setting-change key=ScannerOnlyMode old={configuration.ScannerOnlyMode} new={scannerOnlyMode}");
            configuration.ScannerOnlyMode = scannerOnlyMode;
            configuration.Save();
        }

        ImGui.TextWrapped("Scanner-only mode keeps scanning and target selection active while blocking movement, combat automation, and buff rotation starts.");
    }

    private void DrawShoppingTab()
    {
        var enableManualCurrencyShopping = configuration.EnableManualCurrencyShopping;
        if (ImGui.Checkbox("Enable Manual Currency Shopping", ref enableManualCurrencyShopping))
        {
            logger.Info($"[Config] op=setting-change key=EnableManualCurrencyShopping old={configuration.EnableManualCurrencyShopping} new={enableManualCurrencyShopping}");
            configuration.EnableManualCurrencyShopping = enableManualCurrencyShopping;
            configuration.Save();
        }

        var enableAutoCurrencyShopping = configuration.EnableAutoCurrencyShopping;
        if (ImGui.Checkbox("Enable Automatic Shopping Triggers", ref enableAutoCurrencyShopping))
        {
            logger.Info($"[Config] op=setting-change key=EnableAutoCurrencyShopping old={configuration.EnableAutoCurrencyShopping} new={enableAutoCurrencyShopping}");
            configuration.EnableAutoCurrencyShopping = enableAutoCurrencyShopping;
            configuration.Save();
        }

        var silverStartThreshold = configuration.SilverStartThreshold;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Silver Start Threshold", ref silverStartThreshold))
        {
            configuration.SilverStartThreshold = Math.Max(0, silverStartThreshold);
            configuration.Save();
        }

        var goldStartThreshold = configuration.GoldStartThreshold;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Gold Start Threshold", ref goldStartThreshold))
        {
            configuration.GoldStartThreshold = Math.Max(0, goldStartThreshold);
            configuration.Save();
        }

        var runOnlyWhenIdle = configuration.RunOnlyWhenIdle;
        if (ImGui.Checkbox("Run Only When Idle", ref runOnlyWhenIdle))
        {
            configuration.RunOnlyWhenIdle = runOnlyWhenIdle;
            configuration.Save();
        }

        var requireVendorNearby = configuration.RequireVendorNearby;
        if (ImGui.Checkbox("Require Vendor Nearby", ref requireVendorNearby))
        {
            configuration.RequireVendorNearby = requireVendorNearby;
            configuration.Save();
        }

        var snapshot = plugin.ShopInspectorController.Snapshot;
        var hasMatch = plugin.CurrentCurrencyShopPageMatcher.TryMatch(snapshot, out var currentMatch, out var matchReason);
        var matchedPage = currentMatch?.Page;
        var matchedTab = currentMatch?.Tab;
        ImGui.TextWrapped($"Current Page Match: {(hasMatch && currentMatch != null ? $"{matchedPage!.MenuLabel} / {matchedTab!.TabLabel} (Tab {matchedTab.TabId})" : matchReason)}");
        ImGui.TextWrapped($"Reported Shop Tab: {snapshot.SelectedTabId}");
        ImGui.TextWrapped($"Shopping Status: {plugin.ManualCurrencyShoppingController.Status}");
        ImGui.TextWrapped($"Shopping Progress: groups={plugin.ManualCurrencyShoppingController.CompletedGroupCount} purchases={plugin.ManualCurrencyShoppingController.CompletedPurchaseCount} current={plugin.ManualCurrencyShoppingController.CurrentGroupSummary}");
        ImGui.TextWrapped($"Shopping Trigger: {plugin.ManualCurrencyShoppingController.TriggerStatus}");

        if (plugin.ManualCurrencyShoppingController.IsRunning)
        {
            if (ImGui.Button("Stop Automatic Shopping"))
            {
                logger.Info("[Config] op=ui-action action=stop-automatic-shopping");
                plugin.ManualCurrencyShoppingController.Stop("Automatic shopping stop requested.");
            }
        }
        else
        {
            using (ImRaii.Disabled(!configuration.EnableManualCurrencyShopping))
            {
                if (ImGui.Button("Run Automatic Shopping"))
                {
                    logger.Info("[Config] op=ui-action action=run-automatic-shopping");
                    plugin.ManualCurrencyShoppingController.Start();
                }
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Currency Reserves");
        if (hasMatch && matchedPage != null)
        {
            var reserve = GetOrCreateReserve(matchedPage.CurrencyItemId);
            var reserveAmount = reserve.ReserveAmount;
            ImGui.SetNextItemWidth(120f);
            if (ImGui.InputInt($"{matchedPage.CurrencyName} Reserve##{matchedPage.CurrencyItemId}", ref reserveAmount))
            {
                reserve.ReserveAmount = Math.Max(0, reserveAmount);
                logger.Info($"[Config] op=setting-change key=CurrencyReserve currencyItemId={matchedPage.CurrencyItemId} new={reserve.ReserveAmount}");
                configuration.Save();
            }
        }
        else if (configuration.CurrencyShopReserves.Count == 0)
        {
            ImGui.TextUnformatted("Open a supported currency page to edit its reserve.");
        }
        else
        {
            foreach (var reserve in configuration.CurrencyShopReserves)
            {
                ImGui.TextWrapped($"Currency {reserve.CurrencyItemId}: reserve {reserve.ReserveAmount}");
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Shopping Targets");
        if (configuration.CurrencyShopTargets.Count == 0)
        {
            ImGui.TextUnformatted("No current-page shopping targets configured.");
        }
        else
        {
            for (var i = 0; i < configuration.CurrencyShopTargets.Count; i++)
            {
                var target = configuration.CurrencyShopTargets[i];
                var targetPage = ShopCurrencyCatalog.Pages.FirstOrDefault(page => page.MenuIndex == target.MenuIndex);
                var targetTab = targetPage?.Tabs.FirstOrDefault(tab => tab.TabId == target.TabId);
                var targetItem = targetTab?.Items.FirstOrDefault(item => item.ItemId == target.ItemId);
                var targetItemName = targetItem == null
                    ? null
                    : ShoppingItemNameResolver.ResolveItemName(targetItem.ItemId, targetItem.Name);
                ImGui.PushID($"shopping-target-{i}");

                var enabled = target.Enabled;
                if (ImGui.Checkbox("##enabled", ref enabled))
                {
                    target.Enabled = enabled;
                    configuration.Save();
                }
                ImGui.SameLine();
                ImGui.TextWrapped(targetItem == null
                    ? $"Item {target.ItemId} on menu {target.MenuIndex} tab {target.TabId}"
                    : $"{targetItemName} | {targetPage?.MenuLabel} / {targetTab?.TabLabel}");

                var mode = (int)target.Mode;
                ImGui.SetNextItemWidth(150f);
                if (ImGui.Combo("Mode", ref mode, CurrencyShopTargetModeLabels, CurrencyShopTargetModeLabels.Length))
                {
                    target.Mode = (CurrencyShopTargetMode)mode;
                    configuration.Save();
                }

                ImGui.SetNextItemWidth(90f);
                var amount = target.TargetAmount;
                if (ImGui.InputInt("Amount", ref amount))
                {
                    target.TargetAmount = Math.Max(0, amount);
                    configuration.Save();
                }

                ImGui.SetNextItemWidth(90f);
                var priority = target.Priority;
                if (ImGui.InputInt("Priority", ref priority))
                {
                    target.Priority = Math.Max(0, priority);
                    configuration.Save();
                }

                if (ImGui.Button("Remove"))
                {
                    configuration.CurrencyShopTargets.RemoveAt(i);
                    configuration.Save();
                    ImGui.PopID();
                    break;
                }

                ImGui.Separator();
                ImGui.PopID();
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Current Page/Tab Catalog Browser");
        if (!hasMatch || matchedPage == null || matchedTab == null)
        {
            ImGui.TextUnformatted("Open a supported ShopExchangeCurrency page/tab to browse its items.");
            return;
        }

        if (!ImGui.BeginTable("CurrentPageShoppingCatalog", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Row", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableHeadersRow();

        foreach (var item in matchedTab.Items)
        {
            var existingTarget = configuration.CurrencyShopTargets.FirstOrDefault(target => target.ItemId == item.ItemId && target.MenuIndex == matchedPage.MenuIndex && target.TabId == matchedTab.TabId);
            var itemName = ShoppingItemNameResolver.ResolveItemName(item.ItemId, item.Name);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextWrapped(itemName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Cost.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.RowIndex.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(existingTarget == null ? "No" : existingTarget.Mode.ToString());
            ImGui.TableNextColumn();
            using var disabled = ImRaii.Disabled(existingTarget != null);
            if (ImGui.Button($"Add##{matchedPage.MenuIndex}_{matchedTab.TabId}_{item.ItemId}"))
            {
                configuration.CurrencyShopTargets.Add(new CurrencyShopTarget
                {
                    Enabled = true,
                    ItemId = item.ItemId,
                    MenuIndex = matchedPage.MenuIndex,
                    TabId = matchedTab.TabId,
                    Mode = CurrencyShopTargetMode.Keep,
                    TargetAmount = 1,
                    Priority = configuration.CurrencyShopTargets.Count,
                });
                configuration.Save();
            }
        }

        ImGui.EndTable();
    }

    private CurrencyShopReserveSetting GetOrCreateReserve(uint currencyItemId)
    {
        var reserve = configuration.CurrencyShopReserves.FirstOrDefault(entry => entry.CurrencyItemId == currencyItemId);
        if (reserve != null)
        {
            return reserve;
        }

        reserve = new CurrencyShopReserveSetting
        {
            CurrencyItemId = currencyItemId,
            ReserveAmount = 0,
        };
        configuration.CurrencyShopReserves.Add(reserve);
        configuration.Save();
        return reserve;
    }

    private void DrawCriticalEncounterCheckboxList()
    {
        DrawScrollableCheckboxList(
            "AOCCHCeCheckboxes",
            GetCriticalEncounterEntries(),
            configuration.IsCriticalEncounterEnabled,
            (id, enabled) => configuration.SetCriticalEncounterEnabled(id, enabled),
            "CriticalEncounter");
    }

    private void DrawFateCheckboxList()
    {
        DrawScrollableCheckboxList(
            "AOCCHFateCheckboxes",
            GetDirectFarmFateEntries(),
            configuration.IsFateEnabled,
            (id, enabled) => configuration.SetFateEnabled(id, enabled),
            "FATE");
    }

    private void DrawScrollableCheckboxList(
        string childId,
        IReadOnlyList<(uint Id, string Label)> entries,
        Func<uint, bool> isEnabled,
        Func<uint, bool, bool> setEnabled,
        string logLabel)
    {
        var availableHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y);
        ImGui.BeginChild(childId, new Vector2(0, availableHeight), true);
        foreach (var entry in entries)
        {
            var enabled = isEnabled(entry.Id);
            if (!ImGui.Checkbox($"{entry.Label}##{childId}-{entry.Id}", ref enabled) || !setEnabled(entry.Id, enabled))
            {
                continue;
            }

            logger.Info($"[Config] op=setting-change key={logLabel} targetId={entry.Id} enabled={enabled}");
            configuration.Save();
        }

        ImGui.EndChild();
    }

    private void DrawPotsIntSetting(string label, int currentValue, int minValue, int maxValue, Action<int> applyValue, string logName)
    {
        ImGui.SetNextItemWidth(PotsNumericInputWidth);
        DrawClampedIntSetting(label, currentValue, minValue, maxValue, applyValue, logName);
    }

    private static void DrawSettingTooltip(string text)
    {
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.SetTooltip(text);
    }

    private void DrawClampedIntSetting(string label, int currentValue, int minValue, int maxValue, Action<int> applyValue, string logName)
    {
        var value = currentValue;
        if (!ImGui.InputInt(label, ref value))
        {
            return;
        }

        var nextValue = Math.Clamp(value, minValue, maxValue);
        if (nextValue == currentValue)
        {
            return;
        }

        logger.InfoThrottled($"setting-{logName}", SettingTextLogInterval, $"[Config] op=setting-change key={logName} old={currentValue} new={nextValue}");
        applyValue(nextValue);
        configuration.Save();
    }

    private List<(uint Id, string Label)> GetCriticalEncounterEntries()
        => data.CriticalEncounters
            .Select(criticalEncounter => (
                criticalEncounter.Id,
                nameResolver.GetCriticalEncounterName(criticalEncounter.Id, criticalEncounter.Name)))
            .OrderBy(entry => entry.Item2, StringComparer.Ordinal)
            .ToList();

    private List<(uint Id, string Label)> GetDirectFarmFateEntries()
        => data.Fates
            .Where(fate => !IsPotFate(fate.Id))
            .Select(fate => (
                fate.Id,
                nameResolver.GetFateName(fate.Id, fate.Name)))
            .OrderBy(entry => entry.Item2, StringComparer.Ordinal)
            .ToList();

    private bool IsPotFate(uint fateId)
        => potFateIds.Contains(fateId);
}
