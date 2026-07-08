using System;
using AOCCH.Logging;
using Dalamud.Plugin.Ipc;

namespace AOCCH.IPC;

public sealed class BossModIpc
{
    private readonly AocchLogger logger;
    private readonly ICallGateSubscriber<string> getActivePreset;
    private readonly ICallGateSubscriber<string, bool> setActivePreset;
    private readonly ICallGateSubscriber<bool> clearActivePreset;

    private bool? lastAvailability;

    public BossModIpc(AocchLogger logger)
    {
        this.logger = logger;
        getActivePreset = Plugin.PluginInterface.GetIpcSubscriber<string>("BossMod.Presets.GetActive");
        setActivePreset = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
        clearActivePreset = Plugin.PluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
    }

    public bool IsAvailable()
    {
        try
        {
            _ = getActivePreset.InvokeFunc();
            SetAvailability(true);
            return true;
        }
        catch (Exception ex)
        {
            SetAvailability(false);
            logger.Debug($"IPC call failed for BossMod availability probe: {ex.Message}");
            return false;
        }
    }

    public string GetActivePreset()
        => Invoke("BossMod.Presets.GetActive", getActivePreset.InvokeFunc, string.Empty, logAvailability: true) ?? string.Empty;

    public bool SetActivePreset(string presetName)
        => Invoke($"BossMod.Presets.SetActive({presetName})", () => setActivePreset.InvokeFunc(presetName), false, logAvailability: true);

    public bool ClearActivePreset()
        => Invoke("BossMod.Presets.ClearActive", clearActivePreset.InvokeFunc, false, logAvailability: true);

    private T Invoke<T>(string operation, Func<T> action, T fallback, bool logAvailability)
    {
        try
        {
            var result = action();
            if (logAvailability)
            {
                SetAvailability(true);
            }

            return result;
        }
        catch (Exception ex)
        {
            if (logAvailability)
            {
                SetAvailability(false);
            }

            logger.Debug($"IPC call failed for {operation}: {ex.Message}");
            return fallback;
        }
    }

    private void SetAvailability(bool available)
    {
        if (lastAvailability == available)
        {
            return;
        }

        lastAvailability = available;
        if (available)
        {
            logger.Info("BossMod IPC is available.");
        }
        else
        {
            logger.Warning("BossMod IPC is unavailable.");
        }
    }
}
