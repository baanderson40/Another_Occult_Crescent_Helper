using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using AOCCH.Data;
using AOCCH.Logging;

namespace AOCCH;

public enum FatePriority
{
    LowestProgress,
    Nearest,
}

public enum StartingPotFateMode
{
    Auto,
    PersistentPots,
    PleadingPots,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    [JsonIgnore]
    private AocchLogger? logger;

    [JsonIgnore]
    private bool syncingNinjaGearsetNumbers;

    private int ninjaGearsetNumber;
    private int visibleCofferNinjaGearsetNumber;

    public int Version { get; set; } = 2;

    public string AutorotationPresetName { get; set; } = "Occult";
    public bool EnableCriticalEngagementFarming { get; set; } = true;
    public bool EnableFateFarming { get; set; } = true;
    public bool PrioritizeCe { get; set; } = true;
    public FatePriority FatePriority { get; set; } = FatePriority.LowestProgress;
    public List<uint> DisabledCriticalEncounterIds { get; set; } = [];
    public List<uint> DisabledFateIds { get; set; } = [];
    public bool UseReturn { get; set; } = true;
    public bool EnableBuffRotation { get; set; } = true;
    public int MinimumMountingRange { get; set; } = 20;
    public bool ScannerOnlyMode { get; set; }
    public bool EnablePotFarming { get; set; } = true;
    public StartingPotFateMode StartingPotFate { get; set; } = StartingPotFateMode.Auto;
    public int SpawnLeadMinutes { get; set; } = 5;
    public bool ManageInstanceTime { get; set; } = true;
    public int FateCompletionBudgetMinutes { get; set; } = 5;
    public int TreasureHuntBudgetMinutes { get; set; } = 5;
    public int InstanceExitBufferMinutes { get; set; } = 2;
    public int SpawnArrivalRadius { get; set; } = 18;
    public int MaximumAggroLevel { get; set; } = 19;
    public int VisibleTreasureCofferMaximumAggroLevel { get; set; } = 19;
    public bool EnableAutomaticTreasureCofferRoute { get; set; }
    public int AutomaticTreasureCofferSilverThreshold { get; set; }
    public int AutomaticTreasureCofferBronzeThreshold { get; set; }
    public bool UseNinjaForDangerousArea { get; set; }
    public int HideThresholdDistance { get; set; } = 120;
    public int NinjaGearsetNumber
    {
        get => ninjaGearsetNumber;
        set => SetLinkedNinjaGearsetNumbers(value, updateVisibleCofferGearset: true);
    }

    public bool UseNinjaForDangerousVisibleCoffers { get; set; }
    public int VisibleCofferHideThresholdDistance { get; set; } = 120;
    public int VisibleCofferNinjaGearsetNumber
    {
        get => visibleCofferNinjaGearsetNumber;
        set => SetLinkedNinjaGearsetNumbers(value, updateVisibleCofferGearset: false);
    }

    public int FateGearsetNumber { get; set; }
    public int FateDismountDistance { get; set; } = 10;
    public int ArrivalDistance { get; set; } = 5;
    public bool SkipHighLevelCavernsDuringAshkin { get; set; }
    public int CeFallbackCutoffMinutes { get; set; } = 10;
    public int FateFallbackCutoffMinutes { get; set; } = 5;
    public int MainWindowStatusTextScalePercent { get; set; } = 100;

    public bool IsCriticalEncounterEnabled(uint id)
        => !DisabledCriticalEncounterIds.Contains(id);

    public bool IsFateEnabled(uint id)
        => !DisabledFateIds.Contains(id);

    public bool SetCriticalEncounterEnabled(uint id, bool enabled)
    {
        var changed = SetIdEnabled(DisabledCriticalEncounterIds, id, enabled);
        if (changed)
        {
            logger?.Debug($"Configuration updated CE {id}: enabled={enabled}.");
        }

        return changed;
    }

    public bool SetFateEnabled(uint id, bool enabled)
    {
        var changed = SetIdEnabled(DisabledFateIds, id, enabled);
        if (changed)
        {
            logger?.Debug($"Configuration updated FATE {id}: enabled={enabled}.");
        }

        return changed;
    }

