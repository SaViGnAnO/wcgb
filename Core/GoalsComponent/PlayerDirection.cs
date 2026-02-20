using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Extensions;

using System;
using System.Numerics;
using System.Threading;

using static System.MathF;

#pragma warning disable 162

namespace Core;

public sealed partial class PlayerDirection
{
    private const bool debug = false;
    private const float HalfTurnStabilityBand = 0.20f;

    public const int DefaultIgnoreDistance = 10;

    private readonly ILogger<PlayerDirection> logger;
    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly CancellationToken token;
    private bool lastTurnLeft = true;

    public PlayerDirection(ILogger<PlayerDirection> logger,
        CancellationTokenSource<GoapAgent> cts,
        ConfigurableInput input, PlayerReader playerReader)
    {
        this.logger = logger;
        this.token = cts.Token;
        this.input = input;
        this.playerReader = playerReader;
    }

    public void SetDirection(float targetDir, Vector3 map)
    {
        SetDirection(targetDir, map, DefaultIgnoreDistance, token);
    }

    public void SetDirection(float targetDir, Vector3 world, float ignoreDistance, CancellationToken token)
    {
        float distance = playerReader.WorldPos.WorldDistanceXYTo(world);
        if (distance < ignoreDistance)
        {
            if (debug)
                LogDebugClose(logger, distance, ignoreDistance);

            return;
        }

        if (debug)
            LogDebugSetDirection(logger, playerReader.Direction, targetDir, distance);

        SetDirection(targetDir, token);
    }

    public void SetDirection(float targetDir, CancellationToken token = default)
    {
        if (input.UseMouseLookTurn && !input.KeyboardOnly)
        {
            SetDirectionWithMouseLook(targetDir, token);
            return;
        }

        input.PressFixed(GetDirectionKeyToPress(targetDir),
            TurnDuration(targetDir), token);
    }

    private float TurnAmount(float targetDir)
    {
        float result = (Tau + targetDir - playerReader.Direction) % Tau;
        return result > PI
            ? Tau - result
            : result;
    }

    private bool ShouldTurnLeft(float desiredDirection)
    {
        float delta = (Tau + desiredDirection - playerReader.Direction) % Tau;

        // When heading error is close to 180° (PI), tiny frame-to-frame jitter in
        // player.Direction can flip the computed shortest turn side each update.
        // That causes left/right key oscillation and the bot fails to commit to a turn.
        // In this band we keep the previous turn side to make turning deterministic.
        if (Abs(delta - PI) <= HalfTurnStabilityBand)
        {
            return lastTurnLeft;
        }

        bool turnLeft = delta < PI;
        lastTurnLeft = turnLeft;
        return turnLeft;
    }

    private int TurnDuration(float targetDir)
    {
        return (int)(TurnAmount(targetDir) * 1000f / PI);
    }

    private ConsoleKey GetDirectionKeyToPress(float desiredDirection)
    {
        return ShouldTurnLeft(desiredDirection)
            ? input.TurnLeftKey
            : input.TurnRightKey;
    }

    private void SetDirectionWithMouseLook(float targetDir, CancellationToken token)
    {
        float turnAmount = TurnAmount(targetDir);
        if (turnAmount < 0.005f)
            return;

        bool turnLeft = ShouldTurnLeft(targetDir);
        int pixelsToMove = Math.Max(1, (int)MathF.Round(turnAmount * input.MouseTurnPixelsPerRadian));
        int sign = turnLeft ? -1 : 1;

        int stepPixels = Math.Max(1, input.MouseTurnStepPixels);
        int stepDelayMs = Math.Max(0, input.MouseTurnStepDelayMs);

        input.BeginMouseLook();

        try
        {
            int remaining = pixelsToMove;
            while (remaining > 0 && !token.IsCancellationRequested)
            {
                int step = Math.Min(remaining, stepPixels);
                input.MoveMouseLook(sign * step, 0);
                remaining -= step;

                if (remaining > 0 && stepDelayMs > 0)
                {
                    Thread.Sleep(stepDelayMs);
                }
            }
        }
        finally
        {
            input.EndMouseLook();
        }
    }

    #region Logging

    [LoggerMessage(
        EventId = 0030,
        Level = LogLevel.Debug,
        Message = "SetDirection: Too close, ignored direction change. {distance} < {ignoreDistance}")]
    static partial void LogDebugClose(ILogger logger, float distance, float ignoreDistance);

    [LoggerMessage(
        EventId = 0031,
        Level = LogLevel.Debug,
        Message = "SetDirection: {direction:0.000} -> {desiredDirection:0.000} - {distance:0.000}")]
    static partial void LogDebugSetDirection(ILogger logger, float direction, float desiredDirection, float distance);

    #endregion
}