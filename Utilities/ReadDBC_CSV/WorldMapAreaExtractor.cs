using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using SharedLib;
using nietras.SeparatedValues;

namespace ReadDBC_CSV;

internal sealed class WorldMapAreaExtractor : IExtractor
{
    private readonly string path;

    public string[] FileRequirement { get; } =
    [
        "uimap.csv",
        "uimapassignment.csv",
        "map.csv"
    ];

    public WorldMapAreaExtractor(string path)
    {
        this.path = path;
    }

    public void Run()
    {
        // UIMapId - AreaName
        string uimapFile = Path.Join(path, FileRequirement[0]);
        List<WorldMapArea> wmas = ExtractUIMap(uimapFile);

        // MapID - AreaID - LocBottom - LocRight - LocTop - LocLeft
        string uimapassignmentFile = Path.Join(path, FileRequirement[1]);
        ExtractBoundaries(uimapassignmentFile, wmas);

        // Continent / Directory
        string mapFile = Path.Join(path, FileRequirement[2]);
        ExtractContinent(mapFile, wmas);

        ClearEmptyBound(wmas);

        Console.WriteLine($"WMAs: {wmas.Count}");
        File.WriteAllText(Path.Join(path, "WorldMapArea.json"), JsonConvert.SerializeObject(wmas, Formatting.Indented));
    }

    private static List<WorldMapArea> ExtractUIMap(string path)
    {
        using var reader = Sep.Reader(o => o with
        {
            Unescape = true,
        }).FromFile(path);

        int idIndex = reader.Header.IndexOf("ID");
        int nameIndex = reader.Header.IndexOf("Name_lang");
        int systemIndex = reader.Header.IndexOf("System");

        List<WorldMapArea> items = new();
        foreach (SepReader.Row row in reader)
        {
            int uiMapId = row[idIndex].Parse<int>();
            int system = row[systemIndex].Parse<int>();

            // 1 ([DEPRECATED] Legacy Taxi)
            if (system == 1)
            {
                continue;
            }

            items.Add(new WorldMapArea
            {
                UIMapId = uiMapId,
                AreaName = row[nameIndex].ToString(),
            });
        }

        return items;
    }

    private static void ExtractBoundaries(string path, List<WorldMapArea> wmas)
    {
        using var reader = Sep.Reader(o => o with
        {
            Unescape = true,
        }).FromFile(path);

        int uiMapId = reader.Header.IndexOf("UiMapID");
        int mapId = reader.Header.IndexOf("MapID");
        int areaId = reader.Header.IndexOf("AreaID");

        int orderIndex = reader.Header.IndexOf("OrderIndex");

        int region0 = reader.Header.IndexOf("Region_0", "Region[0]");
        int region1 = reader.Header.IndexOf("Region_1", "Region[1]");

        int region3 = reader.Header.IndexOf("Region_3", "Region[3]");
        int region4 = reader.Header.IndexOf("Region_4", "Region[4]");

        foreach (SepReader.Row row in reader)
        {
            int _uiMapId = row[uiMapId].Parse<int>();
            int _orderIndex = row[orderIndex].Parse<int>();

            int index = wmas.FindIndex(x => x.UIMapId == _uiMapId && _orderIndex == 0);
            if (index > -1)
            {
                WorldMapArea wma = wmas[index];
                wmas[index] = wma with
                {
                    MapID = row[mapId].Parse<int>(),
                    AreaID = row[areaId].Parse<int>(),

                    LocBottom = row[region0].Parse<float>(),
                    LocRight = row[region1].Parse<float>(),

                    LocTop = row[region3].Parse<float>(),
                    LocLeft = row[region4].Parse<float>(),
                };
            }
        }
    }

    private static void ExtractContinent(string path, List<WorldMapArea> wmas)
    {
        using var reader = Sep.Reader(o => o with
        {
            Unescape = true,
        }).FromFile(path);

        int mapId = reader.Header.IndexOf("ID");
        int directory = reader.Header.IndexOf("Directory", 1);

        foreach (SepReader.Row row in reader)
        {
            int _mapId = row[mapId].Parse<int>();
            string _directory = row[directory].ToString();

            for (int i = 0; i < wmas.Count; i++)
            {
                if (wmas[i].MapID != _mapId)
                    continue;

                WorldMapArea wma = wmas[i];
                wmas[i] = wma with
                {
                    Continent = _directory
                };
            }
        }
    }

    private static void ClearEmptyBound(List<WorldMapArea> wmas)
    {
        for (int i = wmas.Count - 1; i >= 0; i--)
        {
            if (wmas[i].LocBottom == 0 &&
                wmas[i].LocLeft == 0 &&
                wmas[i].LocRight == 0 &&
                wmas[i].LocTop == 0)
            {
                wmas.RemoveAt(i);
            }
        }
    }
}
