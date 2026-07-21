using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

using AOCCH.Automation;
using AOCCH.Logging;
using AOCCH.Scanning;
using Dalamud.Plugin;

namespace AOCCH.Data;

public sealed class CofferPositionOverrideStore
{
    private const string FileName = "coffer-position-overrides.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string filePath;
    private readonly AocchLogger logger;
    private readonly object gate = new();
    private Dictionary<string, CofferPositionOverride> overridesByKey = new(StringComparer.OrdinalIgnoreCase);
    private CofferPositionOverride? lastSavedOverride;

    public CofferPositionOverrideStore(IDalamudPluginInterface pluginInterface, AocchLogger logger)
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

    public bool TryResolvePosition(TreasureCandidateKey candidateKey, out Vector3 position)
    {
        lock (gate)
        {
            if (overridesByKey.TryGetValue(BuildKey(candidateKey), out var entry))
            {
                position = entry.ObservedPosition.ToVector3();
                return true;
            }
        }

        position = Vector3.Zero;
        return false;
    }

    public CofferPositionOverride? LastSavedOverride
    {
        get
        {
            lock (gate)
            {
                return lastSavedOverride;
            }
        }
    }

    public bool SaveConfirmedPosition(VisibleCofferMatch match)
    {
        var next = new CofferPositionOverride
        {
            FateId = match.CandidateKey.FateId,
            GroupKey = match.CandidateKey.GroupKey,
            CandidateKey = match.CandidateKey.CandidateKey,
            Label = match.CandidateKey.Label,
            ObservedPosition = new Vector3Data
            {
                X = match.Coffer.Position.X,
                Y = match.Coffer.Position.Y,
                Z = match.Coffer.Position.Z,
            },
            ObservedDataId = match.Coffer.DataId,
            LastConfirmedAt = DateTimeOffset.UtcNow,
        };

        lock (gate)
        {
            var key = BuildKey(match.CandidateKey);
            if (overridesByKey.TryGetValue(key, out var existing)
                && existing.ObservedDataId == next.ObservedDataId
                && AreSamePosition(existing.ObservedPosition, next.ObservedPosition))
            {
                overridesByKey[key] = next;
            }
            else
            {
                overridesByKey[key] = next;
                logger.Info($"[CofferOverrideStore] op=save candidate={match.CandidateKey.Label} position=<{next.ObservedPosition.X:0.000}, {next.ObservedPosition.Y:0.000}, {next.ObservedPosition.Z:0.000}> source=\"{match.Coffer.Name}\" ({match.Coffer.DataId})");
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
                logger.Info($"[CofferOverrideStore] op=load-empty path=\"{filePath}\"");
                return;
            }

            var json = File.ReadAllText(filePath);
            var file = JsonSerializer.Deserialize<CofferPositionOverrideFile>(json, SerializerOptions) ?? new CofferPositionOverrideFile();
            overridesByKey = new Dictionary<string, CofferPositionOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in file.Overrides)
            {
                overridesByKey[BuildKey(entry)] = entry;
            }

            logger.Info($"[CofferOverrideStore] op=load count={overridesByKey.Count} path=\"{filePath}\"");
        }
        catch (Exception ex)
        {
            overridesByKey = new Dictionary<string, CofferPositionOverride>(StringComparer.OrdinalIgnoreCase);
            logger.Error($"Failed to load coffer position overrides from {filePath}: {ex}");
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

            var payload = new CofferPositionOverrideFile
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
            logger.Error($"Failed to persist coffer position overrides to {filePath}: {ex}");
            return false;
        }
    }

    private static bool AreSamePosition(Vector3Data left, Vector3Data right)
        => MathF.Abs(left.X - right.X) < 0.001f
            && MathF.Abs(left.Y - right.Y) < 0.001f
            && MathF.Abs(left.Z - right.Z) < 0.001f;

    private static string BuildKey(TreasureCandidateKey candidateKey)
        => $"{candidateKey.FateId}:{candidateKey.GroupKey}:{candidateKey.CandidateKey}";

    private static string BuildKey(CofferPositionOverride entry)
        => $"{entry.FateId}:{entry.GroupKey}:{entry.CandidateKey}";
}
