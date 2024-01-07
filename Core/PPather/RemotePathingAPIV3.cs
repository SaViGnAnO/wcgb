using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PPather.Data;
using System.Threading;
using AnTCP.Client;
using SharedLib;
using System.Numerics;

#pragma warning disable 162

namespace Core;

public sealed class RemotePathingAPIV3 : IPPather, IDisposable
{
    private const bool debug = false;
    private const int watchdogPollMs = 500;

    public enum EMessageType
    {
        PATH,
        MOVE_ALONG_SURFACE,
        RANDOM_POINT,
        RANDOM_POINT_AROUND,
        CAST_RAY,
        RANDOM_PATH
    }

    public enum PathRequestFlags
    {
        NONE = 0,
        CHAIKIN = 1,
        CATMULLROM = 2,
        FIND_LOCATION = 4
    };

    private readonly ILogger<RemotePathingAPIV3> logger;
    private readonly WorldMapAreaDB areaDB;

    private readonly AnTcpClient client;
    private readonly Thread connectionWatchdog;
    private readonly CancellationTokenSource cts;

    public RemotePathingAPIV3(ILogger<RemotePathingAPIV3> logger,
        string ip, int port, WorldMapAreaDB areaDB)
    {
        this.logger = logger;
        this.areaDB = areaDB;

        cts = new();

        client = new AnTcpClient(ip, port);
        connectionWatchdog = new Thread(ObserveConnection);
        connectionWatchdog.Start();
    }

    public void Dispose()
    {
        RequestDisconnect();
    }

    #region old

    public ValueTask DrawLines(List<LineArgs> lineArgs)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DrawSphere(SphereArgs args)
    {
        return ValueTask.CompletedTask;
    }

    public Vector3[] FindMapRoute(int uiMap, Vector3 mapFrom, Vector3 mapTo)
    {
        if (!client.IsConnected ||
            !areaDB.TryGet(uiMap, out WorldMapArea area))
            return Array.Empty<Vector3>();

        try
        {
            Vector3 worldFrom = areaDB.ToWorld_FlipXY(uiMap, mapFrom);
            Vector3 worldTo = areaDB.ToWorld_FlipXY(uiMap, mapTo);

            // incase haven't asked a pathfinder for a route this value will be 0
            // that case use the highest location
            if (worldFrom.Z == 0)
            {
                worldFrom.Z = area.LocTop / 2;
                worldTo.Z = area.LocTop / 2;
            }

            if (debug)
                logger.LogDebug($"Finding map route from {mapFrom}({worldFrom}) map {uiMap} to {mapTo}({worldTo}) map {uiMap}...");

            Vector3[] path = client.Send((byte)EMessageType.PATH,
                (area.MapID, PathRequestFlags.FIND_LOCATION | PathRequestFlags.CATMULLROM,
                worldFrom.X, worldFrom.Y, worldFrom.Z, worldTo.X, worldTo.Y, worldTo.Z)).AsArray<Vector3>();

            if (path.Length == 1 && path[0] == Vector3.Zero)
                return Array.Empty<Vector3>();

            for (int i = 0; i < path.Length; i++)
            {
                if (debug)
                    logger.LogDebug($"new float[] {{ {path[i].X}f, {path[i].Y}f, {path[i].Z}f }},");

                path[i] = areaDB.ToMap_FlipXY(path[i], area.MapID, uiMap);
            }

            return path;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Finding map route from {mapFrom} to {mapTo}");
            return Array.Empty<Vector3>();
        }
    }

    public Vector3[] FindWorldRoute(int uiMap, Vector3 worldFrom, Vector3 worldTo)
    {
        if (!client.IsConnected)
            return Array.Empty<Vector3>();

        if (!areaDB.TryGet(uiMap, out WorldMapArea area))
            return Array.Empty<Vector3>();

        try
        {
            // incase haven't asked a pathfinder for a route this value will be 0
            // that case use the highest location
            if (worldFrom.Z == 0)
            {
                worldFrom.Z = area.LocTop / 2;
                worldTo.Z = area.LocTop / 2;
            }

            if (debug)
                logger.LogDebug($"Finding world route from {worldFrom}({worldFrom}) map {uiMap} to {worldTo}({worldTo}) map {uiMap}...");

            Vector3[] path = client.Send((byte)EMessageType.PATH,
                (area.MapID, PathRequestFlags.FIND_LOCATION | PathRequestFlags.CATMULLROM,
                worldFrom.X, worldFrom.Y, worldFrom.Z, worldTo.X, worldTo.Y, worldTo.Z)).AsArray<Vector3>();

            if (path.Length == 1 && path[0] == Vector3.Zero)
                return Array.Empty<Vector3>();

            return path;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Finding world route from {worldFrom} to {worldTo}");
            return Array.Empty<Vector3>();
        }
    }


    public bool PingServer()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(watchdogPollMs);

        while (!cts.IsCancellationRequested)
        {
            if (client.IsConnected)
            {
                break;
            }
            cts.Token.WaitHandle.WaitOne(watchdogPollMs / 10);
        }

        return client.IsConnected;
    }

    private void RequestDisconnect()
    {
        cts.Cancel();
        if (client.IsConnected)
        {
            client.Disconnect();
        }
    }

    #endregion old

    private void ObserveConnection()
    {
        while (!cts.IsCancellationRequested)
        {
            if (!client.IsConnected)
            {
                try
                {
                    client.Connect();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                    // ignored, will happen when we cant connect
                }
            }

            cts.Token.WaitHandle.WaitOne(watchdogPollMs);
        }
    }
}