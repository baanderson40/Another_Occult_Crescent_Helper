using System;
using AOCCH.Logging;
using Dalamud.Plugin.Ipc;

namespace AOCCH.IPC;

public sealed class LifestreamIpc
{
    private static readonly TimeSpan IpcFailureLogInterval = TimeSpan.FromSeconds(30);

    private readonly AocchLogger logger;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<object> abort;
    private readonly ICallGateSubscriber<uint, bool> aethernetTeleportByPlaceNameId;
    private readonly ICallGateSubscriber<uint> getActiveAetheryte;
    private readonly ICallGateSubscriber<uint> getActiveCustomAetheryte;

    private bool? lastAvailability;

    public LifestreamIpc(AocchLogger logger)
    {
        this.logger = logger;
        isBusy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        abort = Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
        aethernetTeleportByPlaceNameId = Plugin.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportByPlaceNameId");
        getActiveAetheryte = Plugin.PluginInterface.GetIpcSubscriber<uint>("Lifestream.GetActiveAetheryte");
        getActiveCustomAetheryte = Plugin.PluginInterface.GetIpcSubscriber<uint>("Lifestream.GetActiveCustomAetheryte");
    }

    public bool IsBusy()
        => Invoke("Lifestream.IsBusy", isBusy.InvokeFunc, false, logAvailability: true);

    public bool IsAvailable()
    {
        try
        {
            _ = isBusy.InvokeFunc();
            SetAvailability(true);
            return true;
        }
        catch (Exception ex)
        {
            SetAvailability(false);
            logger.DebugThrottled("lifestream-ipc-failure", IpcFailureLogInterval, $"IPC call failed for Lifestream availability probe: {ex.Message}");
            return false;
        }
    }

    public bool TryAethernetTeleportByPlaceNameId(uint placeNameId)
        => Invoke($"Lifestream.AethernetTeleportByPlaceNameId({placeNameId})",
            () => aethernetTeleportByPlaceNameId.InvokeFunc(placeNameId), false);

    public uint GetActiveAetheryte()
        => Invoke("Lifestream.GetActiveAetheryte", getActiveAetheryte.InvokeFunc, 0u);

    public uint GetActiveCustomAetheryte()
        => Invoke("Lifestream.GetActiveCustomAetheryte", getActiveCustomAetheryte.InvokeFunc, 0u);

    public void Abort()
    {
        try
        {
            abort.InvokeAction();
        }
        catch (Exception ex)
        {
            logger.WarningThrottled("lifestream-ipc-failure", IpcFailureLogInterval, $"[LifestreamIpc] op=abort-failed reason={ex.Message}");
        }
    }

    private T Invoke<T>(string operation, Func<T> action, T fallback, bool logAvailability = false)
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

            logger.DebugThrottled("lifestream-ipc-failure", IpcFailureLogInterval, $"IPC call failed for {operation}: {ex.Message}");
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
            logger.Info("[LifestreamIpc] op=availability available=true");
        }
        else
        {
            logger.Warning("[LifestreamIpc] op=availability available=false");
        }
    }
}
