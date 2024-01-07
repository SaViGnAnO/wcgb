using Core.Talents;

using System;

using SharedLib;

using static System.IO.File;
using static System.IO.Path;
using static Newtonsoft.Json.JsonConvert;

namespace Core.Database;

public sealed class TalentDB
{
    private readonly SpellDB spellDB;

    private readonly TalentTab[] talentTabs;
    private readonly TalentTreeElement[] talentTreeElements;

    public TalentDB(DataConfig dataConfig, SpellDB spellDB)
    {
        this.spellDB = spellDB;

        talentTabs = DeserializeObject<TalentTab[]>(
            ReadAllText(Join(dataConfig.ExpDbc, "talenttab.json")))!;

        talentTreeElements = DeserializeObject<TalentTreeElement[]>(
            ReadAllText(Join(dataConfig.ExpDbc, "talent.json")))!;
    }

    public bool Update(ref Talent talent, UnitClass @class, out int spellId)
    {
        int classMask = (int)Math.Pow(2, (int)@class - 1);

        int tabId = -1;
        int tabIndex = talent.TabNum - 1;
        for (int i = 0; i < talentTabs.Length; i++)
        {
            if (talentTabs[i].ClassMask == classMask &&
                talentTabs[i].OrderIndex == tabIndex)
            {
                tabId = talentTabs[i].Id;
                break;
            }
        }
        spellId = 1;
        if (tabId == -1) return false;

        int tierIndex = talent.TierNum - 1;
        int columnIndex = talent.ColumnNum - 1;
        int rankIndex = talent.CurrentRank - 1;

        int index = -1;
        for (int i = 0; i < talentTreeElements.Length; i++)
        {
            if (talentTreeElements[i].TabID == tabId &&
                talentTreeElements[i].TierID == tierIndex &&
                talentTreeElements[i].ColumnIndex == columnIndex)
            {
                index = i;
                break;
            }
        }

        spellId = talentTreeElements[index].SpellIds[rankIndex];
        if (spellDB.Spells.TryGetValue(spellId, out Spell spell))
        {
            talent.Name = spell.Name;
            return true;
        }

        return false;
    }
}
