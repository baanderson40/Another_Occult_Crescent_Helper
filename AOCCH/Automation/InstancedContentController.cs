using AOCCH.Logging;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace AOCCH.Automation;

public sealed class InstancedContentController
{
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
            return false;
        }

        var director = eventFramework->GetContentDirector();
        if (director == null)
        {
            seconds = 0f;
            return false;
        }

        seconds = director->ContentTimeLeft;
        return seconds > 0f;
    }

    public unsafe bool CanLeaveCurrentContent()
        => EventFramework.CanLeaveCurrentContent();

    public unsafe bool TryLeaveCurrentContent(string reason)
    {
        if (!CanLeaveCurrentContent())
        {
            logger.Warning($"Cannot leave instanced content yet for {reason}.");
            return false;
        }

        EventFramework.LeaveCurrentContent(true);
        logger.Info($"Requested leave from instanced content for {reason}.");
        return true;
    }
}
