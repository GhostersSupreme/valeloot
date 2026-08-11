using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ValeLoot;

/**
 * The mod's own parser, pointed at a folder of filter files, emitting one canonical JSON document.
 *
 * Its opposite number is `parse-ts.ts`, which does the same with the editor's parser. `run.ts`
 * compares the two byte for byte. Nothing here is clever on purpose: the moment this harness starts
 * interpreting, it stops being evidence about the parser and starts being evidence about itself.
 *
 * ## What `ItemCatalog` is doing here
 *
 * `LootFilter.Matches` consults the catalog to answer `Type` and `Stat <name> >= <value>` against a
 * live game. Parsing and the percentage-based StatMatches contract checks below do not need it, but
 * the file will not compile without the symbol, and linking the real one would drag in il2cpp and
 * BepInEx — the whole reason this harness links three files instead of referencing the plugin.
 * Printed-value matching remains outside this harness, so the catalog methods stay inert.
 */
internal static class ItemCatalog
{
    public const string ReferenceFileName = "valeloot-items.txt";

    private static readonly Dictionary<(string ItemId, int StatType), int> Caps = new();

    public static string? TypeName(string itemId) => null;
    public static string? DisplayName(string itemId) => null;

    public static void SetCap(string itemId, int statType, int cap) => Caps[(itemId, statType)] = cap;

    public static bool TryScaledValue(string itemId, int statType, int rollPct, out int value)
    {
        if (!Caps.TryGetValue((itemId, statType), out int cap))
        {
            value = 0;
            return false;
        }
        float scaled = rollPct / 100f * 0.333333f + 0.666667f;
        value = (int)Math.Round((double)(cap * scaled), MidpointRounding.AwayFromZero);
        return true;
    }

    public static bool TryIsDisplayedTop(string itemId, int statType, int rollPct, out bool top)
    {
        if (!Caps.TryGetValue((itemId, statType), out int cap))
        {
            top = false;
            return false;
        }
        int printed = Scale(cap, rollPct);
        int legalTop = Scale(cap, 100);
        top = cap >= 0 ? printed >= legalTop : printed <= legalTop;
        return true;
    }

    private static int Scale(int cap, int rollPct)
    {
        float scaled = rollPct / 100f * 0.333333f + 0.666667f;
        return (int)Math.Round((double)(cap * scaled), MidpointRounding.AwayFromZero);
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: Conformance <cases-directory>");
            return 2;
        }

