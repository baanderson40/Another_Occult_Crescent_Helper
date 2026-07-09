using AOCCH.Logging;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AOCCH.Movement;

public sealed class GameActionController
{
    public const uint ReturnActionId = 8;
    public const uint MountActionId = 9;
    public const uint DismountActionId = 23;

    private readonly AocchLogger logger;

    public GameActionController(AocchLogger logger)
    {
        this.logger = logger;
    }

    public unsafe bool CanUseGeneralAction(uint actionId)
        => ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, actionId) == 0;

    public unsafe bool TryExecuteGeneralAction(uint actionId, string description)
    {
        if (!CanUseGeneralAction(actionId))
        {
            logger.Warning($"General action {actionId} is unavailable for {description}.");
            return false;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.GeneralAction, actionId);
        if (!used)
        {
            logger.Warning($"Failed to execute general action {actionId} for {description}.");
            return false;
        }

        logger.Info($"Executed general action {actionId} for {description}.");
        return true;
    }
}
