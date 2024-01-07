using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;

using static System.Math;

using Core.Database;
using Core.Goals;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.NpcFinder;

using static Core.Requirement;

namespace Core;

public sealed partial class RequirementFactory
{
    private readonly ILogger logger;
    private readonly AddonReader addonReader;
    private readonly PlayerReader playerReader;
    private readonly BuffStatus<IPlayer> buffs;
    private readonly BagReader bagReader;
    private readonly EquipmentReader equipmentReader;
    private readonly SpellBookReader spellBookReader;
    private readonly TalentReader talentReader;
    private readonly CreatureDB creatureDb;
    private readonly ItemDB itemDb;
    private readonly CombatLog combatLog;

    private readonly ClassConfiguration classConfig;

    private readonly ActionBarBits<ICurrentAction> currentAction;
    private readonly ActionBarBits<IUsableAction> usableAction;
    private readonly ActionBarCooldownReader cooldownReader;
    private readonly ActionBarCostReader costReader;

    private readonly FrozenDictionary<int, SchoolMask> npcSchoolImmunity;

    private readonly string[] negateKeywords =
    [
        "not ",
        SymbolNegate
    ];

    private readonly Dictionary<string, Func<int>> intVariables;

    private readonly FrozenDictionary<string, Func<bool>> boolVariables;

    private readonly FrozenDictionary<string, Func<string, Requirement>> requirementMap;

    private const char SEP1 = ':';
    private const char SEP2 = ',';

    private const string Swimming = "Swimming";
    private const string Falling = "Falling";
    private const string Flying = "Flying";

    public const string AddVisible = "AddVisible";
    public const string Drink = "Drink";
    public const string Food = "Food";

    public const string HealthP = "Health%";
    public const string ManaP = "Mana%";

    private const string greaterThenOrEqual = ">=";
    private const string lessThenOrEqual = "<=";
    private const string greaterThen = ">";
    private const string lessThen = "<";
    private const string equals = "==";
    private const string modulo = "%";

