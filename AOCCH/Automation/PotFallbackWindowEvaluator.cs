using System;

using AOCCH.Logging;

namespace AOCCH.Automation;

public sealed class PotFallbackWindowEvaluator
{
    private readonly Configuration configuration;
    private readonly AocchLogger logger;

    public PotFallbackWindowEvaluator(Configuration configuration, AocchLogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    public PotFallbackStartDecision EvaluateCeStart(PotCycleSnapshot potCycleSnapshot, DateTimeOffset now, bool canRunPotTreasure)
    {
        var decision = EvaluateStart(
            potCycleSnapshot,
            now,
            TimeSpan.FromMinutes(Math.Max(0, configuration.CeFallbackCutoffMinutes)),
            "CE",
            canRunPotTreasure);

        logger.DebugThrottled("pot-fallback-ce", TimeSpan.FromSeconds(10), $"Pot fallback evaluation for CE: allow={decision.AllowStart} departureAt={decision.DepartureAt:O} timeUntilDeparture={decision.TimeUntilDeparture} reason={decision.Reason}");
        return decision;
    }

    public PotFallbackStartDecision EvaluateFateStart(PotCycleSnapshot potCycleSnapshot, DateTimeOffset now, bool canRunPotTreasure)
    {
        var decision = EvaluateStart(
            potCycleSnapshot,
            now,
            TimeSpan.FromMinutes(Math.Max(0, configuration.FateFallbackCutoffMinutes)),
            "FATE",
            canRunPotTreasure);

        logger.DebugThrottled("pot-fallback-fate", TimeSpan.FromSeconds(10), $"Pot fallback evaluation for FATE: allow={decision.AllowStart} departureAt={decision.DepartureAt:O} timeUntilDeparture={decision.TimeUntilDeparture} reason={decision.Reason}");
        return decision;
    }

    private PotFallbackStartDecision EvaluateStart(
        PotCycleSnapshot potCycleSnapshot,
        DateTimeOffset now,
        TimeSpan cutoffWindow,
        string activityName,
        bool canRunPotTreasure)
    {
        if (!canRunPotTreasure)
        {
            return Allow($"{activityName} fallback start allowed because pot treasure is unavailable in this territory.");
        }

        if (!configuration.EnablePotFarming)
        {
            return Allow($"{activityName} fallback start allowed because pot farming is disabled.");
        }

        if (!potCycleSnapshot.HasPredictedNextPot)
        {
            return Allow($"{activityName} fallback start allowed because no pot departure is predicted yet.");
        }

        var departureAt = potCycleSnapshot.PredictedNextSpawnAt - TimeSpan.FromMinutes(Math.Max(0, configuration.SpawnLeadMinutes));
        if (departureAt == DateTimeOffset.MinValue)
        {
            return Allow($"{activityName} fallback start allowed because no pot departure is predicted yet.");
        }

        var timeUntilDeparture = departureAt - now;
        if (timeUntilDeparture <= cutoffWindow)
        {
            return new PotFallbackStartDecision
            {
                AllowStart = false,
                Reason = $"{activityName} fallback start blocked: pot departure in {FormatDuration(timeUntilDeparture)} (cutoff {cutoffWindow.TotalMinutes:0}m for {potCycleSnapshot.PredictedNextPotFateName}).",
                DepartureAt = departureAt,
                TimeUntilDeparture = timeUntilDeparture,
            };
        }

        return new PotFallbackStartDecision
        {
            AllowStart = true,
            Reason = $"{activityName} fallback start allowed: pot departure in {FormatDuration(timeUntilDeparture)}.",
            DepartureAt = departureAt,
            TimeUntilDeparture = timeUntilDeparture,
        };
    }

    private static PotFallbackStartDecision Allow(string reason)
        => new()
        {
            AllowStart = true,
            Reason = reason,
        };

    private static string FormatDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return "0m";
        }

        if (value.TotalMinutes >= 1)
        {
            return $"{Math.Floor(value.TotalMinutes):0}m";
        }

        return $"{Math.Ceiling(value.TotalSeconds):0}s";
    }
}
