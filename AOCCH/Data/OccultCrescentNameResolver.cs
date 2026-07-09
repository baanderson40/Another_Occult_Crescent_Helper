using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AOCCH.Data;

public sealed class OccultCrescentNameResolver
{
    private readonly Dictionary<uint, string> criticalEncounterNames = [];
    private readonly Dictionary<uint, string> fateNames = [];

    public OccultCrescentNameResolver(IDataManager dataManager, OccultCrescentData data)
    {
        BuildCriticalEncounterNames(dataManager, data);
        BuildFateNames(dataManager, data);
    }

    public string GetCriticalEncounterName(uint id, string fallbackName)
        => criticalEncounterNames.GetValueOrDefault(id, fallbackName);

    public string GetFateName(uint id, string fallbackName)
        => fateNames.GetValueOrDefault(id, fallbackName);

    private void BuildCriticalEncounterNames(IDataManager dataManager, OccultCrescentData data)
    {
        dynamic? sheet = dataManager.GetExcelSheet<DynamicEvent>();
        if (sheet == null)
        {
            return;
        }

        foreach (var criticalEncounter in data.CriticalEncounters)
        {
            var resolvedName = TryResolveSheetName(sheet, criticalEncounter.Id);
            if (resolvedName.Length > 0)
            {
                criticalEncounterNames[criticalEncounter.Id] = resolvedName;
            }
        }
    }

    private void BuildFateNames(IDataManager dataManager, OccultCrescentData data)
    {
        dynamic? sheet = dataManager.GetExcelSheet<Fate>();
        if (sheet == null)
        {
            return;
        }

        foreach (var fate in data.Fates)
        {
            var resolvedName = TryResolveSheetName(sheet, fate.Id);
            if (resolvedName.Length > 0)
            {
                fateNames[fate.Id] = resolvedName;
            }
        }
    }

    private static string TryResolveSheetName(dynamic? sheet, uint rowId)
    {
        if (sheet == null)
        {
            return string.Empty;
        }

        try
        {
            var row = sheet.GetRow(rowId);
            if (row == null)
            {
                return string.Empty;
            }

            var name = row.Name;
            return CoerceText(name);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string CoerceText(object? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text;
        }

        var extractText = value.GetType().GetMethod("ExtractText", Type.EmptyTypes);
        if (extractText != null)
        {
            var extracted = extractText.Invoke(value, null) as string;
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return extracted;
            }
        }

        return value.ToString() ?? string.Empty;
    }
}
