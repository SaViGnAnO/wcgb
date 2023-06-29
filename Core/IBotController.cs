﻿using Core.GOAP;
using System;
using System.Collections.Generic;

namespace Core;

public interface IBotController
{
    bool IsBotActive { get; }
    string SelectedClassFilename { get; }
    string? SelectedPathFilename { get; }
    ClassConfiguration? ClassConfig { get; }
    GoapAgent? GoapAgent { get; }
    RouteInfo? RouteInfo { get; }
    double AvgScreenLatency { get; }
    double AvgNPCLatency { get; }

    event Action? ProfileLoaded;
    event Action? StatusChanged;

    void ToggleBotStatus();

    void MinimapNodeFound();

    void Shutdown();

    IEnumerable<string> ClassFiles();

    IEnumerable<string> PathFiles();

    void LoadClassProfile(string classFilename);

    void LoadPathProfile(string pathFilename);

    void OverrideClassConfig(ClassConfiguration classConfig);
}