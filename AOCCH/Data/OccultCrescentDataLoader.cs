using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AOCCH.Logging;
using AOCCH.Shopping;
using Dalamud.Plugin;

namespace AOCCH.Data;

public static class OccultCrescentDataLoader
{
    // This shipped JSON is the canonical coffer dataset. The Lua route map and
    // tracker notes in knowledge-base are historical source material only.
    private const string DataFileName = "OccultCrescentData.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static OccultCrescentDataCatalog Load(IDalamudPluginInterface pluginInterface, AocchLogger logger)
    {
        var assemblyDirectory = pluginInterface.AssemblyLocation.DirectoryName;
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            logger.Error("Could not resolve plugin assembly directory for Occult Crescent data loading.");
            return new OccultCrescentDataCatalog();
        }

        var path = Path.Combine(assemblyDirectory, "Data", DataFileName);
        try
        {
            if (!File.Exists(path))
            {
                logger.Error($"Occult Crescent data file was not found: {path}");
                return new OccultCrescentDataCatalog();
            }

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<OccultCrescentDataCatalog>(json, SerializerOptions) ?? new OccultCrescentDataCatalog();
            var validated = ValidateCatalog(data, logger);
            validated.RebuildLookups();
            LogCatalogSummary(validated, logger);
            return validated;
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to load Occult Crescent data from {path}: {ex}");
            return new OccultCrescentDataCatalog();
        }
    }

    private static OccultCrescentDataCatalog ValidateCatalog(OccultCrescentDataCatalog catalog, AocchLogger logger)
    {
        var validTerritories = new List<OccultCrescentTerritoryData>();
        var duplicateKeys = catalog.Territories
            .Where(territory => !string.IsNullOrWhiteSpace(territory.Key))
            .GroupBy(territory => territory.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateTerritoryIds = catalog.Territories
            .Where(territory => territory.TerritoryTypeId != 0)
            .GroupBy(territory => territory.TerritoryTypeId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        foreach (var territory in catalog.Territories)
        {
            var normalizedTerritory = NormalizeTerritory(territory, logger);
            var errors = ValidateTerritory(normalizedTerritory, duplicateKeys, duplicateTerritoryIds);
            if (errors.Count > 0)
            {
                logger.Error($"[OccultCrescentDataLoader] op=territory-invalid key={FormatTerritoryKey(territory.Key)} territoryId={territory.TerritoryTypeId} errors={string.Join(" | ", errors)}");
                continue;
            }

            validTerritories.Add(normalizedTerritory);
        }

        var removedTerritoryCount = catalog.Territories.Count - validTerritories.Count;
        if (removedTerritoryCount > 0)
        {
            logger.Warning($"[OccultCrescentDataLoader] op=validation-removals removedTerritories={removedTerritoryCount} retainedTerritories={validTerritories.Count} totalTerritories={catalog.Territories.Count}");
        }

        return new OccultCrescentDataCatalog
        {
            Territories = validTerritories,
        };
    }

    private static OccultCrescentTerritoryData NormalizeTerritory(OccultCrescentTerritoryData territory, AocchLogger logger)
    {
        if (!string.Equals(territory.Key, "southHorn", StringComparison.OrdinalIgnoreCase)
            || territory.Shopping.Pages.Count > 0)
        {
            return territory;
        }

        // South Horn's established catalog remains the migration source until it is exported to JSON.
        var shopping = ShopCurrencyCatalog.CreateSouthHornData(territory.Shopping.Vendors);
        logger.Info($"[OccultCrescentDataLoader] op=shopping-pages-generated key={territory.Key} source=legacy vendors={shopping.Vendors.Count} pages={shopping.Pages.Count}");
        return new OccultCrescentTerritoryData
        {
            Key = territory.Key,
            DisplayName = territory.DisplayName,
            TerritoryTypeId = territory.TerritoryTypeId,
            Features = territory.Features,
            AethernetInteractDistanceMin = territory.AethernetInteractDistanceMin,
            AethernetInteractDistanceMax = territory.AethernetInteractDistanceMax,
            MountedTravelSpeed = territory.MountedTravelSpeed,
            Aethernets = territory.Aethernets,
            CriticalEncounters = territory.CriticalEncounters,
            Fates = territory.Fates,
            PotFates = territory.PotFates,
            TreasureCofferGroups = territory.TreasureCofferGroups,
            VisibleCofferFarmSpots = territory.VisibleCofferFarmSpots,
            VisibleCofferFarmRoute = territory.VisibleCofferFarmRoute,
            FateAethernetPreferences = territory.FateAethernetPreferences,
            Shopping = shopping,
            Drops = territory.Drops,
            VisibleCoffers = territory.VisibleCoffers,
            PotTreasure = territory.PotTreasure,
        };
    }

    private static List<string> ValidateTerritory(
        OccultCrescentTerritoryData territory,
        ISet<string> duplicateKeys,
        ISet<uint> duplicateTerritoryIds)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(territory.Key))
        {
            errors.Add("missing territory key");
        }
        else if (duplicateKeys.Contains(territory.Key))
        {
            errors.Add($"duplicate territory key '{territory.Key}'");
        }

        if (string.IsNullOrWhiteSpace(territory.DisplayName))
        {
            errors.Add("missing display name");
        }

        if (territory.TerritoryTypeId == 0)
        {
            errors.Add("territory type id must be nonzero");
        }
        else if (duplicateTerritoryIds.Contains(territory.TerritoryTypeId))
        {
            errors.Add($"duplicate territory type id {territory.TerritoryTypeId}");
        }

        ValidateUniqueIds(territory.CriticalEncounters.Select(encounter => encounter.Id), "critical encounter", errors);
        ValidateUniqueIds(territory.Fates.Select(fate => fate.Id), "fate", errors);
        ValidateUniqueIds(territory.PotFates.Select(fate => fate.FateId), "pot fate", errors);
        ValidateNonzeroIds(territory.CriticalEncounters.Select(encounter => encounter.Id), "critical encounter", errors);
        ValidateNonzeroIds(territory.Fates.Select(fate => fate.Id), "fate", errors);
        ValidateNonzeroIds(territory.PotFates.Select(fate => fate.FateId), "pot fate", errors);
        ValidateAethernetNames(territory, errors);
        ValidateTreasureGroups(territory, errors);
        ValidatePotTreasure(territory, errors);
        ValidateVisibleCofferSpots(territory, errors);
        ValidateVisibleCoffers(territory, errors);
        ValidateVisibleCofferSafety(territory, errors);
        ValidateAethernetReferences(territory, errors);
        ValidateShopping(territory, errors);

        return errors;
    }

    private static void ValidateShopping(OccultCrescentTerritoryData territory, List<string> errors)
    {
        if (!territory.Features.Shopping)
        {
            return;
        }

        if (territory.Shopping.Vendors.Count == 0)
        {
            errors.Add("shopping is enabled but no vendors are defined");
        }

        if (territory.Shopping.Pages.Count == 0)
        {
            errors.Add("shopping is enabled but no shop pages are defined");
        }

        foreach (var vendor in territory.Shopping.Vendors)
        {
            if (vendor.DataId == 0)
            {
                errors.Add("shopping vendor data id must be nonzero");
            }

            if (string.IsNullOrWhiteSpace(vendor.Name))
            {
                errors.Add($"shopping vendor dataId={vendor.DataId} is missing a name");
            }
        }

        foreach (var duplicateVendorId in territory.Shopping.Vendors
                     .Where(vendor => vendor.DataId != 0)
                     .GroupBy(vendor => vendor.DataId)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate shopping vendor data id {duplicateVendorId}");
        }

        foreach (var duplicatePage in territory.Shopping.Pages.GroupBy(page => page.MenuIndex).Where(group => group.Count() > 1))
        {
            errors.Add($"duplicate shopping menu index {duplicatePage.Key}");
        }

        foreach (var page in territory.Shopping.Pages)
        {
            if (page.CurrencyItemId == 0 || string.IsNullOrWhiteSpace(page.CurrencyName) || string.IsNullOrWhiteSpace(page.MenuLabel))
            {
                errors.Add($"shopping menu index {page.MenuIndex} is incomplete");
            }

            foreach (var duplicateTab in page.Tabs.GroupBy(tab => tab.TabId).Where(group => group.Count() > 1))
            {
                errors.Add($"shopping menu index {page.MenuIndex} has duplicate tab id {duplicateTab.Key}");
            }

            foreach (var tab in page.Tabs)
            {
                foreach (var duplicateItemId in tab.Items
                             .Where(item => item.ItemId != 0)
                             .GroupBy(item => item.ItemId)
                             .Where(group => group.Count() > 1)
                             .Select(group => group.Key))
                {
                    errors.Add($"shopping menu index {page.MenuIndex} tab {tab.TabId} has duplicate item id {duplicateItemId}");
                }

                foreach (var duplicateRowIndex in tab.Items
                             .GroupBy(item => item.RowIndex)
                             .Where(group => group.Count() > 1)
                             .Select(group => group.Key))
                {
                    errors.Add($"shopping menu index {page.MenuIndex} tab {tab.TabId} has duplicate row index {duplicateRowIndex}");
                }

                if (tab.Items.Any(item => item.ItemId == 0))
                {
                    errors.Add($"shopping menu index {page.MenuIndex} tab {tab.TabId} has an item with no id");
                }
            }
        }
    }

    private static void ValidateUniqueIds(IEnumerable<uint> ids, string label, List<string> errors)
    {
        foreach (var duplicateId in ids
                     .Where(id => id != 0)
                     .GroupBy(id => id)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate {label} id {duplicateId}");
        }
    }

    private static void ValidateNonzeroIds(IEnumerable<uint> ids, string label, List<string> errors)
    {
        if (ids.Any(id => id == 0))
        {
            errors.Add($"{label} id must be nonzero");
        }
    }

    private static void ValidateAethernetNames(OccultCrescentTerritoryData territory, List<string> errors)
    {
        foreach (var duplicateName in territory.Aethernets
                     .Where(aethernet => !string.IsNullOrWhiteSpace(aethernet.Name))
                     .GroupBy(aethernet => aethernet.Name, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate aethernet name '{duplicateName}'");
        }

        if (territory.Aethernets.Any(aethernet => string.IsNullOrWhiteSpace(aethernet.Name)))
        {
            errors.Add("aethernet name is missing");
        }
    }

    private static void ValidateTreasureGroups(OccultCrescentTerritoryData territory, List<string> errors)
    {
        var potFateIds = territory.PotFates.Select(fate => fate.FateId).ToHashSet();
        foreach (var duplicateKey in territory.TreasureCofferGroups
                     .Where(group => group.FateId != 0 && !string.IsNullOrWhiteSpace(group.GroupKey))
                     .GroupBy(group => (group.FateId, GroupKey: group.GroupKey), new FateGroupKeyComparer())
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate treasure group key fateId={duplicateKey.FateId} groupKey='{duplicateKey.GroupKey}'");
        }

        foreach (var group in territory.TreasureCofferGroups)
        {
            if (group.FateId == 0)
            {
                errors.Add("treasure group fate id must be nonzero");
            }
            else if (!potFateIds.Contains(group.FateId))
            {
                errors.Add($"treasure group fateId={group.FateId} does not reference a pot FATE");
            }

            if (string.IsNullOrWhiteSpace(group.GroupKey))
            {
                errors.Add($"treasure group fateId={group.FateId} is missing a group key");
            }

            if (group.Candidates.Count == 0)
            {
                errors.Add($"treasure group fateId={group.FateId} groupKey='{group.GroupKey}' has no candidates");
            }

            foreach (var duplicateKey in group.Candidates
                         .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateKey))
                         .GroupBy(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase)
                         .Where(candidates => candidates.Count() > 1)
                         .Select(candidates => candidates.Key))
            {
                errors.Add($"duplicate treasure candidate key fateId={group.FateId} groupKey='{group.GroupKey}' candidateKey='{duplicateKey}'");
            }

            foreach (var candidate in group.Candidates)
            {
                if (candidate.FateId != group.FateId)
                {
                    errors.Add($"treasure candidate key='{candidate.CandidateKey}' fateId={candidate.FateId} does not match parent fateId={group.FateId}");
                }

                if (!string.Equals(candidate.GroupKey, group.GroupKey, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"treasure candidate key='{candidate.CandidateKey}' groupKey='{candidate.GroupKey}' does not match parent groupKey='{group.GroupKey}'");
                }

                if (string.IsNullOrWhiteSpace(candidate.CandidateKey))
                {
                    errors.Add($"treasure candidate fateId={group.FateId} groupKey='{group.GroupKey}' is missing a candidate key");
                }

                if (candidate.Position.X == 0f && candidate.Position.Y == 0f && candidate.Position.Z == 0f)
                {
                    errors.Add($"treasure candidate key='{candidate.CandidateKey}' has an invalid zero position");
                }

                if (candidate.AggroLevel < 0 || candidate.HideThresholdDistance < 0)
                {
                    errors.Add($"treasure candidate key='{candidate.CandidateKey}' has invalid safety values");
                }

                foreach (var waypoint in candidate.ApproachWaypoints)
                {
                    if (waypoint.Position.X == 0f && waypoint.Position.Y == 0f && waypoint.Position.Z == 0f)
                    {
                        errors.Add($"treasure candidate key='{candidate.CandidateKey}' has an invalid zero approach waypoint");
                    }

                    if (waypoint.ArrivalDistance is <= 0f)
                    {
                        errors.Add($"treasure candidate key='{candidate.CandidateKey}' has an invalid approach waypoint arrival distance");
                    }
                }
            }
        }
    }

    private static void ValidatePotTreasure(OccultCrescentTerritoryData territory, List<string> errors)
    {
        if (!territory.Features.PotTreasure)
        {
            return;
        }

        if (territory.PotFates.Count < 2)
        {
            errors.Add("pot treasure is enabled but fewer than two pot FATEs are defined for cycle prediction");
        }

        var fateIds = territory.Fates.Select(fate => fate.Id).ToHashSet();
        foreach (var potFate in territory.PotFates)
        {
            if (!fateIds.Contains(potFate.FateId))
            {
                errors.Add($"pot FATE {potFate.FateId} is missing from the territory FATE catalog");
            }

            if (potFate.CenterPosition.X == 0f && potFate.CenterPosition.Y == 0f && potFate.CenterPosition.Z == 0f)
            {
                errors.Add($"pot FATE {potFate.FateId} has an invalid zero center position");
            }

            if (!territory.TreasureCofferGroups.Any(group => group.FateId == potFate.FateId))
            {
                errors.Add($"pot FATE {potFate.FateId} has no treasure coffer groups");
            }

            var groupKeys = territory.TreasureCofferGroups
                .Where(group => group.FateId == potFate.FateId)
                .Select(group => group.GroupKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var requiredGroupKey in RequiredTreasureGroupKeys)
            {
                if (!groupKeys.Contains(requiredGroupKey))
                {
                    errors.Add($"pot FATE {potFate.FateId} is missing treasure group '{requiredGroupKey}'");
                }
            }
        }

        if (territory.PotTreasure.TreasureBuffStatusId == 0)
        {
            errors.Add("pot treasure behavior is missing a treasure buff status ID");
        }

        var logMessageIds = new[]
        {
            territory.PotTreasure.CofferRevealLogMessageId,
            territory.PotTreasure.HintImmediateLogMessageId,
            territory.PotTreasure.HintCloseLogMessageId,
            territory.PotTreasure.HintFarLogMessageId,
            territory.PotTreasure.HintBeyondFarLogMessageId,
            territory.PotTreasure.ElixirPromptLogMessageId,
            territory.PotTreasure.BonusOfferLogMessageId,
            territory.PotTreasure.CofferSurveyCountsLogMessageId,
            territory.PotTreasure.CofferSurveyEmptyLogMessageId,
        };
        if (logMessageIds.Any(id => id == 0))
        {
            errors.Add("pot treasure behavior is missing one or more log message IDs");
        }
        else if (logMessageIds.GroupBy(id => id).Any(group => group.Count() > 1))
        {
            errors.Add("pot treasure behavior has duplicate log message IDs");
        }
    }

    private static readonly string[] RequiredTreasureGroupKeys =
    [
        "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest",
    ];

    private static void ValidateVisibleCofferSpots(OccultCrescentTerritoryData territory, List<string> errors)
    {
        var duplicateSpots = territory.VisibleCofferFarmSpots
            .Where(spot => !string.IsNullOrWhiteSpace(spot.Area) && !string.IsNullOrWhiteSpace(spot.Label))
            .GroupBy(spot => (spot.Area, spot.Label), new AreaLabelComparer())
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicateSpot in duplicateSpots)
        {
            errors.Add($"duplicate visible coffer spot area='{duplicateSpot.Area}' label='{duplicateSpot.Label}'");
        }

        var spots = territory.VisibleCofferFarmSpots
            .Where(spot => !string.IsNullOrWhiteSpace(spot.Area) && !string.IsNullOrWhiteSpace(spot.Label))
            .Select(spot => (spot.Area, spot.Label))
            .ToHashSet(new AreaLabelComparer());
        foreach (var routeEntry in territory.VisibleCofferFarmRoute)
        {
            if (string.IsNullOrWhiteSpace(routeEntry.Area) || string.IsNullOrWhiteSpace(routeEntry.Label))
            {
                errors.Add("visible coffer route entry is missing area or label");
                continue;
            }

            if (!spots.Contains((routeEntry.Area, routeEntry.Label)))
            {
                errors.Add($"visible coffer route entry area='{routeEntry.Area}' label='{routeEntry.Label}' does not match a spot");
            }
        }
    }

    private static void ValidateVisibleCoffers(OccultCrescentTerritoryData territory, List<string> errors)
    {
        if (!territory.Features.VisibleCoffers && !territory.Features.PotTreasure)
        {
            return;
        }

        var data = territory.VisibleCoffers;
        if (data.BaseIds.Count == 0)
        {
            errors.Add("visible coffers are enabled but no coffer base ids are defined");
        }

        ValidateUniqueIds(data.BaseIds, "visible coffer base", errors);
        ValidateNonzeroIds(data.BaseIds, "visible coffer base", errors);
        if (data.ObjectKinds.Count == 0 || data.ObjectKinds.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("visible coffers are enabled but object kinds are incomplete");
        }

        if (data.LocalizedNames.Count == 0 || data.LocalizedNames.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("visible coffers are enabled but localized names are incomplete");
        }

        foreach (var duplicateKind in data.ObjectKinds
                     .GroupBy(kind => kind, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate visible coffer object kind '{duplicateKind}'");
        }

        foreach (var duplicateName in data.LocalizedNames
                     .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate visible coffer localized name '{duplicateName}'");
        }

        if (!territory.Features.VisibleCoffers)
        {
            return;
        }

        if (territory.VisibleCofferFarmSpots.Count == 0 || territory.VisibleCofferFarmRoute.Count == 0)
        {
            errors.Add("visible coffers are enabled but route or spot data is missing");
        }

        var aethernetNames = territory.Aethernets.Select(aethernet => aethernet.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappedAreas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in data.AreaAethernetMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Area) || string.IsNullOrWhiteSpace(mapping.Aethernet))
            {
                errors.Add("visible coffer area-to-aethernet mapping is incomplete");
                continue;
            }

            if (!mappedAreas.Add(mapping.Area))
            {
                errors.Add($"duplicate visible coffer area mapping '{mapping.Area}'");
            }

            if (!aethernetNames.Contains(mapping.Aethernet))
            {
                errors.Add($"visible coffer area '{mapping.Area}' references unknown aethernet '{mapping.Aethernet}'");
            }
        }

        foreach (var area in territory.VisibleCofferFarmSpots.Select(spot => spot.Area).Where(area => !string.IsNullOrWhiteSpace(area)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!mappedAreas.Contains(area))
            {
                errors.Add($"visible coffer area '{area}' has no aethernet mapping");
            }
        }

        var baseCampCount = territory.Aethernets.Count(aethernet => aethernet.IsBaseCamp);
        if (baseCampCount == 0)
        {
            errors.Add("visible coffers are enabled but no Base Camp aethernet is defined");
        }
        else if (baseCampCount > 1)
        {
            errors.Add("visible coffers have multiple Base Camp aetherytes defined");
        }

        foreach (var duplicateWeatherId in data.UnsafeWeatherIds.GroupBy(id => id).Where(group => group.Count() > 1).Select(group => group.Key))
        {
            errors.Add($"duplicate unsafe weather id {duplicateWeatherId}");
        }

        foreach (var spot in territory.VisibleCofferFarmSpots)
        {
            if (string.IsNullOrWhiteSpace(spot.Area) || string.IsNullOrWhiteSpace(spot.Label))
            {
                errors.Add("visible coffer spot is missing area or label");
            }

            if (spot.Position.X == 0f && spot.Position.Y == 0f && spot.Position.Z == 0f)
            {
                errors.Add($"visible coffer spot '{spot.Area}:{spot.Label}' has an invalid zero position");
            }

            if (spot.ArrivalDistance is <= 0f)
            {
                errors.Add($"visible coffer spot '{spot.Area}:{spot.Label}' has an invalid arrival distance");
            }
        }

        foreach (var duplicateRouteEntry in territory.VisibleCofferFarmRoute
                     .GroupBy(entry => (entry.Area, entry.Label), new AreaLabelComparer())
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate visible coffer route entry area='{duplicateRouteEntry.Area}' label='{duplicateRouteEntry.Label}'");
        }
    }

    private static void ValidateVisibleCofferSafety(OccultCrescentTerritoryData territory, List<string> errors)
    {
        var data = territory.VisibleCoffers;
        if (territory.VisibleCofferFarmSpots.Any(spot => spot.SkipDuringAshkin)
            && (!data.AshkinStartEorzeaMinute.HasValue || !data.AshkinEndEorzeaMinute.HasValue))
        {
            errors.Add("visible coffer Ashkin timing is missing for spots that use Ashkin rules");
        }

        if (data.AshkinStartEorzeaMinute is { } ashkinStart
            && (ashkinStart < 0 || ashkinStart >= 24 * 60))
        {
            errors.Add("visible coffer Ashkin start minute must be between 0 and 1439");
        }

        if (data.AshkinEndEorzeaMinute is { } ashkinEnd
            && (ashkinEnd < 0 || ashkinEnd >= 24 * 60))
        {
            errors.Add("visible coffer Ashkin end minute must be between 0 and 1439");
        }

        if (data.AshkinStartEorzeaMinute.HasValue
            && data.AshkinStartEorzeaMinute == data.AshkinEndEorzeaMinute)
        {
            errors.Add("visible coffer Ashkin start and end minutes must differ");
        }

        foreach (var spot in territory.VisibleCofferFarmSpots)
        {
            if (spot.AggroLevel < 0 || spot.HideThresholdDistance < 0)
            {
                errors.Add($"visible coffer spot '{spot.Area}:{spot.Label}' has invalid safety values");
            }

        }
    }

    private static void ValidateAethernetReferences(OccultCrescentTerritoryData territory, List<string> errors)
    {
        var aethernetNames = territory.Aethernets
            .Where(aethernet => !string.IsNullOrWhiteSpace(aethernet.Name))
            .Select(aethernet => aethernet.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var preferredAethernet in territory.CriticalEncounters
                     .Where(encounter => !string.IsNullOrWhiteSpace(encounter.PreferredAethernet))
                     .Select(encounter => $"CE {encounter.Id}:{encounter.PreferredAethernet}"))
        {
            ValidateAethernetReference(preferredAethernet, aethernetNames, errors);
        }

        foreach (var preferredAethernet in territory.Fates
                     .Where(fate => !string.IsNullOrWhiteSpace(fate.Aethernet))
                     .Select(fate => $"FATE {fate.Id}:{fate.Aethernet}"))
        {
            ValidateAethernetReference(preferredAethernet, aethernetNames, errors);
        }

        foreach (var preferredAethernet in territory.PotFates
                     .Where(fate => !string.IsNullOrWhiteSpace(fate.PreferredAethernet))
                     .Select(fate => $"pot FATE {fate.FateId}:{fate.PreferredAethernet}"))
        {
            ValidateAethernetReference(preferredAethernet, aethernetNames, errors);
        }

        foreach (var preferredAethernet in territory.FateAethernetPreferences
                     .Where(preference => !string.IsNullOrWhiteSpace(preference.Aethernet))
                     .Select(preference => $"fate aethernet preference {preference.FateId}:{preference.Aethernet}"))
        {
            ValidateAethernetReference(preferredAethernet, aethernetNames, errors);
        }

        var fateIds = territory.Fates.Select(fate => fate.Id).ToHashSet();
        foreach (var duplicateFateId in territory.FateAethernetPreferences
                     .GroupBy(preference => preference.FateId)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate fate aethernet preference for FATE {duplicateFateId}");
        }

        foreach (var preference in territory.FateAethernetPreferences)
        {
            if (preference.FateId == 0 || !fateIds.Contains(preference.FateId))
            {
                errors.Add($"fate aethernet preference references unknown FATE {preference.FateId}");
            }
        }

        foreach (var preferredAethernet in territory.Shopping.Vendors
                     .Where(vendor => !string.IsNullOrWhiteSpace(vendor.PreferredAethernet))
                     .Select(vendor => $"shopping vendor {vendor.DataId}:{vendor.PreferredAethernet}"))
        {
            ValidateAethernetReference(preferredAethernet, aethernetNames, errors);
        }
    }

    private static void ValidateAethernetReference(string reference, ISet<string> aethernetNames, List<string> errors)
    {
        var separatorIndex = reference.IndexOf(':');
        if (separatorIndex < 0 || separatorIndex == reference.Length - 1)
        {
            errors.Add($"invalid aethernet reference {reference}");
            return;
        }

        var label = reference[..separatorIndex];
        var aethernetName = reference[(separatorIndex + 1)..];
        if (!aethernetNames.Contains(aethernetName))
        {
            errors.Add($"{label} references unknown aethernet '{aethernetName}'");
        }
    }

    private static void LogCatalogSummary(OccultCrescentDataCatalog catalog, AocchLogger logger)
    {
        foreach (var territory in catalog.Territories)
        {
            logger.Info(
                $"[OccultCrescentDataLoader] op=load key={territory.Key} territoryId={territory.TerritoryTypeId} displayName=\"{territory.DisplayName}\" features=fates:{territory.Features.Fates},ces:{territory.Features.CriticalEncounters},shopping:{territory.Features.Shopping},visibleCoffers:{territory.Features.VisibleCoffers},potTreasure:{territory.Features.PotTreasure},buffRotation:{territory.Features.BuffRotation} aethernets={territory.Aethernets.Count} criticalEncounters={territory.CriticalEncounters.Count} fates={territory.Fates.Count} potFates={territory.PotFates.Count} shoppingVendors={territory.Shopping.Vendors.Count} shoppingPages={territory.Shopping.Pages.Count} treasureCofferGroups={territory.TreasureCofferGroups.Count} visibleCofferKinds={territory.VisibleCoffers.ObjectKinds.Count} visibleCofferBaseIds={territory.VisibleCoffers.BaseIds.Count} visibleCofferAreas={territory.VisibleCoffers.AreaAethernetMappings.Count} visibleCofferUnsafeWeatherIds={territory.VisibleCoffers.UnsafeWeatherIds.Count} ashkinWindow={territory.VisibleCoffers.AshkinStartEorzeaMinute?.ToString() ?? "none"}-{territory.VisibleCoffers.AshkinEndEorzeaMinute?.ToString() ?? "none"} visibleCofferSpots={territory.VisibleCofferFarmSpots.Count} visibleCofferRouteEntries={territory.VisibleCofferFarmRoute.Count}");
        }
    }

    private static string FormatTerritoryKey(string key)
        => string.IsNullOrWhiteSpace(key) ? "<missing>" : key;

    private sealed class FateGroupKeyComparer : IEqualityComparer<(uint FateId, string GroupKey)>
    {
        public bool Equals((uint FateId, string GroupKey) left, (uint FateId, string GroupKey) right)
            => left.FateId == right.FateId && string.Equals(left.GroupKey, right.GroupKey, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((uint FateId, string GroupKey) value)
            => HashCode.Combine(value.FateId, StringComparer.OrdinalIgnoreCase.GetHashCode(value.GroupKey));
    }

    private sealed class AreaLabelComparer : IEqualityComparer<(string Area, string Label)>
    {
        public bool Equals((string Area, string Label) left, (string Area, string Label) right)
            => string.Equals(left.Area, right.Area, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Area, string Label) value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Area),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Label));
    }
}
