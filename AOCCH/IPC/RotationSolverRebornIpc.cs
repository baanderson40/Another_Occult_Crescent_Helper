using System;
using AOCCH.Logging;
using Dalamud.Plugin.Ipc;

namespace AOCCH.IPC;

public sealed class RotationSolverRebornIpc
{
    private enum StateCommandType : byte
    {
        Off,
        Auto,
        TargetOnly,
        Manual,
        AutoDuty,
        Henched,
        PvP,
    }

    private readonly ICallGateSubscriber<StateCommandType, object> changeOperatingMode;
    private readonly ICallGateSubscriber<string, object> test;
    private readonly AocchLogger logger;

    public RotationSolverRebornIpc(AocchLogger logger)
    {
        this.logger = logger;
        changeOperatingMode = Plugin.PluginInterface.GetIpcSubscriber<StateCommandType, object>("RotationSolverReborn.ChangeOperatingMode");
        test = Plugin.PluginInterface.GetIpcSubscriber<string, object>("RotationSolverReborn.Test");
    }

    public bool IsAvailable()
    {
        // RSR does not expose a harmless readiness call. Presence and loaded state
        // are validated by provider discovery; startup validates the actual gate.
        return true;
    }

    public bool StartManual()
        => Invoke(StateCommandType.Manual, "Manual");

    public bool Stop()
        => Invoke(StateCommandType.Off, "Off");

    public bool Test()
    {
        try
        {
            test.InvokeAction("AOCCH debug IPC test");
            logger.Info("[RotationSolverRebornIpc] op=test result=success");
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning($"[RotationSolverRebornIpc] op=test-failed reason={ex.Message}");
            return false;
        }
    }

    private bool Invoke(StateCommandType mode, string name)
    {
        try
        {
            changeOperatingMode.InvokeAction(mode);
            logger.Info($"[RotationSolverRebornIpc] op=mode mode={name}");
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning($"[RotationSolverRebornIpc] op=mode-failed mode={name} reason={ex.Message}");
            return false;
        }
    }
}
