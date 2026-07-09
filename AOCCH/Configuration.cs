using Dalamud.Configuration;
using System;

namespace AOCCH;

public enum FarmingMode
{
    CeAndFate,
    CeOnly,
    FateOnly,
}

public enum FatePriority
{
    LowestProgress,
    Nearest,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public string AutorotationPresetName { get; set; } = "Occult";
    public FarmingMode FarmingMode { get; set; } = FarmingMode.CeAndFate;
    public bool PrioritizeCe { get; set; } = true;
    public FatePriority FatePriority { get; set; } = FatePriority.LowestProgress;
    public string ExcludedFates { get; set; } = string.Empty;
    public bool UseReturn { get; set; } = true;
    public bool EnableBuffRotation { get; set; } = true;
    public int MinimumMountingRange { get; set; } = 20;
    public bool ScannerOnlyMode { get; set; }

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
