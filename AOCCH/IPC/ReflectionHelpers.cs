using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AOCCH.Logging;
using Dalamud.Plugin;

namespace AOCCH.IPC;

internal static class ReflectionHelpers
{
    private const BindingFlags AllFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static object? GetFoP(this object obj, string name)
    {
        var type = obj.GetType();
        while (type != null)
        {
            var field = type.GetField(name, AllFlags);
            if (field != null) return field.GetValue(obj);

            var property = type.GetProperty(name, AllFlags);
            if (property != null) return property.GetValue(obj);
            type = type.BaseType;
        }

        return null;
    }

    public static T? GetFoP<T>(this object obj, string name)
        => (T?)GetFoP(obj, name);

    public static bool TryGetInstalledPluginEntry(string internalName, out object? pluginEntry, bool logIfMissing, AocchLogger logger)
    {
        pluginEntry = null;
        if (!TryGetInstalledPluginEntries(internalName, out var entries, logIfMissing, logger)) return false;

        pluginEntry = Select(entries, IsLoadedInstalledPlugin)
                      ?? Select(entries, IsLoadedPlugin)
                      ?? Select(entries, IsInstalledPlugin)
                      ?? entries[0];
        return pluginEntry != null;
    }

    public static object? GetDalamudService(string serviceName)
    {
        try
        {
            var assembly = Plugin.PluginInterface.GetType().Assembly;
            var serviceType = assembly.GetType("Dalamud.Service`1", throwOnError: true);
            var targetType = assembly.GetType(serviceName, throwOnError: true);
            if (serviceType == null || targetType == null) return null;

            return serviceType.MakeGenericType(targetType)
                .GetMethod("Get", AllFlags)
                ?.Invoke(null, Array.Empty<object>());
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetInstalledPluginEntries(string internalName, out List<object> entries, bool logIfMissing, AocchLogger logger)
    {
        entries = [];
        var pluginManager = GetDalamudService("Dalamud.Plugin.Internal.PluginManager");
        if (pluginManager?.GetFoP("InstalledPlugins") is not IEnumerable installedPlugins)
        {
            logger.Debug("[BossModToggle] Could not read Dalamud installed plugins.");
            return false;
        }

        foreach (var plugin in installedPlugins)
        {
            if (plugin != null && string.Equals(plugin.GetFoP<string>("InternalName"), internalName, StringComparison.OrdinalIgnoreCase))
                entries.Add(plugin);
        }

        if (entries.Count == 0 && logIfMissing)
            logger.Debug($"[BossModToggle] Plugin entry {internalName} was not found.");
        return entries.Count != 0;
    }

    private static object? Select(IEnumerable<object> entries, Predicate<object> predicate)
    {
        foreach (var entry in entries)
            if (predicate(entry)) return entry;
        return null;
    }

    private static bool IsLoadedPlugin(object entry)
        => entry.GetFoP("IsLoaded") is bool loaded && loaded;

    private static bool IsInstalledPlugin(object entry)
        => !entry.GetType().Name.Contains("LocalDevPlugin", StringComparison.Ordinal);

    private static bool IsLoadedInstalledPlugin(object entry)
        => IsLoadedPlugin(entry) && IsInstalledPlugin(entry);
}
