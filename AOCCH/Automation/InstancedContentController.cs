using AOCCH.Logging;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace AOCCH.Automation;

public sealed class InstancedContentController
{
    private static readonly global::System.TimeSpan WaitLogInterval = global::System.TimeSpan.FromSeconds(10);
    private readonly AocchLogger logger;

    public InstancedContentController(AocchLogger logger)
    {
        this.logger = logger;
    }

    public unsafe bool TryGetContentTimeLeftSeconds(out float seconds)
    {
        var eventFramework = EventFramework.Instance();
        if (eventFramework == null)
        {
            seconds = 0f;
            logger.DebugThrottled("instanced-content-event-framework", WaitLogInterval, "Instanced content timer is unavailable because EventFramework.Instance() returned null.");
            return false;
        }

        var director = eventFramework->GetContentDirector();
        if (director == null)
        {
            seconds = 0f;
            logger.DebugThrottled("instanced-content-director", WaitLogInterval, "Instanced content timer is unavailable because the content director is null.");
            return false;
        }

        seconds = director->ContentTimeLeft;
        var hasTimer = seconds > 0f;
        if (!hasTimer)
        {
            logger.DebugThrottled("instanced-content-no-timer", WaitLogInterval, "Instanced content timer is unavailable because the reported remaining time is not positive.");
        }

        return hasTimer;
    }

    public unsafe bool CanLeaveCurrentContent()
    {
        var canLeave = EventFramework.CanLeaveCurrentContent();
        if (!canLeave)
        {
            logger.DebugThrottled("instanced-content-cannot-leave", WaitLogInterval, "Instanced content cannot be left yet according to EventFramework.CanLeaveCurrentContent().");
        }

        return canLeave;
    }

    public unsafe bool TryLeaveCurrentContent(string reason)
    {
        logger.Info($"[InstancedContent] op=leave-request reason={reason}");
        if (!CanLeaveCurrentContent())
        {
            logger.Warning($"[InstancedContent] op=leave-blocked reason={reason} canLeave=false");
            return false;
        }

        EventFramework.LeaveCurrentContent(true);
        logger.Info($"[InstancedContent] op=leave-dispatched reason={reason}");
        return true;
    }
}
