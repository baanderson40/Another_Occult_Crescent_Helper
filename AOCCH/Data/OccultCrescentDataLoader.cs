using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AOCCH.Logging;
using Dalamud.Plugin;

namespace AOCCH.Data;

public static class OccultCrescentDataLoader
{
    // This shipped JSON is the canonical coffer dataset. The Lua route map and
    // tracker notes in knowledge-base are historical source material only.
    private const string DataFileName = "OccultCrescentData.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static OccultCrescentData Load(IDalamudPluginInterface pluginInterface, AocchLogger logger)
    {
        var assemblyDirectory = pluginInterface.AssemblyLocation.DirectoryName;
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            logger.Error("Could not resolve plugin assembly directory for Occult Crescent data loading.");
            return new OccultCrescentData();
        }

        var path = Path.Combine(assemblyDirectory, "Data", DataFileName);
        try
        {
            if (!File.Exists(path))
            {
                logger.Error($"Occult Crescent data file was not found: {path}");
                return new OccultCrescentData();
            }

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<OccultCrescentData>(json, SerializerOptions) ?? new OccultCrescentData();
            logger.Info(
                $"[OccultCrescentDataLoader] op=load aethernets={data.Aethernets.Count} criticalEncounters={data.CriticalEncounters.Count} fates={data.Fates.Count} potFates={data.PotFates.Count} treasureCofferGroups={data.TreasureCofferGroups.Count} visibleCofferSpots={data.VisibleCofferFarmSpots.Count} visibleCofferRouteEntries={data.VisibleCofferFarmRoute.Count}");

            return data;
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to load Occult Crescent data from {path}: {ex}");
            return new OccultCrescentData();
        }
    }
}
