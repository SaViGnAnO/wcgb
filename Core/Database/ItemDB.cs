using System.Collections.Frozen;
using System.Collections.Generic;
using System;

using SharedLib;

using static System.IO.File;
using static System.IO.Path;
using static Newtonsoft.Json.JsonConvert;

namespace Core.Database;

public sealed class ItemDB
{
    private static readonly Item _emptyItem = new() { Entry = 0, Name = string.Empty, Quality = 0, SellPrice = 0 };
    public static ref readonly Item EmptyItem => ref _emptyItem;

    public static readonly Item Backpack = new() { Entry = -1, Name = "Backpack", Quality = 1, SellPrice = 0 };

    public FrozenDictionary<int, Item> Items { get; }
    public int[] FoodIds { get; }
    public int[] DrinkIds { get; }

    public ItemDB(DataConfig dataConfig)
    {
        ReadOnlySpan<Item> span = DeserializeObject<Item[]>(
            ReadAllText(Join(dataConfig.ExpDbc, "items.json")))!;

        Dictionary<int, Item> items = [];
        for (int i = 0; i < span.Length; i++)
        {
            items.Add(span[i].Entry, span[i]);
        }
        items.Add(Backpack.Entry, Backpack);

        this.Items = items.ToFrozenDictionary();

        FoodIds = DeserializeObject<int[]>(
            ReadAllText(Join(dataConfig.ExpDbc, "foods.json")))!;

        DrinkIds = DeserializeObject<int[]>(
            ReadAllText(Join(dataConfig.ExpDbc, "waters.json")))!;
    }
}
