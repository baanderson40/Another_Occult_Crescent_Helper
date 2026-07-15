using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using AOCCH.Logging;
using Dalamud.Plugin.Ipc;

namespace AOCCH.IPC;

public sealed class VNavmeshIpc
{
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
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, object?> pathfindRoute;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>> navPathfindRoute;

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
        pathfindRoute = Plugin.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, object?>("vnavmesh.Pathfind");
        navPathfindRoute = Plugin.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>("vnavmesh.Nav.Pathfind");
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

    public bool? HasRoute(Vector3 fromPosition, Vector3 toPosition, bool fly = false)
    {
        var legacyTask = TryInvokePathfind("vnavmesh.Pathfind", () => pathfindRoute.InvokeFunc(fromPosition, toPosition, fly));
        if (TryResolvePathResult(legacyTask, out var legacyHasRoute))
        {
            return legacyHasRoute;
        }

        var navTask = TryInvokeNavPathfind(fromPosition, toPosition, fly);
        if (navTask == null)
        {
            return null;
        }

        return TryResolvePathResult(navTask, out var hasRoute)
            ? hasRoute
            : null;
    }

    private Task<List<Vector3>>? TryInvokeNavPathfind(Vector3 fromPosition, Vector3 toPosition, bool fly)
    {
        try
        {
            return navPathfindRoute.InvokeFunc(fromPosition, toPosition, fly);
        }
        catch (Exception ex)
        {
            logger.Debug($"IPC call failed for vnavmesh.Nav.Pathfind: {ex.Message}");
            return null;
        }
    }

    public void Stop()
    {
        try
        {
            stopPath.InvokeAction();
        }
        catch (Exception ex)
        {
            logger.Warning($"[VNavmeshIpc] op=stop-failed reason={ex.Message}");
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

            logger.Debug($"IPC call failed for {operation}: {ex.Message}");
            return fallback;
        }
    }

    private object? TryInvokePathfind(string operation, Func<object?> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            logger.Debug($"IPC call failed for {operation}: {ex.Message}");
            return null;
        }
    }

    private static bool TryResolvePathResult(object task, out bool hasRoute)
    {
        hasRoute = false;

        try
        {
            var taskType = task.GetType();
            var isCompleted = taskType.GetProperty("IsCompleted", BindingFlags.Public | BindingFlags.Instance);
            var resultProperty = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            if (isCompleted == null || resultProperty == null)
            {
                return false;
            }

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (isCompleted.GetValue(task) is true)
                {
                    break;
                }

                System.Threading.Thread.Sleep(10);
            }

            if (isCompleted.GetValue(task) is not true)
            {
                return false;
            }

            var result = resultProperty.GetValue(task);
            hasRoute = CountEnumerableEntries(result) > 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolvePathResult(Task<List<Vector3>> task, out bool hasRoute)
    {
        hasRoute = false;

        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(2)))
            {
                return false;
            }

            hasRoute = task.Status == TaskStatus.RanToCompletion && task.Result.Count > 0;
            return task.Status == TaskStatus.RanToCompletion;
        }
        catch
        {
            return false;
        }
    }

    private static int CountEnumerableEntries(object? result)
    {
        if (result == null)
        {
            return 0;
        }

        var countProperty = result.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        if (countProperty?.GetValue(result) is int count)
        {
            return count;
        }

        if (result is IEnumerable enumerable)
        {
            var total = 0;
            foreach (var _ in enumerable)
            {
                total++;
            }

            return total;
        }

        return 0;
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
