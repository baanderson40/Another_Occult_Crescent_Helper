using System.Collections.Generic;
using AOCCH.Data;
using AOCCH.Logging;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AOCCH.Scanning;

public sealed class CofferNameResolver
{
    private static readonly string[] FallbackVisibleCofferNames = ["Treasure Coffer"];
    private readonly HashSet<string> localizedNames = [];
    private readonly AocchLogger logger;

    public CofferNameResolver(IDataManager dataManager, IEnumerable<uint> cofferBaseIds, AocchLogger logger)
    {
        this.logger = logger;
        BuildLocalizedNames(dataManager, cofferBaseIds);
        this.logger.Info($"[CofferNameResolver] op=init localizedNames={localizedNames.Count}");
    }

    public bool IsKnownLocalizedName(string? name)
        => !string.IsNullOrWhiteSpace(name)
            && localizedNames.Contains(name.Trim().ToLowerInvariant());

    private void BuildLocalizedNames(IDataManager dataManager, IEnumerable<uint> cofferBaseIds)
    {
        foreach (var fallbackName in FallbackVisibleCofferNames)
        {
            localizedNames.Add(fallbackName.ToLowerInvariant());
        }

        var sheet = dataManager.GetExcelSheet<EObjName>();
        if (sheet == null)
        {
            logger.Warning("[CofferNameResolver] op=sheet-load-failed sheet=EObjName");
            return;
        }

        foreach (var baseId in cofferBaseIds)
        {
            var resolvedName = TryResolveSheetName(sheet, baseId);
            if (resolvedName.Length == 0)
            {
                logger.Warning($"[CofferNameResolver] op=name-resolve-failed baseId={baseId}");
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
