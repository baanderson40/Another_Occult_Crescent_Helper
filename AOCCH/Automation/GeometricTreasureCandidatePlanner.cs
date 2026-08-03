using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using AOCCH.Data;

namespace AOCCH.Automation;

public readonly record struct TreasureHintObservation(Vector3 Position, TreasureDirection Direction);

public static class GeometricTreasureCandidatePlanner
{
    public static List<TreasureCofferCandidateData> Rank(
        IEnumerable<TreasureCofferCandidateData> candidates,
        IReadOnlyList<TreasureHintObservation> observations,
        ISet<string> handledCandidateLabels,
        Vector3 currentPosition,
        float maximumHintAngleDegrees)
    {
        var ranked = new List<(TreasureCofferCandidateData Candidate, float WorstAngle, float TotalAngle, float TravelDistance)>();
        foreach (var candidate in candidates)
        {
            if (handledCandidateLabels.Contains(candidate.Label))
            {
                continue;
            }

            var position = candidate.Position.ToVector3();
            var worstAngle = 0f;
            var totalAngle = 0f;
            var rejected = false;
            foreach (var observation in observations)
            {
                if (!TryGetHintAngle(observation, position, out var angle) || angle > maximumHintAngleDegrees)
                {
                    rejected = true;
                    break;
                }

                worstAngle = Math.Max(worstAngle, angle);
                totalAngle += angle;
            }

            if (!rejected)
            {
                ranked.Add((candidate, worstAngle, totalAngle, CalculateFlatDistance(currentPosition, position)));
            }
        }

        return ranked
            .OrderBy(entry => entry.WorstAngle)
            .ThenBy(entry => entry.TotalAngle)
            .ThenBy(entry => entry.TravelDistance)
            .ThenBy(entry => entry.Candidate.CandidateKey, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Candidate)
            .ToList();
    }

    private static bool TryGetHintAngle(TreasureHintObservation observation, Vector3 candidatePosition, out float angle)
    {
        angle = 0f;
        var direction = GetDirectionVector(observation.Direction);
        if (direction == Vector2.Zero)
        {
            return false;
        }

        var candidateDirection = new Vector2(
            candidatePosition.X - observation.Position.X,
            candidatePosition.Z - observation.Position.Z);
        if (candidateDirection.LengthSquared() < 0.0001f)
        {
            return true;
        }

        candidateDirection = Vector2.Normalize(candidateDirection);
        var dot = Math.Clamp(Vector2.Dot(direction, candidateDirection), -1f, 1f);
        angle = MathF.Acos(dot) * (180f / MathF.PI);
        return float.IsFinite(angle);
    }

    private static Vector2 GetDirectionVector(TreasureDirection direction)
        => direction switch
        {
            TreasureDirection.North => new Vector2(0f, -1f),
            TreasureDirection.Northeast => Vector2.Normalize(new Vector2(1f, -1f)),
            TreasureDirection.East => new Vector2(1f, 0f),
            TreasureDirection.Southeast => Vector2.Normalize(new Vector2(1f, 1f)),
            TreasureDirection.South => new Vector2(0f, 1f),
            TreasureDirection.Southwest => Vector2.Normalize(new Vector2(-1f, 1f)),
            TreasureDirection.West => new Vector2(-1f, 0f),
            TreasureDirection.Northwest => Vector2.Normalize(new Vector2(-1f, -1f)),
            _ => Vector2.Zero,
        };

    private static float CalculateFlatDistance(Vector3 from, Vector3 to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
