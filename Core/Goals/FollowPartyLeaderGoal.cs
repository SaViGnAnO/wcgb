using Core.GOAP;
using Core.GoalsComponent;

using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Extensions;
using SharedLib.Data;

using System;
using System.Numerics;

namespace Core.Goals;

public sealed class FollowPartyLeaderGoal : GoapGoal
{
    private const int LosFailureThreshold = 5;
    private const float DefaultCost = 20f;
    private const double StatusLogIntervalSeconds = 5d;

    public override float Cost => DefaultCost;

    private readonly ILogger<FollowPartyLeaderGoal> logger;
    private readonly Navigation navigation;
    private readonly PlayerReader playerReader;
    private readonly ClassConfiguration classConfiguration;
    private PartyOptions Party => classConfiguration.Party;
    private readonly IPartyLeaderProvider leaderProvider;
    private readonly Wait wait;
    private readonly AddonBits bits;

    private DateTime lastPathRequest;
    private DateTime lastStatusLog;
    private Vector3 lastKnownLeaderWorld;
    private int losFailureCount;

    public FollowPartyLeaderGoal(ILogger<FollowPartyLeaderGoal> logger,
        Navigation navigation,
        PlayerReader playerReader,
        ClassConfiguration classConfiguration,
        IPartyLeaderProvider leaderProvider,
        Wait wait,
        AddonBits bits)
        : base(nameof(FollowPartyLeaderGoal))
    {
        this.logger = logger;
        this.navigation = navigation;
        this.playerReader = playerReader;
        this.classConfiguration = classConfiguration;
        this.leaderProvider = leaderProvider;
        this.wait = wait;
        this.bits = bits;

        AddPrecondition(GoapKey.dangercombat, false);
        AddPrecondition(GoapKey.damagedone, false);
        AddPrecondition(GoapKey.damagetaken, false);
        AddPrecondition(GoapKey.producedcorpse, false);
        AddPrecondition(GoapKey.consumecorpse, false);
    }

    public override void OnEnter()
    {
        lastPathRequest = DateTime.MinValue;
        lastStatusLog = DateTime.MinValue;
        losFailureCount = 0;
        navigation.OnNoPathFound += HandleNoPath;
        navigation.OnPathCalculated += ResetFailures;
        navigation.OnAnyPointReached += ResetFailures;
    }

    public override void OnExit()
    {
        navigation.OnNoPathFound -= HandleNoPath;
        navigation.OnPathCalculated -= ResetFailures;
        navigation.OnAnyPointReached -= ResetFailures;
        navigation.Stop();
    }

    public override void Update()
    {
        PartyLeaderSnapshot snapshot = leaderProvider.GetSnapshot(classConfiguration);
        if (!snapshot.HasPosition)
        {
            LogStatus("PartyFollow idle: no leader snapshot/position (Mode={Mode})", Party.Mode);
            navigation.StopMovement();
            wait.Update();
            return;
        }

        if (!snapshot.TryGetWaypoint(Party.CoordinateSource, playerReader.WorldMapArea, out Vector3 waypoint, out Vector3 leaderWorld))
        {
            LogStatus("PartyFollow idle: waypoint unavailable (Source={CoordinateSource}, MapId={MapId})", Party.CoordinateSource, playerReader.UIMapId.Value);
            navigation.StopMovement();
            wait.Update();
            return;
        }

        lastKnownLeaderWorld = leaderWorld;

        bool leaderInCombat = snapshot.LeaderInCombat;
        float distanceToLeader = playerReader.WorldPos.WorldDistanceXYTo(leaderWorld);

        if (leaderInCombat)
        {
            LogStatus("PartyFollow combat leash: dist={Distance:F1}, leash={Leash:F1}", distanceToLeader, Party.CombatLeash);
            EnforceCombatLeash(distanceToLeader, waypoint);
            wait.Update();
            navigation.Update();
            return;
        }

        if (bits.Combat())
        {
            LogStatus("PartyFollow idle: player in combat, waiting for normal combat goals");
            navigation.StopMovement();
            wait.Update();
            return;
        }

        if (distanceToLeader > Party.FollowRadius && ShouldRepath())
        {
            LogStatus("PartyFollow repath: dist={Distance:F1}, radius={Radius:F1}, waypoint={Waypoint}", distanceToLeader, Party.FollowRadius, waypoint.ToStringF());
            RequestPath(waypoint);
        }
        else if (distanceToLeader <= Party.FollowRadius)
        {
            LogStatus("PartyFollow hold: within radius (dist={Distance:F1}, radius={Radius:F1})", distanceToLeader, Party.FollowRadius);
            navigation.StopMovement();
        }
        else
        {
            LogStatus("PartyFollow wait: repath cooldown active (dist={Distance:F1}, interval={Interval:F1}s)", distanceToLeader, Party.RepathIntervalSeconds);
        }

        wait.Update();
        navigation.Update();
    }

    private void EnforceCombatLeash(float distanceToLeader, Vector3 waypoint)
    {
        if (distanceToLeader > Party.CombatLeash)
        {
            RequestPath(waypoint);
        }
        else
        {
            navigation.StopMovement();
        }
    }

    private bool ShouldRepath()
    {
        if (!navigation.HasWaypoint())
            return true;

        double elapsedSeconds = (DateTime.UtcNow - lastPathRequest).TotalSeconds;
        return elapsedSeconds >= Party.RepathIntervalSeconds;
    }

    private void RequestPath(Vector3 waypoint)
    {
        lastPathRequest = DateTime.UtcNow;

        Span<Vector3> points = stackalloc Vector3[1] { waypoint };
        navigation.SetWayPoints(points);
        navigation.ResetStuckParameters();
    }

    private void HandleNoPath()
    {
        if (Party.Enforcements.HasFlag(FollowRadiusEnforcements.LineOfSight))
        {
            losFailureCount++;
            if (losFailureCount >= LosFailureThreshold)
            {
                Regroup();
            }
        }

        if (Party.Enforcements.HasFlag(FollowRadiusEnforcements.FallbackToRegroup))
        {
            Regroup();
        }
    }

    private void ResetFailures()
    {
        losFailureCount = 0;
    }

    private void Regroup()
    {
        if (lastKnownLeaderWorld == Vector3.Zero)
        {
            return;
        }

        Vector3 waypoint = Party.CoordinateSource == CoordinateSource.World
            ? lastKnownLeaderWorld
            : WorldMapAreaDB.ToMap_FlipXY(lastKnownLeaderWorld, playerReader.WorldMapArea);

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Regrouping toward leader at {Location}", waypoint.ToStringF());

        RequestPath(waypoint);
        ResetFailures();
    }

    private void LogStatus(string message, params object?[] args)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if ((now - lastStatusLog).TotalSeconds < StatusLogIntervalSeconds)
        {
            return;
        }

        lastStatusLog = now;
        logger.LogDebug(message, args);
    }
}
