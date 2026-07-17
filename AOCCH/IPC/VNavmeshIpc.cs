using System;
using System.Numerics;
using AOCCH.Logging;
using Dalamud.Plugin.Ipc;

namespace AOCCH.IPC;

public sealed class VNavmeshIpc
{
    private static readonly TimeSpan IpcFailureLogInterval = TimeSpan.FromSeconds(30);

    private readonly AocchLogger logger;
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<float> buildProgress;
    private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveTo;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseTo;
    private readonly ICallGateSubscriber<bool> simpleMovePathfindInProgress;
    private readonly ICallGateSubscriber<bool> isPathRunning;
    private readonly ICallGateSubscriber<object> stopPath;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private bool? lastAvailability;

    public VNavmeshIpc(AocchLogger logger)
    {
        this.logger = logger;
        isReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        buildProgress = Plugin.PluginInterface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");
        pathfindAndMoveTo = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pathfindAndMoveCloseTo = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        simpleMovePathfindInProgress = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        isPathRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        stopPath = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        nearestPoint = Plugin.PluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        pointOnFloor = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
    }

    public bool IsReady()
        => Invoke("vnavmesh.Nav.IsReady", isReady.InvokeFunc, false, logAvailability: true);

    public float GetBuildProgress()
        => Invoke("vnavmesh.Nav.BuildProgress", buildProgress.InvokeFunc, 0f);

    public bool PathfindAndMoveTo(Vector3 destination, bool fly)
        => Invoke($"vnavmesh.PathfindAndMoveTo({destination.X:0.0}, {destination.Y:0.0}, {destination.Z:0.0})",
            () => pathfindAndMoveTo.InvokeFunc(destination, fly), false);

    public bool PathfindAndMoveCloseTo(Vector3 destination, bool fly, float range)
        => Invoke($"vnavmesh.PathfindAndMoveCloseTo({destination.X:0.0}, {destination.Y:0.0}, {destination.Z:0.0}, range={range:0.0})",
            () => pathfindAndMoveCloseTo.InvokeFunc(destination, fly, range), false);

    public bool IsPathRunning()
        => Invoke("vnavmesh.Path.IsRunning", isPathRunning.InvokeFunc, false);

    public bool IsPathfindInProgress()
        => Invoke("vnavmesh.SimpleMove.PathfindInProgress", simpleMovePathfindInProgress.InvokeFunc, false);

    public Vector3? FindNearestPoint(Vector3 position, float halfExtentXZ, float halfExtentY)
        => Invoke("vnavmesh.Query.Mesh.NearestPoint",
            () => nearestPoint.InvokeFunc(position, halfExtentXZ, halfExtentY), null);

    public Vector3? FindPointOnFloor(Vector3 position, bool allowUnlandable, float halfExtentXZ)
        => Invoke("vnavmesh.Query.Mesh.PointOnFloor",
            () => pointOnFloor.InvokeFunc(position, allowUnlandable, halfExtentXZ), null);

    public void Stop()
    {
        try
        {
            stopPath.InvokeAction();
        }
        catch (Exception ex)
        {
            logger.WarningThrottled("vnavmesh-ipc-failure", IpcFailureLogInterval, $"[VNavmeshIpc] op=stop-failed reason={ex.Message}");
        }
    }

    private T Invoke<T>(string operation, Func<T> action, T fallback, bool logAvailability = false)
    {
        try
        {
            var result = action();
            if (logAvailability)
            {
                SetAvailability(true);
            }

            return result;
        }
        catch (Exception ex)
        {
            if (logAvailability)
            {
                SetAvailability(false);
            }

            logger.DebugThrottled("vnavmesh-ipc-failure", IpcFailureLogInterval, $"IPC call failed for {operation}: {ex.Message}");
            return fallback;
        }
    }

    private void SetAvailability(bool available)
    {
        if (lastAvailability == available)
        {
            return;
        }

        lastAvailability = available;
        if (available)
        {
            logger.Info("[VNavmeshIpc] op=availability available=true");
        }
        else
        {
            logger.Warning("[VNavmeshIpc] op=availability available=false");
        }
    }
}
