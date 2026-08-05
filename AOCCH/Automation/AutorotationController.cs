using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AOCCH.IPC;
using AOCCH.Logging;
using AOCCH.Movement;

namespace AOCCH.Automation;

public sealed class AutorotationController : IDisposable
{
    private readonly BossModIpc bossMod;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly GameActionController gameActionController;
    private readonly AutorotationRoleResolver roleResolver;
    private readonly object gate = new();

    private bool bossModAvailable;
    private bool hasOwnership;
    private bool changedPreset;
    private string initialPreset = string.Empty;
    private string ownedPreset = string.Empty;
    private string lastKnownActivePreset = string.Empty;
    private string lastError = string.Empty;
    private string lastStatus = "Idle";
    private bool managedPresetCreated;
    private string selectedSource = "None";
    private string selectedRole = "Unknown";
    private decimal selectedRange;

    public AutorotationController(BossModIpc bossMod, Configuration configuration, GameActionController gameActionController, AocchLogger logger)
    {
        this.bossMod = bossMod;
        this.configuration = configuration;
        this.logger = logger;
        this.gameActionController = gameActionController;
        roleResolver = new AutorotationRoleResolver(logger);
    }

    public bool BossModAvailable
    {
        get
        {
            lock (gate)
            {
                return bossModAvailable;
            }
        }
    }

    public bool HasOwnership
    {
        get
        {
            lock (gate)
            {
                return hasOwnership;
            }
        }
    }

    public string InitialPreset
    {
        get
        {
            lock (gate)
            {
                return initialPreset;
            }
        }
    }

    public string OwnedPreset
    {
        get
        {
            lock (gate)
            {
                return ownedPreset;
            }
        }
    }

    public string LastKnownActivePreset
    {
        get
        {
            lock (gate)
            {
                return lastKnownActivePreset;
            }
        }
    }

    public string LastError
    {
        get
        {
            lock (gate)
            {
                return lastError;
            }
        }
    }

    public string LastStatus
    {
        get
        {
            lock (gate)
            {
                return lastStatus;
            }
        }
    }

    public string ConfiguredPreset
        => (configuration.AutorotationPresetName ?? string.Empty).Trim();

    public string ManagedPreset => "AOCCH";

    public string SelectedSource
    {
        get { lock (gate) return selectedSource; }
    }

    public string SelectedRole
    {
        get { lock (gate) return selectedRole; }
    }

    public decimal SelectedRange
    {
        get { lock (gate) return selectedRange; }
    }

    public bool RefreshBossModAvailability()
        => ProbeAvailability();

    public void Dispose()
    {
        ReleaseOwnership("Plugin disposal");
        DeleteManagedPreset("Plugin disposal");
    }

    public bool ValidateConfiguredPreset()
    {
        if (!ProbeAvailability())
        {
            SetError("BossMod IPC is unavailable.", warning: true);
            return false;
        }
        SetStatus("BossMod IPC is available; autorotation will be selected when combat begins.");
        return true;
    }

    public bool ApplyForCombat(string context)
    {
        if (!ProbeAvailability())
        {
            SetError($"BossMod IPC unavailable while entering {context}; continuing without autorotation.", warning: true);
            return false;
        }

        var preset = ConfiguredPreset;
        var serializedPreset = preset.Length == 0 ? string.Empty : bossMod.GetPreset(preset);
        if (serializedPreset.Length != 0)
        {
            SetStatus($"Using autorotation override '{preset}' for {context}.");
            if (ApplyPreset(preset, context, "Override"))
            {
                return true;
            }

            logger.Warning($"{BuildLogTag()} op=override-fallback preset=\"{preset}\" reason=activation-failed");
        }

        if (preset.Length != 0)
        {
            logger.Warning($"{BuildLogTag()} op=override-fallback preset=\"{preset}\" reason=missing-or-invalid");
        }

        if (!CreateManagedPreset())
        {
            SetError($"Failed to create managed AOCCH autorotation for {context}.", warning: true);
            return false;
        }

        return ApplyPreset(ManagedPreset, context, "Managed");
    }

