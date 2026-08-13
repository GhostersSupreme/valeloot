using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ValeLoot;

/// <summary>
/// The loot filter language — a block filter you edit in a text file, in the shape players already
/// know from Path of Exile.
///
/// <code>
/// Threshold 90                  # what counts as a "top roll", in percent
///
/// # Kunais are only worth keeping with real AGI on them
/// Show "Kunai keepers"
///     Name      Kunai
///     Stat      Agi >= 90%
///     Tag       KEEP
///     Highlight glow
///     Sound     chime
///
/// Show "Triple top roll"
///     TopRolls  >= 3
///     Color     #f472b6
///     Highlight glow
///
/// Hide "rolled badly"
///     AvgRollPct &lt; 35
///     HighRolls  &lt; 1
///
/// AlwaysShow "Spirit Ward", "Windborne Rune"
/// AlwaysHide "Rusty Dagger"
/// </code>
///
/// ## Show and Hide are the only verbs
///
/// `Show` decides how an item is PRESENTED — colour, tag, highlight level, sound. `Hide` claims an
/// item in order to say nothing about it. Neither does anything TO the item; the language has no verb
/// that could, and the plugin has no code that could carry one out. The older, destructive spellings
/// that the overlay's own parser still tolerates for backwards compatibility are deliberately NOT
/// accepted here: this file format is new, so it carries no legacy, and the vocabulary of a mod handed
/// to strangers should not contain a word that ever meant acting on an item. They fail as unknown
/// keywords, with the line number.
///
/// ## `>= 3` versus `>= 90%`
///
/// The `%` suffix is the whole disambiguation, and both sides of it work:
///
///   Stat Agi >= 90%   the line's ROLL QUALITY — top tenth of AGI's legal range, from the item alone
///   Stat Agi >= 3     the VALUE the game prints, from the item's base cap in the game's own catalog
///
/// They are kept as different conditions all the way down. Reinterpreting one as the other would
/// answer a different question than the one asked and look like it worked, which is the failure this
/// parser is built to prevent — and the numbers are not close: a 0% roll already prints two thirds of
/// the cap, so `>= 3` on a cap of 4 is satisfied by nearly every roll while `>= 90%` is satisfied by
/// nearly none.
///
/// The value form needs <see cref="ItemCatalog"/>, which resolves lazily and may never resolve on a
/// game build that moved something. It is accepted at parse time regardless: a rule that cannot be
/// answered matches NOTHING and says so in the log once, which is strictly better than refusing the
/// line and widening the block it was in.
///
/// ## Why a bad line rejects the whole block
///
/// Ignoring an unparseable condition WIDENS the block it was in — drop `Stat Agi >= 90%` from a `Show`
/// block and it claims everything you own, so the bag lights up uniformly and the filter looks broken
/// rather than misread. A block with any bad line is therefore REJECTED WHOLE and reported with its
/// line number, and this parser never emits a partially-understood rule.
/// </summary>
/// <summary>
/// Player-facing stat spellings accepted in addition to the live <c>StatType</c> names.
///
/// Internal names stay canonical. Every consumer calls <see cref="Canonical"/> at its boundary, so
/// the evaluator, generated reference, and editor can share the friendly vocabulary without changing
/// the names read from the game.
/// </summary>
internal static class StatAliases
{
    internal readonly struct Entry
    {
        public readonly string Friendly;
        public readonly string Internal;

        public Entry(string friendly, string internalName)
        {
            Friendly = friendly;
            Internal = internalName;
        }
    }

    internal static readonly Entry[] All =
    {
        new("AttackSpeed", "AtkSpd"),
        new("AttackSpeedLimit", "AtkSpdLimit"),
        new("CastSpeed", "CastSpd"),
        new("AutoAttackChain", "Chain"),
        new("MagicDamage", "DamageMagic"),
        new("MeleeDamage", "DamageMelee"),
        new("RangedDamage", "DamageRanged"),
        new("Multistrike", "DoubleAttack"),
        new("HealthLeech", "Leech"),
        new("MovementSpeed", "MoveSpd"),
    };

    internal static string Canonical(string name)
    {
        for (int i = 0; i < All.Length; i++)
        {
            if (string.Equals(name, All[i].Friendly, StringComparison.OrdinalIgnoreCase))
                return All[i].Internal;
        }
        return name;
    }
}

