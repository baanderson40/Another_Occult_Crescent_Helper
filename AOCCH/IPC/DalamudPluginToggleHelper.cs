using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AOCCH.Logging;
using Dalamud.Plugin;

namespace AOCCH.IPC;

internal sealed record PluginToggleState(bool IsInstalled, bool IsLoaded, bool CanToggle, string? BlockedReason);

// Adapted from FFXIV-CombatReborn/GatherBuddyReborn's profile-aware toggle under Apache-2.0.
// Keep this reflection isolated because it depends on Dalamud internals.
internal sealed class DalamudPluginToggleHelper
{
    private const BindingFlags AllFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly AocchLogger logger;

    public DalamudPluginToggleHelper(AocchLogger logger)
    {
        this.logger = logger;
    }

    public PluginToggleState GetState(string internalName)
    {
        try
        {
            if (!ReflectionHelpers.TryGetInstalledPluginEntry(internalName, out var plugin, false, logger) || plugin == null)
                return new PluginToggleState(false, false, false, $"{internalName} is not installed.");

            var loaded = plugin.GetFoP("IsLoaded") is bool isLoaded && isLoaded;
            if (!TryBuildContext(plugin, internalName, out var context, out var reason))
                return new PluginToggleState(true, loaded, false, reason);

            return new PluginToggleState(true, context.IsLoaded, context.BlockedReason == null, context.BlockedReason);
        }
        catch (Exception ex)
        {
            logger.DebugThrottled("bossmod-toggle-inspect", TimeSpan.FromSeconds(30), $"[BossModToggle] Failed to inspect {internalName}: {ex.Message}");
            return new PluginToggleState(true, false, false, $"Could not inspect {internalName}.");
        }
    }

    public bool TrySetEnabled(string internalName, bool enable, out Task? operationTask, out string? failureReason)
    {
        operationTask = null;
        failureReason = null;
        try
        {
            if (!ReflectionHelpers.TryGetInstalledPluginEntry(internalName, out var plugin, true, logger) || plugin == null)
            {
                failureReason = $"{internalName} is not installed.";
                return false;
            }

            if (!TryBuildContext(plugin, internalName, out var context, out failureReason)) return false;
            logger.Debug($"[BossModToggle] Plugin {internalName} loaded={context.IsLoaded}, requested={enable}, blocked={context.BlockedReason != null}.");
            if (context.BlockedReason != null)
            {
                failureReason = context.BlockedReason;
                logger.Debug($"[BossModToggle] Refusing to toggle {internalName}: {failureReason}");
                return false;
            }

            if (context.IsLoaded == enable)
            {
                operationTask = Task.CompletedTask;
                return true;
            }

            operationTask = enable ? EnableAsync(context, internalName) : DisableAsync(context, internalName);
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"Failed to {(enable ? "enable" : "disable")} {internalName}.";
            logger.Debug($"[BossModToggle] {failureReason} {ex}");
            return false;
        }
    }

    public bool TryCycle(string internalName, out Task? operationTask, out string? failureReason)
    {
        operationTask = null;
        failureReason = null;
        try
        {
            if (!ReflectionHelpers.TryGetInstalledPluginEntry(internalName, out var plugin, true, logger) || plugin == null)
            {
                failureReason = $"{internalName} is not installed.";
                return false;
            }

            if (!TryBuildContext(plugin, internalName, out var context, out failureReason)) return false;
            if (context.BlockedReason != null)
            {
                failureReason = context.BlockedReason;
                return false;
            }

            if (!context.IsLoaded)
            {
                failureReason = $"{internalName} is not loaded.";
                return false;
            }

            operationTask = CycleAsync(context, internalName);
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"Failed to cycle {internalName}.";
            logger.Debug($"[BossModToggle] {failureReason} {ex}");
            return false;
        }
    }

    private async Task EnableAsync(PluginToggleContext context, string internalName)
    {
        logger.Debug($"[BossModToggle] Enabling {internalName} via profile-aware reflected toggle.");
        await InvokeProfileUpdate(context.ApplicableProfile!, context.WorkingPluginId, internalName, true).ConfigureAwait(false);
        await InvokeLoad(context.LocalPlugin, context.LocalPluginType, internalName).ConfigureAwait(false);
        logger.Debug($"[BossModToggle] Finished enabling {internalName}.");
    }

    private async Task DisableAsync(PluginToggleContext context, string internalName)
    {
        logger.Debug($"[BossModToggle] Disabling {internalName} via profile-aware reflected toggle.");
        await InvokeUnload(context.LocalPlugin, context.LocalPluginType, internalName).ConfigureAwait(false);
        await InvokeProfileUpdate(context.ApplicableProfile!, context.WorkingPluginId, internalName, false).ConfigureAwait(false);
        logger.Debug($"[BossModToggle] Finished disabling {internalName}.");
    }

    private async Task CycleAsync(PluginToggleContext context, string internalName)
    {
        logger.Debug($"[BossModToggle] Cycling {internalName} via profile-aware reflected toggle.");
        await InvokeUnload(context.LocalPlugin, context.LocalPluginType, internalName).ConfigureAwait(false);
        await WaitForLoadedState(context.LocalPlugin, expected: false, internalName).ConfigureAwait(false);
        await InvokeProfileUpdate(context.ApplicableProfile!, context.WorkingPluginId, internalName, false).ConfigureAwait(false);
        await InvokeProfileUpdate(context.ApplicableProfile!, context.WorkingPluginId, internalName, true).ConfigureAwait(false);
        await InvokeLoad(context.LocalPlugin, context.LocalPluginType, internalName).ConfigureAwait(false);
        await WaitForLoadedState(context.LocalPlugin, expected: true, internalName).ConfigureAwait(false);
        logger.Debug($"[BossModToggle] Finished cycling {internalName}.");
    }

