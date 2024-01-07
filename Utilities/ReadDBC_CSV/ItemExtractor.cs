using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using SharedLib;
using nietras.SeparatedValues;

namespace ReadDBC_CSV;

internal sealed class ItemExtractor : IExtractor
{
    private readonly string path;

    public string[] FileRequirement { get; } =
    [
        "itemsparse.csv"
    ];

    public ItemExtractor(string path)
    {
        this.path = path;
    }

    public void Run()
    {
        string fileName = Path.Join(path, FileRequirement[0]);
        List<Item> items = ExtractItems(fileName);

        Console.WriteLine($"Items: {items.Count}");
        File.WriteAllText(Path.Join(path, "items.json"), JsonConvert.SerializeObject(items));
    }

    private static List<Item> ExtractItems(string path)
    {
        using var reader = Sep.Reader(o => o with
        {
            Unescape = true,
        }).FromFile(path);

        int id = reader.Header.IndexOf("ID");
        int name = reader.Header.IndexOf("Display_lang");
        int quality = reader.Header.IndexOf("OverallQualityID");
        int sellPrice = reader.Header.IndexOf("SellPrice");

        List<Item> items = [];
        foreach (SepReader.Row row in reader)
        {
            items.Add(new Item
            {
                Entry = row[id].Parse<int>(),
                Quality = row[quality].Parse<int>(),
                Name = row[name].ToString(),
                SellPrice = row[sellPrice].Parse<int>()
            });
        }
        return items;
    }
}
