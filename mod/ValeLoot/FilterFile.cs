using System;
using System.IO;
using System.Text;

namespace ValeLoot;

/// <summary>
/// The player's rule file on disk, and the only thing the mod needs in order to work.
///
/// `BepInEx/config/valeloot-filter.txt`. It is written with a worked example the first time the mod
/// runs, because an empty file is indistinguishable from a broken mod: the first launch should light
/// something up so the player knows the plumbing is sound before they start writing rules.
///
/// ## Reload without restarting the game
///
/// Editing a filter is a loop — change a line, look at the bag, change it again — and a loop that
/// costs a relaunch and a login is a loop nobody runs. So the file is watched, and a change is picked
/// up on the next inventory repaint.
///
/// The watcher deliberately does NOT reload in place. `FileSystemWatcher` raises on a thread pool
/// thread, and half of what a reload touches is read by il2cpp UI code on Unity's main thread; parsing
/// off-thread and then swapping a reference would be defensible, but an editor that saves by
/// write-truncate-rename raises two or three events per save, and re-parsing three times per keystroke
/// batch is work done for nothing. Instead the watcher only sets a flag, and the paint pass — already
/// on the main thread, already the moment the result becomes visible — does the reload. One reload per
/// save, no locks, no races, and the reload lands exactly when the player looks.
/// </summary>
internal static class FilterFile
{
    public const string FileName = "valeloot-filter.txt";

    private static string _path = "";
    private static FileSystemWatcher? _watcher;
    private static Action<string> _log = _ => { };

    /// <summary>Set by the watcher, cleared by the paint pass. Volatile: written off the main thread.</summary>
    private static volatile bool _dirty;

    private static FilterParser.ParsedFilter _filter = new();

    /// <summary>The live rule list. Replaced wholesale on reload; never mutated in place.</summary>
    public static FilterParser.ParsedFilter Current => _filter;

    public static int Reloads;
    public static string LastLoadSummary = "not loaded";

    public static string Path => _path;

    /// <summary>
    /// Find (or create) the filter file, load it, and start watching. Returns false only if the file
    /// could not be created — in which case the mod runs on an empty rule list and says so.
    /// </summary>
    public static bool Install(string configDirectory, Action<string> log)
    {
        _log = log;
        _path = System.IO.Path.Combine(configDirectory, FileName);

        try
        {
            Directory.CreateDirectory(configDirectory);
            if (!File.Exists(_path))
            {
                File.WriteAllText(_path, DefaultFilter, new UTF8Encoding(false));
                log($"wrote a starter filter to {_path}");
            }
        }
        catch (Exception e)
        {
            log($"could not create {_path} — {e.Message}. No rules will load this session.");
            return false;
        }

        Load();

        try
        {
            _watcher = new FileSystemWatcher(configDirectory, FileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += (_, _) => _dirty = true;
            _watcher.Created += (_, _) => _dirty = true;
            _watcher.Renamed += (_, _) => _dirty = true;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e)
        {
            // Not fatal. Without a watcher the file still loads at boot and on an explicit reload; the
            // player just has to ask for it. Saying so beats a silently dead edit loop.
            log($"filter file will not auto-reload — {e.Message}. Edits need a game restart or a `reload` command.");
        }
        return true;
    }

    /// <summary>
    /// Called at the top of a paint pass. Cheap when nothing changed: one volatile read.
    ///
    /// Returns true when the rules were replaced, so the caller can drop anything it had cached about
    /// what those rules decided.
    /// </summary>
    public static bool ReloadIfChanged()
    {
        if (!_dirty) return false;
        _dirty = false;
        Load();
        return true;
    }

    /// <summary>Parse the file and swap it in. Never throws: a filter you cannot read is not a crash.</summary>
    public static void Load()
    {
        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception e)
        {
            // The commonest cause is the editor still holding the file open mid-save. Keeping the last
            // good rules beats going dark, and the next save raises another event.
            _log($"could not read {FileName} — {e.Message}. Keeping the rules already loaded.");
            return;
        }

        FilterParser.ParsedFilter parsed = FilterParser.Parse(text);
        _filter = parsed;
        Reloads++;

        int sounds = 0;
        foreach (LootFilter.LootRule rule in parsed.Rules) if (rule.Sound is not null) sounds++;

        LastLoadSummary =
            $"{parsed.Rules.Length} rule(s), {parsed.Pinned.Length} always-show, {parsed.Muted.Length} always-hide, "
          + $"{sounds} with sound, {parsed.Errors.Length} error(s)";
        _log($"filter loaded: {LastLoadSummary}");

        /**
         * Errors are logged one per line, with the line number, and they are logged as ERRORS.
         *
         * A rejected block is a rule the player believes is running. Burying that in an info line, or
         * summarising it as a count, produces the exact failure this whole design is trying to avoid: a
         * filter that looks installed and quietly does less than it says.
         */
        foreach (FilterParser.FilterError error in parsed.Errors) _log($"filter ERROR {error}");

        if (parsed.Rules.Length == 0 && parsed.Errors.Length == 0)
        {
            _log($"filter has no rules — nothing will be highlighted. Edit {_path} and save; it reloads by itself.");
        }
    }