    private static async Task WaitForLoadedState(object plugin, bool expected, string internalName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (plugin.GetFoP("IsLoaded") is bool loaded && loaded == expected) return;
            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for {internalName} loaded={expected}.");
    }

    private sealed record PluginToggleContext(object LocalPlugin, Type LocalPluginType, Guid WorkingPluginId, bool IsLoaded, object? ApplicableProfile, string? BlockedReason);

    private bool TryBuildContext(object plugin, string internalName, out PluginToggleContext context, out string? failureReason)
    {
        context = null!;
        failureReason = null;
        var type = plugin.GetType();
        var loaded = plugin.GetFoP("IsLoaded") is bool isLoaded && isLoaded;
        if (plugin.GetFoP("EffectiveWorkingPluginId") is not Guid id || id == Guid.Empty)
        {
            failureReason = $"Could not resolve {internalName}'s plugin id.";
            return false;
        }

        var manager = ReflectionHelpers.GetDalamudService("Dalamud.Plugin.Internal.Profiles.ProfileManager");
        if (manager?.GetFoP("Profiles") is not IEnumerable profiles)
        {
            failureReason = "Could not read Dalamud plugin profiles.";
            return false;
        }

        var matching = new List<object>();
        foreach (var profile in profiles)
        {
            if (profile == null || !TryInvokeBool(profile, "WantsPlugin", [typeof(Guid)], [id], out var wants))
            {
                if (profile != null) failureReason = "Could not inspect Dalamud plugin profile assignments.";
                return false;
            }
            if (wants) matching.Add(profile);
        }

        var defaultProfile = manager.GetFoP("DefaultProfile");
        if (!TryInvokeBool(manager, "IsInDefaultProfile", [typeof(Guid)], [id], out var inDefault))
        {
            failureReason = "Could not determine the default plugin profile state.";
            return false;
        }

        object? applicable = matching.Count == 0 ? defaultProfile : inDefault ? defaultProfile : matching.Count == 1 ? matching[0] : null;
        var blocked = matching.Count > 1 ? $"{internalName} is assigned to multiple plugin profiles." : null;
        if (blocked == null && applicable == null)
        {
            failureReason = $"Could not find an applicable plugin profile for {internalName}.";
            return false;
        }

        if (blocked == null && applicable != null && applicable.GetFoP("IsDefaultProfile") is bool isDefault && !isDefault)
        {
            var profileName = applicable.GetFoP<string>("Name") ?? "unknown";
            if (applicable.GetFoP("IsEnabled") is bool profileEnabled && !profileEnabled)
                blocked = $"{internalName} belongs to disabled plugin profile '{profileName}'.";
            else if (!TryInvokeBool(applicable, "CheckWantsActiveFromGameState", [typeof(ulong)], [Plugin.PlayerState.ContentId], out var active))
            {
                failureReason = $"Could not determine whether plugin profile '{profileName}' is active.";
                return false;
            }
            else if (!active)
                blocked = $"{internalName} belongs to inactive plugin profile '{profileName}'.";
        }

        context = new PluginToggleContext(plugin, type, id, loaded, applicable, blocked);
        return true;
    }

    private Task InvokeProfileUpdate(object profile, Guid id, string internalName, bool enabled)
    {
        var method = profile.GetType().GetMethod("AddOrUpdateAsync", AllFlags, null, [typeof(Guid), typeof(string), typeof(bool), typeof(bool)], null);
        if (method?.Invoke(profile, [id, internalName, enabled, false]) is Task task) return task;
        throw new InvalidOperationException($"Could not update {internalName}'s plugin profile state.");
    }

    private static Task InvokeLoad(object plugin, Type pluginType, string internalName)
    {
        var method = pluginType.GetMethod("LoadAsync", AllFlags, null, [typeof(PluginLoadReason), typeof(bool), typeof(CancellationToken)], null)
                     ?? pluginType.GetMethod("LoadAsync", AllFlags, null, [typeof(PluginLoadReason), typeof(bool)], null);
        if (method == null) throw new InvalidOperationException($"Could not load {internalName}.");
        object?[] args = method.GetParameters().Length == 3
            ? [PluginLoadReason.Installer, false, CancellationToken.None]
            : [PluginLoadReason.Installer, false];
        return method.Invoke(plugin, args) as Task ?? throw new InvalidOperationException($"Could not load {internalName}.");
    }

    private static Task InvokeUnload(object plugin, Type pluginType, string internalName)
    {
        var modeType = Plugin.PluginInterface.GetType().Assembly.GetType("Dalamud.Plugin.Internal.Types.PluginLoaderDisposalMode", true)
                       ?? throw new InvalidOperationException("Could not resolve plugin disposal mode.");
        var method = pluginType.GetMethod("UnloadAsync", AllFlags, null, [modeType], null)
                     ?? throw new InvalidOperationException($"Could not unload {internalName}.");
        var mode = Enum.Parse(modeType, "WaitBeforeDispose");
        return method.Invoke(plugin, [mode]) as Task ?? throw new InvalidOperationException($"Could not unload {internalName}.");
    }

    private static bool TryInvokeBool(object target, string name, Type[] parameterTypes, object?[] args, out bool result)
    {
        result = false;
        var method = target.GetType().GetMethod(name, AllFlags, null, parameterTypes, null);
        if (method?.Invoke(target, args) is not bool value) return false;
        result = value;
        return true;
    }
}
