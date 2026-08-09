using System;
using System.Linq;
using System.Threading.Tasks;
using AOCCH.Logging;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

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
    private readonly IFramework framework;
    private readonly DalamudPluginToggleHelper toggleHelper;
    private readonly object recoveryGate = new();

    private bool? lastAvailability;
    private Task? recoveryTask;
    private DateTime recoveryDeadline = DateTime.MinValue;
    private DateTime nextRecoveryAttempt = DateTime.MinValue;
    private Func<bool>? recoveryAllowed;
    private bool disposed;

    public BossModIpc(AocchLogger logger, IFramework framework)
    {
        this.logger = logger;
        this.framework = framework;
        toggleHelper = new DalamudPluginToggleHelper(logger);
        getActivePreset = Plugin.PluginInterface.GetIpcSubscriber<string>("BossMod.Presets.GetActive");
        getPreset = Plugin.PluginInterface.GetIpcSubscriber<string, string>("BossMod.Presets.Get");
        createPreset = Plugin.PluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
        deletePreset = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.Delete");
        setActivePreset = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
        clearActivePreset = Plugin.PluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
        framework.Update += OnFrameworkUpdate;
    }

    public void SetRecoveryGuard(Func<bool> guard)
        => recoveryAllowed = guard;

    public void Dispose()
    {
        disposed = true;
        framework.Update -= OnFrameworkUpdate;
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
            StartRecoveryIfNeeded();
            logger.DebugThrottled("bossmod-ipc-failure", IpcFailureLogInterval, $"IPC call failed for BossMod availability probe: {ex.Message}");
            return false;
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        Task? task;
        DateTime deadline;
        lock (recoveryGate)
        {
            task = recoveryTask;
            deadline = recoveryDeadline;
        }

        if (task == null) return;
        if (!task.IsCompleted && DateTime.UtcNow < deadline) return;

        lock (recoveryGate)
        {
            if (recoveryTask != task) return;
            recoveryTask = null;
            recoveryDeadline = DateTime.MinValue;
            nextRecoveryAttempt = DateTime.UtcNow.AddSeconds(30);
        }

        if (task.IsFaulted || task.IsCanceled)
        {
            logger.Warning($"[BossModIpc] op=recovery-finished result=failed reason={FormatTaskFailure(task)}");
            return;
        }

        if (ProbeWithoutRecovery())
            logger.Info("[BossModIpc] op=recovery-finished result=ipc-ready");
        else
            logger.Warning("[BossModIpc] op=recovery-finished result=ipc-unavailable");
    }

    private void StartRecoveryIfNeeded()
    {
        if (disposed) return;
        if (recoveryAllowed?.Invoke() == false) return;

        var pluginName = Plugin.PluginInterface.InstalledPlugins
            .Where(plugin => plugin.IsLoaded)
            .Select(plugin => plugin.InternalName)
            .FirstOrDefault(name => string.Equals(name, "BossModReborn", StringComparison.OrdinalIgnoreCase))
            ?? Plugin.PluginInterface.InstalledPlugins
                .Where(plugin => plugin.IsLoaded)
                .Select(plugin => plugin.InternalName)
                .FirstOrDefault(name => string.Equals(name, "BossMod", StringComparison.OrdinalIgnoreCase));
        if (pluginName == null) return;

        lock (recoveryGate)
        {
            if (recoveryTask != null || DateTime.UtcNow < nextRecoveryAttempt) return;
            recoveryDeadline = DateTime.UtcNow.AddSeconds(20);
            if (!toggleHelper.TryCycle(pluginName, out var task, out var failureReason) || task == null)
            {
                recoveryTask = null;
                recoveryDeadline = DateTime.MinValue;
                nextRecoveryAttempt = DateTime.UtcNow.AddSeconds(30);
                logger.Warning($"[BossModIpc] op=recovery-start result=failed plugin={pluginName} reason={failureReason ?? "unknown"}");
                return;
            }

            recoveryTask = CyclePluginAsync(pluginName, task);
        }

        logger.Warning($"[BossModIpc] op=recovery-start plugin={pluginName} reason=loaded-but-ipc-unavailable");
    }

    private async Task CyclePluginAsync(string pluginName, Task cycleTask)
    {
        await cycleTask.ConfigureAwait(false);
        await WaitForLoadedState(pluginName, loaded: true).ConfigureAwait(false);
    }

    private async Task WaitForLoadedState(string pluginName, bool loaded)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (toggleHelper.GetState(pluginName).IsLoaded == loaded) return;
            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for {pluginName} loaded={loaded}.");
    }

    private bool ProbeWithoutRecovery()
    {
        try
        {
            _ = getActivePreset.InvokeFunc();
            SetAvailability(true);
            return true;
        }
        catch
        {
            SetAvailability(false);
            return false;
        }
    }

    private static string FormatTaskFailure(Task task)
        => task.Exception?.GetBaseException().Message ?? (task.IsCanceled ? "canceled" : "unknown");

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
