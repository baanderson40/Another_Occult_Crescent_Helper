using System.Collections.Generic;
using AOCCH.Logging;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AOCCH.Data;

public sealed class OccultCrescentNameResolver
{
    private readonly Dictionary<(uint TerritoryTypeId, uint Id), string> criticalEncounterNames = [];
    private readonly Dictionary<(uint TerritoryTypeId, uint Id), string> fateNames = [];
    private readonly AocchLogger logger;

    public OccultCrescentNameResolver(IDataManager dataManager, OccultCrescentDataCatalog catalog, AocchLogger logger)
    {
        this.logger = logger;
        BuildCriticalEncounterNames(dataManager, catalog);
        BuildFateNames(dataManager, catalog);
        this.logger.Info($"[OccultCrescentNameResolver] op=init ceNames={criticalEncounterNames.Count} fateNames={fateNames.Count}");
    }

    public string GetCriticalEncounterName(uint territoryTypeId, uint id, string fallbackName)
        => criticalEncounterNames.GetValueOrDefault((territoryTypeId, id), fallbackName);

    public string GetFateName(uint territoryTypeId, uint id, string fallbackName)
        => fateNames.GetValueOrDefault((territoryTypeId, id), fallbackName);

    private void BuildCriticalEncounterNames(IDataManager dataManager, OccultCrescentDataCatalog catalog)
    {
        var sheet = dataManager.GetExcelSheet<DynamicEvent>();
        if (sheet == null)
        {
            logger.Warning("[OccultCrescentNameResolver] op=sheet-load-failed sheet=DynamicEvent");
            return;
        }

        foreach (var territory in catalog.Territories)
        {
            foreach (var criticalEncounter in territory.CriticalEncounters)
            {
                var resolvedName = TryResolveCriticalEncounterName(sheet, criticalEncounter.Id);
                if (resolvedName.Length > 0)
                {
                    criticalEncounterNames[(territory.TerritoryTypeId, criticalEncounter.Id)] = resolvedName;
                    continue;
                }

                logger.Warning($"[OccultCrescentNameResolver] op=name-resolve-failed type=CE territoryId={territory.TerritoryTypeId} id={criticalEncounter.Id}");
            }
        }
    }

    private void BuildFateNames(IDataManager dataManager, OccultCrescentDataCatalog catalog)
    {
        var sheet = dataManager.GetExcelSheet<Fate>();
        if (sheet == null)
        {
            logger.Warning("[OccultCrescentNameResolver] op=sheet-load-failed sheet=Fate");
            return;
        }

        foreach (var territory in catalog.Territories)
        {
            foreach (var fate in territory.Fates)
            {
                var resolvedName = TryResolveFateName(sheet, fate.Id);
                if (resolvedName.Length > 0)
                {
                    fateNames[(territory.TerritoryTypeId, fate.Id)] = resolvedName;
                    continue;
                }

                logger.Warning($"[OccultCrescentNameResolver] op=name-resolve-failed type=FATE territoryId={territory.TerritoryTypeId} id={fate.Id}");
            }
        }
    }

    private static string TryResolveCriticalEncounterName(ExcelSheet<DynamicEvent> sheet, uint rowId)
    {
        var row = sheet.GetRowOrDefault(rowId);
        if (!row.HasValue)
        {
            return string.Empty;
        }

        return ExcelTextResolver.ResolvePropertyText(row.Value, "Name");
    }

    private static string TryResolveFateName(ExcelSheet<Fate> sheet, uint rowId)
    {
        var row = sheet.GetRowOrDefault(rowId);
        if (!row.HasValue)
        {
            return string.Empty;
        }

        return ExcelTextResolver.ResolvePropertyText(row.Value, "Name");
    }
}