        string directory = args[0];
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"no such directory: {directory}");
            return 2;
        }

        VerifyStatMatches();
        VerifyAnyOf();
        VerifyDisplayedStatRules();
        VerifyStatAliases();

        string[] files = Directory.GetFiles(directory, "*.txt");
        Array.Sort(files, StringComparer.Ordinal);

        var json = new StringBuilder();
        json.Append("[\n");
        for (int i = 0; i < files.Length; i++)
        {
            if (i > 0) json.Append(",\n");
            Emit(json, Path.GetFileNameWithoutExtension(files[i]), File.ReadAllText(files[i]));
        }
        json.Append("\n]\n");

        Console.Out.Write(json.ToString());
        return 0;
    }

    /// <summary>Executable contract for the count aggregation added by StatMatches.</summary>
    private static void VerifyStatMatches()
    {
        var item = new LootFilter.ItemFacts();
        item.AddStat("Str", 0, 80, "");
        item.AddStat("Vit", 0, 60, "");

        var stats = new[]
        {
            new LootFilter.StatCondition { Stat = "Str", MinRollPct = 70 },
            new LootFilter.StatCondition { Stat = "Vit", MinRollPct = 50 },
            new LootFilter.StatCondition { Stat = "Agi", MinRollPct = 50 },
        };

        AssertMatch(true, item, stats, 2, null, "at least two of three");
        AssertMatch(false, item, stats, 3, null, "at least three of three");
        AssertMatch(false, item, stats, null, 1, "at most one of three");
        AssertMatch(true, item, stats, 2, 2, "exactly two of three");
        AssertMatch(false, item, stats, 1, 1, "exactly one of three");
    }

    /// <summary>Friendly spellings canonicalize; collision exclusions stay distinct live stats.</summary>
    private static void VerifyStatAliases()
    {
        foreach (StatAliases.Entry alias in StatAliases.All)
        {
            FilterParser.ParsedFilter parsed = FilterParser.Parse(
                $"Show \"alias\"\n    Stat {alias.Friendly.ToLowerInvariant()} >= 70%");
            if (parsed.Errors.Length != 0 || parsed.Rules.Length != 1)
                throw new InvalidOperationException($"could not parse stat alias {alias.Friendly}");

            LootFilter.StatCondition condition = parsed.Rules[0].When.Stats![0];
            if (!string.Equals(condition.Stat, alias.Internal, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"stat alias {alias.Friendly} canonicalized to {condition.Stat}, expected {alias.Internal}");

            var item = new LootFilter.ItemFacts();
            item.AddStat(alias.Internal.ToUpperInvariant(), 0, 80, "");
            AssertCondition(true, item, parsed.Rules[0].When,
                            $"friendly alias {alias.Friendly} -> {alias.Internal}");
        }

        (string Source, string Collision)[] excluded =
        {
            ("AtkMult", "Atk"),
            ("MatkMult", "Matk"),
            ("DefMult", "Def"),
            ("MdefMult", "Mdef"),
            ("HpMult", "Hp"),
            ("MpMult", "Mp"),
            ("HpRegenMult", "HpRegen"),
            ("MpRegenMult", "MpRegen"),
        };
        foreach ((string source, string collision) in excluded)
        {
            FilterParser.ParsedFilter parsed = FilterParser.Parse(
                $"Show \"collision\"\n    Stat {collision.ToLowerInvariant()} >= 70%");
            if (parsed.Errors.Length != 0 || parsed.Rules.Length != 1)
                throw new InvalidOperationException($"could not parse collision exclusion {collision}");

            var sourceItem = new LootFilter.ItemFacts();
            sourceItem.AddStat(source, 0, 80, "");
            AssertCondition(false, sourceItem, parsed.Rules[0].When,
                            $"{collision} must not alias {source}");

            var collisionItem = new LootFilter.ItemFacts();
            collisionItem.AddStat(collision, 0, 80, "");
            AssertCondition(true, collisionItem, parsed.Rules[0].When,
                            $"{collision} remains its own internal stat");
        }
    }

    /// <summary>Executable contract for required stats combined with grouped alternatives.</summary>
    private static void VerifyAnyOf()
    {
        var agiAndAtk = new LootFilter.LootCondition
        {
            Stats = new[]
            {
                new LootFilter.StatCondition { Stat = "Agi", MinRollPct = 1 },
            },
            AnyOfStats = new[]
            {
                new[]
                {
                    new LootFilter.StatCondition { Stat = "AtkMult", MinRollPct = 1 },
                    new LootFilter.StatCondition { Stat = "Atk", MinRollPct = 1 },
                },
            },
        };

        var agiAtk = new LootFilter.ItemFacts();
        agiAtk.AddStat("Agi", 0, 80, "");
        agiAtk.AddStat("Atk", 0, 60, "");
        AssertCondition(true, agiAtk, agiAndAtk, "required AGI plus one attack alternative");

        var agiOnly = new LootFilter.ItemFacts();
        agiOnly.AddStat("Agi", 0, 80, "");
        AssertCondition(false, agiOnly, agiAndAtk, "required AGI without an attack alternative");

        var attacksOnly = new LootFilter.ItemFacts();
        attacksOnly.AddStat("AtkMult", 0, 80, "");
        attacksOnly.AddStat("Atk", 0, 60, "");
        AssertCondition(false, attacksOnly, agiAndAtk, "attack alternatives without required AGI");
    }

    /// <summary>Executable contract for displayed artifact values and displayed top rolls.</summary>
    private static void VerifyDisplayedStatRules()
    {
        const string artifactId = "test-artifact";
        ItemCatalog.SetCap(artifactId, 1, 3);
        ItemCatalog.SetCap(artifactId, 2, 2);
        ItemCatalog.SetCap(artifactId, 3, 2);

        var top = new LootFilter.ItemFacts { Id = artifactId, Type = "Artifact" };
        top.AddStat("Vit", 1, 80, "");
        top.AddStat("HpMult", 2, 70, "");
        top.AddStat("MatkMult", 3, 60, "");

        var displayed = new LootFilter.LootCondition
        {
            Types = new[] { "Artifact" },
            Stats = new[]
            {
                new LootFilter.StatCondition { Stat = "Vit", MinValue = 3 },
                new LootFilter.StatCondition { Stat = "HpMult", MinValue = 2 },
                new LootFilter.StatCondition { Stat = "MatkMult", MinValue = 2 },
            },
        };
        AssertCondition(true, top, displayed, "artifact displayed maxima");
        AssertCondition(true, top, new LootFilter.LootCondition { MinTopRolls = 3 },
                        "displayed top rolls below the raw threshold");
        AssertCondition(false, top, new LootFilter.LootCondition { MinHighRolls = 1 },
                        "displayed top rolls stay separate from raw high rolls");

        var low = new LootFilter.ItemFacts { Id = artifactId, Type = "Artifact" };
        low.AddStat("Vit", 1, 0, "");
        AssertCondition(false, low, new LootFilter.LootCondition
        {
            Stats = new[] { new LootFilter.StatCondition { Stat = "Vit", MinValue = 3 } },
        }, "lower displayed artifact value");
        AssertCondition(false, top, new LootFilter.LootCondition
        {
            Stats = new[] { new LootFilter.StatCondition { Stat = "Vit", MinRollPct = 90 } },
        }, "artifact percent form stays raw");
    }

    private static void AssertCondition(
        bool expected,
        LootFilter.ItemFacts item,
        LootFilter.LootCondition when,
        string scenario)
    {
        bool actual = LootFilter.Matches(item, when, LootFilter.DefaultThreshold);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"roll semantics contract failed for {scenario}: expected {expected}, got {actual}");
        }
    }

    private static void AssertMatch(
        bool expected,
        LootFilter.ItemFacts item,
        LootFilter.StatCondition[] stats,
        int? min,
        int? max,
        string scenario)
    {
        var when = new LootFilter.LootCondition
        {
            Stats = stats,
            MinStatMatches = min,
            MaxStatMatches = max,
        };
        bool actual = LootFilter.Matches(item, when, LootFilter.DefaultThreshold);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"StatMatches contract failed for {scenario}: expected {expected}, got {actual}");
        }
    }

    private static void Emit(StringBuilder json, string name, string text)
    {
        FilterParser.ParsedFilter parsed = FilterParser.Parse(text);

        json.Append("  {\"case\": ");
        Str(json, name);
        json.Append(", \"threshold\": ").Append(parsed.Threshold.ToString(CultureInfo.InvariantCulture));

        json.Append(", \"pinned\": ");
        Strings(json, parsed.Pinned);
        json.Append(", \"muted\": ");
        Strings(json, parsed.Muted);

        // LINES only. The two implementations word their messages for their own readers and always
        // will; what must never differ is which lines they refuse. Sorted NUMERICALLY — sorting the
        // rendered strings puts line 9 after line 30 and invents a mismatch out of nothing.
        var lines = new List<int>(parsed.Errors.Length);
        foreach (FilterParser.FilterError error in parsed.Errors) lines.Add(error.Line);
        lines.Sort();
        json.Append(", \"errorLines\": [").Append(string.Join(", ", lines)).Append(']');

        json.Append(", \"rules\": [");
        for (int i = 0; i < parsed.Rules.Length; i++)
        {
            if (i > 0) json.Append(", ");
            Rule(json, parsed.Rules[i]);
        }
        json.Append("]}");
    }

    private static void Rule(StringBuilder json, LootFilter.LootRule rule)
    {
        json.Append("{\"name\": ");
        Str(json, rule.Name);
        json.Append(", \"color\": ");
        // Lower-cased on both sides: the file may spell a hex any way it likes and the two parsers
        // are not required to agree about its case, only about the colour.
        Str(json, rule.Color.ToLowerInvariant());
        json.Append(", \"label\": ");
        Str(json, rule.Label);
        json.Append(", \"level\": ");
        Str(json, LootFilter.LevelName(rule.Level));
        json.Append(", \"sound\": ");
        if (rule.Sound is null) json.Append("null"); else Str(json, rule.Sound);
        json.Append(", \"mute\": ").Append(rule.Mute ? "true" : "false");

        LootFilter.LootCondition when = rule.When;
        json.Append(", \"when\": {\"names\": ");
        Strings(json, when.Names);
        json.Append(", \"types\": ");
        Strings(json, when.Types);
        json.Append(", \"minRefine\": ").Append(Int(when.MinRefine))
            .Append(", \"minTopRolls\": ").Append(Int(when.MinTopRolls))
            .Append(", \"maxTopRolls\": ").Append(Int(when.MaxTopRolls))
            .Append(", \"minHighRolls\": ").Append(Int(when.MinHighRolls))
            .Append(", \"maxHighRolls\": ").Append(Int(when.MaxHighRolls))
            .Append(", \"minAvgRollPct\": ").Append(Int(when.MinAvgRollPct))
            .Append(", \"maxAvgRollPct\": ").Append(Int(when.MaxAvgRollPct))
            .Append(", \"minStatMatches\": ").Append(Int(when.MinStatMatches))
            .Append(", \"maxStatMatches\": ").Append(Int(when.MaxStatMatches))
            .Append(", \"statsAll\": ").Append(when.StatsAll ? "true" : "false")
            .Append(", \"hasChaos\": ").Append(Bool(when.HasChaos))
            .Append(", \"favorite\": ").Append(Bool(when.Favorite))
            .Append(", \"overRoll\": ").Append(Bool(when.OverRoll));

        json.Append(", \"stats\": [");
        if (when.Stats is not null)
        {
            for (int i = 0; i < when.Stats.Length; i++)
            {
                if (i > 0) json.Append(", ");
                LootFilter.StatCondition stat = when.Stats[i];
                json.Append("{\"stat\": ");
                Str(json, stat.Stat);
                json.Append(", \"minRollPct\": ").Append(Int(stat.MinRollPct))
                    .Append(", \"minValue\": ").Append(Int(stat.MinValue))
                    .Append('}');
            }
        }
        json.Append("], \"anyOfStats\": [");
        if (when.AnyOfStats is not null)
        {
            for (int groupIndex = 0; groupIndex < when.AnyOfStats.Length; groupIndex++)
            {
                if (groupIndex > 0) json.Append(", ");
                json.Append('[');
                LootFilter.StatCondition[] group = when.AnyOfStats[groupIndex];
                for (int statIndex = 0; statIndex < group.Length; statIndex++)
                {
                    if (statIndex > 0) json.Append(", ");
                    LootFilter.StatCondition stat = group[statIndex];
                    json.Append("{\"stat\": ");
                    Str(json, stat.Stat);
                    json.Append(", \"minRollPct\": ").Append(Int(stat.MinRollPct))
                        .Append(", \"minValue\": ").Append(Int(stat.MinValue))
                        .Append('}');
                }
                json.Append(']');
            }
        }
        json.Append("]}}");
    }

    private static string Int(int? value)
        => value is int number ? number.ToString(CultureInfo.InvariantCulture) : "null";

    private static string Bool(bool? value)
        => value is bool flag ? (flag ? "true" : "false") : "null";

    private static void Strings(StringBuilder json, string[]? values)
    {
        if (values is null) { json.Append("null"); return; }
        json.Append('[');
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) json.Append(", ");
            Str(json, values[i]);
        }
        json.Append(']');
    }

    private static void Str(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    if (c < ' ') json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else json.Append(c);
                    break;
            }
        }
        json.Append('"');
    }
}
