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
        => InvokeMutating("BossMod.Presets.SetActive", $"BossMod.Presets.SetActive({presetName})", () => setActivePreset.InvokeFunc(presetName));

    public bool ClearActivePreset()
        => InvokeMutating("BossMod.Presets.ClearActive", "BossMod.Presets.ClearActive", clearActivePreset.InvokeFunc);

    public string GetPreset(string presetName)
        => Invoke($"BossMod.Presets.Get({presetName})", () => getPreset.InvokeFunc(presetName), string.Empty, logAvailability: true) ?? string.Empty;

    public bool CreatePreset(string serializedPreset, bool overwrite)
        => InvokeMutating("BossMod.Presets.Create", "BossMod.Presets.Create", () => createPreset.InvokeFunc(serializedPreset, overwrite));

    public bool DeletePreset(string presetName)
        => InvokeMutating("BossMod.Presets.Delete", $"BossMod.Presets.Delete({presetName})", () => deletePreset.InvokeFunc(presetName));

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

    private bool InvokeMutating(string logKey, string operation, Func<bool> action)
    {
        try
        {
            var result = action();
            SetAvailability(true);
            if (!result)
            {
                logger.WarningThrottled($"bossmod-ipc-false-{logKey}", IpcFailureLogInterval, $"[BossModIpc] op=mutation-failed request={operation} reason=false-return");
            }

            return result;
        }
        catch (Exception ex)
        {
            SetAvailability(false);
            logger.DebugThrottled("bossmod-ipc-failure", IpcFailureLogInterval, $"IPC call failed for {operation}: {ex.Message}");
            return false;
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
