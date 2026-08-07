using System;
using AOCCH.Logging;
using Dalamud.Plugin.Ipc;

namespace AOCCH.IPC;

public sealed class WrathComboIpc
{
    private enum SetResult
    {
        Okay = 0,
        OkayWorking = 1,
        InvalidLease = 11,
    }

    private readonly ICallGateSubscriber<bool> ipcReady;
    private readonly ICallGateSubscriber<object> test;
    private readonly ICallGateSubscriber<string, string, string, Guid?> registerForLeaseWithCallback;
    private readonly ICallGateSubscriber<Guid, bool, SetResult> setAutoRotationState;
    private readonly ICallGateSubscriber<Guid, SetResult> setCurrentJobReady;
    private readonly ICallGateSubscriber<Guid, object, object, SetResult> setAutoRotationConfigState;
    private readonly ICallGateSubscriber<Guid, object> releaseControl;
    private readonly AocchLogger logger;
    private readonly object gate = new();
    private readonly ICallGateProvider<int, string, object> callbackProvider;

    private Guid? lease;

    public event Action<int>? LeaseCancelled;

    public WrathComboIpc(AocchLogger logger)
    {
        this.logger = logger;
        ipcReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("WrathCombo.IPCReady");
        test = Plugin.PluginInterface.GetIpcSubscriber<object>("WrathCombo.Test");
        registerForLeaseWithCallback = Plugin.PluginInterface.GetIpcSubscriber<string, string, string, Guid?>("WrathCombo.RegisterForLeaseWithCallback");
        setAutoRotationState = Plugin.PluginInterface.GetIpcSubscriber<Guid, bool, SetResult>("WrathCombo.SetAutoRotationState");
        setCurrentJobReady = Plugin.PluginInterface.GetIpcSubscriber<Guid, SetResult>("WrathCombo.SetCurrentJobAutoRotationReady");
        setAutoRotationConfigState = Plugin.PluginInterface.GetIpcSubscriber<Guid, object, object, SetResult>("WrathCombo.SetAutoRotationConfigState");
        releaseControl = Plugin.PluginInterface.GetIpcSubscriber<Guid, object>("WrathCombo.ReleaseControl");
        callbackProvider = Plugin.PluginInterface.GetIpcProvider<int, string, object>("AOCCH.WrathComboCallback");
        callbackProvider.RegisterAction(OnLeaseCancelled);
    }

    public bool IsAvailable()
    {
        try
        {
            return ipcReady.InvokeFunc();
        }
        catch (Exception ex)
        {
            logger.DebugThrottled("wrath-ipc-failure", TimeSpan.FromSeconds(30), $"Wrath IPC unavailable: {ex.Message}");
            return false;
        }
    }

    public bool Start()
    {
        if (!IsAvailable()) return false;

        lock (gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!lease.HasValue)
                {
                    try
                    {
                        lease = registerForLeaseWithCallback.InvokeFunc("AOCCH", "Another Occult Crescent Helper", "AOCCH");
                        if (!lease.HasValue)
                        {
                            logger.Warning("[WrathComboIpc] op=lease-register-failed reason=no-lease-returned");
                            return false;
                        }

                        logger.Info("[WrathComboIpc] op=lease-registered");
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"[WrathComboIpc] op=lease-register-failed reason={FormatException(ex)}");
                        return false;
                    }
                }

                try
                {
                    var id = lease.Value;
                    var enabled = setAutoRotationState.InvokeFunc(id, true);
                    var dps = setAutoRotationConfigState.InvokeFunc(id, 1, 0);
                    var healer = setAutoRotationConfigState.InvokeFunc(id, 2, 0);
                    var ready = setCurrentJobReady.InvokeFunc(id);
                    if (IsSuccess(dps) && IsSuccess(healer) && IsSuccess(enabled) && IsSuccess(ready))
                    {
                        logger.Info($"[WrathComboIpc] op=start-success auto={enabled} dps={dps} healer={healer} jobReady={ready}");
                        return true;
                    }

                    logger.Warning($"[WrathComboIpc] op=start-rejected auto={enabled} dps={dps} healer={healer} jobReady={ready}");
                    if (!IsInvalidLease(enabled) && !IsInvalidLease(dps) && !IsInvalidLease(healer) && !IsInvalidLease(ready))
                    {
                        return false;
                    }

                    lease = null;
                }
                catch (Exception ex)
                {
                    logger.Warning($"[WrathComboIpc] op=start-failed reason={FormatException(ex)}");
                    return false;
                }
            }

            return false;
        }
    }

    public void Release()
    {
        lock (gate)
        {
            if (!lease.HasValue) return;
            try
            {
                releaseControl.InvokeAction(lease.Value);
                logger.Info("[WrathComboIpc] op=release-success");
                lease = null;
            }
            catch (Exception ex)
            {
                // Retain the lease so a transient IPC failure cannot create a duplicate lease.
                logger.Warning($"[WrathComboIpc] op=release-deferred reason={FormatException(ex)}");
            }
        }
    }

    public bool Test()
    {
        try
        {
            if (!ipcReady.InvokeFunc()) return false;
            test.InvokeAction();
            logger.Info("[WrathComboIpc] op=test result=success");
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning($"[WrathComboIpc] op=test-failed reason={FormatException(ex)}");
            return false;
        }
    }

    private static bool IsSuccess(SetResult result)
        => result is SetResult.Okay or SetResult.OkayWorking;

    private static bool IsInvalidLease(SetResult result)
        => result is SetResult.InvalidLease;

    private void OnLeaseCancelled(int reason, string additionalInfo)
    {
        lock (gate)
        {
            lease = null;
        }

        if (reason == 3)
        {
            logger.Info("[WrathComboIpc] op=lease-released reason=own-release");
        }
        else
        {
            logger.Warning($"[WrathComboIpc] op=lease-cancelled reason={reason} info={additionalInfo}");
        }
        LeaseCancelled?.Invoke(reason);
    }

    private static string FormatException(Exception ex)
        => ex.InnerException == null ? ex.Message : $"{ex.Message}; inner={ex.InnerException.Message}";
}
