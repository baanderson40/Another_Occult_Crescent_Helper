using System;
using AOCCH.Logging;
using Lumina.Excel.Sheets;

namespace AOCCH.Automation;

public enum AutorotationJobType
{
    Melee,
    Ranged,
    Unknown,
}

public sealed class AutorotationRoleResolver
{
    private readonly AocchLogger logger;

    public AutorotationRoleResolver(AocchLogger logger)
    {
        this.logger = logger;
    }

    public AutorotationJobType Resolve(uint classJobId)
    {
        if (classJobId == 0)
        {
            return AutorotationJobType.Unknown;
        }

        var row = Plugin.DataManager.GetExcelSheet<ClassJob>()?.GetRowOrDefault(classJobId);
        if (!row.HasValue)
        {
            logger.Warning($"[Autorotation] Could not resolve ClassJob {classJobId}.");
            return AutorotationJobType.Unknown;
        }

        return (row.Value.UIPriority / 10) switch
        {
            0 or 2 => AutorotationJobType.Melee,
            1 or 3 or 4 => AutorotationJobType.Ranged,
            _ => AutorotationJobType.Unknown,
        };
    }
}
