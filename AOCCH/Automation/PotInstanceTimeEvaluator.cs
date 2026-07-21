using System;

using AOCCH.Logging;
using AOCCH.Scanning;

namespace AOCCH.Automation;

public sealed class PotInstanceTimeEvaluator
{
    private const float PotCycleSeconds = 30f * 60f;
    private const float SpawnGraceSeconds = 60f;

    private readonly Configuration configuration;
    private readonly AocchLogger logger;

    public PotInstanceTimeEvaluator(Configuration configuration, AocchLogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    public PotInstanceTimeDecision Evaluate(ScannerSnapshot scannerSnapshot, PotCycleSnapshot potCycleSnapshot, DateTimeOffset now, float remainingSeconds, bool hasContentTimer)
    {
        PotInstanceTimeDecision decision;
        if (!configuration.ManageInstanceTime)
        {
            decision = new PotInstanceTimeDecision
            {
                ManageInstanceTimeEnabled = false,
                IsContentTimerAvailable = hasContentTimer,
                RemainingSeconds = remainingSeconds,
                Reason = "Instance-time management is disabled.",
            };
            logger.DebugThrottled("pot-instance-time-evaluator", TimeSpan.FromSeconds(10), $"Pot instance-time evaluation: allow={decision.AllowNextPotCycle} shouldLeave={decision.ShouldAttemptLeave} timerAvailable={decision.IsContentTimerAvailable} remainingSeconds={remainingSeconds:0.0} reason={decision.Reason}");
            return decision;
        }

        if (!hasContentTimer || remainingSeconds <= 0f)
        {
            decision = new PotInstanceTimeDecision
            {
                ManageInstanceTimeEnabled = true,
                IsContentTimerAvailable = false,
                RemainingSeconds = remainingSeconds,
                Reason = "Instance-time management is enabled, but no instanced-content timer is available.",
            };
            logger.DebugThrottled("pot-instance-time-evaluator", TimeSpan.FromSeconds(10), $"Pot instance-time evaluation: allow={decision.AllowNextPotCycle} shouldLeave={decision.ShouldAttemptLeave} timerAvailable={decision.IsContentTimerAvailable} remainingSeconds={remainingSeconds:0.0} reason={decision.Reason}");
            return decision;
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

        decision = new PotInstanceTimeDecision
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

        logger.DebugThrottled("pot-instance-time-evaluator", TimeSpan.FromSeconds(10), $"Pot instance-time evaluation: allow={decision.AllowNextPotCycle} shouldLeave={decision.ShouldAttemptLeave} timerAvailable={decision.IsContentTimerAvailable} remainingSeconds={remainingSeconds:0.0} waitSeconds={decision.WaitSecondsUntilNextPot:0.0} requiredSeconds={decision.RequiredSeconds:0.0} timingSource={decision.TimingSource} activePot={scannerSnapshot.ActivePotFate?.Name ?? "none"} reason={decision.Reason}");
        return decision;
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
