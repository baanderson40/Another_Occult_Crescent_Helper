using System;
using AOCCH.IPC;
using AOCCH.Logging;

namespace AOCCH.Automation;

public sealed class AutorotationController : IDisposable
{
    private readonly BossModIpc bossMod;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private bool bossModAvailable;
    private bool hasOwnership;
    private bool changedPreset;
    private string initialPreset = string.Empty;
    private string ownedPreset = string.Empty;
    private string lastKnownActivePreset = string.Empty;
    private string lastError = string.Empty;
    private string lastStatus = "Idle";

    public AutorotationController(BossModIpc bossMod, Configuration configuration, AocchLogger logger)
    {
        this.bossMod = bossMod;
        this.configuration = configuration;
        this.logger = logger;
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

    public bool RefreshBossModAvailability()
        => ProbeAvailability();

    public void Dispose()
    {
        ReleaseOwnership("Plugin disposal");
    }

    public bool ValidateConfiguredPreset()
    {
        var preset = ConfiguredPreset;
        if (preset.Length == 0)
        {
            SetStatus("Autorotation disabled; no preset configured.");
            return false;
        }

        if (!ProbeAvailability())
        {
            SetError("BossMod IPC is unavailable.", warning: true);
            return false;
        }

        var activePreset = bossMod.GetActivePreset();
        SetLastKnownActivePreset(activePreset);
        if (activePreset.Length != 0)
        {
            SetStatus($"Skipped destructive autorotation validation because BossMod already has active preset '{activePreset}'.");
            logger.Info($"{BuildLogTag()} op=validate-skip activePreset=\"{activePreset}\" reason=already-active");
            return true;
        }

        if (!bossMod.SetActivePreset(preset))
        {
            SetError($"Failed to activate BossMod preset '{preset}' during validation.", warning: true);
            return false;
        }

        activePreset = bossMod.GetActivePreset();
        SetLastKnownActivePreset(activePreset);
        if (!string.Equals(activePreset, preset, StringComparison.Ordinal))
        {
            SetError($"BossMod reported active preset '{activePreset}' instead of '{preset}' during validation.", warning: true);
            return false;
        }

        if (!bossMod.ClearActivePreset())
        {
            SetError($"Failed to clear BossMod preset '{preset}' after validation.", warning: true);
            return false;
        }

        activePreset = bossMod.GetActivePreset();
        SetLastKnownActivePreset(activePreset);
        if (activePreset.Length != 0)
        {
            SetError($"BossMod still reported active preset '{activePreset}' after validation clear.", warning: true);
            return false;
        }

        SetStatus($"Validated BossMod preset '{preset}'.");
        logger.Info($"{BuildLogTag()} op=validate preset=\"{preset}\" result=success");
        return true;
    }

    public bool ApplyForCombat(string context)
    {
        var preset = ConfiguredPreset;
        if (preset.Length == 0)
        {
            SetStatus($"Autorotation disabled for {context}; no preset configured.");
            return false;
        }

        if (!ProbeAvailability())
        {
            SetError($"BossMod IPC unavailable while entering {context}; continuing without autorotation.", warning: true);
            return false;
        }

        var activePreset = bossMod.GetActivePreset();
        SetLastKnownActivePreset(activePreset);

        lock (gate)
        {
            initialPreset = activePreset;
        }

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

            SetStatus($"BossMod preset '{preset}' was already active before {context}; leaving ownership unchanged.");
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

        SetStatus($"Applied BossMod preset '{preset}' for {context}.");
        logger.Info($"{BuildLogTag()} op=apply context=\"{context}\" preset=\"{preset}\" ownership=true");
        return true;
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