    public static void Uninstall()
    {
        try { _watcher?.Dispose(); } catch { /* teardown must never throw */ }
        _watcher = null;
    }

    public static string StatusJson()
        => "{\"kind\":\"filter\",\"path\":" + JsonString(_path)
         + ",\"rules\":" + _filter.Rules.Length
         + ",\"pinned\":" + _filter.Pinned.Length
         + ",\"muted\":" + _filter.Muted.Length
         + ",\"errors\":" + _filter.Errors.Length
         + ",\"reloads\":" + Reloads
         + "}";

    private static string JsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (char c in value)
        {
            if (c == '"' || c == '\\') builder.Append('\\').Append(c);
            else if (c < ' ') builder.Append(' ');
            else builder.Append(c);
        }
        return builder.Append('"').ToString();
    }

    /**
     * The file a fresh install gets.
     *
     * It is a complete general-purpose filter, not a blank page with instructions: a fresh install
     * immediately demonstrates every display tier against the player's real bag. Twenty-eight ordered
     * rules preserve focused ATK/MAGIC/DEF/MDEF classification without shipping the former 94-rule
     * wall of near-duplicates.
     */
    private const string DefaultFilter = @"# =====================================================================
# VALELOOT — CATEGORY GENERAL DEFAULT
#
# A focused 28-rule cut of the former 94-rule consolidated filter.
# FIRST MATCH WINS. Keep the tier bands and GENERAL fallbacks in order.
#
# Display language:
#   4+ top  rotating holo background, no frame, glow + alert
#   3 top   flat full-card background, no frame, glow + chime
#   2 top   flat full-card background, no frame, mark
#   1 top   coloured frame only, quiet dot
# =====================================================================

Threshold 90

# Exceptional items beat every category.
Show ""Chaos over-roll""
    OverRoll
    Tag        OVER
    Color      #ffd166
    Highlight  glow
    Background holo
    Border     off
    Sound      alert

Show ""Favourites""
    Favorite
    Tag        FAV
    Color      #facc15
    Highlight  glow
    Background fill
    Border     off

# Artifacts require a top +3 primary attribute, then split by total top rolls.
Show ""Artifact — perfect""
    Type        Artifact
    TopRolls   >= 3
    AnyOf
        Stat    Str >= 3
        Stat    Vit >= 3
        Stat    Dex >= 3
        Stat    Agi >= 3
        Stat    Int >= 3
        Stat    Luk >= 3
    Tag         ART-P
    Color       #f4d35e
    Highlight   glow
    Background  holo
    Border      off
    Sound       alert

Show ""Artifact — semi""
    Type        Artifact
    TopRolls   >= 2
    AnyOf
        Stat    Str >= 3
        Stat    Vit >= 3
        Stat    Dex >= 3
        Stat    Agi >= 3
        Stat    Int >= 3
        Stat    Luk >= 3
    Tag         ART-S
    Color       #d9a441
    Highlight   mark
    Background  fill
    Border      off

Hide ""Artifact trash""
    Type        Artifact