    public RequirementFactory(IServiceProvider sp, ClassConfiguration classConfig)
    {
        this.logger = sp.GetRequiredService<ILogger>();
        this.addonReader = sp.GetRequiredService<AddonReader>();
        this.playerReader = sp.GetRequiredService<PlayerReader>();
        this.buffs = sp.GetRequiredService<BuffStatus<IPlayer>>();
        this.bagReader = sp.GetRequiredService<BagReader>();
        this.equipmentReader = sp.GetRequiredService<EquipmentReader>();
        this.spellBookReader = sp.GetRequiredService<SpellBookReader>();
        this.talentReader = sp.GetRequiredService<TalentReader>();
        this.creatureDb = sp.GetRequiredService<CreatureDB>();
        this.itemDb = sp.GetRequiredService<ItemDB>();

        this.currentAction = sp.GetRequiredService<ActionBarBits<ICurrentAction>>();
        this.usableAction = sp.GetRequiredService<ActionBarBits<IUsableAction>>();
        this.cooldownReader = sp.GetRequiredService<ActionBarCooldownReader>();
        this.costReader = sp.GetRequiredService<ActionBarCostReader>();

        this.classConfig = classConfig;

        this.npcSchoolImmunity = classConfig.NpcSchoolImmunity.ToFrozenDictionary();

        NpcNameFinder npcNameFinder = sp.GetRequiredService<NpcNameFinder>();
        AddonBits bits = sp.GetRequiredService<AddonBits>();
        BuffStatus<IPlayer> playerBuffs = sp.GetRequiredService<BuffStatus<IPlayer>>();
        BuffStatus<IFocus> focusBuffs = sp.GetRequiredService<BuffStatus<IFocus>>();
        TargetDebuffStatus targetDebuffs = sp.GetRequiredService<TargetDebuffStatus>();
        SessionStat sessionStat = sp.GetRequiredService<SessionStat>();
        combatLog = sp.GetRequiredService<CombatLog>();

        var playerBuff = sp.GetRequiredService<AuraTimeReader<IPlayerBuffTimeReader>>();
        var targetDebuff = sp.GetRequiredService<AuraTimeReader<ITargetDebuffTimeReader>>();
        var targetBuff = sp.GetRequiredService<AuraTimeReader<ITargetBuffTimeReader>>();
        var focusBuff = sp.GetRequiredService<AuraTimeReader<IFocusBuffTimeReader>>();

        Dictionary<string, Func<string, Requirement>> requirementMap = new()
        {
            { greaterThenOrEqual, CreateGreaterOrEquals },
            { lessThenOrEqual, CreateLesserOrEquals },
            { greaterThen, CreateGreaterThen },
            { lessThen, CreateLesserThen },
            { equals, CreateEquals },
            { modulo, CreateModulo },
            { "npcID:", CreateNpcId },
            { "BagItem:", CreateBagItem },
            { "SpellInRange:", CreateSpellInRange },
            { "TargetCastingSpell", CreateTargetCastingSpell },
            { "Form", CreateForm },
            { "Race", CreateRace },
            { "Spell", CreateSpell },
            { "Talent", CreateTalent },
            { "Trigger:", CreateTrigger },
            { "Usable:", CreateUsable }
        };
        this.requirementMap = requirementMap.ToFrozenDictionary();

        Dictionary<string, Func<bool>> boolVariables = new(StringComparer.InvariantCultureIgnoreCase)
        {
            // Target Based
            { "TargetYieldXP", bits.Target_NotTrivial },
            { "TargetsMe", playerReader.TargetsMe },
            { "TargetsPet", playerReader.TargetsPet },
            { "TargetsNone", playerReader.TargetsNone },

            { AddVisible, npcNameFinder._PotentialAddsExist },
            { "InCombat", bits.Combat },

            // Range
            { "InMeleeRange", playerReader.IsInMeleeRange },
            { "InCloseMeleeRange", playerReader.InCloseMeleeRange },
            { "InDeadZoneRange", playerReader.IsInDeadZone },
            { "OutOfCombatRange", playerReader.OutOfCombatRange },
            { "InCombatRange", playerReader.WithInCombatRange },
            
            // Pet
            { "Has Pet", bits.Pet },
            { "Pet Happy", bits.Pet_Happy },
            { "Pet HasTarget", playerReader.PetTarget },
            { "Mounted", bits.Mounted },
            
            // Auto Spell
            { "AutoAttacking", bits.Auto_Attack },
            { "Shooting", bits.Shoot },
            { "AutoShot", bits.AutoShot },
            
            // Temporary Enchants
            { "HasMainHandEnchant", bits.MainHandTempEnchant },
            { "HasOffHandEnchant", bits.OffHandTempEnchant },
            
            // Equipment - Bag
            { "Items Broken", bits.Items_Broken },
            { "BagFull", bagReader.BagsFull },
            { "BagGreyItem", bagReader.AnyGreyItem },
            { "HasRangedWeapon", equipmentReader.RangedWeapon },
            { "HasAmmo", bits.Ammo },

            { "Casting", playerReader.IsCasting },
            { "HasTarget", bits.Target },
            { "TargetHostile", bits.Target_Hostile },
            { "TargetAlive", bits.Target_Alive },

            // Player Affected
            { Swimming, bits.Swimming },
            { Falling, bits.Falling },
            { Flying, bits.Flying },
            { "Dead", bits.Dead },
        };

        AddAura("", boolVariables, playerBuffs);
        AddAura("F_", boolVariables, focusBuffs);
        AddAura("", boolVariables, targetDebuffs);

        this.boolVariables = boolVariables.ToFrozenDictionary();

        intVariables = new Dictionary<string, Func<int>>
        {
            { HealthP, playerReader.HealthPercent },
            { "TargetHealth%", playerReader.TargetHealthPercent },
            { "FocusHealth%", playerReader.FocusHealthPercent },
            { "PetHealth%", playerReader.PetHealthPercent },
            { ManaP, playerReader.ManaPercent },
            { "Mana", playerReader.ManaCurrent },
            { "Energy", playerReader.PTCurrent },
            { "Rage", playerReader.PTCurrent },
            { "RunicPower", playerReader.PTCurrent },
            { "BloodRune", playerReader.BloodRune },
            { "FrostRune", playerReader.FrostRune },
            { "UnholyRune", playerReader.UnholyRune },
            { "TotalRune", playerReader.MaxRune },
            { "Combo Point", playerReader.ComboPoints },
            { "Durability%", playerReader.AvgEquipDurability },
            { "BagCount", bagReader.BagItemCount },
            { "FoodCount", bagReader.FoodItemCount },
            { "DrinkCount", bagReader.DrinkItemCount },
            { "MobCount", combatLog.DamageTakenCount },
            { "MinRange", playerReader.MinRange },
            { "MaxRange", playerReader.MaxRange },
            { "LastAutoShotMs", playerReader.AutoShot.ElapsedMs },
            { "LastMainHandMs", playerReader.MainHandSwing.ElapsedMs },
            { "LastTargetDodgeMs", LastTargetDodgeMs },
            //"CD"
            //"CD_{KeyAction.Name}
            //"Cost"
            //"Cost_{KeyAction.Name}"
            //"Cost_{KeyAction.Name}_{0..4}"
            //"Buff_{textureId}"
            //"Debuff_{textureId}"
            //"TBuff_{textureId}"
            //"FBuff_{textureId}"
            { "MainHandSpeed", playerReader.MainHandSpeedMs },
            { "MainHandSwing", MainHandSwing },
            { "RangedSpeed", playerReader.RangedSpeedMs },
            { "RangedSwing", RangedSwing },
            { "CurGCD", playerReader.GCD._Value },
            { "GCD", CastingHandler._GCD },

            // Session Stat
            { "Deaths", sessionStat._Deaths },
            { "Kills", sessionStat._Kills },
        };

        InitUserDefinedIntVariables(classConfig.IntVariables,
            playerBuff, targetDebuff,
            targetBuff, focusBuff);
    }

