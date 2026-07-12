using System;
using System.Collections.Generic;
using AOCCH.Logging;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AOCCH.Scanning;

public sealed class CofferNameResolver
{
    private readonly HashSet<string> localizedNames = [];
    private readonly AocchLogger logger;

    public CofferNameResolver(IDataManager dataManager, IEnumerable<uint> cofferBaseIds, AocchLogger logger)
    {
        this.logger = logger;
        BuildLocalizedNames(dataManager, cofferBaseIds);
        this.logger.Info($"Coffer name resolver initialized with {localizedNames.Count} localized coffer name(s).");
    }

    public bool IsKnownLocalizedName(string? name)
        => !string.IsNullOrWhiteSpace(name)
            && localizedNames.Contains(name.Trim().ToLowerInvariant());

    private void BuildLocalizedNames(IDataManager dataManager, IEnumerable<uint> cofferBaseIds)
    {
        dynamic? sheet = dataManager.GetExcelSheet<EObjName>();
        if (sheet == null)
        {
            logger.Warning("Coffer name resolver could not load the EObjName sheet.");
            return;
        }

        foreach (var baseId in cofferBaseIds)
        {
            var resolvedName = TryResolveSheetName(sheet, baseId);
            if (resolvedName.Length == 0)
            {
                logger.Warning($"Coffer name resolver could not resolve a localized name for baseId {baseId}.");
                continue;
            }

            localizedNames.Add(resolvedName.ToLowerInvariant());
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

            var name = row.Singular;
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