# A required physical group plus a required magic group replaces dozens of pair rules.
Hide ""Mixed physical + magic""
    AnyOf
        Stat    AtkMult >= 2
        Stat    DamageMelee >= 5
        Stat    Atk >= 3
        Stat    Crit >= 5
        Stat    Hit >= 10
        Stat    CritDamage >= 10
        Stat    Chain >= 1
        Stat    DoubleAttack >= 20
        Stat    DamageRanged >= 5
        Stat    Range >= 1
    AnyOf
        Stat    DamageMagic >= 5
        Stat    MatkMult >= 2
        Stat    CastSpd >= 10
        Stat    Matk >= 3
        Stat    Healing >= 10
        Stat    CastRange >= 1

# 4+ TOP — rotating, borderless, unmistakable.
Show ""ATK — 4+ top""
    Stat        AtkMult >= 2
    Stat        DamageMelee >= 5
    Stat        Atk >= 3
    Stat        Crit >= 5
    Stat        Hit >= 10
    Stat        CritDamage >= 10
    Stat        Chain >= 1
    Stat        DoubleAttack >= 20
    Stat        DamageRanged >= 5
    Stat        Range >= 1
    StatMatches >= 2
    TopRolls   >= 4
    Tag         ATK-4
    Color       #ff5a5f
    Highlight   glow
    Background  holo
    Border      off
    Sound       alert

Show ""MAGIC — 4+ top""
    Stat        DamageMagic >= 5
    Stat        MatkMult >= 2
    Stat        CastSpd >= 10
    Stat        Matk >= 3
    Stat        Healing >= 10
    Stat        CastRange >= 1
    StatMatches >= 2
    TopRolls   >= 4
    Tag         MAG-4
    Color       #2fb8ff
    Highlight   glow
    Background  holo
    Border      off
    Sound       alert

Show ""DEF — 4+ top""
    Stat        Def >= 5
    Stat        DefMult >= 5
    Stat        Flee >= 15
    StatMatches >= 2
    TopRolls   >= 4
    Tag         DEF-4
    Color       #52d273
    Highlight   glow
    Background  holo
    Border      off
    Sound       alert

Show ""MDEF — 4+ top""
    Stat        Mdef >= 5
    Stat        MdefMult >= 5
    StatMatches >= 2
    TopRolls   >= 4
    Tag         MDF-4
    Color       #a86cff
    Highlight   glow
    Background  holo
    Border      off
    Sound       alert

Show ""GENERAL — 4+ top""
    TopRolls   >= 4
    Tag         GEN-4
    Color       #e6efe9
    Highlight   glow
    Background  holo
    Border      off
    Sound       alert

# 3 TOP — flat full-card colour, no frame.
Show ""ATK — 3 top""
    Stat        AtkMult >= 2
    Stat        DamageMelee >= 5
    Stat        Atk >= 3
    Stat        Crit >= 5
    Stat        Hit >= 10
    Stat        CritDamage >= 10
    Stat        Chain >= 1
    Stat        DoubleAttack >= 20
    Stat        DamageRanged >= 5
    Stat        Range >= 1
    StatMatches >= 2
    TopRolls   >= 3
    Tag         ATK-3
    Color       #ff5a5f
    Highlight   glow
    Background  fill
    Border      off
    Sound       chime

Show ""MAGIC — 3 top""
    Stat        DamageMagic >= 5
    Stat        MatkMult >= 2
    Stat        CastSpd >= 10
    Stat        Matk >= 3
    Stat        Healing >= 10
    Stat        CastRange >= 1
    StatMatches >= 2
    TopRolls   >= 3
    Tag         MAG-3
    Color       #2fb8ff
    Highlight   glow
    Background  fill
    Border      off
    Sound       chime

Show ""DEF — 3 top""
    Stat        Def >= 5
    Stat        DefMult >= 5
    Stat        Flee >= 15
    StatMatches >= 2
    TopRolls   >= 3
    Tag         DEF-3
    Color       #52d273
    Highlight   glow
    Background  fill
    Border      off
    Sound       chime

