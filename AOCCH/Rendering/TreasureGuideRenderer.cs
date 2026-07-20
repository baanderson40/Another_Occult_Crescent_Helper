using System;
using System.Linq;
using System.Numerics;
using AOCCH.Logging;
using AOCCH.Scanning;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Pictomancy;

namespace AOCCH.Rendering;

public sealed class TreasureGuideRenderer : IDisposable
{
    private static readonly Vector4 NearLineColor = new(0.2f, 1f, 0.25f, 1f);
    private static readonly Vector4 FarLineColor = new(1f, 0.9f, 0.1f, 1f);
    private static readonly Vector4 MarkerColor = new(0.25f, 0.9f, 1f, 0.25f);
    private const float LineHalfWidth = 0.1f;
    private const float LineStartOffset = 0.9f;
    private const float MarkerRadius = 0.5f;
    private const float MarkerHeight = 0.25f;
    private const float LineColorNearDistance = 25f;
    private const float LineColorFarDistance = 120f;

    private readonly Configuration configuration;
    private readonly OccultCrescentScanner scanner;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly AocchLogger logger;
    private PctContext? pictomancyContext;
    private bool drawFailureLogged;

    public TreasureGuideRenderer(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        OccultCrescentScanner scanner,
        ICondition condition,
        IObjectTable objectTable,
        AocchLogger logger)
    {
        this.configuration = configuration;
        this.scanner = scanner;
        this.condition = condition;
        this.objectTable = objectTable;
        this.logger = logger;

        try
        {
            pictomancyContext = PctService.Initialize(pluginInterface, new PctOptions
            {
                EnableVfxRenderer = false,
            });
        }
        catch (Exception ex)
        {
            logger.Error($"[TreasureGuide] Pictomancy initialization failed; guide disabled. {ex}");
        }
    }

    public void Draw()
    {
        if (pictomancyContext == null || !configuration.EnableOverworldTreasureGuide)
        {
            return;
        }

        var snapshot = scanner.Snapshot;
        if (!snapshot.IsInSupportedTerritory
            || snapshot.IsInCriticalEncounter
            || snapshot.Fates.Any(fate => fate.IsInFate)
            || condition[ConditionFlag.BetweenAreas]
            || condition[ConditionFlag.OccupiedInQuestEvent]
            || condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.WatchingCutscene78]
            || objectTable.LocalPlayer is not { CurrentHp: > 0 } player
            || snapshot.DetectedTreasures.Count == 0)
        {
            return;
        }

        try
        {
            var target = snapshot.DetectedTreasures[0];
            var hints = new PctDrawHints
            {
                DefaultParams = new PctDxParams
                {
                    OccludedAlpha = 0.5f,
                    OcclusionTolerance = 1f,
                    FadeStart = 25f,
                    FadeStop = 120f,
                },
            };

            using var draw = PctService.Draw(hints: hints);
            if (draw == null)
            {
                return;
            }

            var lineDistance = Vector3.Distance(player.Position, target.Position);
            var distanceT = Math.Clamp(
                (lineDistance - LineColorNearDistance) / (LineColorFarDistance - LineColorNearDistance),
                0f,
                1f);
            var lineColor = ImGuiColor(Vector4.Lerp(NearLineColor, FarLineColor, distanceT));
            var markerColor = ImGuiColor(MarkerColor);
            var markerParams = hints.DefaultParams with
            {
                FresnelSpread = 0.5f,
                FresnelIntensity = 0.5f,
                FresnelOpacity = 0.8f,
            };
            var direction = new Vector3(
                target.Position.X - player.Position.X,
                0f,
                target.Position.Z - player.Position.Z);
            if (direction.LengthSquared() > 0.01f)
            {
                direction = Vector3.Normalize(direction);
            }
            else
            {
                direction = Vector3.UnitZ;
            }

            var lineStart = player.Position + direction * LineStartOffset;
            draw.AddLineFilled(lineStart, target.Position, LineHalfWidth, lineColor, lineColor, p: hints.DefaultParams);
            draw.AddSphere(target.Position + new Vector3(0f, MarkerHeight, 0f), MarkerRadius, markerColor, markerParams);
            drawFailureLogged = false;
        }
        catch (Exception ex)
        {
            if (drawFailureLogged)
            {
                return;
            }

            drawFailureLogged = true;
            logger.Error($"[TreasureGuide] Pictomancy draw failed; guide disabled for this draw path. {ex}");
        }
    }

    public void Dispose()
    {
        pictomancyContext?.Dispose();
        pictomancyContext = null;
    }

    private static uint ImGuiColor(Vector4 color)
        => Dalamud.Bindings.ImGui.ImGui.ColorConvertFloat4ToU32(color);
}