    private static void AddAura<T>(string prefix,
        Dictionary<string, Func<bool>> boolVariables, T t) where T : notnull
    {
        foreach (MethodInfo mInfo in t.GetType().GetMethods(
            BindingFlags.DeclaredOnly |
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (mInfo.ReturnType != typeof(bool))
                continue;

            NamesAttribute? names =
                (NamesAttribute?)Attribute.GetCustomAttribute(
                    mInfo, typeof(NamesAttribute));

            if (names is not null)
            {
                foreach (string name in names.Values)
                {
                    boolVariables.Add($"{prefix}{name}",
                        mInfo.CreateDelegate<Func<bool>>(t));
                }
            }
            else
            {
                string name = $"{prefix}{mInfo.Name.Replace("_", " ")}";
                boolVariables.Add(name, mInfo.CreateDelegate<Func<bool>>(t));
            }
        }
    }

    private int LastTargetDodgeMs()
    {
        return Max(0, combatLog.TargetDodge.ElapsedMs());
    }

    private int MainHandSwing()
    {
        return Clamp(
            playerReader.MainHandSwing.ElapsedMs() -
            playerReader.MainHandSpeedMs(),
            -playerReader.MainHandSpeedMs(), 0);
    }

    private int RangedSwing()
    {
        return Clamp(
            playerReader.AutoShot.ElapsedMs() -
            playerReader.RangedSpeedMs(),
            -playerReader.RangedSpeedMs(), 0);
    }

    public void Init(KeyAction item)
    {
        if (item.Name is Drink or Food)
            AddConsumable(item);

        List<Requirement> list = new();

        if (item.Slot > 0)
        {
            InitPerKeyAction(item);

            AddMinPower(list, item, costReader, buffs);
            AddGameCooldown(list, item, playerReader, intVariables);

            if (item.WhenUsable)
            {
                list.Add(
                    CreateActionUsable(item, playerReader,
                    classConfig.Form, costReader, usableAction));

                list.Add(CreateActionCurrent(item, currentAction));
            }
        }

        Process(list, item.Name, item.Requirements);

        AddTargetIsCasting(list, item, playerReader);

        AddKeyActionCooldown(list, item);
        AddCharge(list, item);

        AddSpellSchool(list, item, playerReader, npcSchoolImmunity);

        item.RequirementsRuntime = list.ToArray();

        list.Clear();
        Process(list, item.Name, item.Interrupts);
        item.InterruptsRuntime = list.ToArray();
    }

    private void Process(List<Requirement> output, string name,
        List<string> requirements)
    {
        foreach (string requirement in CollectionsMarshal.AsSpan(requirements))
        {
            List<string> expressions = InfixToPostfix.Convert(requirement);
            Stack<Requirement> stack = new();
            foreach (string expr in CollectionsMarshal.AsSpan(expressions))
            {
                if (expr.Contains(SymbolAnd))
                {
                    Requirement a = stack.Pop();
                    Requirement b = stack.Pop();
                    b.And(a);

                    stack.Push(b);
                }
                else if (expr.Contains(SymbolOr))
                {
                    Requirement a = stack.Pop();
                    Requirement b = stack.Pop();
                    b.Or(a);

                    stack.Push(b);
                }
                else
                {
                    string trim = expr.Trim();
                    if (string.IsNullOrEmpty(trim))
                    {
                        continue;
                    }

                    LogProcessing(logger, name, trim);
                    stack.Push(CreateRequirement(trim));
                }
            }
            output.Add(stack.Pop());
        }
    }

    public void InitUserDefinedIntVariables(Dictionary<string, int> intKeyValues,
        AuraTimeReader<IPlayerBuffTimeReader> playerBuffTimeReader,
        AuraTimeReader<ITargetDebuffTimeReader> targetDebuffTimeReader,
        AuraTimeReader<ITargetBuffTimeReader> targetBuffTimeReader,
        AuraTimeReader<IFocusBuffTimeReader> focusBuffTimeReader)
    {
        foreach ((string key, int value) in intKeyValues)
        {
            int f() => value;

            if (!intVariables.TryAdd(key, f))
            {
                throw new Exception($"Unable to add user defined variable to values. [{key} -> {value}]");
            }

            if (key.StartsWith("Buff_"))
            {
                int l() => playerBuffTimeReader.GetRemainingTimeMs(value);
                intVariables.TryAdd($"{value}", l);
            }
            else if (key.StartsWith("Debuff_"))
            {
                int l() => targetDebuffTimeReader.GetRemainingTimeMs(value);
                intVariables.TryAdd($"{value}", l);
            }
            else if (key.StartsWith("TBuff_"))
            {
                int l() => targetBuffTimeReader.GetRemainingTimeMs(value);
                intVariables.TryAdd($"{value}", l);
            }
            else if (key.StartsWith("FBuff_"))
            {
                int l() => focusBuffTimeReader.GetRemainingTimeMs(value);
                intVariables.TryAdd($"{value}", l);
            }

            LogUserDefinedValue(logger, nameof(RequirementFactory), key, value);
        }
    }

    public void InitAutoBinds(KeyAction item)
    {
        if (item.Slot == 0)
            return;

        BindCooldown(item, cooldownReader);
        BindMinCost(item, costReader);
    }

    private void BindCooldown(KeyAction item, ActionBarCooldownReader reader)
    {
        string key = $"CD_{item.Name}";
        if (intVariables.ContainsKey(key))
            return;

        intVariables.Add(key, get);
        int get() => reader.Get(item);
    }

    private void BindMinCost(KeyAction item, ActionBarCostReader reader)
    {
        string key = $"Cost_{item.Name}";
        if (intVariables.ContainsKey(key))
            return;

        intVariables.Add(key, get);
        int get() => reader.Get(item).Cost;

        for (int i = 0; i < ActionBar.NUM_OF_COST; i++)
        {
            key = $"Cost_{item.Name}_{i}";
            if (intVariables.ContainsKey(key))
                return;

            intVariables.Add(key, get_i);
            int get_i() => reader.Get(item, i).Cost;
        }
    }

    private void InitPerKeyAction(KeyAction item)
    {
        InitPerKeyAction(item, "CD");
        InitPerKeyAction(item, "Cost");
    }

    private void InitPerKeyAction(KeyAction item, string prefixKey)
    {
        string key = $"{prefixKey}_{item.Name}";
        intVariables.Remove(prefixKey);

        if (intVariables.TryGetValue(key, out Func<int>? func))
            intVariables.Add(prefixKey, func);
    }

    private void AddTargetIsCasting(List<Requirement> list,
        KeyAction item, PlayerReader playerReader)
    {
        if (item.UseWhenTargetIsCasting == null)
            return;

        bool f() =>
            playerReader.IsTargetCasting() == item.UseWhenTargetIsCasting.Value;
        string l() => "Target casting";
        list.Add(new Requirement
        {
            HasRequirement = f,
            LogMessage = l
        });
    }

    private void AddMinPower(List<Requirement> list, KeyAction item,
        ActionBarCostReader costReader, BuffStatus<IPlayer> buffs)
    {
        AddMinPower(list, item, playerReader, costReader, buffs);

        AddMinComboPoints(list, item, playerReader);
    }

    private void AddMinPower(List<Requirement> list, KeyAction keyAction,
        PlayerReader playerReader, ActionBarCostReader costReader,
        BuffStatus<IPlayer> buffs)
    {
        static int Empty() => 0;

        Func<int> formCost = Empty;
        PowerType formPowerType = PowerType.None;

        if (keyAction.HasForm)
        {
            if (!classConfig.Form.Get(keyAction.FormValue, out KeyAction? form))
                throw new ArgumentNullException(
                    keyAction.FormValue.ToStringF(),
                    $"Requires a {nameof(KeyAction)} " +
                    $"to be defined under {nameof(ClassConfiguration)}." +
                    $"{nameof(classConfig.Form)}.{nameof(KeyActions.Sequence)} with");

            formPowerType = costReader.Get(form!).PowerType;
            formCost = formChangeCost;

            int formChangeCost() =>
                playerReader.Form != keyAction.FormValue
                ? costReader.Get(form!).Cost
                : 0;
        }

        for (int i = 0; i < ActionBar.NUM_OF_COST; i++)
        {
            ActionBarCost abc = costReader.Get(keyAction, i);
            if (abc.Equals(ActionBarCostReader.DefaultCost))
                continue;

            PowerType type = abc.PowerType;

            Func<bool> fCost;
            Func<string> sCost;

            int index = i;

            if (formPowerType == type)
            {
                fCost = fCostWithForm;
                bool fCostWithForm()
                {
                    ActionBarCost abc = costReader.Get(keyAction, index);
                    Func<int> func = PowerTypeDelegate(playerReader, abc.PowerType);
                    return buffs.Clearcasting() || func() >= abc.Cost + formCost();
                }

                sCost = sCostWithForm;
                string sCostWithForm()
                {
                    ActionBarCost abc = costReader.Get(keyAction, index);
                    Func<int> func = PowerTypeDelegate(playerReader, abc.PowerType);
                    return $"{abc.PowerType.ToStringF()} " +
                        $"{func()} >= {abc.Cost}{(formCost() > 0 ? $"+{formCost()}" : "")}";
                }
            }
            else
            {
                fCost = fCostWithoutForm;
                bool fCostWithoutForm()
                {
                    ActionBarCost abc = costReader.Get(keyAction, index);
                    Func<int> func = PowerTypeDelegate(playerReader, abc.PowerType);
                    return buffs.Clearcasting() || func() >= abc.Cost;
                }

                sCost = sCostWithoutForm;
                string sCostWithoutForm()
                {
                    ActionBarCost abc = costReader.Get(keyAction, index);
                    Func<int> func = PowerTypeDelegate(playerReader, abc.PowerType);
                    return $"{abc.PowerType.ToStringF()} {func()} >= {abc.Cost}";
                }
            }

            list.Add(new Requirement
            {
                HasRequirement = fCost,
                LogMessage = sCost
            });
        }
    }

    private static Func<int> PowerTypeDelegate(PlayerReader playerReader, PowerType type)
    => type switch
    {
        PowerType.Mana => playerReader.ManaCurrent,
        PowerType.Rage or
        PowerType.Energy or
        PowerType.RunicPower => playerReader.PTCurrent,
        PowerType.RuneBlood => playerReader.BloodRune,
        PowerType.RuneFrost => playerReader.FrostRune,
        PowerType.RuneUnholy => playerReader.UnholyRune,
        PowerType.HealthCost => playerReader.HealthCurrent,
        //PowerType.ComboPoints => playerReader.ComboPoints,
        _ => throw new NotImplementedException($"{type.ToStringF()}"),
    };

    private void AddMinComboPoints(List<Requirement> list, KeyAction item,
        PlayerReader playerReader)
    {
        if (item.MinComboPoints <= 0)
            return;

        bool f() => playerReader.ComboPoints() >= item.MinComboPoints;
        string s()
            => $"Combo point {playerReader.ComboPoints()} >= {item.MinComboPoints}";
        list.Add(new Requirement
        {
            HasRequirement = f,
            LogMessage = s
        });
    }

    private static void AddKeyActionCooldown(List<Requirement> list, KeyAction item)
    {
        if (item.Cooldown <= 0)
            return;

        bool f() => item.GetRemainingCooldown() == 0;
        string s() => $"Cooldown {item.GetRemainingCooldown() / 1000:F1}";
        list.Add(new Requirement
        {
            HasRequirement = f,
            LogMessage = s
        });
    }

    private static void AddCharge(List<Requirement> list, KeyAction item)
    {
        if (item.BaseAction || item.Charge < 1)
            return;

        bool f() => item.GetChargeRemaining() != 0;
        string s() => $"Charge {item.GetChargeRemaining()}";
        list.Add(new Requirement
        {
            HasRequirement = f,
            LogMessage = s
        });
    }

    private static void AddConsumable(KeyAction item)
    {
        item.BeforeCastStop = true;
        item.WhenUsable = true;

        item.Requirements.Add($"{SymbolNegate}{item.Name}");
        item.Requirements.Add($"{SymbolNegate}{Swimming}");
        item.Requirements.Add($"{SymbolNegate}{Falling}");
    }

    private void AddSpellSchool(List<Requirement> list, KeyAction item,
        PlayerReader playerReader, FrozenDictionary<int, SchoolMask> npcSchoolImmunity)
    {
        if (item.School == SchoolMask.None)
            return;

        bool f() =>
            !npcSchoolImmunity.TryGetValue(playerReader.TargetId,
                out SchoolMask immuneAgaints) ||
                !immuneAgaints.HasValue(item.School);

        string s() => item.School.ToStringF();
        list.Add(new Requirement
        {
            HasRequirement = f,
            LogMessage = s
        });
    }


    public Requirement CreateRequirement(string requirement)
    {
        string? negated = negateKeywords.FirstOrDefault(requirement.StartsWith);
        if (!string.IsNullOrEmpty(negated))
        {
            requirement = requirement[negated.Length..];
        }

        string? key = requirementMap.Keys.FirstOrDefault(requirement.Contains);
        if (!string.IsNullOrEmpty(key))
        {
            Requirement r = requirementMap[key](requirement);
            if (negated != null)
            {
                r.Negate(negated);
            }
            return r;
        }

        if (!boolVariables.TryGetValue(requirement, out Func<bool>? value))
        {
            LogUnknown(logger, requirement, string.Join(", ", boolVariables.Keys));
            return new Requirement
            {
                LogMessage = () => $"UNKNOWN REQUIREMENT! {requirement}"
            };
        }

        string s() => requirement;
        Requirement req = new()
        {
            HasRequirement = value,
            LogMessage = s
        };

        if (negated != null)
        {
            req.Negate(negated);
        }
        return req;
    }

    private Requirement CreateActionUsable(KeyAction item,
        PlayerReader playerReader, FormKeyActions forms,
        ActionBarCostReader costReader,
        ActionBarBits<IUsableAction> usableAction)
    {
        bool CanDoFormChange()
        {
            if (!forms.Get(item.FormValue, out KeyAction? formKeyAction))
                return true;

            ActionBarCost abc = costReader.Get(formKeyAction!);
            Func<int> powerTypeCurrent = PowerTypeDelegate(playerReader, abc.PowerType);

            return powerTypeCurrent() >= abc.Cost;
        }

        bool f() =>
            !item.HasForm
            ? usableAction.Is(item)
            : (playerReader.Form == item.FormValue && usableAction.Is(item)) ||
            (playerReader.Form != item.FormValue && CanDoFormChange());

        string s() =>
            !item.HasForm
            ? "Usable"
            : (playerReader.Form != item.FormValue && CanDoFormChange())
            ? $"May Usable {item.FormValue.ToStringF()}"
            : (playerReader.Form == item.FormValue && usableAction.Is(item))
            ? $"Usable in Form" : "Unusable";

        return new Requirement
        {
            HasRequirement = f,
            LogMessage = s
        };
    }

    private Requirement CreateActionCurrent(KeyAction item,
        ActionBarBits<ICurrentAction> currentAction)
    {
        bool f() => !currentAction.Is(item);
        static string s() => $"{SymbolNegate}Current";

        return new Requirement
        {
            HasRequirement = f,
            LogMessage = s
        };
    }

    private void AddGameCooldown(List<Requirement> list,
        KeyAction item, PlayerReader playerReader,
        Dictionary<string, Func<int>> intVariables)
    {
        string key = $"CD_{item.Name}";
        bool f() => UsableGCD(key, playerReader, intVariables);
        string s() => $"CD {intVariables[key]() / 1000f:F1}";

        list.Add(new Requirement
        {
            HasRequirement = f,
            LogMessage = s
        });
    }

    private static bool UsableGCD(string key, PlayerReader playerReader,
        Dictionary<string, Func<int>> intVariables)
    {
        return intVariables[key]() <=
            Max(0, playerReader.SpellQueueTimeMs - playerReader.NetworkLatency);
    }

    private Requirement CreateTargetCastingSpell(string requirement)
    {
        return create(requirement, playerReader);
        static Requirement create(string requirement, PlayerReader playerReader)
        {
            ReadOnlySpan<char> span = requirement;
            int sep1 = span.IndexOf(SEP1);
            // 'TargetCastingSpell'
            if (sep1 == -1)
            {
                return new Requirement
                {
                    HasRequirement = playerReader.IsTargetCasting,
                    LogMessage = () => "Target casting"
                };
            }

            // 'TargetCastingSpell:_1_?,_n_'
            string[] spellsPart = span[(sep1 + 1)..].ToString().Split(SEP2);
            int[] spellIds = spellsPart.Select(int.Parse).ToArray();

            bool f() => spellIds.Contains(playerReader.SpellBeingCastByTarget);
            string s() => $"Target casts {playerReader.SpellBeingCastByTarget} ∈ [{string.Join(SEP2, spellIds)}]";
            return new Requirement
            {
                HasRequirement = f,
                LogMessage = s
            };
        }
    }

    private Requirement CreateForm(string requirement)
    {
        return create(requirement, playerReader);
        static Requirement create(string requirement, PlayerReader playerReader)
        {
            // 'Form:_FORM_'
            ReadOnlySpan<char> span = requirement;
            int sep = span.IndexOf(SEP1);
            Form form = Enum.Parse<Form>(span[(sep + 1)..]);

            bool f() => playerReader.Form == form;
            string s() => playerReader.Form.ToStringF();

            return new Requirement
            {
                HasRequirement = f,
                LogMessage = s
            };
        }
    }

    private Requirement CreateRace(string requirement)
    {
        return create(requirement, playerReader);
        static Requirement create(string requirement, PlayerReader playerReader)
        {
            // 'Race:_RACE_'
            ReadOnlySpan<char> span = requirement;
            int sep = span.IndexOf(SEP1);
            UnitRace race = Enum.Parse<UnitRace>(span[(sep + 1)..]);

            bool f() => playerReader.Race == race;
            string s() => playerReader.Race.ToStringF();

            return new Requirement
            {
                HasRequirement = f,
                LogMessage = s
            };
        }
    }

    private Requirement CreateSpell(string requirement)
    {
        return create(requirement, spellBookReader);
        static Requirement create(string requirement, SpellBookReader spellBookReader)
        {
            // 'Spell:_NAME_OR_ID_'
            ReadOnlySpan<char> span = requirement;
            int sep = span.IndexOf(SEP1);
            string name = span[(sep + 1)..].Trim().ToString();

            int id;
            if (int.TryParse(name, out id) &&
                spellBookReader.TryGetValue(id, out Spell spell))
            {
                name = $"{spell.Name}({id})";
            }
            else
            {
                id = spellBookReader.GetId(name);
            }

            bool f() => spellBookReader.Has(id);
            string s() => $"Spell {name}";

            return new Requirement
            {
                HasRequirement = f,
                LogMessage = s
            };
        }
    }

    private Requirement CreateTalent(string requirement)
    {
        return create(requirement, talentReader);
        static Requirement create(string requirement, TalentReader talentReader)
        {
            // 'Talent:_NAME_?:_RANK_'
            ReadOnlySpan<char> span = requirement;

            int firstSep = span.IndexOf(SEP1);
            int lastSep = span.LastIndexOf(SEP1);

            int rank = 1;
            if (firstSep != lastSep)
            {
                rank = int.Parse(span[(lastSep + 1)..]);
            }
            else
            {
                lastSep = span.Length;
            }

            string name = span[(firstSep + 1)..lastSep].ToString();

            bool f() => talentReader.HasTalent(name, rank);
            string s() => rank == 1 ? $"Talent {name}" : $"Talent {name} (Rank {rank})";

            return new Requirement
            {
                HasRequirement = f,
                LogMessage = s
            };
        }
    }

    private Requirement CreateTrigger(string requirement)
    {
        return create(requirement, playerReader);
        static Requirement create(string requirement, PlayerReader playerReader)
        {
            // 'Trigger:_BIT_NUM_?:_TEXT_'
            ReadOnlySpan<char> span = requirement;
            int firstSep = span.IndexOf(SEP1);
            int lastSep = span.LastIndexOf(SEP1);

            string text = string.Empty;
            if (firstSep != lastSep)
            {
                text = span[(lastSep + 1)..].ToString();
            }
            else
            {
                lastSep = span.Length;
            }

            int bitNum = int.Parse(span[(firstSep + 1)..lastSep]);
            int bitMask = Mask.M[bitNum];

            bool f() => playerReader.CustomTrigger1[bitMask];
            string s() => $"Trigger({bitNum}) {text}";

            return new Requirement
            {
                HasRequirement = f,
                LogMessage = s
            };
        }
    }

    private Requirement CreateNpcId(string requirement)
    {
        return create(requirement, playerReader, intVariables, creatureDb);
        static Requirement create(string requirement, PlayerReader playerReader,
            Dictionary<string, Func<int>> intVariables, CreatureDB creatureDb)
        {
            // 'npcID:_INTVARIABLE_OR_ID_'
            ReadOnlySpan<char> span = requirement;
            int sep = span.IndexOf(SEP1);
            ReadOnlySpan<char> name_or_id = span[(sep + 1)..];

            int npcId;
            if (intVariables.TryGetValue(name_or_id.ToString(), out Func<int>? value))
                npcId = value();
            else
                npcId = int.Parse(name_or_id);

            if (!creatureDb.Entries.TryGetValue(npcId, out string? npcName))
            {
                npcName = string.Empty;
            }

            bool f() => playerReader.TargetId == npcId;
            string s() => $"TargetID {npcName}({npcId})";

            return new Requirement
            {
                HasRequirement = f,
                LogMessage = s
            };
        }
    }

    private Requirement CreateBagItem(string requirement)
    {
        return create(requirement, bagReader, intVariables, itemDb);
        static Requirement create(string requirement, BagReader bagReader,
            Dictionary<string, Func<int>> intVariables, ItemDB itemDb)
        {
            // 'BagItem:_INTVARIABLE_OR_ID_?:_COUNT_'
            ReadOnlySpan<char> span = requirement;

            int firstSep = span.IndexOf(SEP1);
            int lastSep = span.LastIndexOf(SEP1);

            int count = 1;
            if (firstSep != lastSep)
            {
                count = int.Parse(span[(lastSep + 1)..]);
            }
            else
            {
                lastSep = span.Length;
            }

            ReadOnlySpan<char> name_or_id = span[(firstSep + 1)..lastSep];

            int itemId;
            if (intVariables.TryGetValue(name_or_id.ToString(), out Func<int>? value))
                itemId = value();
            else
                itemId = int.Parse(name_or_id);

            string itemName = string.Empty;
            if (itemDb.Items.TryGetValue(itemId, out Item item))
            {
                itemName = item.Name;
            }

            bool f() => bagReader.ItemCount(itemId) >= count;
            string s() => count == 1 ? $"in bag {itemName}({itemId})" : $"{itemName}({itemId}) count >= {count}";

            return new Requirement
            {
                HasRequirement = f,
                LogMessage = s
            };
        }
    }

    private Requirement CreateSpellInRange(string requirement)
    {
        return create(requirement, playerReader.SpellInRange);
        static Requirement create(string requirement, SpellInRange range)
        {
            // 'SpellInRange:_BIT_NUM_'
            ReadOnlySpan<char> span = requirement;
            int sep = span.IndexOf(SEP1);
            int bitNum = int.Parse(span[(sep + 1)..]);
            int bitMask = Mask.M[bitNum];

            bool f() => range[bitMask];
            string s() => $"SpellInRange {bitNum}";

            return new Requirement
            {
                HasRequirement = f,
                LogMessage = s
            };
        }
    }

    private Requirement CreateUsable(string requirement)
    {
        // 'Usable:_KeyAction_Name_'
        ReadOnlySpan<char> span = requirement;
        int sep = span.IndexOf(SEP1);
        ReadOnlySpan<char> name = span[(sep + 1)..].Trim();

        List<(string _, KeyActions)> groups = classConfig.GetByType<KeyActions>();

        foreach ((string _, KeyActions keyActions) in groups)
        {
            foreach (KeyAction keyAction in keyActions.Sequence)
            {
                if (name.SequenceEqual(keyAction.Name))
                {
                    return CreateActionUsable(keyAction, playerReader,
                        classConfig.Form, costReader, usableAction);
                }
            }
        }

        throw new InvalidOperationException($"'{requirement}' " +
            $"related named '{name}' {nameof(KeyAction)} not found!");
    }

    private Requirement CreateGreaterThen(string requirement)
    {
        return CreateArithmetic(greaterThen, requirement, intVariables);
    }

    private Requirement CreateLesserThen(string requirement)
    {
        return CreateArithmetic(lessThen, requirement, intVariables);
    }

    private Requirement CreateGreaterOrEquals(string requirement)
    {
        return CreateArithmetic(greaterThenOrEqual, requirement, intVariables);
    }

    private Requirement CreateLesserOrEquals(string requirement)
    {
        return CreateArithmetic(lessThenOrEqual, requirement, intVariables);
    }

    private Requirement CreateEquals(string requirement)
    {
        return CreateArithmetic(equals, requirement, intVariables);
    }

    private Requirement CreateModulo(string requirement)
    {
        return CreateArithmetic(modulo, requirement, intVariables);
    }

    private Requirement CreateArithmetic(string symbol, string requirement,
        Dictionary<string, Func<int>> intVariables)
    {
        ReadOnlySpan<char> span = requirement;
        int sep = span.IndexOf(symbol);

        string key = span[..sep].Trim().ToString();
        ReadOnlySpan<char> varOrConst = span[(sep + symbol.Length)..];

        if (!intVariables.TryGetValue(key, out Func<int>? aliasOrKey))
        {
            LogUnknown(logger, requirement, string.Join(", ", intVariables.Keys));
            throw new ArgumentOutOfRangeException(requirement);
        }

        string display = key;

        string aliasKey = aliasOrKey().ToString();
        if (intVariables.ContainsKey(aliasKey))
        {
            key = aliasKey;
        }

        Func<int> lValue = intVariables[key];

        string varOrConstName = "";
        Func<int> rValue;
        if (int.TryParse(varOrConst, out int constValue))
        {
            int _constValue() => constValue;
            rValue = _constValue;
        }
        else
        {
            varOrConstName = varOrConst.Trim().ToString();
            rValue = intVariables.TryGetValue(varOrConstName, out Func<int>? v)
                ? v
                : throw new ArgumentOutOfRangeException(varOrConstName);
        }

        if (!string.IsNullOrEmpty(varOrConstName))
            varOrConstName += " ";

        string msg() => $"{display} {lValue()} {symbol} {varOrConstName}{rValue()}";
        switch (symbol)
        {
            case modulo:
                bool m() => lValue() % rValue() == 0;
                return new Requirement { HasRequirement = m, LogMessage = msg };
            case equals:
                bool e() => lValue() == rValue();
                return new Requirement { HasRequirement = e, LogMessage = msg };
            case greaterThen:
                bool g() => lValue() > rValue();
                return new Requirement { HasRequirement = g, LogMessage = msg };
            case lessThen:
                bool l() => lValue() < rValue();
                return new Requirement { HasRequirement = l, LogMessage = msg };
            case greaterThenOrEqual:
                bool ge() => lValue() >= rValue();
                return new Requirement { HasRequirement = ge, LogMessage = msg };
            case lessThenOrEqual:
                bool le() => lValue() <= rValue();
                return new Requirement { HasRequirement = le, LogMessage = msg };
            default:
                throw new ArgumentOutOfRangeException(requirement);
        };
    }

    #region Logging

    [LoggerMessage(
        EventId = 0017,
        Level = LogLevel.Information,
        Message = "[{typeName}] Defined int variable [{key} -> {value}]")]
    static partial void LogUserDefinedValue(ILogger logger, string typeName, string key, int value);

    [LoggerMessage(
        EventId = 0018,
        Level = LogLevel.Information,
        Message = "[{name,-15}] Requirement: \"{requirement}\"")]
    static partial void LogProcessing(ILogger logger, string name, string requirement);

    [LoggerMessage(
        EventId = 0019,
        Level = LogLevel.Error,
        Message = "UNKNOWN REQUIREMENT! {requirement}: try one of: {available}")]
    static partial void LogUnknown(ILogger logger, string requirement, string available);

    #endregion
}