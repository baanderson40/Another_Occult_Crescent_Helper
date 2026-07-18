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
            var normalizedTerritory = NormalizeTerritory(territory);
            var errors = ValidateTerritory(normalizedTerritory, duplicateKeys, duplicateTerritoryIds);
            if (errors.Count > 0)
            {
                logger.Error($"[OccultCrescentDataLoader] op=territory-invalid key={FormatTerritoryKey(territory.Key)} territoryId={territory.TerritoryTypeId} errors={string.Join(" | ", errors)}");
                continue;
            }

            validTerritories.Add(normalizedTerritory);
        }

        return new OccultCrescentDataCatalog
        {
            Territories = validTerritories,
        };
    }

    private static OccultCrescentTerritoryData NormalizeTerritory(OccultCrescentTerritoryData territory)
    {
        if (!string.Equals(territory.Key, "southHorn", StringComparison.OrdinalIgnoreCase)
            || territory.Shopping.Pages.Count > 0)
        {
            return territory;
        }

        // South Horn's established catalog remains the migration source until it is exported to JSON.
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
            Shopping = ShopCurrencyCatalog.CreateSouthHornData(territory.Shopping.Vendors),
            Drops = territory.Drops,
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
        ValidateVisibleCofferSpots(territory, errors);
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
            }
        }
    }

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
                $"[OccultCrescentDataLoader] op=load key={territory.Key} territoryId={territory.TerritoryTypeId} displayName=\"{territory.DisplayName}\" features=fates:{territory.Features.Fates},ces:{territory.Features.CriticalEncounters},shopping:{territory.Features.Shopping},visibleCoffers:{territory.Features.VisibleCoffers},potTreasure:{territory.Features.PotTreasure},buffRotation:{territory.Features.BuffRotation} aethernets={territory.Aethernets.Count} criticalEncounters={territory.CriticalEncounters.Count} fates={territory.Fates.Count} potFates={territory.PotFates.Count} shoppingVendors={territory.Shopping.Vendors.Count} shoppingPages={territory.Shopping.Pages.Count} treasureCofferGroups={territory.TreasureCofferGroups.Count} visibleCofferSpots={territory.VisibleCofferFarmSpots.Count} visibleCofferRouteEntries={territory.VisibleCofferFarmRoute.Count}");
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
