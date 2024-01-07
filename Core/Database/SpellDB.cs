using System.Collections.Frozen;
using System.Collections.Generic;
using System;

using SharedLib;

using static System.IO.File;
using static System.IO.Path;
using static Newtonsoft.Json.JsonConvert;

namespace Core.Database;

public sealed class SpellDB
{
    public FrozenDictionary<int, Spell> Spells { get; }

    public SpellDB(DataConfig dataConfig)
    {
        ReadOnlySpan<Spell> span = DeserializeObject<Spell[]>(
            ReadAllText(Join(dataConfig.ExpDbc, "spells.json")))!;

        Dictionary<int, Spell> spells = [];

        for (int i = 0; i < span.Length; i++)
        {
            spells.Add(span[i].Id, span[i]);
        }

        this.Spells = spells.ToFrozenDictionary();
    }
}
