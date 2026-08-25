using System;
using System.Collections.Generic;
using System.Numerics;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace AOCCH.Automation;

public sealed class ForkedTowerRouteProbeController : IDisposable
{
    private static readonly TimeSpan TransitionStableTime = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(10);
    private const uint ForkedTowerCeId = 64;

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly OccultCrescentScanner scanner;
    private readonly MovementController movementController;
    private readonly AocchLogger logger;
    private readonly object gate = new();
    private readonly IReadOnlyList<RouteProbeStep> steps = CreateSteps();

    private ForkedTowerRouteProbeState state = ForkedTowerRouteProbeState.Idle;
    private string lastTransition = "Idle";
    private string lastError = string.Empty;
    private int stepIndex = -1;
    private DateTimeOffset transitionClearSince = DateTimeOffset.MinValue;

    public ForkedTowerRouteProbeController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        OccultCrescentScanner scanner,
        MovementController movementController,
        AocchLogger logger)
    {
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.scanner = scanner;
        this.movementController = movementController;
        this.logger = logger;
        framework.Update += OnFrameworkUpdate;
    }

    public ForkedTowerRouteProbeState State
    {
        get { lock (gate) return state; }
    }

    public string LastTransition
    {
        get { lock (gate) return lastTransition; }
    }

    public string LastError
    {
        get { lock (gate) return lastError; }
    }

    public int StepIndex
    {
        get { lock (gate) return stepIndex; }
    }

    public string CurrentStepDescription
    {
        get
        {
            lock (gate)
            {
                return stepIndex >= 0 && stepIndex < steps.Count ? steps[stepIndex].Description : "None";
            }
        }
    }

    public int StepCount => steps.Count;

    public bool Start()
    {
        if (State is not ForkedTowerRouteProbeState.Idle
            and not ForkedTowerRouteProbeState.Stopped
            and not ForkedTowerRouteProbeState.Completed
            and not ForkedTowerRouteProbeState.Failed)
        {
            return false;
        }

        if (scanner.Snapshot.CurrentCriticalEncounterId != ForkedTowerCeId)
        {
            SetFailure("Forked Tower route probe requires active CE 64.");
            return false;
        }

        lock (gate)
        {
            stepIndex = 0;
            lastError = string.Empty;
        }

        StartCurrentStep();
        return true;
    }

    public bool Advance()
    {
        if (State != ForkedTowerRouteProbeState.WaitingForManualAdvance)
        {
            return false;
        }

        lock (gate)
        {
            stepIndex++;
        }

        if (StepIndex >= steps.Count)
        {
            TransitionTo(ForkedTowerRouteProbeState.Completed, "Route probe completed.");
            return true;
        }

        StartCurrentStep();
        return true;
    }

    public void Stop(string reason)
    {
        movementController.Stop(reason);
        TransitionTo(ForkedTowerRouteProbeState.Stopped, reason);
    }

    public void ResetInstanceState(string reason)
    {
        movementController.Stop(reason);
        lock (gate)
        {
            state = ForkedTowerRouteProbeState.Idle;
            lastTransition = "Idle";
            lastError = string.Empty;
            stepIndex = -1;
            transitionClearSince = DateTimeOffset.MinValue;
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        if (State is not ForkedTowerRouteProbeState.Idle
            and not ForkedTowerRouteProbeState.Stopped
            and not ForkedTowerRouteProbeState.Completed
            and not ForkedTowerRouteProbeState.Failed)
        {
            Stop("Forked Tower route probe disposal");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var currentState = State;
        if (currentState is ForkedTowerRouteProbeState.Idle
            or ForkedTowerRouteProbeState.Stopped
            or ForkedTowerRouteProbeState.Completed
            or ForkedTowerRouteProbeState.Failed)
        {
            return;
        }

        var player = objectTable.LocalPlayer;
        if (player == null || player.CurrentHp == 0)
        {
            if (currentState != ForkedTowerRouteProbeState.PausedForDeath)
            {
                movementController.Stop("Forked Tower route probe paused while player is dead or unavailable.");
                TransitionTo(ForkedTowerRouteProbeState.PausedForDeath, "Paused while waiting for player recovery.");
            }

            return;
        }

        if (currentState == ForkedTowerRouteProbeState.PausedForDeath)
        {
            StartCurrentStep();
            return;
        }

        if (currentState == ForkedTowerRouteProbeState.WaitingForTransition)
        {
            TickTransitionWait();
            return;
        }

        if (currentState != ForkedTowerRouteProbeState.Moving)
        {
            return;
        }

        var step = steps[StepIndex];
        if (step.IsZoneLine && condition[ConditionFlag.BetweenAreas])
        {
            movementController.Stop("Forked Tower zone-line transition detected; preventing vnav reroute.");
            transitionClearSince = DateTimeOffset.MinValue;
            TransitionTo(ForkedTowerRouteProbeState.WaitingForTransition, $"Transition started at {step.Description}.");
            return;
        }

        if (movementController.State is MovementState.Failed or MovementState.TimedOut)
        {
            SetFailure($"Route probe movement failed: {movementController.LastError}");
            return;
        }

        if (movementController.State == MovementState.Arrived)
        {
            movementController.Stop($"Reached Forked Tower route probe landmark: {step.Description}.");
            LogPlayerPosition(step.Description);
            TransitionTo(ForkedTowerRouteProbeState.WaitingForManualAdvance, $"Reached {step.Description}; waiting for manual advance.");
            return;
        }

        logger.DebugThrottled(
            "forked-tower-route-probe",
            WaitLogInterval,
            $"Route probe moving step={StepIndex + 1}/{steps.Count} description=\"{step.Description}\" movementState={movementController.State} distance={movementController.DistanceRemaining:0.0}.");
    }

    private void TickTransitionWait()
    {
        if (condition[ConditionFlag.BetweenAreas])
        {
            transitionClearSince = DateTimeOffset.MinValue;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (transitionClearSince == DateTimeOffset.MinValue)
        {
            transitionClearSince = now;
            return;
        }

        if (now - transitionClearSince < TransitionStableTime)
        {
            return;
        }

        LogPlayerPosition("transition-complete");
        TransitionTo(ForkedTowerRouteProbeState.WaitingForManualAdvance, "Zone-line transition completed; vnav remains stopped until manual advance.");
    }

    private void StartCurrentStep()
    {
        var step = steps[StepIndex];
        movementController.SetLogOwner("ForkedTowerRouteProbe");
        if (!movementController.StartDirectMove(
                step.Description,
                step.Destination,
                step.ArrivalTolerance,
                shouldMountBeforeStep: false,
                destinationAlreadyResolved: true))
        {
            SetFailure($"Failed to start route probe movement: {movementController.LastError}");
            return;
        }

        TransitionTo(ForkedTowerRouteProbeState.Moving, $"Moving to {step.Description}.");
    }

    private void LogPlayerPosition(string context)
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            logger.Warning($"[ForkedTowerRouteProbe] op=player-position context=\"{context}\" available=false");
            return;
        }

        var position = player.Position;
        logger.Info(
            $"[ForkedTowerRouteProbe] op=player-position context=\"{context}\" " +
            $"territory={scanner.Snapshot.TerritoryTypeId} territoryKey={scanner.Snapshot.TerritoryKey} " +
            $"step={StepIndex + 1}/{steps.Count} position=<{position.X:0.000},{position.Y:0.000},{position.Z:0.000}>");
    }

    private void SetFailure(string reason)
    {
        movementController.Stop(reason);
        TransitionTo(ForkedTowerRouteProbeState.Failed, reason, reason);
    }

    private void TransitionTo(ForkedTowerRouteProbeState nextState, string reason, string? error = null)
    {
        lock (gate)
        {
            state = nextState;
            lastTransition = reason;
            if (error != null)
            {
                lastError = error;
            }
        }

        logger.Info($"[ForkedTowerRouteProbe] op=transition state={nextState} step={StepIndex + 1}/{steps.Count} reason=\"{reason}\"");
    }

    private static IReadOnlyList<RouteProbeStep> CreateSteps()
        =>
        [
            new("Teleportation Sigil 2015189", new Vector3(-900f, -986.1f, 772.7f), 2f, false),
            new("Personal Spoils 1996", new Vector3(-900.02f, -980.01f, 692.99f), 2f, false),
            new("Teleportation Sigil 2015190", new Vector3(-900f, -980f, 687.27f), 2f, false),
            new("Zone line 1 beyond-point / checkpoint", new Vector3(393.130f, -699.907f, 833.137f), 5f, true),
            new("Teleportation Sigil 2015191", new Vector3(540f, -700f, 926f), 2f, false),
            new("Checkpoint 2", new Vector3(531.417f, -679.900f, 829.602f), 5f, false),
            new("Zone line 2 beyond-point / checkpoint", new Vector3(538.551f, -700f, 127.088f), 5f, true),
            new("Checkpoint 4", new Vector3(597.918f, -700f, 105.892f), 5f, false),
            new("Zone line 3 beyond-point / checkpoint", new Vector3(100.001f, -708f, 674.337f), 5f, true),
            new("Checkpoint 6", new Vector3(0.158f, -708f, -442.808f), 5f, false),
            new("Zone line 4 beyond-point / checkpoint", new Vector3(17.780f, -680.544f, -749.331f), 5f, true),
            new("Expedition Base Camp exit object", new Vector3(795f, -600f, -721f), 6f, false),
        ];

    private sealed record RouteProbeStep(string Description, Vector3 Destination, float ArrivalTolerance, bool IsZoneLine);
}
