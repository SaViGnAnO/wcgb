using Microsoft.Extensions.Logging;

using SixLabors.ImageSharp;

using System;
using System.Collections;
using System.Threading;

using WinAPI;

#pragma warning disable 162

namespace Game;

public sealed partial class WowProcessInput : IMouseInput
{
    private const bool LogInput = false;
    private const bool LogMove = false;

    private readonly ILogger<WowProcessInput> logger;

    private readonly WowProcess process;
    private readonly InputWindowsNative nativeInput;
    private readonly InputSimulator simulatorInput;

    private readonly BitArray keysDown;

    public ConsoleKey ForwardKey { get; set; }
    public ConsoleKey BackwardKey { get; set; }
    public ConsoleKey TurnLeftKey { get; set; }
    public ConsoleKey TurnRightKey { get; set; }
    public ConsoleKey InteractMouseover { get; set; }
    public int InteractMouseoverPress { get; set; }

    public WowProcessInput(ILogger<WowProcessInput> logger, CancellationTokenSource cts, WowProcess process)
    {
        this.logger = logger;
        this.process = process;

        keysDown = new((int)ConsoleKey.OemClear);

        nativeInput = new(process, cts, InputDuration.FastPress);
        simulatorInput = new(process, cts, InputDuration.FastPress);
    }

    public void Reset()
    {
        lock (keysDown)
        {
            keysDown.SetAll(false);
        }
    }

    public void KeyDown(ConsoleKey key, bool forced)
    {
        if (IsKeyDown(key))
        {
            if (!forced)
                return;
        }

        if (LogInput)
        {
            if (key == ForwardKey || key == BackwardKey || key == TurnLeftKey || key == TurnRightKey)
            {
                if (LogMove)
                    LogKeyDown(logger, key);
            }
            else
            {
                LogKeyDown(logger, key);
            }
        }

        keysDown[(int)key] = true;
        nativeInput.KeyDown((int)key);
    }

    public void KeyUp(ConsoleKey key, bool forced)
    {
        if (!IsKeyDown(key))
        {
            if (!forced)
                return;
        }

        if (LogInput)
        {
            if (key == ForwardKey || key == BackwardKey || key == TurnLeftKey || key == TurnRightKey)
            {
                if (LogMove)
                    LogKeyUp(logger, key);
            }
            else
            {
                LogKeyUp(logger, key);
            }
        }

        nativeInput.KeyUp((int)key);
        keysDown[(int)key] = false;
    }

    public bool IsKeyDown(ConsoleKey key)
    {
        return keysDown[(int)key];
    }

    public void SendText(string payload)
    {
        simulatorInput.SendText(payload);
    }

    public void SetClipboard(string text)
    {
        simulatorInput.SetClipboard(text);
    }

    public void PasteFromClipboard()
    {
        simulatorInput.PasteFromClipboard();
    }

    public void SetForegroundWindow()
    {
        NativeMethods.SetForegroundWindow(process.MainWindowHandle);
    }

    public int PressRandom(ConsoleKey key, int milliseconds)
    {
        return PressRandom(key, milliseconds, CancellationToken.None);
    }

    public int PressRandom(ConsoleKey key, int milliseconds, CancellationToken token)
    {
        keysDown[(int)key] = true;
        int elapsedMs = nativeInput.PressRandom((int)key, milliseconds, token);
        keysDown[(int)key] = false;

        if (LogInput)
        {
            LogKeyPress(logger, key, elapsedMs);
        }

        return elapsedMs;
    }

    public void PressFixed(ConsoleKey key, int milliseconds, CancellationToken token)
    {
        if (milliseconds < 1)
            return;

        if (LogInput)
        {
            if (key == ForwardKey || key == BackwardKey || key == TurnLeftKey || key == TurnRightKey)
            {
                if (LogMove)
                    LogKeyPress(logger, key, milliseconds);
            }
            else
            {
                LogKeyPress(logger, key, milliseconds);
            }
        }

        keysDown[(int)key] = true;
        nativeInput.PressFixed((int)key, milliseconds, token);
        keysDown[(int)key] = false;
    }

    public void SetKeyState(ConsoleKey key, bool pressDown, bool forced)
    {
        if (pressDown)
            KeyDown(key, forced);
        else
            KeyUp(key, forced);
    }

    public void SetCursorPos(Point p)
    {
        nativeInput.SetCursorPos(p);
    }

    public void RightClick(Point p)
    {
        nativeInput.RightClick(p);
    }

    public void LeftClick(Point p)
    {
        nativeInput.LeftClick(p);
    }

    public void InteractMouseOver(CancellationToken token)
    {
        PressFixed(InteractMouseover, InteractMouseoverPress, token);
    }

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Debug,
        Message = @"[{key}] KeyDown")]
    static partial void LogKeyDown(ILogger logger, ConsoleKey key);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Debug,
        Message = @"[{key}] KeyUp")]
    static partial void LogKeyUp(ILogger logger, ConsoleKey key);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Debug,
        Message = @"[{key}] pressed {milliseconds}ms")]
    static partial void LogKeyPress(ILogger logger, ConsoleKey key, int milliseconds);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Debug,
        Message = @"[{key}] pressing {milliseconds}ms")]
    static partial void LogKeyPressNoDelay(ILogger logger, ConsoleKey key, int milliseconds);
}