    public void DeleteManagedPreset(string reason)
    {
        bool shouldDelete;
        lock (gate)
        {
            shouldDelete = managedPresetCreated;
        }

        if (!ProbeAvailability())
        {
            return;
        }

        if (!shouldDelete)
        {
            return;
        }

        if (string.Equals(bossMod.GetActivePreset(), ManagedPreset, StringComparison.Ordinal))
        {
            if (!bossMod.ClearActivePreset())
            {
                logger.Warning($"{BuildLogTag()} op=managed-delete reason=\"{reason}\" result=active-clear-failed");
                return;
            }
        }

        if (bossMod.DeletePreset(ManagedPreset))
        {
            lock (gate) managedPresetCreated = false;
            logger.Info($"{BuildLogTag()} op=managed-delete reason=\"{reason}\" result=success");
        }
        else
        {
            logger.Warning($"{BuildLogTag()} op=managed-delete reason=\"{reason}\" result=failed");
        }
    }

    private bool ApplyPreset(string preset, string context, string source)
    {
        var activePreset = bossMod.GetActivePreset();
        SetLastKnownActivePreset(activePreset);

        lock (gate) initialPreset = activePreset;

        logger.Info(activePreset.Length == 0
            ? $"{BuildLogTag()} op=capture context=\"{context}\" activePreset=empty"
            : $"{BuildLogTag()} op=capture context=\"{context}\" activePreset=\"{activePreset}\"");

        if (string.Equals(activePreset, preset, StringComparison.Ordinal))
        {
            lock (gate)
            {
                hasOwnership = false;
                changedPreset = false;
                ownedPreset = string.Empty;
            }

            lock (gate) selectedSource = source;
            SetStatus($"BossMod {source.ToLowerInvariant()} preset '{preset}' was already active before {context}; leaving ownership unchanged.");
            return true;
        }

        if (!bossMod.SetActivePreset(preset))
        {
            SetError($"Failed to activate BossMod preset '{preset}' for {context}; continuing without autorotation.", warning: true);
            return false;
        }

        activePreset = bossMod.GetActivePreset();
        SetLastKnownActivePreset(activePreset);
        if (!string.Equals(activePreset, preset, StringComparison.Ordinal))
        {
            SetError($"BossMod reported active preset '{activePreset}' instead of '{preset}' while entering {context}; continuing without autorotation.", warning: true);
            return false;
        }

        lock (gate)
        {
            hasOwnership = true;
            changedPreset = true;
            ownedPreset = preset;
        }

        lock (gate) selectedSource = source;
        SetStatus($"Applied BossMod {source.ToLowerInvariant()} preset '{preset}' for {context}.");
        logger.Info($"{BuildLogTag()} op=apply context=\"{context}\" preset=\"{preset}\" ownership=true");
        return true;
    }

    private bool CreateManagedPreset()
    {
        var assemblyDirectory = Plugin.PluginInterface.AssemblyLocation.DirectoryName;
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            SetError("Could not resolve plugin directory for managed autorotation.", warning: true);
            return false;
        }

        var path = Path.Combine(assemblyDirectory, "Data", "AocchAutorotation.json");
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new JsonException("Preset root is not an object.");
            root["Name"] = ManagedPreset;
            var modules = root["Modules"]?.AsObject() ?? throw new JsonException("Preset has no Modules object.");
            var stayClose = modules["BossMod.Autorotation.MiscAI.StayCloseToTarget"]?.AsArray() ?? throw new JsonException("Preset has no StayCloseToTarget module.");
            var role = roleResolver.Resolve(gameActionController.CurrentClassJobId);
            var range = role == AutorotationJobType.Melee ? configuration.MeleeTargetRange : configuration.RangedTargetRange;
            if (role == AutorotationJobType.Unknown)
            {
                logger.Warning($"{BuildLogTag()} op=role-unknown classJob={gameActionController.CurrentClassJobId}; using ranged target range.");
            }

            var rangeSetting = stayClose
                .Select(node => node?.AsObject())
                .FirstOrDefault(setting => string.Equals(setting?["Track"]?.GetValue<string>(), "range", StringComparison.Ordinal));
            if (rangeSetting == null)
            {
                throw new JsonException("Preset has no StayCloseToTarget range setting.");
            }

            rangeSetting["Option"] = range.ToString("0.##", CultureInfo.InvariantCulture);
            var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            if (!bossMod.CreatePreset(serialized, overwrite: true))
            {
                return false;
            }

