using System.Collections.Generic;
using AOCCH.Data;
using AOCCH.Logging;
using Dalamud.Plugin.Services;
using Lumina.Excel;
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
        var sheet = dataManager.GetExcelSheet<EObjName>();
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

    private static string TryResolveSheetName(ExcelSheet<EObjName> sheet, uint rowId)
    {
        var row = sheet.GetRowOrDefault(rowId);
        if (!row.HasValue)
        {
            return string.Empty;
        }

        return ExcelTextResolver.ResolvePropertyText(row.Value, "Singular", "Name", "Unknown0", "Text");
    }
}
