using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using AOCCH.Data;

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
    public int Version { get; set; } = 1;

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
    public StartingPotFateMode StartingPotFate { get; set; } = StartingPotFateMode.Auto;
    public int SpawnLeadMinutes { get; set; } = 5;
    public bool ManageInstanceTime { get; set; } = true;
    public int FateCompletionBudgetMinutes { get; set; } = 5;
    public int TreasureHuntBudgetMinutes { get; set; } = 5;
    public int InstanceExitBufferMinutes { get; set; } = 2;
    public int SpawnArrivalRadius { get; set; } = 18;
    public int MaximumAggroLevel { get; set; } = 19;
    public bool UseNinjaForDangerousArea { get; set; }
    public int HideThresholdDistance { get; set; } = 120;
    public int NinjaGearsetNumber { get; set; }
    public int FateGearsetNumber { get; set; }
    public int CeFallbackCutoffMinutes { get; set; } = 10;
    public int FateFallbackCutoffMinutes { get; set; } = 5;

    public bool IsCriticalEncounterEnabled(uint id)
        => !DisabledCriticalEncounterIds.Contains(id);

    public bool IsFateEnabled(uint id)
        => !DisabledFateIds.Contains(id);

    public bool SetCriticalEncounterEnabled(uint id, bool enabled)
        => SetIdEnabled(DisabledCriticalEncounterIds, id, enabled);

    public bool SetFateEnabled(uint id, bool enabled)
        => SetIdEnabled(DisabledFateIds, id, enabled);

    [JsonPropertyName("FarmingMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyFarmingMode { get; set; }

    [JsonPropertyName("ExcludedFates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyExcludedFates { get; set; }

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }

    public bool Migrate(OccultCrescentData data)
    {
        if (Version >= 1)
        {
            return false;
        }

        if (LegacyFarmingMode.HasValue)
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

        if (!string.IsNullOrWhiteSpace(LegacyExcludedFates))
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

        LegacyFarmingMode = null;
        LegacyExcludedFates = null;
        Version = 1;
        return true;
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
