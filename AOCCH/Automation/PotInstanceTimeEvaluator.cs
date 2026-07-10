using System;

using AOCCH.Scanning;

namespace AOCCH.Automation;

public sealed class PotInstanceTimeEvaluator
{
    private const float PotCycleSeconds = 30f * 60f;
    private const float SpawnGraceSeconds = 60f;

    private readonly Configuration configuration;

    public PotInstanceTimeEvaluator(Configuration configuration)
    {
        this.configuration = configuration;
    }

    public PotInstanceTimeDecision Evaluate(ScannerSnapshot scannerSnapshot, PotCycleSnapshot potCycleSnapshot, DateTimeOffset now, float remainingSeconds, bool hasContentTimer)
    {
        if (!configuration.ManageInstanceTime)
        {
            return new PotInstanceTimeDecision
            {
                ManageInstanceTimeEnabled = false,
                IsContentTimerAvailable = hasContentTimer,
                RemainingSeconds = remainingSeconds,
                Reason = "Instance-time management is disabled.",
            };
        }

        if (!hasContentTimer || remainingSeconds <= 0f)
        {
            return new PotInstanceTimeDecision
            {
                ManageInstanceTimeEnabled = true,
                IsContentTimerAvailable = false,
                RemainingSeconds = remainingSeconds,
                Reason = "Instance-time management is enabled, but no instanced-content timer is available.",
            };
        }

        var waitSeconds = 0f;
        var timingSource = "active_pot_fate";
        if (scannerSnapshot.ActivePotFate == null)
        {
            (waitSeconds, timingSource) = GetSecondsUntilNextPotSpawn(potCycleSnapshot, now);
        }

        var requiredSeconds = waitSeconds
            + (Math.Max(0, configuration.FateCompletionBudgetMinutes) * 60f)
            + (Math.Max(0, configuration.TreasureHuntBudgetMinutes) * 60f)
            + (Math.Max(0, configuration.InstanceExitBufferMinutes) * 60f);
        var allowNextPotCycle = remainingSeconds >= requiredSeconds;

        return new PotInstanceTimeDecision
        {
            ManageInstanceTimeEnabled = true,
            IsContentTimerAvailable = true,
            AllowNextPotCycle = allowNextPotCycle,
            ShouldAttemptLeave = !allowNextPotCycle,
            RemainingSeconds = remainingSeconds,
            WaitSecondsUntilNextPot = waitSeconds,
            RequiredSeconds = requiredSeconds,
            TimingSource = timingSource,
            Reason = allowNextPotCycle
                ? $"Instance time check allows another pot cycle: remaining {FormatMinutes(remainingSeconds)}, required {FormatMinutes(requiredSeconds)}."
                : $"Instance time check blocks another pot cycle: remaining {FormatMinutes(remainingSeconds)}, required {FormatMinutes(requiredSeconds)}.",
        };
    }

    private static (float WaitSeconds, string TimingSource) GetSecondsUntilNextPotSpawn(PotCycleSnapshot potCycleSnapshot, DateTimeOffset now)
    {
        if (!potCycleSnapshot.HasKnownAnchor || potCycleSnapshot.LastObservedSpawnAt == DateTimeOffset.MinValue)
        {
            return (PotCycleSeconds, "unknown_cycle_worst_case");
        }

        var elapsedSeconds = (float)Math.Max(0d, (now - potCycleSnapshot.LastObservedSpawnAt).TotalSeconds);
        if (elapsedSeconds < PotCycleSeconds)
        {
            return (PotCycleSeconds - elapsedSeconds, "predicted_from_last_spawn");
        }

        var phase = elapsedSeconds % PotCycleSeconds;
        if (phase <= SpawnGraceSeconds)
        {
            return (0f, "predicted_spawn_grace");
        }

        return (PotCycleSeconds - phase, "rolled_spawn_schedule");
    }

    private static string FormatMinutes(float seconds)
        => $"{(seconds / 60f):0.0}m";
}
