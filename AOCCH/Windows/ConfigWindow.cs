using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Shopping;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public class ConfigWindow : Window, IDisposable
{
    private enum ConfigTab
    {
        CriticalEngagements,
        Fates,
        Pots,
        TreasureCoffers,
        Shopping,
        Settings,
    }

    private static readonly string[] FatePriorityLabels = ["Lowest Progress", "Nearest"];
    private static readonly TimeSpan SettingTextLogInterval = TimeSpan.FromSeconds(10);
    private const float PotsNumericInputWidth = 60f;
    private const float SettingsNumericInputWidth = 60f;
    private const float SettingsTextInputMinWidth = 120f;
    private const float SettingsTextInputMaxWidth = 240f;

    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly OccultCrescentNameResolver nameResolver;
    private readonly AocchLogger logger;
    private int selectedShoppingPageIndex;
    private int selectedShoppingTabIndex;
    private int selectedShoppingItemIndex;
    private ConfigTab? selectedTabOverride;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(
        Plugin plugin,
        Configuration configuration,
        OccultCrescentNameResolver nameResolver,
        AocchLogger logger) : base("AOCCH Configuration###AOCCHConfig")
    {
        Size = new Vector2(620, 360);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        this.configuration = configuration;
        this.nameResolver = nameResolver;
        this.logger = logger;
    }

    public void Dispose() { }

    public void OpenShoppingTab()
    {
        IsOpen = true;
        selectedTabOverride = ConfigTab.Shopping;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("AOCCHConfigTabs"))
        {
            return;
        }

        if (BeginConfigTabItem("Critical Engagements", ConfigTab.CriticalEngagements))
        {
            DrawCriticalEngagementsTab();
            ImGui.EndTabItem();
        }

        if (BeginConfigTabItem("FATEs", ConfigTab.Fates))
        {
            DrawFatesTab();
            ImGui.EndTabItem();
        }

        if (BeginConfigTabItem("Pots", ConfigTab.Pots))
        {
            DrawPotsTab();
            ImGui.EndTabItem();
        }

        if (BeginConfigTabItem("Treasure Coffers", ConfigTab.TreasureCoffers))
        {
            DrawTreasureCoffersTab();
            ImGui.EndTabItem();
        }

        if (BeginConfigTabItem("Shopping", ConfigTab.Shopping))
        {
            DrawShoppingTab();
            ImGui.EndTabItem();
        }

        if (BeginConfigTabItem("Settings", ConfigTab.Settings))
        {
            DrawSettingsTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private bool BeginConfigTabItem(string label, ConfigTab tab)
    {
        var flags = selectedTabOverride == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        var opened = ImGui.BeginTabItem(label, flags);
        if (opened && selectedTabOverride == tab)
        {
            selectedTabOverride = null;
        }

        return opened;
    }

    private void DrawCriticalEngagementsTab()
    {
        if (!RequireFeature(plugin.Scanner.Snapshot.CanFarmCriticalEncounters, "CE data"))
        {
            return;
        }

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
        if (!RequireFeature(plugin.Scanner.Snapshot.CanFarmFates, "FATE data"))
        {
            return;
        }

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
        if (!RequireFeature(plugin.Scanner.Snapshot.CanRunPotTreasure, "Pot treasure data"))
        {
            return;
        }

        var enablePotFarming = configuration.EnablePotFarming;
        if (ImGui.Checkbox("Enable Pot Farming", ref enablePotFarming))
        {
            logger.Info($"[Config] op=setting-change key=EnablePotFarming old={configuration.EnablePotFarming} new={enablePotFarming}");
            configuration.EnablePotFarming = enablePotFarming;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Route Setup");

        var territory = plugin.Scanner.ActiveTerritoryData;
        var potFates = territory?.PotFates.OrderBy(pot => pot.Name, StringComparer.Ordinal).ToArray() ?? [];
        var startingPotFateId = territory == null ? 0 : configuration.GetStartingPotFateId(territory.Key);
        var startingPotFate = Array.FindIndex(potFates, pot => pot.FateId == startingPotFateId) + 1;
        var startingPotFateLabels = new[] { "Auto" }.Concat(potFates.Select(pot => $"{pot.Name} ({pot.FateId})")).ToArray();
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Starting Pot FATE", ref startingPotFate, startingPotFateLabels, startingPotFateLabels.Length) && territory != null)
        {
            var nextFateId = startingPotFate == 0 ? 0 : potFates[startingPotFate - 1].FateId;
            logger.Info($"[Config] op=setting-change key=StartingPotFate territoryKey={territory.Key} old={startingPotFateId} new={nextFateId}");
            configuration.SetStartingPotFateId(territory.Key, nextFateId);
            configuration.Save();
        }

        DrawNarrowIntSetting(
            "Spawn Lead Minutes",
            configuration.SpawnLeadMinutes,
            0,
            30,
            value => configuration.SpawnLeadMinutes = value,
            nameof(configuration.SpawnLeadMinutes));

        DrawNarrowIntSetting(
            "Arrival Radius",
            configuration.SpawnArrivalRadius,
            0,
            100,
            value => configuration.SpawnArrivalRadius = value,
            nameof(configuration.SpawnArrivalRadius));

        ImGui.Separator();
        ImGui.TextUnformatted("Dangerous Travel");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        DrawSettingTooltip("This feature is still experimental and designed for characters at maximum Knowledge level.");

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
            DrawNarrowIntSetting(
                "Ninja Gearset Number",
                configuration.NinjaGearsetNumber,
                0,
                100,
                value => configuration.NinjaGearsetNumber = value,
                nameof(configuration.NinjaGearsetNumber));
            DrawSettingTooltip("This gearset value is linked with the overworld coffer Ninja gearset setting. Changing either one updates both.");

            DrawNarrowIntSetting(
                "FATE Gearset Number",
                configuration.FateGearsetNumber,
                0,
                100,
                value => configuration.FateGearsetNumber = value,
                nameof(configuration.FateGearsetNumber));
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Threat Handling");

        using (ImRaii.Disabled(!configuration.UseNinjaForDangerousArea))
        {
            var liveHideLevelLabel = GetLiveKnowledgeHideLevelLabel("Live Knowledge Hide Offset", configuration.PotKnowledgeHideOffset);
            DrawNarrowIntSetting(
                liveHideLevelLabel,
                configuration.PotKnowledgeHideOffset,
                -27,
                27,
                value => configuration.PotKnowledgeHideOffset = value,
                nameof(configuration.PotKnowledgeHideOffset));
            DrawSettingTooltip("Hide for entities at or above your Knowledge level plus this offset. 0 hides at equal level; negative values are more cautious.");

            DrawNarrowIntSetting(
                "Knowledge Threat Enter Range",
                configuration.KnowledgeThreatEnterDistance,
                1,
                50,
                value => configuration.KnowledgeThreatEnterDistance = value,
                nameof(configuration.KnowledgeThreatEnterDistance));
            DrawNarrowIntSetting(
                "Knowledge Threat Exit Range",
                configuration.KnowledgeThreatExitDistance,
                configuration.KnowledgeThreatEnterDistance,
                100,
                value => configuration.KnowledgeThreatExitDistance = value,
                nameof(configuration.KnowledgeThreatExitDistance));
            DrawSettingTooltip("Shared with overworld coffers. A nearby high-level entity starts Hide inside the enter range. Hidden travel resumes mounted movement only after none remain inside the exit range.");
        }

        if (ImGui.CollapsingHeader("Fallback"))
        {
            using (ImRaii.Disabled(!configuration.UseNinjaForDangerousArea))
            {
                DrawNarrowIntSetting(
                    "Fallback Maximum Aggro Level",
                    configuration.MaximumAggroLevel,
                    0,
                    20,
                    value => configuration.MaximumAggroLevel = value,
                    nameof(configuration.MaximumAggroLevel));
                DrawSettingTooltip("Used only when live Foray knowledge data is unavailable.");

                DrawNarrowIntSetting(
                    "Fallback Hide Threshold Distance",
                    configuration.HideThresholdDistance,
                    0,
                    500,
                    value => configuration.HideThresholdDistance = value,
                    nameof(configuration.HideThresholdDistance));
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Time Management");

        var manageInstanceTime = configuration.ManageInstanceTime;
        if (ImGui.Checkbox("Manage Instance Time", ref manageInstanceTime))
        {
            logger.Info($"[Config] op=setting-change key=ManageInstanceTime old={configuration.ManageInstanceTime} new={manageInstanceTime}");
            configuration.ManageInstanceTime = manageInstanceTime;
            configuration.Save();
        }
        DrawSettingTooltip("When enabled, pot timing can respect the remaining instance window and exit buffer.");

        DrawNarrowIntSetting(
            "FATE Completion Budget Minutes",
            configuration.FateCompletionBudgetMinutes,
            0,
            60,
            value => configuration.FateCompletionBudgetMinutes = value,
            nameof(configuration.FateCompletionBudgetMinutes));
        DrawNarrowIntSetting(
            "Treasure Hunt Budget Minutes",
            configuration.TreasureHuntBudgetMinutes,
            0,
            60,
            value => configuration.TreasureHuntBudgetMinutes = value,
            nameof(configuration.TreasureHuntBudgetMinutes));
        DrawNarrowIntSetting(
            "Instance Exit Buffer Minutes",
            configuration.InstanceExitBufferMinutes,
            0,
            30,
            value => configuration.InstanceExitBufferMinutes = value,
            nameof(configuration.InstanceExitBufferMinutes));

        DrawNarrowIntSetting(
            "CE Fallback Cutoff Minutes",
            configuration.CeFallbackCutoffMinutes,
            0,
            30,
            value => configuration.CeFallbackCutoffMinutes = value,
            nameof(configuration.CeFallbackCutoffMinutes));
        DrawNarrowIntSetting(
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
        if (!RequireFeature(plugin.Scanner.Snapshot.CanRunVisibleCofferRoute || plugin.Scanner.Snapshot.CanRunPotTreasure, "Treasure coffer data"))
        {
            return;
        }

        var enableAutomaticTreasureCofferRoute = configuration.EnableAutomaticTreasureCofferRoute;
        if (ImGui.Checkbox("Enable Automatic Coffer Route", ref enableAutomaticTreasureCofferRoute))
        {
            logger.Info($"[Config] op=setting-change key=EnableAutomaticTreasureCofferRoute old={configuration.EnableAutomaticTreasureCofferRoute} new={enableAutomaticTreasureCofferRoute}");
            configuration.EnableAutomaticTreasureCofferRoute = enableAutomaticTreasureCofferRoute;
            configuration.Save();
        }
        DrawSettingTooltip("When enabled, base-camp recovery can use Occult Treasuresight and automatically start the overworld coffer route once both threshold rules are satisfied.");

        var enableOverworldTreasureGuide = configuration.EnableOverworldTreasureGuide;
        if (ImGui.Checkbox("Enable Overworld Treasure Guide", ref enableOverworldTreasureGuide))
        {
            logger.Info($"[Config] op=setting-change key=EnableOverworldTreasureGuide old={configuration.EnableOverworldTreasureGuide} new={enableOverworldTreasureGuide}");
            configuration.EnableOverworldTreasureGuide = enableOverworldTreasureGuide;
            configuration.Save();
        }
        DrawSettingTooltip("Draws a world-space line and marker to the nearest detected overworld treasure. It does not start or control coffer automation.");

        DrawNarrowIntSetting(
            "Automatic Silver Threshold",
            configuration.AutomaticTreasureCofferSilverThreshold,
            0,
            8,
            value => configuration.AutomaticTreasureCofferSilverThreshold = value,
            nameof(configuration.AutomaticTreasureCofferSilverThreshold));
        DrawNarrowIntSetting(
            "Automatic Bronze Threshold",
            configuration.AutomaticTreasureCofferBronzeThreshold,
            0,
            30,
            value => configuration.AutomaticTreasureCofferBronzeThreshold = value,
            nameof(configuration.AutomaticTreasureCofferBronzeThreshold));
        DrawSettingTooltip("0 means any amount of that type. Both automatic threshold checks must pass. A 0/0 configuration starts the route when the survey finds at least one coffer of either type.");

        ImGui.Separator();
        ImGui.TextUnformatted("Route Setup");

        DrawNarrowIntSetting(
            "Arrival Distance",
            configuration.ArrivalDistance,
            1,
            50,
            value => configuration.ArrivalDistance = value,
            nameof(configuration.ArrivalDistance));

        var skipHighLevelCavernsDuringAshkin = configuration.SkipHighLevelCavernsDuringAshkin;
        if (ImGui.Checkbox("Skip High-Level Caverns During Ashkin", ref skipHighLevelCavernsDuringAshkin))
        {
            logger.Info($"[Config] op=setting-change key=SkipHighLevelCavernsDuringAshkin old={configuration.SkipHighLevelCavernsDuringAshkin} new={skipHighLevelCavernsDuringAshkin}");
            configuration.SkipHighLevelCavernsDuringAshkin = skipHighLevelCavernsDuringAshkin;
            configuration.Save();
        }

        DrawSettingTooltip("Skips route entries explicitly marked for Ashkin time.");

        var skipUnsafeWeatherRoutes = configuration.SkipUnsafeWeatherRoutes;
        if (ImGui.Checkbox("Skip Unsafe-Weather Routes", ref skipUnsafeWeatherRoutes))
        {
            logger.Info($"[Config] op=setting-change key=SkipUnsafeWeatherRoutes old={configuration.SkipUnsafeWeatherRoutes} new={skipUnsafeWeatherRoutes}");
            configuration.SkipUnsafeWeatherRoutes = skipUnsafeWeatherRoutes;
            configuration.Save();
        }

        DrawSettingTooltip("During unsafe weather, skips the Abandoned Ascent 7 route. Heathcliff_10 uses Ninja Hide when enabled, or is skipped when Ninja travel is disabled.");

        ImGui.Separator();
        ImGui.TextUnformatted("Dangerous Travel");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        DrawSettingTooltip("This feature is still experimental and designed for characters at maximum Knowledge level.");

        var useNinjaForDangerousVisibleCoffers = configuration.UseNinjaForDangerousVisibleCoffers;
        if (ImGui.Checkbox("Use Ninja For Dangerous Coffers", ref useNinjaForDangerousVisibleCoffers))
        {
            logger.Info($"[Config] op=setting-change key=UseNinjaForDangerousVisibleCoffers old={configuration.UseNinjaForDangerousVisibleCoffers} new={useNinjaForDangerousVisibleCoffers}");
            configuration.UseNinjaForDangerousVisibleCoffers = useNinjaForDangerousVisibleCoffers;
            configuration.Save();
        }
        DrawSettingTooltip("When enabled, dangerous overworld coffer route spots can switch to the configured Ninja gearset, use Hide, and finish the last stretch on foot.");

        using (ImRaii.Disabled(!configuration.UseNinjaForDangerousVisibleCoffers))
        {
            DrawNarrowIntSetting(
                "Ninja Gearset Number",
                configuration.VisibleCofferNinjaGearsetNumber,
                0,
                100,
                value => configuration.VisibleCofferNinjaGearsetNumber = value,
                nameof(configuration.VisibleCofferNinjaGearsetNumber));
            DrawSettingTooltip("This gearset value is linked with the Pots tab Ninja gearset setting. Changing either one updates both.");

            DrawNarrowIntSetting(
                "FATE Gearset Number",
                configuration.FateGearsetNumber,
                0,
                100,
                value => configuration.FateGearsetNumber = value,
                nameof(configuration.FateGearsetNumber));
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Threat Handling");

        using (ImRaii.Disabled(!configuration.UseNinjaForDangerousVisibleCoffers))
        {
            var liveHideLevelLabel = GetLiveKnowledgeHideLevelLabel("Live Knowledge Hide Offset", configuration.VisibleCofferKnowledgeHideOffset);
            DrawNarrowIntSetting(
                liveHideLevelLabel,
                configuration.VisibleCofferKnowledgeHideOffset,
                -27,
                27,
                value => configuration.VisibleCofferKnowledgeHideOffset = value,
                nameof(configuration.VisibleCofferKnowledgeHideOffset));
            DrawSettingTooltip("Hide for entities at or above your Knowledge level plus this offset. 4 means a Knowledge 20 player hides at level 24 and above.");

            DrawNarrowIntSetting(
                "Knowledge Threat Enter Range",
                configuration.KnowledgeThreatEnterDistance,
                1,
                50,
                value => configuration.KnowledgeThreatEnterDistance = value,
                nameof(configuration.KnowledgeThreatEnterDistance));
            DrawNarrowIntSetting(
                "Knowledge Threat Exit Range",
                configuration.KnowledgeThreatExitDistance,
                configuration.KnowledgeThreatEnterDistance,
                100,
                value => configuration.KnowledgeThreatExitDistance = value,
                nameof(configuration.KnowledgeThreatExitDistance));
            DrawSettingTooltip("Shared with pots. A nearby high-level entity starts Hide inside the enter range. Hidden travel resumes mounted movement only after none remain inside the exit range.");
        }

        if (ImGui.CollapsingHeader("Fallback"))
        {
            using (ImRaii.Disabled(!configuration.UseNinjaForDangerousVisibleCoffers))
            {
                DrawNarrowIntSetting(
                    "Fallback Maximum Aggro Level",
                    configuration.VisibleTreasureCofferMaximumAggroLevel,
                    0,
                    28,
                    value => configuration.VisibleTreasureCofferMaximumAggroLevel = value,
                    nameof(configuration.VisibleTreasureCofferMaximumAggroLevel));
                DrawSettingTooltip("Used only when live Foray knowledge data is unavailable.");

                DrawNarrowIntSetting(
                    "Fallback Hide Threshold Distance",
                    configuration.VisibleCofferHideThresholdDistance,
                    0,
                    500,
                    value => configuration.VisibleCofferHideThresholdDistance = value,
                    nameof(configuration.VisibleCofferHideThresholdDistance));
            }
        }

    }

    private void DrawSettingsTab()
    {
        ImGui.TextUnformatted("Coffer Observations");

        var enableCofferObservationSubmission = configuration.EnableCofferObservationSubmission;
        if (ImGui.Checkbox("Share Confirmed Coffer Observations", ref enableCofferObservationSubmission))
        {
            logger.Info($"[Config] op=setting-change key=EnableCofferObservationSubmission old={configuration.EnableCofferObservationSubmission} new={enableCofferObservationSubmission}");
            configuration.EnableCofferObservationSubmission = enableCofferObservationSubmission;
            configuration.Save();
        }
        DrawSettingTooltip("When enabled, confirmed coffer positions are submitted anonymously to the public observation endpoint. No character or account identity is transmitted.");

        ImGui.Separator();
        ImGui.TextUnformatted("Combat");

        var autorotationPresetName = configuration.AutorotationPresetName;
        var presetWidth = ImGui.CalcTextSize(autorotationPresetName).X + 24f;
        presetWidth = Math.Clamp(presetWidth, SettingsTextInputMinWidth, SettingsTextInputMaxWidth);
        ImGui.SetNextItemWidth(presetWidth);
        if (ImGui.InputText("Autorotation Override Preset Name", ref autorotationPresetName, 120))
        {
            logger.InfoThrottled("setting-autorotation-preset-name", SettingTextLogInterval, $"Setting changed: AutorotationPresetName: '{configuration.AutorotationPresetName}' -> '{autorotationPresetName}'.");
            configuration.AutorotationPresetName = autorotationPresetName;
            configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        DrawSettingTooltip("Leave the override blank to use the AOCCH-managed BossMod rotation. A configured override is used unchanged when available; failures fall back to the managed rotation.");

        DrawTargetRangeSetting("Melee Target Range", configuration.MeleeTargetRange, value => configuration.MeleeTargetRange = value, nameof(configuration.MeleeTargetRange));
        DrawTargetRangeSetting("Ranged Target Range", configuration.RangedTargetRange, value => configuration.RangedTargetRange = value, nameof(configuration.RangedTargetRange));

        ImGui.Separator();
        ImGui.TextUnformatted("Automation");

        var enableBuffRotation = configuration.EnableBuffRotation;
        if (ImGui.Checkbox("Enable Buff Rotation", ref enableBuffRotation))
        {
            logger.Info($"[Config] op=setting-change key=EnableBuffRotation old={configuration.EnableBuffRotation} new={enableBuffRotation}");
            configuration.EnableBuffRotation = enableBuffRotation;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Movement");

        var useReturn = configuration.UseReturn;
        if (ImGui.Checkbox("Use Return", ref useReturn))
        {
            logger.Info($"[Config] op=setting-change key=UseReturn old={configuration.UseReturn} new={useReturn}");
            configuration.UseReturn = useReturn;
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

        ImGui.Separator();
        ImGui.TextUnformatted("Interface");

        var mainWindowStatusTextScalePercent = configuration.MainWindowStatusTextScalePercent;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderInt("Main Window Status Text Size", ref mainWindowStatusTextScalePercent, 85, 150, "%d%%"))
        {
            var nextValue = Math.Clamp(mainWindowStatusTextScalePercent, 85, 150);
            logger.Info($"[Config] op=setting-change key=MainWindowStatusTextScalePercent old={configuration.MainWindowStatusTextScalePercent} new={nextValue}");
            configuration.MainWindowStatusTextScalePercent = nextValue;
            configuration.Save();
        }
    }

    private void DrawTargetRangeSetting(string label, decimal currentValue, Action<decimal> setter, string key)
    {
        var value = (float)currentValue;
        ImGui.SetNextItemWidth(SettingsNumericInputWidth);
        if (ImGui.InputFloat(label, ref value, 0f, 0f, "%.1f"))
        {
            var nextValue = Math.Clamp((decimal)Math.Round(value, 1), 1.1m, 30m);
            logger.Info($"[Config] op=setting-change key={key} old={currentValue} new={nextValue}");
            setter(nextValue);
            configuration.Save();
        }
    }

    private void DrawShoppingTab()
    {
        if (!RequireFeature(plugin.Scanner.Snapshot.CanUseShopping, "Shopping data"))
        {
            return;
        }

        var territory = plugin.Scanner.ActiveTerritoryData;
        if (territory == null)
        {
            ImGui.TextUnformatted("Shopping metadata is unavailable.");
            return;
        }

        var territoryKey = territory.Key;
        var shoppingPages = territory.Shopping.Pages.Where(page => page.Tabs.Any(tab => tab.Items.Count > 0)).ToList();
        var enableManualCurrencyShopping = configuration.EnableManualCurrencyShopping;
        if (ImGui.Checkbox("Enable Shopping", ref enableManualCurrencyShopping))
        {
            logger.Info($"[Config] op=setting-change key=EnableManualCurrencyShopping old={configuration.EnableManualCurrencyShopping} new={enableManualCurrencyShopping}");
            configuration.EnableManualCurrencyShopping = enableManualCurrencyShopping;
            configuration.Save();
        }

        ImGui.TextWrapped($"Shopping: {plugin.ManualCurrencyShoppingController.CurrentStatusSummary}");

        ImGui.Separator();
        ImGui.TextUnformatted("Currency");
        if (ImGui.BeginTable("ShoppingCurrencySettings", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("");
            ImGui.TableSetupColumn("Reserved", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("Threshold", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableHeadersRow();

            foreach (var currency in shoppingPages.GroupBy(page => page.CurrencyItemId).Select(group => group.First()))
            {
                DrawCurrencySettingsRow(territoryKey, currency.CurrencyItemId, currency.CurrencyName, configuration.GetCurrencyShopThreshold(territoryKey, currency.CurrencyItemId));
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Add Item");
        if (shoppingPages.Count == 0)
        {
            ImGui.TextUnformatted("No supported shopping catalog items are defined.");
        }
        else
        {
            selectedShoppingPageIndex = Math.Clamp(selectedShoppingPageIndex, 0, shoppingPages.Count - 1);
            var selectedPage = shoppingPages[selectedShoppingPageIndex];
            var pageLabels = shoppingPages.Select(page => page.MenuLabel).ToArray();
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.Combo("##shopping-page", ref selectedShoppingPageIndex, pageLabels, pageLabels.Length))
            {
                selectedShoppingTabIndex = 0;
                selectedShoppingItemIndex = 0;
                selectedPage = shoppingPages[selectedShoppingPageIndex];
            }

            var shoppingTabs = selectedPage.Tabs.Where(tab => tab.Items.Count > 0).ToList();
            selectedShoppingTabIndex = shoppingTabs.Count == 0 ? 0 : Math.Clamp(selectedShoppingTabIndex, 0, shoppingTabs.Count - 1);
            if (shoppingTabs.Count == 0)
            {
                ImGui.TextUnformatted("No populated tabs are defined for this page.");
            }
            else
            {
                var tabLabels = shoppingTabs.Select(tab => tab.TabLabel).ToArray();
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.Combo("##shopping-tab", ref selectedShoppingTabIndex, tabLabels, tabLabels.Length))
                {
                    selectedShoppingItemIndex = 0;
                }

                var selectedTab = shoppingTabs[selectedShoppingTabIndex];
                var shoppingItems = selectedTab.Items.ToList();
                selectedShoppingItemIndex = Math.Clamp(selectedShoppingItemIndex, 0, shoppingItems.Count - 1);
                var itemLabels = shoppingItems
                    .Select(item => ShoppingItemNameResolver.ResolveItemName(item.ItemId, item.Name))
                    .ToArray();
                var addItemButtonWidth = ImGui.CalcTextSize("Add Item").X + (ImGui.GetStyle().FramePadding.X * 2f);
                var reservedWidth = addItemButtonWidth + ImGui.GetStyle().ItemSpacing.X;
                ImGui.SetNextItemWidth(-reservedWidth);
                ImGui.Combo("##shopping-item", ref selectedShoppingItemIndex, itemLabels, itemLabels.Length);
                ImGui.SameLine();

                if (ImGui.Button("Add Item"))
                {
                    var selectedItem = shoppingItems[selectedShoppingItemIndex];
                    if (!configuration.CurrencyShopTargets.Any(target => string.Equals(target.TerritoryKey, territoryKey, StringComparison.OrdinalIgnoreCase) && target.ItemId == selectedItem.ItemId && target.MenuIndex == selectedPage.MenuIndex && target.TabId == selectedTab.TabId))
                    {
                        configuration.CurrencyShopTargets.Add(new CurrencyShopTarget
                        {
                            TerritoryKey = territoryKey,
                            ItemId = selectedItem.ItemId,
                            MenuIndex = selectedPage.MenuIndex,
                            TabId = selectedTab.TabId,
                            KeepAmount = 1,
                            BuyAmount = 0,
                            KeepBuying = false,
                            Priority = configuration.CurrencyShopTargets.Count(target => string.Equals(target.TerritoryKey, territoryKey, StringComparison.OrdinalIgnoreCase)),
                        });
                        NormalizeShoppingTargetPriorities();
                        configuration.Save();
                    }
                }
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Shopping Priority List");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Keep maintains stock. Buy is one-off. Per item priority is Keep, then Buy, then Keep Buying. Only one item can be marked Keep Buying at a time.");
        }
        var activeTargetIndices = configuration.CurrencyShopTargets
            .Select((target, index) => (Target: target, Index: index))
            .Where(entry => string.Equals(entry.Target.TerritoryKey, territoryKey, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Index)
            .ToList();
        if (activeTargetIndices.Count == 0)
        {
            ImGui.TextUnformatted("No shopping items configured.");
        }
        else
        {
            var listHeight = (ImGui.GetFrameHeightWithSpacing() * 11f) + 8f;
            if (ImGui.BeginChild("ShoppingPriorityListChild", new Vector2(0, listHeight), true))
            {
                if (ImGui.BeginTable("ShoppingPriorityList", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, 80f);
                    ImGui.TableSetupColumn("Item");
                    ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, 80f);
                    ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed, 80f);
                    ImGui.TableSetupColumn("Keep Buying", ImGuiTableColumnFlags.WidthFixed, 100f);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 90f);
                    ImGui.TableHeadersRow();

                    for (var displayIndex = 0; displayIndex < activeTargetIndices.Count; displayIndex++)
                    {
                        var i = activeTargetIndices[displayIndex];
                        var target = configuration.CurrencyShopTargets[i];
                        var targetPage = shoppingPages.FirstOrDefault(page => page.MenuIndex == target.MenuIndex);
                        var targetTab = targetPage?.Tabs.FirstOrDefault(tab => tab.TabId == target.TabId);
                        var targetItem = targetTab?.Items.FirstOrDefault(item => item.ItemId == target.ItemId);
                        var targetItemName = targetItem == null
                            ? $"[{target.TerritoryKey}] Item {target.ItemId}"
                            : ShoppingItemNameResolver.ResolveItemName(targetItem.ItemId, targetItem.Name);

                        ImGui.PushID($"shopping-target-{i}");
                        ImGui.TableNextRow();

                        ImGui.TableSetColumnIndex(0);
                        var iconButtonSize = new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight());
                        var iconButtonSpacing = ImGui.GetStyle().ItemSpacing.X;
                        var iconButtonGroupWidth = (iconButtonSize.X * 2f) + iconButtonSpacing;
                        var cellStartX = ImGui.GetCursorPosX();
                        var cellWidth = ImGui.GetColumnWidth();
                        var centeredX = cellStartX + MathF.Max(0f, (cellWidth - iconButtonGroupWidth) * 0.5f);
                        ImGui.SetCursorPosX(centeredX);

                        if (DrawIconButton(FontAwesomeIcon.AngleUp, $"shopping-up-{i}", "Move Up", displayIndex > 0, iconButtonSize) && displayIndex > 0)
                        {
                            var previousIndex = activeTargetIndices[displayIndex - 1];
                            (configuration.CurrencyShopTargets[previousIndex], configuration.CurrencyShopTargets[i]) = (configuration.CurrencyShopTargets[i], configuration.CurrencyShopTargets[previousIndex]);
                            NormalizeShoppingTargetPriorities();
                            configuration.Save();
                            ImGui.PopID();
                            break;
                        }
                        ImGui.SameLine();
                        if (DrawIconButton(FontAwesomeIcon.AngleDown, $"shopping-down-{i}", "Move Down", displayIndex < activeTargetIndices.Count - 1, iconButtonSize) && displayIndex < activeTargetIndices.Count - 1)
                        {
                            var nextIndex = activeTargetIndices[displayIndex + 1];
                            (configuration.CurrencyShopTargets[nextIndex], configuration.CurrencyShopTargets[i]) = (configuration.CurrencyShopTargets[i], configuration.CurrencyShopTargets[nextIndex]);
                            NormalizeShoppingTargetPriorities();
                            configuration.Save();
                            ImGui.PopID();
                            break;
                        }

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(targetItemName);

                        ImGui.TableNextColumn();
                        var keepAmount = target.KeepAmount;
                        ImGui.SetNextItemWidth(70f);
                        if (ImGui.InputInt("##keep", ref keepAmount))
                        {
                            target.KeepAmount = Math.Max(0, keepAmount);
                            configuration.Save();
                        }

                        ImGui.TableNextColumn();
                        var buyAmount = target.BuyAmount;
                        ImGui.SetNextItemWidth(70f);
                        if (ImGui.InputInt("##buy", ref buyAmount))
                        {
                            target.BuyAmount = Math.Max(0, buyAmount);
                            configuration.Save();
                        }

                        ImGui.TableNextColumn();
                        var keepBuying = target.KeepBuying;
                        if (ImGui.Checkbox("##keepbuying", ref keepBuying))
                        {
                            if (keepBuying)
                            {
                                foreach (var existingTarget in configuration.CurrencyShopTargets.Where(existingTarget => string.Equals(existingTarget.TerritoryKey, territoryKey, StringComparison.OrdinalIgnoreCase)))
                                {
                                    existingTarget.KeepBuying = false;
                                }
                            }

                            target.KeepBuying = keepBuying;
                            configuration.Save();
                        }

                        ImGui.TableNextColumn();
                        if (DrawIconButton(FontAwesomeIcon.Trash, $"shopping-remove-{i}", "Remove", true, iconButtonSize))
                        {
                            configuration.CurrencyShopTargets.RemoveAt(i);
                            NormalizeShoppingTargetPriorities();
                            configuration.Save();
                            ImGui.PopID();
                            break;
                        }

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }
                ImGui.EndChild();
            }
        }
    }

    private void DrawCurrencySettingsRow(string territoryKey, uint currencyItemId, string label, int threshold)
    {
        var reserve = configuration.CurrencyShopReserves.FirstOrDefault(entry => string.Equals(entry.TerritoryKey, territoryKey, StringComparison.OrdinalIgnoreCase) && entry.CurrencyItemId == currencyItemId);
        var reserveAmount = reserve?.ReserveAmount ?? 0;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt($"##reserve_{currencyItemId}", ref reserveAmount))
        {
            var clampedReserveAmount = Math.Clamp(reserveAmount, 0, 9999);
            reserve ??= new CurrencyShopReserveSetting
            {
                TerritoryKey = territoryKey,
                CurrencyItemId = currencyItemId,
                ReserveAmount = 0,
            };
            if (!configuration.CurrencyShopReserves.Contains(reserve))
            {
                configuration.CurrencyShopReserves.Add(reserve);
            }

            reserve.ReserveAmount = clampedReserveAmount;
            logger.Info($"[Config] op=setting-change key=CurrencyReserve currencyItemId={currencyItemId} new={reserve.ReserveAmount}");
            configuration.Save();
        }

        ImGui.TableNextColumn();
        var thresholdValue = threshold;
        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt($"##threshold_{currencyItemId}", ref thresholdValue))
        {
            var thresholdSetting = configuration.CurrencyShopThresholds.FirstOrDefault(entry => string.Equals(entry.TerritoryKey, territoryKey, StringComparison.OrdinalIgnoreCase) && entry.CurrencyItemId == currencyItemId);
            thresholdSetting ??= new CurrencyShopThresholdSetting { TerritoryKey = territoryKey, CurrencyItemId = currencyItemId };
            if (!configuration.CurrencyShopThresholds.Contains(thresholdSetting))
            {
                configuration.CurrencyShopThresholds.Add(thresholdSetting);
            }

            thresholdSetting.StartThreshold = Math.Clamp(thresholdValue, 0, 9999);
            configuration.Save();
        }
    }

    private void NormalizeShoppingTargetPriorities()
    {
        foreach (var territoryKey in configuration.CurrencyShopTargets
                     .Select(target => target.TerritoryKey)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var priority = 0;
            foreach (var target in configuration.CurrencyShopTargets.Where(target => string.Equals(target.TerritoryKey, territoryKey, StringComparison.OrdinalIgnoreCase)))
            {
                target.Priority = priority++;
            }
        }
    }

    private static bool DrawIconButton(FontAwesomeIcon icon, string id, string tooltip, bool enabled, Vector2 size)
    {
        var clicked = false;
        ImGui.BeginDisabled(!enabled);
        if (ImGui.Button($"##{id}", size))
        {
            clicked = true;
        }
        ImGui.EndDisabled();

        var rectMin = ImGui.GetItemRectMin();
        var rectMax = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var iconColor = ImGui.GetColorU32(enabled ? ImGuiCol.Text : ImGuiCol.TextDisabled);
        var iconText = icon.ToIconString();
        const float iconScale = 0.85f;
        var iconFontSize = UiBuilder.IconFont.FontSize * iconScale;

        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconText) * iconScale;
        ImGui.PopFont();

        var iconPosition = new Vector2(
            rectMin.X + ((rectMax.X - rectMin.X) - iconSize.X) * 0.5f,
            rectMin.Y + ((rectMax.Y - rectMin.Y) - iconSize.Y) * 0.5f);
        drawList.AddText(UiBuilder.IconFont, iconFontSize, iconPosition, iconColor, iconText);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(tooltip);
        }

        return enabled && clicked;
    }

    private void DrawCriticalEncounterCheckboxList()
    {
        var territoryKey = plugin.Scanner.Snapshot.TerritoryKey;
        DrawScrollableCheckboxList(
            "AOCCHCeCheckboxes",
            GetCriticalEncounterEntries(),
            id => configuration.IsCriticalEncounterEnabled(territoryKey, id),
            (id, enabled) => configuration.SetCriticalEncounterEnabled(territoryKey, id, enabled),
            "CriticalEncounter");
    }

    private void DrawFateCheckboxList()
    {
        var territoryKey = plugin.Scanner.Snapshot.TerritoryKey;
        DrawScrollableCheckboxList(
            "AOCCHFateCheckboxes",
            GetDirectFarmFateEntries(),
            id => configuration.IsFateEnabled(territoryKey, id),
            (id, enabled) => configuration.SetFateEnabled(territoryKey, id, enabled),
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

    private void DrawNarrowIntSetting(string label, int currentValue, int minValue, int maxValue, Action<int> applyValue, string logName)
    {
        ImGui.SetNextItemWidth(PotsNumericInputWidth);
        DrawClampedIntSetting(label, currentValue, minValue, maxValue, applyValue, logName);
    }

    private string GetLiveKnowledgeHideLevelLabel(string label, int offset)
    {
        var playerKnowledgeLevel = plugin.Scanner.Snapshot.PlayerForayLevel;
        var hideLevel = playerKnowledgeLevel.HasValue
            ? Math.Clamp(playerKnowledgeLevel.Value + offset, 1, 28).ToString()
            : "?";
        return $"{label} ({hideLevel})";
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
        => (plugin.Scanner.ActiveTerritoryData?.CriticalEncounters ?? [])
            .Select(criticalEncounter => (
                criticalEncounter.Id,
                nameResolver.GetCriticalEncounterName(plugin.Scanner.Snapshot.TerritoryTypeId, criticalEncounter.Id, criticalEncounter.Name)))
            .OrderBy(entry => entry.Item2, StringComparer.Ordinal)
            .ToList();

    private List<(uint Id, string Label)> GetDirectFarmFateEntries()
        => (plugin.Scanner.ActiveTerritoryData?.Fates ?? [])
            .Where(fate => !IsPotFate(fate.Id))
            .Select(fate => (
                fate.Id,
                nameResolver.GetFateName(plugin.Scanner.Snapshot.TerritoryTypeId, fate.Id, fate.Name)))
            .OrderBy(entry => entry.Item2, StringComparer.Ordinal)
            .ToList();

    private bool IsPotFate(uint fateId)
        => plugin.Scanner.ActiveTerritoryData?.PotFates.Any(potFate => potFate.FateId == fateId) == true;

    private bool RequireFeature(bool available, string featureName)
    {
        if (available)
        {
            return true;
        }

        var snapshot = plugin.Scanner.Snapshot;
        ImGui.TextWrapped(snapshot.IsInSupportedTerritory
            ? $"{featureName} is unavailable in {snapshot.TerritoryDisplayName}."
            : $"{featureName} requires a supported Occult Crescent territory.");
        return false;
    }
}
