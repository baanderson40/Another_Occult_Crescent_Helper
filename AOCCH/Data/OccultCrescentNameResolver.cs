using System.Collections.Generic;
using AOCCH.Logging;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AOCCH.Data;

public sealed class OccultCrescentNameResolver
{
    private readonly Dictionary<uint, string> criticalEncounterNames = [];
    private readonly Dictionary<uint, string> fateNames = [];
    private readonly AocchLogger logger;

    public OccultCrescentNameResolver(IDataManager dataManager, OccultCrescentData data, AocchLogger logger)
    {
        this.logger = logger;
        BuildCriticalEncounterNames(dataManager, data);
        BuildFateNames(dataManager, data);
        this.logger.Info($"[OccultCrescentNameResolver] op=init ceNames={criticalEncounterNames.Count} fateNames={fateNames.Count}");
    }

    public string GetCriticalEncounterName(uint id, string fallbackName)
        => criticalEncounterNames.GetValueOrDefault(id, fallbackName);

    public string GetFateName(uint id, string fallbackName)
        => fateNames.GetValueOrDefault(id, fallbackName);

    private void BuildCriticalEncounterNames(IDataManager dataManager, OccultCrescentData data)
    {
        var sheet = dataManager.GetExcelSheet<DynamicEvent>();
        if (sheet == null)
        {
            logger.Warning("[OccultCrescentNameResolver] op=sheet-load-failed sheet=DynamicEvent");
            return;
        }

        foreach (var criticalEncounter in data.CriticalEncounters)
        {
            var resolvedName = TryResolveCriticalEncounterName(sheet, criticalEncounter.Id);
            if (resolvedName.Length > 0)
            {
                criticalEncounterNames[criticalEncounter.Id] = resolvedName;
                continue;
            }

            logger.Warning($"[OccultCrescentNameResolver] op=name-resolve-failed type=CE id={criticalEncounter.Id}");
        }
    }

    private void BuildFateNames(IDataManager dataManager, OccultCrescentData data)
    {
        var sheet = dataManager.GetExcelSheet<Fate>();
        if (sheet == null)
        {
            logger.Warning("[OccultCrescentNameResolver] op=sheet-load-failed sheet=Fate");
            return;
        }

        foreach (var fate in data.Fates)
        {
            var resolvedName = TryResolveFateName(sheet, fate.Id);
            if (resolvedName.Length > 0)
            {
                fateNames[fate.Id] = resolvedName;
                continue;
            }

            logger.Warning($"[OccultCrescentNameResolver] op=name-resolve-failed type=FATE id={fate.Id}");
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
