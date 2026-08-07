using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AOCCH.Data;
using AOCCH.Automation;
using AOCCH.Logging;
using AOCCH.Shopping;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
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
    private const float CombatRotationControlWidth = 160f;
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
        DrawSettingTooltip("Automatically joins and runs Critical Engagements during farm sessions. Turn this off to skip CEs entirely.");

        var prioritizeCe = configuration.PrioritizeCe;
        if (ImGui.Checkbox("Prioritize CE", ref prioritizeCe))
        {
            logger.Info($"[Config] op=setting-change key=PrioritizeCe old={configuration.PrioritizeCe} new={prioritizeCe}");
            configuration.PrioritizeCe = prioritizeCe;
            configuration.Save();
        }
        DrawSettingTooltip("Prioritizes available CEs over running FATEs or other activities.");

        ImGui.Separator();
        ImGui.TextUnformatted("Enabled Critical Engagements");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        DrawSettingTooltip("Pick which CEs to join in this zone. Unchecked CEs will be ignored.");
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
        DrawSettingTooltip("Automatically farms FATEs during farm sessions. Turn this off if you don't want to farm FATEs.");

        var fatePriority = (int)configuration.FatePriority;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("FATE Priority", ref fatePriority, FatePriorityLabels, FatePriorityLabels.Length))
        {
            logger.Info($"[Config] op=setting-change key=FatePriority old={configuration.FatePriority} new={(FatePriority)fatePriority}");
            configuration.FatePriority = (FatePriority)fatePriority;
            configuration.Save();
        }
        DrawSettingTooltip("Decides which FATE to head to next. 'Lowest Progress' targets newly spawned FATEs, while 'Nearest' picks whichever is closest.");

        ImGui.SetNextItemWidth(PotsNumericInputWidth);
        DrawClampedIntSetting(
            "FATE Dismount Distance",
            configuration.FateDismountDistance,
            5,
            50,
            value => configuration.FateDismountDistance = value,
            nameof(configuration.FateDismountDistance));
        DrawSettingTooltip("How close to get to the FATE marker before dismounting (5 to 50 yalms).");

        ImGui.Separator();
        ImGui.TextUnformatted("Enabled FATEs");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        DrawSettingTooltip("Pick which FATEs to farm in this zone. Unchecked FATEs will be skipped.");
        DrawFateCheckboxList();
    }

    private void DrawPotsTab()
    {
        if (!RequireFeature(plugin.Scanner.Snapshot.CanRunPotTreasure, "Pot treasure data"))
        {
            return;
        }

        var territory = plugin.Scanner.ActiveTerritoryData;
        if (territory == null)
        {
            return;
        }

        var enablePotFarming = configuration.IsPotFarmingEnabled(territory.Key);
        if (ImGui.Checkbox($"Enable Pot Farming in {territory.DisplayName}", ref enablePotFarming))
        {
            var oldValue = configuration.IsPotFarmingEnabled(territory.Key);
            logger.Info($"[Config] op=setting-change key=EnablePotFarming territoryKey={territory.Key} old={oldValue} new={enablePotFarming}");
            if (configuration.SetPotFarmingEnabled(territory.Key, enablePotFarming))
            {
                configuration.Save();
            }
        }
        DrawSettingTooltip("Enables automated pot FATE cycling and treasure hunting.");

        ImGui.Separator();
        ImGui.TextUnformatted("Route Setup");

        var potFates = territory.PotFates.OrderBy(pot => pot.Name, StringComparer.Ordinal).ToArray();
        var startingPotFateId = configuration.GetStartingPotFateId(territory.Key);
        var startingPotFate = Array.FindIndex(potFates, pot => pot.FateId == startingPotFateId) + 1;
        var startingPotFateLabels = new[] { "Auto" }.Concat(potFates.Select(pot => $"{pot.Name} ({pot.FateId})")).ToArray();
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Starting Pot FATE", ref startingPotFate, startingPotFateLabels, startingPotFateLabels.Length))
        {
            var nextFateId = startingPotFate == 0 ? 0 : potFates[startingPotFate - 1].FateId;
            logger.Info($"[Config] op=setting-change key=StartingPotFate territoryKey={territory.Key} old={startingPotFateId} new={nextFateId}");
            configuration.SetStartingPotFateId(territory.Key, nextFateId);
            configuration.Save();
        }
        DrawSettingTooltip("Choose which pot FATE to kick off the route with in this zone. 'Auto' picks the best starting point for you.");

        DrawNarrowIntSetting(
            "Spawn Lead Minutes",
            configuration.SpawnLeadMinutes,
            0,
            30,
            value => configuration.SpawnLeadMinutes = value,
            nameof(configuration.SpawnLeadMinutes));
        DrawSettingTooltip("How many minutes before a pot FATE spawns to head over and wait for it.");

        DrawNarrowIntSetting(
            "Arrival Radius",
            configuration.SpawnArrivalRadius,
            0,
            100,
            value => configuration.SpawnArrivalRadius = value,
            nameof(configuration.SpawnArrivalRadius));
        DrawSettingTooltip("How close you need to be to the pot FATE marker before stopping to wait.");

        if (string.Equals(territory.Key, "northHorn", StringComparison.OrdinalIgnoreCase))
        {
            var enableSecondChance = configuration.EnableNorthHornSecondChanceCoffers;
            if (ImGui.Checkbox("Enable Bonus Coffer", ref enableSecondChance))
            {
                logger.Info($"[Config] op=setting-change key=EnableNorthHornSecondChanceCoffers old={configuration.EnableNorthHornSecondChanceCoffers} new={enableSecondChance}");
                configuration.EnableNorthHornSecondChanceCoffers = enableSecondChance;
                configuration.Save();
            }
            DrawSettingTooltip("After the first coffer, returns to Base Camp, uses another Magical Elixir, teleports to the KI-selected area, and searches the high-aggro bonus coffer.");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Dangerous Travel");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        DrawSettingTooltip("This feature is experimental and is recommended for characters at max Knowledge level.");

        var useNinjaForDangerousArea = configuration.UseNinjaForDangerousArea;
        if (ImGui.Checkbox("Use Ninja For Dangerous Area", ref useNinjaForDangerousArea))
        {
            logger.Info($"[Config] op=setting-change key=UseNinjaForDangerousArea old={configuration.UseNinjaForDangerousArea} new={useNinjaForDangerousArea}");
            configuration.UseNinjaForDangerousArea = useNinjaForDangerousArea;
            configuration.Save();
        }
        DrawSettingTooltip("Switches to Ninja and uses Hide to sneak through dangerous high-level areas on foot. (Experimental; recommended for max Knowledge level.)");

        if (configuration.UseNinjaForDangerousArea)
        {
            DrawNarrowIntSetting(
                "Ninja Gearset Number",
                configuration.NinjaGearsetNumber,
                0,
                100,
                value => configuration.NinjaGearsetNumber = value,
                nameof(configuration.NinjaGearsetNumber));
            DrawSettingTooltip("Your Ninja gearset number. Used whenever sneak travel is required.");

            DrawNarrowIntSetting(
                "FATE Gearset Number",
                configuration.FateGearsetNumber,
                0,
                100,
                value => configuration.FateGearsetNumber = value,
                nameof(configuration.FateGearsetNumber));
            DrawSettingTooltip("The gearset number you want to swap to when fighting FATEs.");
        }

        if (configuration.UseNinjaForDangerousArea)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Threat Handling");

            var liveHideLevelLabel = GetLiveKnowledgeHideLevelLabel("Live Knowledge Hide Offset", configuration.PotKnowledgeHideOffset);
            DrawNarrowIntSetting(
                liveHideLevelLabel,
                configuration.PotKnowledgeHideOffset,
                1 - territory.MaximumKnowledgeLevel,
                territory.MaximumKnowledgeLevel - 1,
                value => configuration.PotKnowledgeHideOffset = value,
                nameof(configuration.PotKnowledgeHideOffset));
            DrawSettingTooltip("Adjusts the mob level threshold for using Hide relative to your Knowledge level. Set to 0 to hide from mobs at your level or higher.");

            DrawNarrowIntSetting(
                "Knowledge Threat Enter Range",
                configuration.KnowledgeThreatEnterDistance,
                1,
                50,
                value => configuration.KnowledgeThreatEnterDistance = value,
                nameof(configuration.KnowledgeThreatEnterDistance));
            DrawSettingTooltip("Triggers Hide when a dangerous mob comes within this distance.");
            DrawNarrowIntSetting(
                "Knowledge Threat Exit Range",
                configuration.KnowledgeThreatExitDistance,
                configuration.KnowledgeThreatEnterDistance,
                100,
                value => configuration.KnowledgeThreatExitDistance = value,
                nameof(configuration.KnowledgeThreatExitDistance));
            DrawSettingTooltip("Distance required to clear dangerous mobs before mounting back up. Keep this higher than Enter Range.");

            if (ImGui.CollapsingHeader("Fallback"))
            {
                DrawNarrowIntSetting(
                    "Fallback Maximum Aggro Level",
                    configuration.PotTreasureFallbackMaximumAggroLevel,
                    0,
                    50,
                    value => configuration.PotTreasureFallbackMaximumAggroLevel = value,
                    nameof(configuration.PotTreasureFallbackMaximumAggroLevel));
                DrawSettingTooltip("Mob aggro level limit used as a safety fallback when live zone knowledge data isn't loaded.");

                DrawNarrowIntSetting(
                    "Fallback Hide Threshold Distance",
                    configuration.HideThresholdDistance,
                    0,
                    500,
                    value => configuration.HideThresholdDistance = value,
                    nameof(configuration.HideThresholdDistance));
                DrawSettingTooltip("Threat detection distance used as a fallback when live zone knowledge data isn't loaded.");
            }
        }
        else
        {
            var playerKnowledgeLevel = plugin.Scanner.Snapshot.PlayerForayLevel;
            var aggroOffsetMinimum = playerKnowledgeLevel.HasValue
                ? 1 - playerKnowledgeLevel.Value
                : 1 - territory.MaximumKnowledgeLevel;
            var aggroOffsetMaximum = playerKnowledgeLevel.HasValue
                ? territory.MaximumKnowledgeLevel - playerKnowledgeLevel.Value
                : territory.MaximumKnowledgeLevel - 1;
            DrawNarrowIntSetting(
                "Aggro Level Offset",
                configuration.PotTreasureAggroLevelOffset,
                aggroOffsetMinimum,
                aggroOffsetMaximum,
                value => configuration.PotTreasureAggroLevelOffset = value,
                nameof(configuration.PotTreasureAggroLevelOffset));
            DrawSettingTooltip("Skips pot locations when their aggro level exceeds your Knowledge Level by this offset. -1 allows locations one level below your Knowledge Level.");
            ImGui.SameLine();
            ImGui.TextDisabled($"(Cutoff: {GetPotTreasureAggroCutoffLabel()})");

            if (ImGui.CollapsingHeader("Fallback"))
            {
                DrawNarrowIntSetting(
                    "Fallback Maximum Aggro Level",
                    configuration.PotTreasureFallbackMaximumAggroLevel,
                    0,
                    50,
                    value => configuration.PotTreasureFallbackMaximumAggroLevel = value,
                    nameof(configuration.PotTreasureFallbackMaximumAggroLevel));
                DrawSettingTooltip("Maximum aggro level used when your Knowledge Level is unavailable.");
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
        DrawSettingTooltip("Tracks remaining instance time so you don't start a new pot cycle if you're about to get booted from the zone.");

        DrawNarrowIntSetting(
            "FATE Completion Budget Minutes",
            configuration.FateCompletionBudgetMinutes,
            0,
            60,
            value => configuration.FateCompletionBudgetMinutes = value,
            nameof(configuration.FateCompletionBudgetMinutes));
        DrawSettingTooltip("Estimated time needed to finish a FATE. Won't start a FATE if a pot departure is coming up sooner than this.");
        DrawNarrowIntSetting(
            "Treasure Hunt Budget Minutes",
            configuration.TreasureHuntBudgetMinutes,
            0,
            60,
            value => configuration.TreasureHuntBudgetMinutes = value,
            nameof(configuration.TreasureHuntBudgetMinutes));
        DrawSettingTooltip("Estimated time needed to complete a treasure step before the next pot departure.");
        DrawNarrowIntSetting(
            "Instance Exit Buffer Minutes",
            configuration.InstanceExitBufferMinutes,
            0,
            30,
            value => configuration.InstanceExitBufferMinutes = value,
            nameof(configuration.InstanceExitBufferMinutes));
        DrawSettingTooltip("Safety margin left before the instance timer expires to safely leave or re-queue.");

        DrawNarrowIntSetting(
            "CE Fallback Cutoff Minutes",
            configuration.CeFallbackCutoffMinutes,
            0,
            30,
            value => configuration.CeFallbackCutoffMinutes = value,
            nameof(configuration.CeFallbackCutoffMinutes));
        DrawSettingTooltip("Stops joining fallback CEs if a pot departure is scheduled within this many minutes.");
        DrawNarrowIntSetting(
            "FATE Fallback Cutoff Minutes",
            configuration.FateFallbackCutoffMinutes,
            0,
            30,
            value => configuration.FateFallbackCutoffMinutes = value,
            nameof(configuration.FateFallbackCutoffMinutes));
        DrawSettingTooltip("Stops starting fallback FATEs if a pot departure is scheduled within this many minutes.");

    }

    private void DrawTreasureCoffersTab()
    {
        if (!RequireFeature(plugin.Scanner.Snapshot.CanRunVisibleCofferRoute || plugin.Scanner.Snapshot.CanRunPotTreasure, "Treasure coffer data"))
        {
            return;
        }

        var territory = plugin.Scanner.ActiveTerritoryData;
        if (territory == null)
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
        DrawSettingTooltip("Automatically scans for overworld coffers at base camp using Treasuresight and runs the route if enough coffers are reported.");

        DrawOverworldTreasureGuideSetting();

        DrawNarrowIntSetting(
            "Automatic Silver Threshold",
            configuration.AutomaticTreasureCofferSilverThreshold,
            0,
            8,
            value => configuration.AutomaticTreasureCofferSilverThreshold = value,
            nameof(configuration.AutomaticTreasureCofferSilverThreshold));
        DrawSettingTooltip("Minimum Silver Coffers needed from a Treasuresight scan to trigger the automatic route (0 = any).");
        DrawNarrowIntSetting(
            "Automatic Bronze Threshold",
            configuration.AutomaticTreasureCofferBronzeThreshold,
            0,
            30,
            value => configuration.AutomaticTreasureCofferBronzeThreshold = value,
            nameof(configuration.AutomaticTreasureCofferBronzeThreshold));
        DrawSettingTooltip("Minimum Bronze Coffers needed from a Treasuresight scan to trigger the automatic route (0 = any).");

        ImGui.Separator();
        ImGui.TextUnformatted("Route Setup");

        DrawNarrowIntSetting(
            "Arrival Distance",
            configuration.ArrivalDistance,
            1,
            50,
            value => configuration.ArrivalDistance = value,
            nameof(configuration.ArrivalDistance));
        DrawSettingTooltip("How close to get to a coffer spot before performing a final search and moving on.");

        if (string.Equals(territory.Key, "southHorn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(territory.Key, "northHorn", StringComparison.OrdinalIgnoreCase))
        {
            var skipHighLevelCavernsDuringAshkin = configuration.GetSkipHighLevelCavernsDuringAshkin(territory.Key);
            var ashkinSettingLabel = string.Equals(territory.Key, "northHorn", StringComparison.OrdinalIgnoreCase)
                ? "Skip Ashkin-Time Coffer Positions"
                : "Skip High-Level Caverns During Ashkin";
            if (ImGui.Checkbox(ashkinSettingLabel, ref skipHighLevelCavernsDuringAshkin))
            {
                var oldValue = configuration.GetSkipHighLevelCavernsDuringAshkin(territory.Key);
                logger.Info($"[Config] op=setting-change key=SkipHighLevelCavernsDuringAshkin territoryKey={territory.Key} old={oldValue} new={skipHighLevelCavernsDuringAshkin}");
                configuration.SetSkipHighLevelCavernsDuringAshkin(territory.Key, skipHighLevelCavernsDuringAshkin);
                configuration.Save();
            }

            DrawSettingTooltip(string.Equals(territory.Key, "northHorn", StringComparison.OrdinalIgnoreCase)
                ? "Skips coffer positions marked as unsafe during the North Horn Ashkin window."
                : "Bypasses high-level cavern coffers when aggressive Ashkin mobs are active at night.");

            if (string.Equals(territory.Key, "southHorn", StringComparison.OrdinalIgnoreCase))
            {
                var skipUnsafeWeatherRoutes = configuration.GetSkipUnsafeWeatherRoutes(territory.Key);
                if (ImGui.Checkbox("Skip Unsafe-Weather Routes", ref skipUnsafeWeatherRoutes))
                {
                    var oldValue = configuration.GetSkipUnsafeWeatherRoutes(territory.Key);
                    logger.Info($"[Config] op=setting-change key=SkipUnsafeWeatherRoutes territoryKey={territory.Key} old={oldValue} new={skipUnsafeWeatherRoutes}");
                    configuration.SetSkipUnsafeWeatherRoutes(territory.Key, skipUnsafeWeatherRoutes);
                    configuration.Save();
                }

                DrawSettingTooltip("Avoids dangerous route paths when unsafe weather spawns aggressive mobs.");
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Dangerous Travel");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        DrawSettingTooltip("This feature is experimental and is recommended for characters at max Knowledge level.");

        var useNinjaForDangerousVisibleCoffers = configuration.UseNinjaForDangerousVisibleCoffers;
        if (ImGui.Checkbox("Use Ninja For Dangerous Coffers", ref useNinjaForDangerousVisibleCoffers))
        {
            logger.Info($"[Config] op=setting-change key=UseNinjaForDangerousVisibleCoffers old={configuration.UseNinjaForDangerousVisibleCoffers} new={useNinjaForDangerousVisibleCoffers}");
            configuration.UseNinjaForDangerousVisibleCoffers = useNinjaForDangerousVisibleCoffers;
            configuration.Save();
        }
        DrawSettingTooltip("Switches to Ninja and uses Hide to safely reach dangerous coffer spots on foot. (Experimental; recommended for max Knowledge level.)");

        if (configuration.UseNinjaForDangerousVisibleCoffers)
        {
            DrawNarrowIntSetting(
                "Ninja Gearset Number",
                configuration.VisibleCofferNinjaGearsetNumber,
                0,
                100,
                value => configuration.VisibleCofferNinjaGearsetNumber = value,
                nameof(configuration.VisibleCofferNinjaGearsetNumber));
            DrawSettingTooltip("Your Ninja gearset number. Used whenever sneak travel is required.");

            DrawNarrowIntSetting(
                "FATE Gearset Number",
                configuration.FateGearsetNumber,
                0,
                100,
                value => configuration.FateGearsetNumber = value,
                nameof(configuration.FateGearsetNumber));
            DrawSettingTooltip("The gearset number you want to swap to when fighting FATEs during a coffer run.");
        }

        if (configuration.UseNinjaForDangerousVisibleCoffers)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Threat Handling");

            var liveHideLevelLabel = GetLiveKnowledgeHideLevelLabel("Live Knowledge Hide Offset", configuration.VisibleCofferKnowledgeHideOffset);
            DrawNarrowIntSetting(
                liveHideLevelLabel,
                configuration.VisibleCofferKnowledgeHideOffset,
                1 - (plugin.Scanner.ActiveTerritoryData?.MaximumKnowledgeLevel ?? 28),
                (plugin.Scanner.ActiveTerritoryData?.MaximumKnowledgeLevel ?? 28) - 1,
                value => configuration.VisibleCofferKnowledgeHideOffset = value,
                nameof(configuration.VisibleCofferKnowledgeHideOffset));
            DrawSettingTooltip("Adjusts the mob level threshold for using Hide relative to your Knowledge level. Set to 0 to hide from mobs at your level or higher.");

            DrawNarrowIntSetting(
                "Knowledge Threat Enter Range",
                configuration.KnowledgeThreatEnterDistance,
                1,
                50,
                value => configuration.KnowledgeThreatEnterDistance = value,
                nameof(configuration.KnowledgeThreatEnterDistance));
            DrawSettingTooltip("Triggers Hide when a dangerous mob gets within this distance.");
            DrawNarrowIntSetting(
                "Knowledge Threat Exit Range",
                configuration.KnowledgeThreatExitDistance,
                configuration.KnowledgeThreatEnterDistance,
                100,
                value => configuration.KnowledgeThreatExitDistance = value,
                nameof(configuration.KnowledgeThreatExitDistance));
            DrawSettingTooltip("Distance required to clear dangerous mobs before mounting back up. Keep this higher than Enter Range.");

            if (ImGui.CollapsingHeader("Fallback"))
            {
                DrawNarrowIntSetting(
                    "Fallback Maximum Aggro Level",
                    configuration.VisibleTreasureCofferFallbackMaximumAggroLevel,
                    0,
                    50,
                    value => configuration.VisibleTreasureCofferFallbackMaximumAggroLevel = value,
                    nameof(configuration.VisibleTreasureCofferFallbackMaximumAggroLevel));
                DrawSettingTooltip("Maximum aggro level used when your Knowledge Level is unavailable.");

                DrawNarrowIntSetting(
                    "Fallback Hide Threshold Distance",
                    configuration.VisibleCofferHideThresholdDistance,
                    0,
                    500,
                    value => configuration.VisibleCofferHideThresholdDistance = value,
                    nameof(configuration.VisibleCofferHideThresholdDistance));
                DrawSettingTooltip("Threat detection distance used as a fallback when live zone knowledge data isn't loaded.");
            }
        }
        else
        {
            var playerKnowledgeLevel = plugin.Scanner.Snapshot.PlayerForayLevel;
            var aggroOffsetMinimum = playerKnowledgeLevel.HasValue ? 1 - playerKnowledgeLevel.Value : -40;
            var aggroOffsetMaximum = playerKnowledgeLevel.HasValue ? 50 - playerKnowledgeLevel.Value : 50;
            DrawNarrowIntSetting(
                "Aggro Level Offset",
                configuration.VisibleTreasureCofferAggroLevelOffset,
                aggroOffsetMinimum,
                aggroOffsetMaximum,
                value => configuration.VisibleTreasureCofferAggroLevelOffset = value,
                nameof(configuration.VisibleTreasureCofferAggroLevelOffset));
            DrawSettingTooltip("Skips coffer spots when their aggro level exceeds your Knowledge Level by this offset. -1 allows spots one level below your Knowledge Level.");
            ImGui.SameLine();
            ImGui.TextDisabled($"(Cutoff: {GetVisibleCofferAggroCutoffLabel()})");

            if (ImGui.CollapsingHeader("Fallback"))
            {
                DrawNarrowIntSetting(
                    "Fallback Maximum Aggro Level",
                    configuration.VisibleTreasureCofferFallbackMaximumAggroLevel,
                    0,
                    50,
                    value => configuration.VisibleTreasureCofferFallbackMaximumAggroLevel = value,
                    nameof(configuration.VisibleTreasureCofferFallbackMaximumAggroLevel));
                DrawSettingTooltip("Maximum aggro level used when your Knowledge Level is unavailable.");
            }
        }

    }

    private void DrawOverworldTreasureGuideSetting()
    {
        var enableOverworldTreasureGuide = configuration.EnableOverworldTreasureGuide;
        if (ImGui.Checkbox("Enable Overworld Treasure Guide", ref enableOverworldTreasureGuide))
        {
            logger.Info($"[Config] op=setting-change key=EnableOverworldTreasureGuide old={configuration.EnableOverworldTreasureGuide} new={enableOverworldTreasureGuide}");
            configuration.EnableOverworldTreasureGuide = enableOverworldTreasureGuide;
            configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        DrawSettingTooltip("Draws a visual guide line and marker in-game pointing to the closest coffer. Purely visual—it doesn't automate movement.");
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
        DrawSettingTooltip("Anonymously sends confirmed coffer locations to help map out spawn points for the community. No character or account data is ever sent.");

        ImGui.Separator();
        ImGui.TextUnformatted("Combat");

        var providers = AutorotationProviderDiscovery.GetAvailable();
        var provider = configuration.AutorotationProviderUserSelected && providers.Contains(configuration.AutorotationProvider)
            ? configuration.AutorotationProvider
            : AutorotationProviderDiscovery.GetDefault(providers) ?? configuration.AutorotationProvider;
        var providerIndex = Math.Max(0, providers.ToList().IndexOf(provider));
        var providerLabels = providers.Select(AutorotationProviderDiscovery.GetDisplayName).ToArray();
        var automationRunning = plugin.FarmSessionController.IsRunning
            || plugin.CriticalEngagementAutomationController.IsRunning
            || plugin.FateAutomationController.IsRunning
            || plugin.PotFarmController.IsRunning
            || plugin.TreasureCofferFarmController.IsRunning;
        var autorotationPresetName = configuration.AutorotationPresetName;
        var presetWidth = CombatRotationControlWidth;
        ImGui.BeginDisabled(automationRunning || providers.Count == 0);
        ImGui.SetNextItemWidth(presetWidth);
        if (ImGui.Combo("Autorotation Provider", ref providerIndex, providerLabels, providerLabels.Length))
        {
            var selectedProvider = providers[providerIndex];
            logger.Info($"[Config] op=setting-change key=AutorotationProvider old={configuration.AutorotationProvider} new={selectedProvider}");
            configuration.AutorotationProvider = selectedProvider;
            configuration.AutorotationProviderUserSelected = true;
            configuration.Save();
        }
        ImGui.EndDisabled();
        DrawSettingTooltip(automationRunning
            ? "Stop automation before changing the autorotation provider."
            : "Only enabled and loaded rotation plugins are listed. BossMod or BossModReborn is required for dodging.");

        var bossModProvider = provider is AutorotationProvider.BossMod or AutorotationProvider.BossModReborn;
        ImGui.BeginDisabled(!bossModProvider);
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
        ImGui.EndDisabled();

        DrawTargetRangeSetting("Melee Target Range", configuration.MeleeTargetRange, value => configuration.MeleeTargetRange = value, nameof(configuration.MeleeTargetRange));
        DrawSettingTooltip("Max targeting range for melee jobs before engaging (1.1 to 30 yalms).");
        DrawTargetRangeSetting("Ranged Target Range", configuration.RangedTargetRange, value => configuration.RangedTargetRange = value, nameof(configuration.RangedTargetRange));
        DrawSettingTooltip("Max targeting range for ranged and caster jobs before engaging (1.1 to 30 yalms).");

        ImGui.Separator();
        ImGui.TextUnformatted("Automation");

        var enableBuffRotation = configuration.EnableBuffRotation;
        if (ImGui.Checkbox("Enable Buff Rotation", ref enableBuffRotation))
        {
            logger.Info($"[Config] op=setting-change key=EnableBuffRotation old={configuration.EnableBuffRotation} new={enableBuffRotation}");
            configuration.EnableBuffRotation = enableBuffRotation;
            configuration.Save();
        }
        DrawSettingTooltip("Automatically applies job and foray buff actions during combat and route travel.");

        ImGui.Separator();
        ImGui.TextUnformatted("Movement");

        var useReturn = configuration.UseReturn;
        if (ImGui.Checkbox("Use Return", ref useReturn))
        {
            logger.Info($"[Config] op=setting-change key=UseReturn old={configuration.UseReturn} new={useReturn}");
            configuration.UseReturn = useReturn;
            configuration.Save();
        }
        DrawSettingTooltip("Uses the Return spell to quickly teleport back to base camp when needed.");

        var minimumMountingRange = configuration.MinimumMountingRange;
        ImGui.SetNextItemWidth(SettingsNumericInputWidth);
        if (ImGui.InputInt("Minimum Mounting Range", ref minimumMountingRange))
        {
            var nextValue = Math.Clamp(minimumMountingRange, 0, 100);
            logger.InfoThrottled("setting-minimum-mounting-range", SettingTextLogInterval, $"Setting changed: MinimumMountingRange: {configuration.MinimumMountingRange} -> {nextValue}.");
            configuration.MinimumMountingRange = nextValue;
            configuration.Save();
        }
        DrawSettingTooltip("Only mounts up if your destination is further away than this distance. Walks instead for shorter distances.");

        ImGui.Separator();
        ImGui.TextUnformatted("Interface");

        var mainWindowStatusTextScalePercent = configuration.MainWindowStatusTextScalePercent;
        ImGui.SetNextItemWidth(CombatRotationControlWidth);
        if (ImGui.SliderInt("Main Window Status Text Size", ref mainWindowStatusTextScalePercent, 85, 150, "%d%%"))
        {
            var nextValue = Math.Clamp(mainWindowStatusTextScalePercent, 85, 150);
            logger.Info($"[Config] op=setting-change key=MainWindowStatusTextScalePercent old={configuration.MainWindowStatusTextScalePercent} new={nextValue}");
            configuration.MainWindowStatusTextScalePercent = nextValue;
            configuration.Save();
        }
        DrawSettingTooltip("Adjusts the status font size in the main window (85% to 150%).");

        var showTooltips = configuration.ShowTooltips;
        if (ImGui.Checkbox("Show Tooltips", ref showTooltips))
        {
            logger.Info($"[Config] op=setting-change key=ShowTooltips old={configuration.ShowTooltips} new={showTooltips}");
            configuration.ShowTooltips = showTooltips;
            configuration.Save();
        }
        DrawSettingTooltip("Shows helpful descriptions when you hover over settings and interface buttons.");
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
        DrawSettingTooltip("Automatically buys items from zone vendors based on your shopping list.");

        ImGui.TextWrapped($"Shopping: {plugin.ManualCurrencyShoppingController.CurrentStatusSummary}");

        ImGui.Separator();
        ImGui.TextUnformatted("Currency");
        if (ImGui.BeginTable("ShoppingCurrencySettings", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("");
            ImGui.TableSetupColumn("Reserved", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("Threshold", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableHeadersRow();

            ImGui.TableSetColumnIndex(1);
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            DrawSettingTooltip("Amount of this currency to save and never spend automatically.");
            ImGui.TableSetColumnIndex(2);
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            DrawSettingTooltip("Triggers a vendor visit once you hold at least this much currency.");

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
                            KeepAmount = 0,
                            BuyAmount = 1,
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
        if (configuration.ShowTooltips && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Order matters—items at the top get bought first. Items process Keep targets first, then Buy targets, then Keep Buying.");
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
                        DrawSettingTooltip("Target stock to keep in your inventory. AOCCH buys enough to maintain this amount.");

                        ImGui.TableNextColumn();
                        var buyAmount = target.BuyAmount;
                        ImGui.SetNextItemWidth(70f);
                        if (ImGui.InputInt("##buy", ref buyAmount))
                        {
                            target.BuyAmount = Math.Max(0, buyAmount);
                            configuration.Save();
                        }
                        DrawSettingTooltip("One-time purchase quantity. Once bought, it won't keep re-buying.");

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
                        DrawSettingTooltip("Continuously dumps extra currency into this item whenever available. Only one item can have this set at a time.");

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
        DrawSettingTooltip("Amount of this currency to save and never spend automatically.");

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
        DrawSettingTooltip("Triggers a vendor visit once you hold at least this much currency.");
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

    private bool DrawIconButton(FontAwesomeIcon icon, string id, string tooltip, bool enabled, Vector2 size)
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

        if (configuration.ShowTooltips && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
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
        var maximumKnowledgeLevel = plugin.Scanner.ActiveTerritoryData?.MaximumKnowledgeLevel ?? 28;
        var hideLevel = playerKnowledgeLevel.HasValue
            ? Math.Clamp(playerKnowledgeLevel.Value + offset, 1, maximumKnowledgeLevel).ToString()
            : "?";
        return $"{label} ({hideLevel})";
    }

    private string GetVisibleCofferAggroCutoffLabel()
    {
        var playerKnowledgeLevel = plugin.Scanner.Snapshot.PlayerForayLevel;
        return playerKnowledgeLevel.HasValue
            ? Math.Clamp(playerKnowledgeLevel.Value + configuration.VisibleTreasureCofferAggroLevelOffset, 1, 50).ToString()
            : $"fallback {configuration.VisibleTreasureCofferFallbackMaximumAggroLevel}";
    }

    private string GetPotTreasureAggroCutoffLabel()
    {
        var playerKnowledgeLevel = plugin.Scanner.Snapshot.PlayerForayLevel;
        var maximumKnowledgeLevel = plugin.Scanner.ActiveTerritoryData?.MaximumKnowledgeLevel ?? 28;
        return playerKnowledgeLevel.HasValue
            ? Math.Clamp(playerKnowledgeLevel.Value + configuration.PotTreasureAggroLevelOffset, 1, maximumKnowledgeLevel).ToString()
            : $"fallback {configuration.PotTreasureFallbackMaximumAggroLevel}";
    }

    private void DrawSettingTooltip(string text)
    {
        if (!configuration.ShowTooltips || !ImGui.IsItemHovered())
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