internal static class FilterParser
{
    internal readonly struct FilterError
    {
        public readonly int Line;
        public readonly string Text;
        public readonly string Message;

        public FilterError(int line, string text, string message)
        {
            Line = line;
            Text = text;
            Message = message;
        }

        public override string ToString() => $"line {Line}: {Message} — \"{Text}\"";
    }

    internal sealed class ParsedFilter
    {
        public LootFilter.LootRule[] Rules = Array.Empty<LootFilter.LootRule>();
        /// <summary>Item names or ids that always light up, whatever the rules say.</summary>
        public string[] Pinned = Array.Empty<string>();
        /// <summary>Item names or ids that never light up, whatever the rules say.</summary>
        public string[] Muted = Array.Empty<string>();
        /// <summary>Roll percentage that counts as a top roll. `Threshold 95` in the file.</summary>
        public int Threshold = LootFilter.DefaultThreshold;
        public FilterError[] Errors = Array.Empty<FilterError>();
    }

    /**
     * `#` starts a comment — EXCEPT when it starts a colour.
     *
     * Stripping every `#` to end of line makes the documented `Color #4ade80` decoration impossible to
     * write: the value vanishes and the line reports "Color needs #rrggbb", so the one decoration a
     * player is most likely to reach for is the one that cannot work. A `#` followed by exactly six hex
     * digits is a value, anything else opens a comment — and `Color #4ade80  # nicer green` still works,
     * because the scan continues past the colour.
     */
    private static readonly Regex CommentPattern = new(@"(^|\s)#(?![0-9a-fA-F]{6}\b).*$", RegexOptions.Compiled);
    private static readonly Regex ListPattern = new(@"""([^""]*)""|([^,]+)", RegexOptions.Compiled);
    private static readonly Regex ComparePattern = new(@"^([<>]=?|=)\s*(-?\d+)$", RegexOptions.Compiled);
    private static readonly Regex StatPattern = new(@"^(\S+)\s*([<>]=?|=)\s*(-?\d+(?:\.\d+)?)\s*(%?)$", RegexOptions.Compiled);
    private static readonly Regex ColorPattern = new(@"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private sealed class Block
    {
        public bool Hide;
        public string Name = "";
        public int StartLine;
        public readonly List<(int Line, string Text, int Indent)> Body = new();
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"'
            ? value.Substring(1, value.Length - 2)
            : value;

    /// <summary>Split `"a", "b", c` into three pieces, honouring quotes.</summary>
    private static List<string> SplitList(string value)
    {
        var pieces = new List<string>();
        foreach (Match match in ListPattern.Matches(value))
        {
            // The unquoted branch can swallow a following item's quotes (`, "b"` matches as ` "b"`), so
            // every piece is unquoted after trimming rather than trusting which branch matched.
            string piece = Unquote((match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim());
            if (piece.Length > 0) pieces.Add(piece);
        }
        return pieces;
    }

    public static ParsedFilter Parse(string text)
    {
        var errors = new List<FilterError>();
        var rules = new List<LootFilter.LootRule>();
        var pinned = new List<string>();
        var muted = new List<string>();
        var blocks = new List<Block>();
        int threshold = LootFilter.DefaultThreshold;

        Block? current = null;

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i].TrimEnd('\r');
            string stripped = CommentPattern.Replace(raw, "$1");
            string trimmed = stripped.Trim();
            if (trimmed.Length == 0) continue;

            bool indented = char.IsWhiteSpace(stripped[0]);
            int indent = stripped.Length - stripped.TrimStart().Length;
            string[] parts = Whitespace.Split(trimmed);
            string head = parts[0];
            string keyword = head.ToLowerInvariant();
            string remainder = parts.Length > 1 ? string.Join(" ", parts, 1, parts.Length - 1).Trim() : "";

            // One-line directives; valid at any indentation because they open no block.
            if (keyword is "alwaysshow" or "alwayskeep" or "alwayshide" or "alwaysmute")
            {
                List<string> ids = SplitList(remainder);
                if (ids.Count == 0) errors.Add(new FilterError(i + 1, trimmed, $"{head} needs at least one item name"));
                (keyword is "alwaysshow" or "alwayskeep" ? pinned : muted).AddRange(ids);
                current = null;
                continue;
            }

            if (keyword == "threshold")
            {
                // The same knob the overlay's editor has, so one file means one thing in both places.
                if (!int.TryParse(remainder, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wanted)
                    || wanted < 1 || wanted > 100)
                {
                    errors.Add(new FilterError(i + 1, trimmed, "Threshold needs a whole percentage from 1 to 100, e.g. \"Threshold 90\""));
                }
                else
                {
                    threshold = wanted;
                }
                current = null;
                continue;
            }

            if (!indented && keyword is "show" or "keep" or "hide" or "mute")
            {
                string name = Unquote(remainder);
                current = new Block
                {
                    Hide = keyword is "hide" or "mute",
                    Name = name.Length > 0 ? name : keyword,
                    StartLine = i + 1,
                };
                blocks.Add(current);
                continue;
            }

            if (current is null)
            {
                errors.Add(new FilterError(i + 1, trimmed, $"\"{head}\" is not inside a Show or Hide block"));
                continue;
            }
            current.Body.Add((i + 1, trimmed, indent));
        }

        foreach (Block block in blocks)
        {
            var blockErrors = new List<FilterError>();
            LootFilter.LootRule rule = ParseBlock(block, blockErrors);
            if (blockErrors.Count > 0)
            {
                // Whole-block rejection: a half-understood block matches more than it was told to.
                errors.AddRange(blockErrors);
                continue;
            }
            rules.Add(rule);
        }

        return new ParsedFilter
        {
            Rules = rules.ToArray(),
            Pinned = Dedupe(pinned),
            Muted = Dedupe(muted),
            Threshold = threshold,
            Errors = errors.ToArray(),
        };
    }

    private static string[] Dedupe(List<string> values)
    {
        var seen = new List<string>();
        foreach (string value in values)
        {
            bool duplicate = false;
            foreach (string other in seen)
            {
                if (string.Equals(other, value, StringComparison.OrdinalIgnoreCase)) { duplicate = true; break; }
            }
            if (!duplicate) seen.Add(value);
        }
        return seen.ToArray();
    }

    /**
     * Parse `>= 60` into an INCLUSIVE integer bound.
     *
     * Everything this language compares is a whole number — a roll percentage, a count of lines, a
     * refine level — so `> 2` is exactly `>= 3` and `< 35` is exactly `<= 34`, with no epsilon and no
     * argument about the boundary. The overlay nudges its strict bounds by 1e-9 because it compares
     * unrounded floats against a figure it draws in its own HUD; there is no such HUD here, and a
     * boundary the player cannot reproduce by hand is a boundary they cannot trust.
     */
    private static bool Bound(string remainder, out int? min, out int? max)
    {
        min = null;
        max = null;
        Match match = ComparePattern.Match(remainder);
        if (!match.Success) return false;
        string op = match.Groups[1].Value;
        int value = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        switch (op)
        {
            case "=": min = value; max = value; return true;
            case ">=": min = value; return true;
            case ">": min = value + 1; return true;
            case "<=": max = value; return true;
            default: max = value - 1; return true;
        }
    }

    private static LootFilter.StatCondition? ParseStat(
        int line,
        string text,
        string remainder,
        List<FilterError> errors)
    {
        Match match = StatPattern.Match(remainder);
        if (!match.Success)
        {
            errors.Add(new FilterError(line, text,
                "Stat needs e.g. \"Stat Agi >= 90%\" (roll quality) or \"Stat Agi >= 3\" (the printed value)"));
            return null;
        }

        string op = match.Groups[2].Value;
        if (op != ">=" && op != ">")
        {
            errors.Add(new FilterError(line, text,
                "Stat supports only >= and > (a maximum on one line is not a filter anyone wants yet)"));
            return null;
        }

        double value = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        int minimum = op == ">" ? (int)Math.Floor(value) + 1 : (int)Math.Ceiling(value);
        var condition = new LootFilter.StatCondition { Stat = StatAliases.Canonical(match.Groups[1].Value) };
        if (match.Groups[4].Value.Length > 0) condition.MinRollPct = minimum;
        else condition.MinValue = minimum;
        return condition;
    }

    private static LootFilter.LootRule ParseBlock(Block block, List<FilterError> errors)
    {
        var when = new LootFilter.LootCondition();
        var stats = new List<LootFilter.StatCondition>();
        var requiredStats = new List<LootFilter.StatCondition>();
        var anyOfStats = new List<LootFilter.StatCondition[]>();
        string color = block.Hide ? "#6b7a73" : "#4ade80";
        string label = "";
        int level = 0;
        int background = LootFilter.BackgroundBorder;
        bool border = true;
        string? sound = null;
        bool statModeExplicit = false;
        int? statMatchesLine = null;
        string statMatchesText = "";

        for (int bodyIndex = 0; bodyIndex < block.Body.Count; bodyIndex++)
        {
            (int line, string text, int indent) = block.Body[bodyIndex];
            string[] parts = Whitespace.Split(text);
            string head = parts[0];
            string keyword = head.ToLowerInvariant();
            string remainder = parts.Length > 1 ? string.Join(" ", parts, 1, parts.Length - 1).Trim() : "";

            if (keyword == "anyof")
            {
                if (remainder.Length > 0)
                {
                    errors.Add(new FilterError(line, text,
                        "AnyOf takes no value; indent Stat conditions beneath it"));
                }

                var group = new List<LootFilter.StatCondition>();
                while (bodyIndex + 1 < block.Body.Count && block.Body[bodyIndex + 1].Indent > indent)
                {
                    (int childLine, string childText, int _) = block.Body[++bodyIndex];
                    string[] childParts = Whitespace.Split(childText);
                    if (!string.Equals(childParts[0], "stat", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(new FilterError(childLine, childText,
                            "AnyOf currently accepts only Stat conditions"));
                        continue;
                    }

                    string childRemainder = childParts.Length > 1
                        ? string.Join(" ", childParts, 1, childParts.Length - 1).Trim()
                        : "";
                    LootFilter.StatCondition? condition = ParseStat(
                        childLine, childText, childRemainder, errors);
                    if (condition is not null) group.Add(condition);
                }

                if (group.Count == 0)
                {
                    errors.Add(new FilterError(line, text,
                        "AnyOf requires at least one indented Stat condition"));
                }
                else
                {
                    anyOfStats.Add(group.ToArray());
                }
                continue;
            }

            switch (keyword)
            {
                case "name":
                {
                    // `SplitList` and not `Unquote`: `Name Vampiric Fang Clip` is still one piece,
                    // because only a COMMA separates, so every filter written before this line took
                    // a list reads the same. A name with a comma in it was never expressible.
                    List<string> names = SplitList(remainder);
                    if (names.Count == 0) { errors.Add(new FilterError(line, text, "Name needs a value")); break; }
                    when.Names = names.ToArray();
                    break;
                }

                case "type":
                {
                    List<string> types = SplitList(remainder);
                    if (types.Count == 0) { errors.Add(new FilterError(line, text, "Type needs at least one item type")); break; }
                    when.Types = types.ToArray();
                    break;
                }

                case "stat":
                {
                    LootFilter.StatCondition? condition = ParseStat(line, text, remainder, errors);
                    if (condition is not null) stats.Add(condition);
                    break;
                }

                case "requirestat":
                {
                    LootFilter.StatCondition? condition = ParseStat(line, text, remainder, errors);
                    if (condition is not null) requiredStats.Add(condition);
                    break;
                }

                case "anystat":
                    if (statMatchesLine is not null)
                    {
                        errors.Add(new FilterError(
                            line,
                            text,
                            "AnyStat cannot be combined with StatMatches — StatMatches already defines how listed Stat lines are aggregated"));
                    }
                    else
                    {
                        when.StatsAll = false;
                        statModeExplicit = true;
                    }
                    break;

                case "allstats":
                    if (statMatchesLine is not null)
                    {
                        errors.Add(new FilterError(
                            line,
                            text,
                            "AllStats cannot be combined with StatMatches — StatMatches already defines how listed Stat lines are aggregated"));
                    }
                    else
                    {
                        when.StatsAll = true;
                        statModeExplicit = true;
                    }
                    break;

                case "statmatches":
                {
                    if (statModeExplicit)
                    {
                        errors.Add(new FilterError(
                            line,
                            text,
                            "StatMatches cannot be combined with AnyStat or AllStats"));
                        break;
                    }

                    if (!Bound(remainder, out int? min, out int? max))
                    {
                        errors.Add(new FilterError(
                            line,
                            text,
                            $"StatMatches needs a comparison like \">= 3\", got \"{remainder}\""));
                        break;
                    }

                    if (min is int minimum)
                    {
                        when.MinStatMatches = minimum;
                    }

                    if (max is int maximum)
                    {
                        when.MaxStatMatches = maximum;
                    }

                    statMatchesLine = line;
                    statMatchesText = text;
                    break;
                }

                case "toprolls":
                {
                    if (!Bound(remainder, out int? min, out int? max))
                    {
                        errors.Add(new FilterError(line, text, $"TopRolls needs a comparison like \">= 3\", got \"{remainder}\""));
                        break;
                    }
                    if (min is int minimum) when.MinTopRolls = minimum;
                    // `TopRolls < 1` means no line reaches its displayed cap, so the maximum floors
                    // at zero instead of going negative and matching nothing.
                    if (max is int maximum) when.MaxTopRolls = Math.Max(0, maximum);
                    break;
                }

                case "highrolls":
                {
                    if (!Bound(remainder, out int? min, out int? max))
                    {
                        errors.Add(new FilterError(line, text, $"HighRolls needs a comparison like \">= 3\", got \"{remainder}\""));
                        break;
                    }
                    if (min is int minimum) when.MinHighRolls = minimum;
                    if (max is int maximum) when.MaxHighRolls = Math.Max(0, maximum);
                    break;
                }

                // Legacy spelling remains accepted so existing filters keep their raw-percentage
                // behavior. Formatting and new documentation use the explicit name.
                case "avgroll":
                case "avgrollpct":
                {
                    if (!Bound(remainder, out int? min, out int? max))
                    {
                        errors.Add(new FilterError(line, text, $"AvgRollPct needs a comparison like \"< 35\", got \"{remainder}\""));
                        break;
                    }
                    if (min is int minimum) when.MinAvgRollPct = minimum;
                    if (max is int maximum) when.MaxAvgRollPct = maximum;
                    break;
                }

                case "refine":
                {
                    if (!Bound(remainder, out int? min, out int? _))
                    {
                        errors.Add(new FilterError(line, text, $"Refine needs a comparison like \">= 3\", got \"{remainder}\""));
                        break;
                    }
                    // Only a MINIMUM exists in the model. The overlay's parser silently drops `<`/`<=`
                    // here, which widens the block to every item — refuse instead, and say why.
                    if (min is null)
                    {
                        errors.Add(new FilterError(line, text, "Refine takes a minimum only (>=, > or =); a maximum refine is not expressible yet"));
                        break;
                    }
                    when.MinRefine = min;
                    break;
                }

                case "overroll": when.OverRoll = true; break;
                case "nooverroll": when.OverRoll = false; break;
                case "chaos": when.HasChaos = true; break;
                case "nochaos": when.HasChaos = false; break;
                case "favorite":
                case "favourite": when.Favorite = true; break;
                case "notfavorite":
                case "notfavourite": when.Favorite = false; break;

                // Conditions the language has but this build cannot answer. Named individually so the
                // message says which line to change and why, rather than "unknown condition".
                case "sharedstats":
                    errors.Add(new FilterError(line, text,
                        "SharedStats compares against the gear you are wearing, which this build does not read"));
                    break;
                case "unknown":
                case "known":
                    // Not "no catalog" any more — the mod reads the game's own. But that catalog is
                    // the same one the item came from, so "the catalog has not seen this" is a
                    // statement about the MOD, not about the item, and there is nothing useful to
                    // filter on. Refused with the reason rather than answered.
                    errors.Add(new FilterError(line, text,
                        $"{head} asks whether a reference catalog is missing this item. ValeLoot reads the game's OWN "
                      + $"catalog, so a miss would be a bug in the mod rather than a fact about the item — see "
                      + $"{ItemCatalog.ReferenceFileName} for everything it holds"));
                    break;
                case "verdict":
                    errors.Add(new FilterError(line, text,
                        "Verdict asks for an upgrade comparison against your worn gear, which this build does not do"));
                    break;

                case "color":
                case "colour":
                    if (!ColorPattern.IsMatch(remainder)) { errors.Add(new FilterError(line, text, "Color needs #rrggbb")); break; }
                    color = remainder.ToLowerInvariant();
                    break;

                case "tag":
                {
                    string wanted = Unquote(remainder);
                    label = wanted.Length > 12 ? wanted.Substring(0, 12) : wanted;
                    break;
                }

                case "highlight":
                {
                    int wanted = LootFilter.ParseLevel(remainder.ToLowerInvariant());
                    if (wanted == 0) { errors.Add(new FilterError(line, text, "Highlight needs one of: dot, mark, glow")); break; }
                    level = wanted;
                    break;
                }

                case "background":
                {
                    int wanted = LootFilter.ParseBackground(remainder.ToLowerInvariant());
                    if (wanted < 0)
                    {
                        errors.Add(new FilterError(line, text, "Background needs one of: border, fill, holo"));
                        break;
                    }
                    background = wanted;
                    break;
                }

                case "border":
                {
                    string wanted = remainder.ToLowerInvariant();
                    if (wanted == "on") border = true;
                    else if (wanted == "off") border = false;
                    else errors.Add(new FilterError(line, text, "Border needs one of: on, off"));
                    break;
                }

                // How `Highlight glow` was spelled before there were levels. Kept so a filter copied from
                // an older overlay install keeps parsing.
                case "flash": level = LootFilter.LevelGlow; break;

                case "sound":
                {
                    string name = Unquote(remainder);
                    // Vetted here and nowhere else: the name is resolved as a FILE inside the sounds
                    // directory, so a filter that could name `../../something` would turn the filter file
                    // into a way to reach the rest of the disk.
                    if (!IsPlainName(name))
                    {
                        errors.Add(new FilterError(line, text,
                            "Sound needs a plain name like \"chime\" — letters, digits, dot, dash, underscore, 40 characters at most"));
                        break;
                    }
                    sound = name;
                    break;
                }

                default:
                    errors.Add(new FilterError(line, text, $"unknown condition \"{head}\""));
                    break;
            }
        }

        if (requiredStats.Count > 0) when.RequiredStats = requiredStats.ToArray();
        if (stats.Count > 0) when.Stats = stats.ToArray();
        if (anyOfStats.Count > 0) when.AnyOfStats = anyOfStats.ToArray();

        if (statMatchesLine is int boundsLine && stats.Count > 0)
        {
            int? min = when.MinStatMatches;
            int? max = when.MaxStatMatches;
            string? message = null;

            if ((min is int minimum && minimum < 0) ||
                (max is int maximum && maximum < 0))
            {
                message = "StatMatches cannot be negative";
            }
            else if (min is int lower && max is int upper && lower > upper)
            {
                message = $"StatMatches minimum {lower} cannot exceed maximum {upper}";
            }
            else if (min is int required && required > stats.Count)
            {
                message = $"StatMatches cannot require {required} matches from only {stats.Count} Stat line(s)";
            }

            if (message is not null)
            {
                errors.Add(new FilterError(boundsLine, statMatchesText, message));
            }
        }

        if (statMatchesLine is int matchesLine && stats.Count == 0)
        {
            errors.Add(new FilterError(
                matchesLine,
                statMatchesText,
                "StatMatches requires at least one Stat line in the same block"));
        }

        /**
         * A `Hide` block with NO conditions claims every item in the bag and lights nothing — the whole
         * inventory goes dark and looks broken. It is reachable by deleting one line, so it has to be
         * typed out deliberately.
         */
        if (block.Hide && when.IsEmpty && !string.Equals(block.Name, "everything", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new FilterError(block.StartLine, $"Hide \"{block.Name}\"",
                "a Hide block with no conditions silences EVERYTHING — name it \"everything\" if you truly mean it"));
        }

        // Decoration a Hide block cannot honour. Silently accepting it means an author who asked for
        // a visible treatment gets darkness and no explanation, which is the hardest filter bug to see.
        if (block.Hide && (sound is not null || level != 0
            || background != LootFilter.BackgroundBorder || !border))
        {
            errors.Add(new FilterError(block.StartLine, $"Hide \"{block.Name}\"",
                "a Hide block draws nothing and plays nothing — remove its Highlight/Background/Border/Sound, or make it a Show block"));
        }

        (float r, float g, float b) = LootFilter.ParseColor(color);
        return new LootFilter.LootRule
        {
            Name = block.Name,
            Color = color,
            R = r,
            G = g,
            B = b,
            Label = label,
            Level = level == 0 ? LootFilter.LevelDot : level,
            Background = background,
            Border = border,
            Sound = sound,
            Mute = block.Hide,
            When = when,
            Line = block.StartLine,
        };
    }

    /// <summary>The sounds directory owns this rule — see <see cref="LootSound.IsPlainName"/>. It is
    /// applied HERE as well so a bad name is a load-time error with a line number, rather than a rule
    /// that parses fine and is silent for ever.</summary>
    private static bool IsPlainName(string value) => LootSound.IsPlainName(value);
}