            lock (gate)
            {
                managedPresetCreated = true;
                selectedRole = role.ToString();
                selectedRange = range;
            }
            logger.Info($"{BuildLogTag()} op=managed-create role={role} range={range.ToString(CultureInfo.InvariantCulture)}");
            return true;
        }
        catch (Exception ex)
        {
            SetError($"Failed to prepare managed autorotation: {ex.Message}", warning: true);
            return false;
        }
    }

    public void ReleaseOwnership(string reason)
    {
        var preset = string.Empty;
        var ownsPreset = false;

        lock (gate)
        {
            preset = ownedPreset;
            ownsPreset = hasOwnership && changedPreset && preset.Length != 0;
        }

        if (!ownsPreset)
        {
            ResetOwnershipState($"No BossMod preset owned while releasing for {reason}.");
            return;
        }

        if (!ProbeAvailability())
        {
            SetError($"BossMod IPC unavailable while releasing preset for {reason}.", warning: true);
            ResetOwnershipState($"Lost BossMod ownership tracking while releasing for {reason}.", clearError: false);
            return;
        }

        var activePreset = bossMod.GetActivePreset();
        SetLastKnownActivePreset(activePreset);
        if (!string.Equals(activePreset, preset, StringComparison.Ordinal))
        {
            logger.Warning($"{BuildLogTag()} op=release-conflict reason=\"{reason}\" expectedPreset=\"{preset}\" activePreset=\"{activePreset}\"");
            ResetOwnershipState($"BossMod ownership lost before release for {reason}.");
            return;
        }

        if (!bossMod.ClearActivePreset())
        {
            SetError($"Failed to clear BossMod preset '{preset}' for {reason}.", warning: true);
            return;
        }

        activePreset = bossMod.GetActivePreset();
        SetLastKnownActivePreset(activePreset);
        if (activePreset.Length != 0)
        {
            SetError($"BossMod still reported active preset '{activePreset}' after clear for {reason}.", warning: true);
            return;
        }

        logger.Info($"{BuildLogTag()} op=release reason=\"{reason}\" preset=\"{preset}\" result=cleared");
        ResetOwnershipState($"Cleared BossMod preset for {reason}.");
    }

    public void ResetInstanceState(string reason)
    {
        lock (gate)
        {
            hasOwnership = false;
            changedPreset = false;
            initialPreset = string.Empty;
            ownedPreset = string.Empty;
            lastKnownActivePreset = string.Empty;
            lastError = string.Empty;
            lastStatus = "Idle";
            selectedSource = "None";
            selectedRole = "Unknown";
            selectedRange = 0;
        }

        logger.Info($"[Autorotation] op=reset reason={reason}");
    }

    private bool ProbeAvailability()
    {
        var available = bossMod.IsAvailable();
        lock (gate)
        {
            bossModAvailable = available;
        }

        return available;
    }

    private void ResetOwnershipState(string status, bool clearError = true)
    {
        lock (gate)
        {
            hasOwnership = false;
            changedPreset = false;
            ownedPreset = string.Empty;
            initialPreset = string.Empty;
            if (clearError)
            {
                lastError = string.Empty;
            }

            lastStatus = status;
        }
    }

    private void SetStatus(string status)
    {
        lock (gate)
        {
            lastStatus = status;
            lastError = string.Empty;
        }
    }

    private void SetError(string error, bool warning)
    {
        lock (gate)
        {
            lastStatus = error;
            lastError = error;
            hasOwnership = false;
            changedPreset = false;
            initialPreset = string.Empty;
            ownedPreset = string.Empty;
        }

        if (warning)
        {
            logger.Warning($"{BuildLogTag()} op=error status=warning reason={error}");
        }
        else
        {
            logger.Info($"{BuildLogTag()} op=error status=info reason={error}");
        }
    }

    private string BuildLogTag()
        => $"[Autorotation preset=\"{ConfiguredPreset}\" owned=\"{OwnedPreset}\" hasOwnership={HasOwnership}]";

    private void SetLastKnownActivePreset(string preset)
    {
        lock (gate)
        {
            lastKnownActivePreset = preset;
        }
    }
}
