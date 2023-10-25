using System;
using System.Drawing;
using System.Threading;
using SharedLib.Extensions;
using SharedLib.NpcFinder;
using Game;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace Core.Goals;

public sealed partial class NpcNameTargeting : IDisposable
{
    private const int INTERACT_DELAY = 5;

    private readonly ILogger<NpcNameTargeting> logger;
    private readonly CancellationToken ct;
    private readonly IWowScreen wowScreen;
    private readonly NpcNameFinder npcNameFinder;
    private readonly NpcNameTargetingLocations locations;
    private readonly IMouseInput input;
    private readonly IMouseOverReader mouseOverReader;
    private readonly Wait wait;

    private readonly CursorClassifier classifier;

    private readonly IBlacklist mouseOverBlacklist;

    private readonly IGameMenuWindowShown gmws;

    private int index;
    private int npcCount = -1;

    public int NpcCount => npcNameFinder.NpcCount;

    private Point[] Targeting => locations.Targeting;
    private Point[] locFindBy => locations.FindBy;

    public NpcNameTargeting(ILogger<NpcNameTargeting> logger,
        CancellationTokenSource cts, IWowScreen wowScreen,
        NpcNameFinder npcNameFinder,
        NpcNameTargetingLocations locations,
        IMouseInput input,
        IMouseOverReader mouseOverReader, IBlacklist blacklist, Wait wait,
        IGameMenuWindowShown gmws)
    {
        this.logger = logger;
        ct = cts.Token;
        this.wowScreen = wowScreen;
        this.npcNameFinder = npcNameFinder;
        this.locations = locations;
        this.input = input;
        this.mouseOverReader = mouseOverReader;
        this.mouseOverBlacklist = blacklist;
        this.wait = wait;

        this.gmws = gmws;

        classifier = new();
    }

    public void Dispose()
    {
        classifier.Dispose();
    }

    public void ChangeNpcType(NpcNames npcNames)
    {
        npcNameFinder.ChangeNpcType(npcNames);
        wowScreen.Enabled = npcNames != NpcNames.None;
    }

    public void Reset()
    {
        npcCount = -1;
        index = 0;
    }

    public void WaitForUpdate()
    {
        npcNameFinder.WaitForUpdate();
    }

    public bool FoundAny()
    {
        return npcNameFinder.NpcCount > 0;
    }

    public bool AcquireNonBlacklisted(CancellationToken token)
    {
        if (npcCount != NpcCount)
        {
            npcCount = NpcCount;
            index = 0;
        }

        if (index > NpcCount - 1)
            return false;

        ReadOnlySpan<NpcPosition> span = npcNameFinder.Npcs;
        ref readonly NpcPosition npc = ref span[index];

        Point p = Targeting[Random.Shared.Next(Targeting.Length)];
        p.Offset(npc.ClickPoint);
        p.Offset(npcNameFinder.ToScreenCoordinates());

        input.SetCursorPos(p);
        classifier.Classify(out CursorType cls, out _);

        if (cls is CursorType.Kill && mouseOverReader.MouseOverId != 0)
        {
            if (mouseOverBlacklist.Is())
            {
                LogBlacklistAdded(logger, index,
                    mouseOverReader.MouseOverId, npc.Rect);
                index++;
                return false;
            }

            LogFoundTarget(logger, cls.ToStringF(), mouseOverReader.MouseOverId,
                npc.Rect);

            input.InteractMouseOver(token);
            return true;
        }

        return false;
    }

    public bool FindBy(ReadOnlySpan<CursorType> cursors, CancellationToken token)
    {
        int c = locFindBy.Length;
        const int e = 3;
        Span<Point> attempts = stackalloc Point[c + (c * e)];

        float w = npcNameFinder.ScaleToRefWidth;
        float h = npcNameFinder.ScaleToRefHeight;

        ReadOnlySpan<NpcPosition> span = npcNameFinder.Npcs;
        for (int i = 0;
            i < span.Length &&
            !token.IsCancellationRequested &&
            !gmws.GameMenuWindowShown();
            i++)
        {
            ref readonly NpcPosition npc = ref span[i];
            for (int j = 0; j < c; j += e)
            {
                Point p = locFindBy[j];
                attempts[j] = p;
                attempts[j + c] = new Point(npc.Rect.Width / 2, p.Y).Scale(w, h);
                attempts[j + c + 1] = new Point(-npc.Rect.Width / 2, p.Y).Scale(w, h);
            }

            for (int k = 0;
                k < attempts.Length &&
                !token.IsCancellationRequested &&
                !gmws.GameMenuWindowShown();
                k++)
            {
                Point p = attempts[k];
                p.Offset(npc.ClickPoint);
                p.Offset(npcNameFinder.ToScreenCoordinates());

                input.SetCursorPos(p);

                classifier.Classify(out CursorType cls, out _);
                if (cursors.BinarySearch(cls, Comparer<CursorType>.Default) != -1)
                {
                    input.InteractMouseOver(token);
                    LogFoundTarget(logger, cls.ToStringF(), mouseOverReader.MouseOverId, npc.Rect);
                    return true;
                }

                wait.Update();
            }
        }
        return false;
    }

    #region Logging

    [LoggerMessage(
        EventId = 0160,
        Level = LogLevel.Warning,
        Message = "NPC {index} added to blacklist {mouseOverId} | {rect}")]
    static partial void LogBlacklistAdded(ILogger logger, int index, int mouseOverId, Rectangle rect);

    [LoggerMessage(
        EventId = 0161,
        Level = LogLevel.Information,
        Message = "NPC found: {cursorType} | {mouseOverId} | {rect}")]
    static partial void LogFoundTarget(ILogger logger, string cursorType, int mouseOverId, Rectangle rect);

    #endregion
}
