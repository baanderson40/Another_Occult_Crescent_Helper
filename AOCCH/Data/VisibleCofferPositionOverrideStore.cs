using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

using AOCCH.Logging;
using AOCCH.Scanning;
using Dalamud.Plugin;

namespace AOCCH.Data;

public sealed class VisibleCofferPositionOverrideStore
{
    private const string FileName = "visible-coffer-position-overrides.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string filePath;
    private readonly AocchLogger logger;
    private readonly object gate = new();
    private Dictionary<string, VisibleCofferPositionOverride> overridesByKey = new(StringComparer.OrdinalIgnoreCase);
    private VisibleCofferPositionOverride? lastSavedOverride;

    public VisibleCofferPositionOverrideStore(IDalamudPluginInterface pluginInterface, AocchLogger logger)
    {
        this.logger = logger;
        filePath = Path.Combine(pluginInterface.ConfigDirectory.FullName, FileName);
        Load();
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return overridesByKey.Count;
            }
        }
    }

    public VisibleCofferPositionOverride? LastSavedOverride
    {
        get
        {
            lock (gate)
            {
                return lastSavedOverride;
            }
        }
    }

    public bool TryResolvePosition(string territoryKey, string area, string label, out Vector3 position)
    {
        lock (gate)
        {
            if (overridesByKey.TryGetValue(BuildKey(territoryKey, area, label), out var entry))
            {
                position = entry.ObservedPosition.ToVector3();
                return true;
            }
        }

        position = Vector3.Zero;
        return false;
    }

    public VisibleCofferPositionOverride? TryGetOverride(string territoryKey, string area, string label)
    {
        lock (gate)
        {
            overridesByKey.TryGetValue(BuildKey(territoryKey, area, label), out var entry);
            return entry;
        }
    }

    public bool SaveConfirmedPosition(string territoryKey, string area, string label, VisibleCoffer coffer)
    {
        var next = new VisibleCofferPositionOverride
        {
            TerritoryKey = territoryKey,
            Area = area,
            Label = label,
            ObservedPosition = new Vector3Data
            {
                X = coffer.Position.X,
                Y = coffer.Position.Y,
                Z = coffer.Position.Z,
            },
            ObservedDataId = coffer.DataId,
            ObservedObjectName = coffer.Name,
            LastConfirmedAt = DateTimeOffset.UtcNow,
        };

        lock (gate)
        {
            var key = BuildKey(territoryKey, area, label);
            if (overridesByKey.TryGetValue(key, out var existing)
                && existing.ObservedDataId == next.ObservedDataId
                && existing.ObservedObjectName == next.ObservedObjectName
                && AreSamePosition(existing.ObservedPosition, next.ObservedPosition))
            {
                overridesByKey[key] = next;
            }
            else
            {
                overridesByKey[key] = next;
                logger.Info($"[VisibleCofferOverrideStore] op=save territoryKey={territoryKey} area={area} label={label} position=<{next.ObservedPosition.X:0.000}, {next.ObservedPosition.Y:0.000}, {next.ObservedPosition.Z:0.000}> source=\"{coffer.Name}\" ({coffer.DataId})");
            }

            var persisted = PersistLocked();
            if (persisted)
            {
                lastSavedOverride = next;
            }

            return persisted;
        }
    }

    private void Load()
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(filePath))
            {
                logger.Info($"[VisibleCofferOverrideStore] op=load-empty path=\"{filePath}\"");
                return;
            }

            var json = File.ReadAllText(filePath);
            var file = JsonSerializer.Deserialize<VisibleCofferPositionOverrideFile>(json, SerializerOptions) ?? new VisibleCofferPositionOverrideFile();
            overridesByKey = new Dictionary<string, VisibleCofferPositionOverride>(StringComparer.OrdinalIgnoreCase);
            var migratedLegacyEntries = false;
            foreach (var entry in file.Overrides)
            {
                var territoryKey = string.IsNullOrWhiteSpace(entry.TerritoryKey) ? "southHorn" : entry.TerritoryKey;
                migratedLegacyEntries |= !string.Equals(territoryKey, entry.TerritoryKey, StringComparison.Ordinal);
                var normalizedEntry = string.Equals(territoryKey, entry.TerritoryKey, StringComparison.Ordinal)
                    ? entry
                    : new VisibleCofferPositionOverride
                    {
                        TerritoryKey = territoryKey,
                        Area = entry.Area,
                        Label = entry.Label,
                        ObservedPosition = entry.ObservedPosition,
                        ObservedDataId = entry.ObservedDataId,
                        ObservedObjectName = entry.ObservedObjectName,
                        LastConfirmedAt = entry.LastConfirmedAt,
                    };
                overridesByKey[BuildKey(territoryKey, entry.Area, entry.Label)] = normalizedEntry;
            }

            if (migratedLegacyEntries && PersistLocked())
            {
                logger.Info("[VisibleCofferOverrideStore] op=migrate-legacy territoryKey=southHorn");
            }

            logger.Info($"[VisibleCofferOverrideStore] op=load count={overridesByKey.Count} path=\"{filePath}\"");
        }
        catch (Exception ex)
        {
            overridesByKey = new Dictionary<string, VisibleCofferPositionOverride>(StringComparer.OrdinalIgnoreCase);
            logger.Error($"Failed to load overworld coffer position overrides from {filePath}: {ex}");
        }
    }

    private bool PersistLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new VisibleCofferPositionOverrideFile
            {
                Overrides = [.. overridesByKey.Values],
            };
            var json = JsonSerializer.Serialize(payload, SerializerOptions);
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to persist overworld coffer position overrides to {filePath}: {ex}");
            return false;
        }
    }

    private static bool AreSamePosition(Vector3Data left, Vector3Data right)
        => MathF.Abs(left.X - right.X) < 0.001f
            && MathF.Abs(left.Y - right.Y) < 0.001f
            && MathF.Abs(left.Z - right.Z) < 0.001f;

    private static string BuildKey(string territoryKey, string area, string label)
        => $"{territoryKey}:{area}:{label}";
}