Show ""MDEF — 3 top""
    Stat        Mdef >= 5
    Stat        MdefMult >= 5
    StatMatches >= 2
    TopRolls   >= 3
    Tag         MDF-3
    Color       #a86cff
    Highlight   glow
    Background  fill
    Border      off
    Sound       chime

Show ""GENERAL — 3 top""
    TopRolls   >= 3
    Tag         GEN-3
    Color       #c7d0dc
    Highlight   glow
    Background  fill
    Border      off
    Sound       chime

# 2 TOP — flat full-card colour, quieter mark.
Show ""ATK — 2 top""
    Stat        AtkMult >= 2
    Stat        DamageMelee >= 5
    Stat        Atk >= 3
    Stat        Crit >= 5
    Stat        Hit >= 10
    Stat        CritDamage >= 10
    Stat        Chain >= 1
    Stat        DoubleAttack >= 20
    Stat        DamageRanged >= 5
    Stat        Range >= 1
    StatMatches >= 2
    TopRolls   >= 2
    Tag         ATK-2
    Color       #ff5a5f
    Highlight   mark
    Background  fill
    Border      off

Show ""MAGIC — 2 top""
    Stat        DamageMagic >= 5
    Stat        MatkMult >= 2
    Stat        CastSpd >= 10
    Stat        Matk >= 3
    Stat        Healing >= 10
    Stat        CastRange >= 1
    StatMatches >= 2
    TopRolls   >= 2
    Tag         MAG-2
    Color       #2fb8ff
    Highlight   mark
    Background  fill
    Border      off

Show ""DEF — 2 top""
    Stat        Def >= 5
    Stat        DefMult >= 5
    Stat        Flee >= 15
    StatMatches >= 2
    TopRolls   >= 2
    Tag         DEF-2
    Color       #52d273
    Highlight   mark
    Background  fill
    Border      off

Show ""MDEF — 2 top""
    Stat        Mdef >= 5
    Stat        MdefMult >= 5
    StatMatches >= 2
    TopRolls   >= 2
    Tag         MDF-2
    Color       #a86cff
    Highlight   mark
    Background  fill
    Border      off

Show ""GENERAL — 2 top""
    TopRolls   >= 2
    Tag         GEN-2
    Color       #9aa8a0
    Highlight   mark
    Background  fill
    Border      off

# 1 TOP — quiet frame-only potential. Category rules precede GENERAL.
Show ""ATK — 1 top""
    Stat        AtkMult >= 2
    Stat        DamageMelee >= 5
    Stat        Atk >= 3
    Stat        Crit >= 5
    Stat        Hit >= 10
    Stat        CritDamage >= 10
    Stat        Chain >= 1
    Stat        DoubleAttack >= 20
    Stat        DamageRanged >= 5
    Stat        Range >= 1
    StatMatches >= 1
    TopRolls   >= 1
    Tag         ATK-1
    Color       #ff5a5f

Show ""MAGIC — 1 top""
    Stat        DamageMagic >= 5
    Stat        MatkMult >= 2
    Stat        CastSpd >= 10
    Stat        Matk >= 3
    Stat        Healing >= 10
    Stat        CastRange >= 1
    StatMatches >= 1
    TopRolls   >= 1
    Tag         MAG-1
    Color       #2fb8ff

Show ""DEF — 1 top""
    Stat        Def >= 5
    Stat        DefMult >= 5
    Stat        Flee >= 15
    StatMatches >= 1
    TopRolls   >= 1
    Tag         DEF-1
    Color       #52d273

Show ""MDEF — 1 top""
    Stat        Mdef >= 5
    Stat        MdefMult >= 5
    StatMatches >= 1
    TopRolls   >= 1
    Tag         MDF-1
    Color       #a86cff

Show ""GENERAL — 1 top""
    TopRolls   >= 1
    Tag         GEN-1
    Color       #7f8b85

# Work already invested remains visible even when no line is top-rolled.
Show ""Refined work""
    Refine     >= 5
    Tag         +5
    Color       #ff9f6b
    Highlight   mark
    Background  fill
    Border      off

Hide ""everything""
";
}
