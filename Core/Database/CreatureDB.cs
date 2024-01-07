using System.Collections.Frozen;
using System.Collections.Generic;
using System;

using SharedLib;

using static System.IO.File;
using static System.IO.Path;
using static Newtonsoft.Json.JsonConvert;

namespace Core.Database;

public sealed class CreatureDB
{
    public FrozenDictionary<int, string> Entries { get; }

    public CreatureDB(DataConfig dataConfig)
    {
        ReadOnlySpan<Creature> creatures = DeserializeObject<Creature[]>(
            ReadAllText(Join(dataConfig.ExpDbc, "creatures.json")))!;

        Dictionary<int, string> entries = [];
        for (int i = 0; i < creatures.Length; i++)
        {
            entries.Add(creatures[i].Entry, creatures[i].Name);
        }

        Entries = entries.ToFrozenDictionary();
    }
}
