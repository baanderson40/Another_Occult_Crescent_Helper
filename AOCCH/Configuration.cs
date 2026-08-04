using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using AOCCH.Data;
using AOCCH.Logging;
using AOCCH.Shopping;

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
public sealed class CurrencyShopReserveSetting
{
    public string TerritoryKey { get; set; } = string.Empty;
    public uint CurrencyItemId { get; set; }
    public int ReserveAmount { get; set; }
}

[Serializable]
public sealed class CurrencyShopTarget
{
    public string TerritoryKey { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public int MenuIndex { get; set; }
    public int TabId { get; set; } = -1;
    public int KeepAmount { get; set; } = 1;
    public int BuyAmount { get; set; }
    public bool KeepBuying { get; set; }
    public int Priority { get; set; }
}

[Serializable]
public sealed class CurrencyShopThresholdSetting
{
    public string TerritoryKey { get; set; } = string.Empty;
    public uint CurrencyItemId { get; set; }
    public int StartThreshold { get; set; }
}

[Serializable]
public sealed class TerritoryEventSetting
{
    public string TerritoryKey { get; set; } = string.Empty;
    public uint EventId { get; set; }
}

[Serializable]
public sealed class TerritoryPotStartingSetting
{
    public string TerritoryKey { get; set; } = string.Empty;
    public uint FateId { get; set; }
}

[Serializable]
public sealed class TerritoryPotFarmingSetting
{
    public string TerritoryKey { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

[Serializable]
public sealed class TerritoryVisibleCofferSafetySetting
{
    public string TerritoryKey { get; set; } = string.Empty;
    public bool SkipHighLevelCavernsDuringAshkin { get; set; }
    public bool SkipUnsafeWeatherRoutes { get; set; }
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

    public int Version { get; set; } = 11;

    public string AutorotationPresetName { get; set; } = string.Empty;
    public decimal MeleeTargetRange { get; set; } = 3;
    public decimal RangedTargetRange { get; set; } = 25;
    public bool EnableCriticalEngagementFarming { get; set; } = true;
    public bool EnableFateFarming { get; set; } = true;
    public bool PrioritizeCe { get; set; } = true;
    public FatePriority FatePriority { get; set; } = FatePriority.LowestProgress;
    public List<uint> DisabledCriticalEncounterIds { get; set; } = [];
    public List<uint> DisabledFateIds { get; set; } = [];
    public List<TerritoryEventSetting> DisabledTerritoryCriticalEncounterIds { get; set; } = [];
    public List<TerritoryEventSetting> DisabledTerritoryFateIds { get; set; } = [];
    public bool UseReturn { get; set; } = true;
    public bool EnableBuffRotation { get; set; } = true;
    public int MinimumMountingRange { get; set; } = 20;
    public bool ScannerOnlyMode { get; set; }
    // Retained only to migrate the version 8 global preference into South Horn.
    public bool EnablePotFarming { get; set; } = true;
    public StartingPotFateMode StartingPotFate { get; set; } = StartingPotFateMode.Auto;
    public List<TerritoryPotStartingSetting> StartingPotFates { get; set; } = [];
    public List<TerritoryPotFarmingSetting> PotFarmingTerritories { get; set; } = [];
    public int SpawnLeadMinutes { get; set; } = 5;
    public bool ManageInstanceTime { get; set; }
    public int FateCompletionBudgetMinutes { get; set; } = 5;
    public int TreasureHuntBudgetMinutes { get; set; } = 5;
    public int InstanceExitBufferMinutes { get; set; } = 2;
    public int SpawnArrivalRadius { get; set; } = 18;
    // Retained only to migrate the version 9 Pot fallback cutoff.
    public int MaximumAggroLevel { get; set; } = 20;
    public int PotTreasureAggroLevelOffset { get; set; } = -1;
    public int PotTreasureFallbackMaximumAggroLevel { get; set; } = 20;
    public int VisibleTreasureCofferAggroLevelOffset { get; set; } = -1;
    public int VisibleTreasureCofferFallbackMaximumAggroLevel { get; set; } = 20;
    public bool EnableAutomaticTreasureCofferRoute { get; set; }
    public bool EnableOverworldTreasureGuide { get; set; }
    public bool EnableCofferObservationSubmission { get; set; }
    public int NorthHornStatusDismissedRevision { get; set; }
    public int AutomaticTreasureCofferSilverThreshold { get; set; }
    public int AutomaticTreasureCofferBronzeThreshold { get; set; }
    public bool UseNinjaForDangerousArea { get; set; }
    public int HideThresholdDistance { get; set; } = 120;
    public int PotKnowledgeHideOffset { get; set; }
    public int NinjaGearsetNumber
    {
        get => ninjaGearsetNumber;
        set => SetLinkedNinjaGearsetNumbers(value, updateVisibleCofferGearset: true);
    }

    public bool UseNinjaForDangerousVisibleCoffers { get; set; }
    public int VisibleCofferHideThresholdDistance { get; set; } = 120;
    public int VisibleCofferKnowledgeHideOffset { get; set; }
    public int KnowledgeThreatEnterDistance { get; set; } = 10;
    public int KnowledgeThreatExitDistance { get; set; } = 20;
    public int VisibleCofferNinjaGearsetNumber
    {
        get => visibleCofferNinjaGearsetNumber;
        set => SetLinkedNinjaGearsetNumbers(value, updateVisibleCofferGearset: false);
    }

    public int FateGearsetNumber { get; set; }
    public int FateDismountDistance { get; set; } = 10;
    public int ArrivalDistance { get; set; } = 5;
    public List<TerritoryVisibleCofferSafetySetting> VisibleCofferSafetySettings { get; set; } = [];
    public int CeFallbackCutoffMinutes { get; set; } = 10;
    public int FateFallbackCutoffMinutes { get; set; } = 5;
    public int MainWindowStatusTextScalePercent { get; set; } = 100;
    public bool ShowTooltips { get; set; } = true;
    public bool EnableManualCurrencyShopping { get; set; }
    public int SilverStartThreshold { get; set; }
    public int GoldStartThreshold { get; set; }
    public List<CurrencyShopReserveSetting> CurrencyShopReserves { get; set; } = [];
    public List<CurrencyShopThresholdSetting> CurrencyShopThresholds { get; set; } = [];
    public List<CurrencyShopTarget> CurrencyShopTargets { get; set; } = [];

    [JsonPropertyName("VisibleTreasureCofferMaximumAggroLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyVisibleTreasureCofferMaximumAggroLevel { get; set; }

    [JsonPropertyName("SkipHighLevelCavernsDuringAshkin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacySkipHighLevelCavernsDuringAshkin { get; set; }

    [JsonPropertyName("SkipUnsafeWeatherRoutes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacySkipUnsafeWeatherRoutes { get; set; }

    public bool IsCriticalEncounterEnabled(string territoryKey, uint id)
    {
        NormalizeTerritorySettings();
        return !IsEventDisabled(DisabledTerritoryCriticalEncounterIds, territoryKey, id);
    }

    public bool IsFateEnabled(string territoryKey, uint id)
    {
        NormalizeTerritorySettings();
        return !IsEventDisabled(DisabledTerritoryFateIds, territoryKey, id);
    }

    public bool SetCriticalEncounterEnabled(string territoryKey, uint id, bool enabled)
    {
        NormalizeTerritorySettings();
        var changed = SetEventEnabled(DisabledTerritoryCriticalEncounterIds, territoryKey, id, enabled);
        if (changed)
        {
            logger?.Debug($"Configuration updated CE {id}: enabled={enabled}.");
        }

        return changed;
    }

    public bool SetFateEnabled(string territoryKey, uint id, bool enabled)
    {
        NormalizeTerritorySettings();
        var changed = SetEventEnabled(DisabledTerritoryFateIds, territoryKey, id, enabled);
        if (changed)
        {
            logger?.Debug($"Configuration updated FATE {id}: enabled={enabled}.");
        }

        return changed;
    }

    public uint GetStartingPotFateId(string territoryKey)
    {
        NormalizeTerritorySettings();
        return StartingPotFates.FirstOrDefault(setting => MatchesTerritory(setting.TerritoryKey, territoryKey))?.FateId ?? 0u;
    }

    public bool SetStartingPotFateId(string territoryKey, uint fateId)
    {
        NormalizeTerritorySettings();
        var setting = StartingPotFates.FirstOrDefault(entry => MatchesTerritory(entry.TerritoryKey, territoryKey));
        if (setting == null)
        {
            StartingPotFates.Add(new TerritoryPotStartingSetting { TerritoryKey = territoryKey, FateId = fateId });
            return true;
        }

        if (setting.FateId == fateId)
        {
            return false;
        }

        setting.FateId = fateId;
        return true;
    }

    public bool IsPotFarmingEnabled(string territoryKey)
    {
        NormalizeTerritorySettings();
        var setting = PotFarmingTerritories.FirstOrDefault(entry => MatchesTerritory(entry.TerritoryKey, territoryKey));
        return setting?.Enabled
            ?? string.Equals(territoryKey, "southHorn", StringComparison.OrdinalIgnoreCase);
    }

    public bool SetPotFarmingEnabled(string territoryKey, bool enabled)
    {
        NormalizeTerritorySettings();
        var setting = PotFarmingTerritories.FirstOrDefault(entry => MatchesTerritory(entry.TerritoryKey, territoryKey));
        if (setting == null)
        {
            PotFarmingTerritories.Add(new TerritoryPotFarmingSetting { TerritoryKey = territoryKey, Enabled = enabled });
            return true;
        }

        if (setting.Enabled == enabled)
        {
            return false;
        }

        setting.Enabled = enabled;
        return true;
    }

    public bool GetSkipHighLevelCavernsDuringAshkin(string territoryKey)
    {
        NormalizeTerritorySettings();
        return VisibleCofferSafetySettings.FirstOrDefault(setting => MatchesTerritory(setting.TerritoryKey, territoryKey))?.SkipHighLevelCavernsDuringAshkin == true;
    }

    public bool SetSkipHighLevelCavernsDuringAshkin(string territoryKey, bool enabled)
    {
        NormalizeTerritorySettings();
        var setting = GetOrAddVisibleCofferSafetySetting(territoryKey);
        if (setting.SkipHighLevelCavernsDuringAshkin == enabled)
        {
            return false;
        }

        setting.SkipHighLevelCavernsDuringAshkin = enabled;
        return true;
    }

    public bool GetSkipUnsafeWeatherRoutes(string territoryKey)
    {
        NormalizeTerritorySettings();
        return VisibleCofferSafetySettings.FirstOrDefault(setting => MatchesTerritory(setting.TerritoryKey, territoryKey))?.SkipUnsafeWeatherRoutes == true;
    }

    public bool SetSkipUnsafeWeatherRoutes(string territoryKey, bool enabled)
    {
        NormalizeTerritorySettings();
        var setting = GetOrAddVisibleCofferSafetySetting(territoryKey);
        if (setting.SkipUnsafeWeatherRoutes == enabled)
        {
            return false;
        }

        setting.SkipUnsafeWeatherRoutes = enabled;
        return true;
    }

    private TerritoryVisibleCofferSafetySetting GetOrAddVisibleCofferSafetySetting(string territoryKey)
    {
        var setting = VisibleCofferSafetySettings.FirstOrDefault(entry => MatchesTerritory(entry.TerritoryKey, territoryKey));
        if (setting != null)
        {
            return setting;
        }

        setting = new TerritoryVisibleCofferSafetySetting { TerritoryKey = territoryKey };
        VisibleCofferSafetySettings.Add(setting);
        return setting;
    }

    public int GetCurrencyShopReserve(string territoryKey, uint currencyItemId)
        => CurrencyShopReserves.FirstOrDefault(setting => MatchesTerritory(setting.TerritoryKey, territoryKey) && setting.CurrencyItemId == currencyItemId)?.ReserveAmount ?? 0;

    public int GetCurrencyShopThreshold(string territoryKey, uint currencyItemId)
        => CurrencyShopThresholds.FirstOrDefault(setting => MatchesTerritory(setting.TerritoryKey, territoryKey) && setting.CurrencyItemId == currencyItemId)?.StartThreshold ?? 0;

    private static bool MatchesTerritory(string configuredKey, string territoryKey)
        => string.Equals(configuredKey, territoryKey, StringComparison.OrdinalIgnoreCase);

    private static bool IsEventDisabled(IEnumerable<TerritoryEventSetting> settings, string territoryKey, uint id)
        => settings.Any(setting => MatchesTerritory(setting.TerritoryKey, territoryKey) && setting.EventId == id);

    private static bool SetEventEnabled(List<TerritoryEventSetting> settings, string territoryKey, uint id, bool enabled)
    {
        var existing = settings.FirstOrDefault(setting => MatchesTerritory(setting.TerritoryKey, territoryKey) && setting.EventId == id);
        if (enabled)
        {
            return existing != null && settings.Remove(existing);
        }

        if (existing != null)
        {
            return false;
        }

        settings.Add(new TerritoryEventSetting { TerritoryKey = territoryKey, EventId = id });
        return true;
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
        NormalizeTerritorySettings();
        AutomaticTreasureCofferSilverThreshold = Math.Clamp(AutomaticTreasureCofferSilverThreshold, 0, 8);
        AutomaticTreasureCofferBronzeThreshold = Math.Clamp(AutomaticTreasureCofferBronzeThreshold, 0, 30);
        ClampPotTreasureAggroSettings();
        ClampVisibleCofferAggroSettings();
        MainWindowStatusTextScalePercent = Math.Clamp(MainWindowStatusTextScalePercent, 85, 150);
        MeleeTargetRange = ClampTargetRange(MeleeTargetRange);
        RangedTargetRange = ClampTargetRange(RangedTargetRange);
        ClampKnowledgeThreatSettings();
        ClampCurrencyShopSettings();
        Plugin.PluginInterface.SavePluginConfig(this);
        logger?.Debug("Configuration saved.");
    }

    public bool Migrate(OccultCrescentDataCatalog catalog)
    {
        NormalizeTerritorySettings();
        var southHorn = catalog.GetTerritoryOrNull("southHorn");
        AutomaticTreasureCofferSilverThreshold = Math.Clamp(AutomaticTreasureCofferSilverThreshold, 0, 8);
        AutomaticTreasureCofferBronzeThreshold = Math.Clamp(AutomaticTreasureCofferBronzeThreshold, 0, 30);
        ClampPotTreasureAggroSettings();
        ClampVisibleCofferAggroSettings();
        MainWindowStatusTextScalePercent = Math.Clamp(MainWindowStatusTextScalePercent, 85, 150);
        MeleeTargetRange = ClampTargetRange(MeleeTargetRange);
        RangedTargetRange = ClampTargetRange(RangedTargetRange);
        ClampKnowledgeThreatSettings();
        ClampCurrencyShopSettings();

        if (Version >= 11)
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
            var potFateIds = southHorn?.PotFates.Select(potFate => potFate.FateId).ToHashSet() ?? [];

            foreach (var fate in southHorn?.Fates.Where(fate => !potFateIds.Contains(fate.Id)) ?? [])
            {
                if (excludedNames.Contains(fate.Name))
                {
                    SetFateEnabled("southHorn", fate.Id, enabled: false);
                }
            }
        }

        if (Version < 1)
        {
            LegacyVisibleTreasureCofferMaximumAggroLevel ??= MaximumAggroLevel;
            logger?.Info($"[Configuration] op=migration-copy source=MaximumAggroLevel value={MaximumAggroLevel} target=VisibleTreasureCofferFallbackMaximumAggroLevel");
        }

        if (Version < 8)
        {
            if (LegacyVisibleTreasureCofferMaximumAggroLevel.HasValue)
            {
                VisibleTreasureCofferFallbackMaximumAggroLevel = LegacyVisibleTreasureCofferMaximumAggroLevel.Value;
            }

            LegacyVisibleTreasureCofferMaximumAggroLevel = null;
            logger?.Info($"[Configuration] op=migration-copy source=VisibleTreasureCofferMaximumAggroLevel value={VisibleTreasureCofferFallbackMaximumAggroLevel} target=VisibleTreasureCofferFallbackMaximumAggroLevel");
        }

        if (Version < 2)
        {
            VisibleCofferHideThresholdDistance = HideThresholdDistance;
            SetLinkedNinjaGearsetNumbers(NinjaGearsetNumber, updateVisibleCofferGearset: true);
            logger?.Info($"[Configuration] op=migration-copy source=HideThresholdDistance value={HideThresholdDistance} target=VisibleCofferHideThresholdDistance");
            logger?.Info($"[Configuration] op=migration-copy source=NinjaGearsetNumber value={NinjaGearsetNumber} target=VisibleCofferNinjaGearsetNumber");
        }

        if (Version < 3 && string.Equals(AutorotationPresetName?.Trim(), "Occult", StringComparison.OrdinalIgnoreCase))
        {
            AutorotationPresetName = string.Empty;
            logger?.Info("[Configuration] op=migration-clear legacy autorotation default=Occult");
        }

        if (Version < 4)
        {
            foreach (var reserve in CurrencyShopReserves.Where(reserve => string.IsNullOrWhiteSpace(reserve.TerritoryKey)))
            {
                reserve.TerritoryKey = "southHorn";
            }

            foreach (var target in CurrencyShopTargets.Where(target => string.IsNullOrWhiteSpace(target.TerritoryKey)))
            {
                target.TerritoryKey = "southHorn";
            }

            if (!CurrencyShopThresholds.Any(setting => string.Equals(setting.TerritoryKey, "southHorn", StringComparison.OrdinalIgnoreCase)))
            {
                CurrencyShopThresholds.Add(new CurrencyShopThresholdSetting { TerritoryKey = "southHorn", CurrencyItemId = 45043, StartThreshold = SilverStartThreshold });
                CurrencyShopThresholds.Add(new CurrencyShopThresholdSetting { TerritoryKey = "southHorn", CurrencyItemId = 45044, StartThreshold = GoldStartThreshold });
            }
        }

        if (Version < 5)
        {
            // Legacy selections predate territory profiles and therefore belong to South Horn.
            logger?.Info("[Configuration] op=migration-scope-legacy-events territoryKey=southHorn");
            foreach (var id in DisabledCriticalEncounterIds)
            {
                SetCriticalEncounterEnabled("southHorn", id, enabled: false);
            }

            foreach (var id in DisabledFateIds)
            {
                SetFateEnabled("southHorn", id, enabled: false);
            }

            var legacyStartingPotId = StartingPotFate switch
            {
                StartingPotFateMode.PersistentPots => southHorn?.PotFates.FirstOrDefault(pot => string.Equals(pot.Name, "Persistent Pots", StringComparison.OrdinalIgnoreCase))?.FateId ?? 0u,
                StartingPotFateMode.PleadingPots => southHorn?.PotFates.FirstOrDefault(pot => string.Equals(pot.Name, "Pleading Pots", StringComparison.OrdinalIgnoreCase))?.FateId ?? 0u,
                _ => 0u,
            };
            SetStartingPotFateId("southHorn", legacyStartingPotId);
            DisabledCriticalEncounterIds.Clear();
            DisabledFateIds.Clear();
            StartingPotFate = StartingPotFateMode.Auto;
        }

        if (Version < 9)
        {
            var southEnabled = EnablePotFarming;
            if (!PotFarmingTerritories.Any(setting => MatchesTerritory(setting.TerritoryKey, "southHorn")))
            {
                SetPotFarmingEnabled("southHorn", southEnabled);
            }

            if (!PotFarmingTerritories.Any(setting => MatchesTerritory(setting.TerritoryKey, "northHorn")))
            {
                SetPotFarmingEnabled("northHorn", false);
            }

            logger?.Info($"[Configuration] op=migration-scope-pot-farming territoryKey=southHorn enabled={IsPotFarmingEnabled("southHorn")}");
            logger?.Info($"[Configuration] op=migration-scope-pot-farming territoryKey=northHorn enabled={IsPotFarmingEnabled("northHorn")}");
        }

        if (Version < 10)
        {
            PotTreasureFallbackMaximumAggroLevel = Math.Clamp(MaximumAggroLevel, 0, 50);
            logger?.Info($"[Configuration] op=migration-copy source=MaximumAggroLevel value={MaximumAggroLevel} target=PotTreasureFallbackMaximumAggroLevel value={PotTreasureFallbackMaximumAggroLevel}");
        }

        if (Version < 11)
        {
            var southHornSafety = GetOrAddVisibleCofferSafetySetting("southHorn");
            southHornSafety.SkipHighLevelCavernsDuringAshkin = LegacySkipHighLevelCavernsDuringAshkin ?? false;
            southHornSafety.SkipUnsafeWeatherRoutes = LegacySkipUnsafeWeatherRoutes ?? false;
            LegacySkipHighLevelCavernsDuringAshkin = null;
            LegacySkipUnsafeWeatherRoutes = null;
            logger?.Info($"[Configuration] op=migration-scope-visible-coffer-safety territoryKey=southHorn skipHighLevelCavernsDuringAshkin={southHornSafety.SkipHighLevelCavernsDuringAshkin} skipUnsafeWeatherRoutes={southHornSafety.SkipUnsafeWeatherRoutes}");
        }

        LegacyFarmingMode = null;
        LegacyExcludedFates = null;
        Version = 11;
        logger?.Info("[Configuration] op=migration-complete version=11");
        return true;
    }

    private static decimal ClampTargetRange(decimal value)
        => Math.Clamp(Math.Round(value, 1), 1.1m, 30m);

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

    private void ClampCurrencyShopSettings()
    {
        CurrencyShopReserves ??= [];
        CurrencyShopThresholds ??= [];
        CurrencyShopTargets ??= [];

        foreach (var reserve in CurrencyShopReserves)
        {
            reserve.TerritoryKey ??= string.Empty;
            reserve.ReserveAmount = Math.Clamp(reserve.ReserveAmount, 0, 9999);
        }

        foreach (var threshold in CurrencyShopThresholds)
        {
            threshold.TerritoryKey ??= string.Empty;
            threshold.StartThreshold = Math.Clamp(threshold.StartThreshold, 0, 9999);
        }

        SilverStartThreshold = Math.Clamp(SilverStartThreshold, 0, 9999);
        GoldStartThreshold = Math.Clamp(GoldStartThreshold, 0, 9999);

        foreach (var target in CurrencyShopTargets)
        {
            target.TerritoryKey ??= string.Empty;
            target.TabId = Math.Max(-1, target.TabId);

            target.KeepAmount = Math.Max(0, target.KeepAmount);
            target.BuyAmount = Math.Max(0, target.BuyAmount);
            target.Priority = Math.Max(0, target.Priority);
        }
    }

    private void NormalizeTerritorySettings()
    {
        DisabledTerritoryCriticalEncounterIds ??= [];
        DisabledTerritoryFateIds ??= [];
        StartingPotFates ??= [];
        PotFarmingTerritories ??= [];
        VisibleCofferSafetySettings ??= [];

        foreach (var setting in DisabledTerritoryCriticalEncounterIds)
        {
            setting.TerritoryKey ??= string.Empty;
        }

        foreach (var setting in DisabledTerritoryFateIds)
        {
            setting.TerritoryKey ??= string.Empty;
        }

        foreach (var setting in StartingPotFates)
        {
            setting.TerritoryKey ??= string.Empty;
        }

        foreach (var setting in PotFarmingTerritories)
        {
            setting.TerritoryKey ??= string.Empty;
        }

        foreach (var setting in VisibleCofferSafetySettings)
        {
            setting.TerritoryKey ??= string.Empty;
        }
    }

    private void ClampKnowledgeThreatSettings()
    {
        PotKnowledgeHideOffset = Math.Clamp(PotKnowledgeHideOffset, -49, 49);
        VisibleCofferKnowledgeHideOffset = Math.Clamp(VisibleCofferKnowledgeHideOffset, -49, 49);
        KnowledgeThreatEnterDistance = Math.Clamp(KnowledgeThreatEnterDistance, 1, 50);
        KnowledgeThreatExitDistance = Math.Clamp(KnowledgeThreatExitDistance, KnowledgeThreatEnterDistance, 100);
    }

    private void ClampVisibleCofferAggroSettings()
    {
        VisibleTreasureCofferAggroLevelOffset = Math.Clamp(VisibleTreasureCofferAggroLevelOffset, -40, 50);
        VisibleTreasureCofferFallbackMaximumAggroLevel = Math.Clamp(VisibleTreasureCofferFallbackMaximumAggroLevel, 0, 50);
    }

    private void ClampPotTreasureAggroSettings()
    {
        PotTreasureAggroLevelOffset = Math.Clamp(PotTreasureAggroLevelOffset, -49, 50);
        PotTreasureFallbackMaximumAggroLevel = Math.Clamp(PotTreasureFallbackMaximumAggroLevel, 0, 50);
    }
}
