using System;
using AOCCH.Logging;
using Dalamud.Plugin.Ipc;

namespace AOCCH.IPC;

public sealed class BossModIpc
{
    private static readonly TimeSpan IpcFailureLogInterval = TimeSpan.FromSeconds(30);

    private readonly AocchLogger logger;
    private readonly ICallGateSubscriber<string> getActivePreset;
    private readonly ICallGateSubscriber<string, string> getPreset;
    private readonly ICallGateSubscriber<string, bool, bool> createPreset;
    private readonly ICallGateSubscriber<string, bool> deletePreset;
    private readonly ICallGateSubscriber<string, bool> setActivePreset;
    private readonly ICallGateSubscriber<bool> clearActivePreset;

    private bool? lastAvailability;

    public BossModIpc(AocchLogger logger)
    {
        this.logger = logger;
        getActivePreset = Plugin.PluginInterface.GetIpcSubscriber<string>("BossMod.Presets.GetActive");
        getPreset = Plugin.PluginInterface.GetIpcSubscriber<string, string>("BossMod.Presets.Get");
        createPreset = Plugin.PluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
        deletePreset = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.Delete");
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
            logger.DebugThrottled("bossmod-ipc-failure", IpcFailureLogInterval, $"IPC call failed for BossMod availability probe: {ex.Message}");
            return false;
        }
    }

    public string GetActivePreset()
        => Invoke("BossMod.Presets.GetActive", getActivePreset.InvokeFunc, string.Empty, logAvailability: true) ?? string.Empty;

    public bool SetActivePreset(string presetName)
        => Invoke($"BossMod.Presets.SetActive({presetName})", () => setActivePreset.InvokeFunc(presetName), false, logAvailability: true);

    public bool ClearActivePreset()
        => Invoke("BossMod.Presets.ClearActive", clearActivePreset.InvokeFunc, false, logAvailability: true);

    public string GetPreset(string presetName)
        => Invoke($"BossMod.Presets.Get({presetName})", () => getPreset.InvokeFunc(presetName), string.Empty, logAvailability: true) ?? string.Empty;

    public bool CreatePreset(string serializedPreset, bool overwrite)
        => Invoke("BossMod.Presets.Create", () => createPreset.InvokeFunc(serializedPreset, overwrite), false, logAvailability: true);

    public bool DeletePreset(string presetName)
        => Invoke($"BossMod.Presets.Delete({presetName})", () => deletePreset.InvokeFunc(presetName), false, logAvailability: true);

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

            logger.DebugThrottled("bossmod-ipc-failure", IpcFailureLogInterval, $"IPC call failed for {operation}: {ex.Message}");
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
            logger.Info("[BossModIpc] op=availability available=true");
        }
        else
        {
            logger.Warning("[BossModIpc] op=availability available=false");
        }
    }
}
