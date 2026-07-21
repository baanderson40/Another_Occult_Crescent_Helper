using AOCCH.Logging;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AOCCH.Automation;

public sealed class InstancedContentController
{
    private static readonly global::System.TimeSpan WaitLogInterval = global::System.TimeSpan.FromSeconds(10);
    private const string ContentMemberListAddonName = "ContentMemberList";
    private const int ExpectedContentMemberListAtkValueCount = 14;

    private readonly IGameGui gameGui;
    private readonly AocchLogger logger;
    private string contentMemberListAtkValueDumpStatus = "Run /search, then dump the open ContentMemberList addon.";

    public InstancedContentController(IGameGui gameGui, AocchLogger logger)
    {
        this.gameGui = gameGui;
        this.logger = logger;
    }

    public string ContentMemberListAtkValueDumpStatus => contentMemberListAtkValueDumpStatus;

    public unsafe bool DumpContentMemberListAtkValues()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(ContentMemberListAddonName, 1).Address;
        if (addon == null || !addon->IsReady)
        {
            contentMemberListAtkValueDumpStatus = "ContentMemberList is not open. Run /search and try again.";
            logger.Warning($"[InstancedContent] op=content-member-list-atkvalue-dump-failed reason=addon-not-open addon={ContentMemberListAddonName}");
            return false;
        }

        if (addon->AtkValues == null)
        {
            contentMemberListAtkValueDumpStatus = "ContentMemberList has no readable AtkValues.";
            logger.Warning($"[InstancedContent] op=content-member-list-atkvalue-dump-failed reason=no-atkvalues addon={ContentMemberListAddonName}");
            return false;
        }

        var atkValuesCount = addon->AtkValuesCount;
        if (atkValuesCount != ExpectedContentMemberListAtkValueCount)
        {
            contentMemberListAtkValueDumpStatus = $"ContentMemberList has {atkValuesCount} AtkValues; expected {ExpectedContentMemberListAtkValueCount}.";
            logger.Warning($"[InstancedContent] op=content-member-list-atkvalue-dump-failed reason=unexpected-count addon={ContentMemberListAddonName} atkValuesCount={atkValuesCount} expectedCount={ExpectedContentMemberListAtkValueCount}");
            return false;
        }

        contentMemberListAtkValueDumpStatus = $"Dumped all {atkValuesCount} ContentMemberList AtkValues to the plugin log.";
        logger.Info($"[InstancedContent] op=content-member-list-atkvalue-dump addon={ContentMemberListAddonName} atkValuesCount={atkValuesCount}");
        for (var index = 0; index < atkValuesCount; index++)
        {
            var value = addon->AtkValues[index];
            logger.Info($"[InstancedContent] op=content-member-list-atkvalue index={index} type={value.Type} int={value.Int} uint={value.UInt} float={value.Float} byte={value.Byte}");
        }

        return true;
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