    [JsonPropertyName("FarmingMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyFarmingMode { get; set; }

    [JsonPropertyName("ExcludedFates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyExcludedFates { get; set; }

    // The below exists just to make saving less cumbersome
    public void SetLogger(AocchLogger logger)
        => this.logger = logger;

    public void Save()
    {
        AutomaticTreasureCofferSilverThreshold = Math.Clamp(AutomaticTreasureCofferSilverThreshold, 0, 8);
        AutomaticTreasureCofferBronzeThreshold = Math.Clamp(AutomaticTreasureCofferBronzeThreshold, 0, 30);
        MainWindowStatusTextScalePercent = Math.Clamp(MainWindowStatusTextScalePercent, 85, 150);
        Plugin.PluginInterface.SavePluginConfig(this);
        logger?.Debug("Configuration saved.");
    }

    public bool Migrate(OccultCrescentData data)
    {
        AutomaticTreasureCofferSilverThreshold = Math.Clamp(AutomaticTreasureCofferSilverThreshold, 0, 8);
        AutomaticTreasureCofferBronzeThreshold = Math.Clamp(AutomaticTreasureCofferBronzeThreshold, 0, 30);
        MainWindowStatusTextScalePercent = Math.Clamp(MainWindowStatusTextScalePercent, 85, 150);

        if (Version >= 2)
        {
            logger?.Debug($"Configuration migration skipped because version {Version} is current.");
            return false;
        }

        logger?.Info($"[Configuration] op=migration-start version={Version}");

        if (Version < 1 && LegacyFarmingMode.HasValue)
        {
            switch (LegacyFarmingMode.Value)
            {
                case 1:
                    EnableCriticalEngagementFarming = true;
                    EnableFateFarming = false;
                    break;
                case 2:
                    EnableCriticalEngagementFarming = false;
                    EnableFateFarming = true;
                    break;
                default:
                    EnableCriticalEngagementFarming = true;
                    EnableFateFarming = true;
                    break;
            }
        }

        if (Version < 1 && !string.IsNullOrWhiteSpace(LegacyExcludedFates))
        {
            var excludedNames = LegacyExcludedFates
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var potFateIds = data.PotFates.Select(potFate => potFate.FateId).ToHashSet();

            foreach (var fate in data.Fates.Where(fate => !potFateIds.Contains(fate.Id)))
            {
                if (excludedNames.Contains(fate.Name))
                {
                    SetFateEnabled(fate.Id, enabled: false);
                }
            }
        }

        if (Version < 1)
        {
            VisibleTreasureCofferMaximumAggroLevel = MaximumAggroLevel;
            logger?.Info($"[Configuration] op=migration-copy source=MaximumAggroLevel value={MaximumAggroLevel} target=VisibleTreasureCofferMaximumAggroLevel");
        }

        if (Version < 2)
        {
            VisibleCofferHideThresholdDistance = HideThresholdDistance;
            SetLinkedNinjaGearsetNumbers(NinjaGearsetNumber, updateVisibleCofferGearset: true);
            logger?.Info($"[Configuration] op=migration-copy source=HideThresholdDistance value={HideThresholdDistance} target=VisibleCofferHideThresholdDistance");
            logger?.Info($"[Configuration] op=migration-copy source=NinjaGearsetNumber value={NinjaGearsetNumber} target=VisibleCofferNinjaGearsetNumber");
        }

        LegacyFarmingMode = null;
        LegacyExcludedFates = null;
        Version = 2;
        logger?.Info("[Configuration] op=migration-complete version=2");
        return true;
    }

    private void SetLinkedNinjaGearsetNumbers(int value, bool updateVisibleCofferGearset)
    {
        if (syncingNinjaGearsetNumbers)
        {
            if (updateVisibleCofferGearset)
            {
                ninjaGearsetNumber = value;
            }
            else
            {
                visibleCofferNinjaGearsetNumber = value;
            }

            return;
        }

        syncingNinjaGearsetNumbers = true;
        try
        {
            ninjaGearsetNumber = value;
            visibleCofferNinjaGearsetNumber = value;
        }
        finally
        {
            syncingNinjaGearsetNumbers = false;
        }
    }

    private static bool SetIdEnabled(List<uint> disabledIds, uint id, bool enabled)
    {
        var wasDisabled = disabledIds.Contains(id);
        if (enabled)
        {
            if (!wasDisabled)
            {
                return false;
            }

            disabledIds.RemoveAll(existingId => existingId == id);
            return true;
        }

        if (wasDisabled)
        {
            return false;
        }

        disabledIds.Add(id);
        return true;
    }
}
