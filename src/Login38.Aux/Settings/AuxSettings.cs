using System.Text.Json.Serialization;
using Login38.Aux.Hunt;

namespace Login38.Aux.Settings;

/// <summary>One rule for drinking: below this much, use that.</summary>
public sealed class PotionRow
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// The level to act below — a percentage or an absolute amount, depending on
    /// <see cref="AuxSettings.PotionUsePercent"/>.
    /// </summary>
    [JsonPropertyName("threshold")]
    public uint Threshold { get; set; }

    /// <summary>What to use, by the name the player sees.</summary>
    [JsonPropertyName("item")]
    public string Item { get; set; } = string.Empty;
}

/// <summary>
/// A rule for topping up mana only while it is safe to.
/// </summary>
/// <remarks>
/// Mana potions in this game cost hit points, so drinking one at the wrong moment is worse
/// than being out of mana. Both conditions have to hold.
/// </remarks>
public sealed class ManaWhenSafeRule
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Hit points must be at least this, as a percentage.</summary>
    [JsonPropertyName("hp_lower")]
    public uint HitPointsAtLeast { get; set; }

    /// <summary>And mana at most this, as a percentage.</summary>
    [JsonPropertyName("mp_upper")]
    public uint ManaAtMost { get; set; }

    [JsonPropertyName("item")]
    public string Item { get; set; } = string.Empty;
}

/// <summary>A command bound to one of the function keys.</summary>
public sealed class FunctionKeyMacro
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;
}

/// <summary>A command run on a fixed interval.</summary>
public sealed class TimerRow
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("interval_sec")]
    public uint IntervalSeconds { get; set; } = 5;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;
}

/// <summary>The switches that do not belong to a list.</summary>
public sealed class MiscToggles
{
    /// <summary>Keep the world lit as if it were noon.</summary>
    [JsonPropertyName("all_day")]
    public bool AllDay { get; set; }

    /// <summary>Keep breathing underwater.</summary>
    [JsonPropertyName("underwater_pump")]
    public bool UnderwaterPump { get; set; }

    /// <summary>Let the client idle instead of spinning a core.</summary>
    [JsonPropertyName("low_cpu")]
    public bool LowCpu { get; set; }

    /// <summary>Colour a monster's name by how dangerous it is.</summary>
    [JsonPropertyName("monster_level_color")]
    public bool MonsterLevelColour { get; set; }

    /// <summary>Show the time of day.</summary>
    [JsonPropertyName("show_clock")]
    public bool ShowClock { get; set; }

    /// <summary>Show damage numbers.</summary>
    [JsonPropertyName("show_attack_dmg")]
    public bool ShowAttackDamage { get; set; }

    /// <summary>Show them at the target's feet rather than over its head.</summary>
    [JsonPropertyName("damage_at_feet")]
    public bool DamageAtFeet { get; set; }

    /// <summary>Name what goes into the bag, in the bottom-left corner.</summary>
    [JsonPropertyName("pickup_toast")]
    public bool PickupToast { get; set; }

    /// <summary>Show what a kill was worth, drifting up the middle of the screen.</summary>
    [JsonPropertyName("gain_drift")]
    public bool GainDrift { get; set; }
}

/// <summary>
/// Everything a player has set up for one character.
/// </summary>
/// <remarks>
/// <para>
/// Read when the character enters the world and written when they leave it. The helper
/// loop reads this on every pass, so a change made in the window takes effect on the next
/// one — there is nothing to restart and nothing to re-attach.
/// </para>
/// <para>
/// The property names on the wire are the ones the reference wrote, so a player upgrading
/// keeps the setup they built. Several of these hold a lot of manual work.
/// </para>
/// </remarks>
public sealed class AuxSettings
{
    /// <summary>How many drinking rules there are.</summary>
    public const int PotionRows = 7;

    /// <summary>How many function keys can carry a macro.</summary>
    public const int FunctionKeyMacros = 4;

    /// <summary>How many timers there are.</summary>
    public const int Timers = 6;

    /// <summary>Which character these belong to.</summary>
    [JsonPropertyName("current_profile")]
    public string Profile { get; set; } = string.Empty;

    [JsonPropertyName("potion_rows")]
    public PotionRow[] Potions { get; set; } = Fill<PotionRow>(PotionRows);

    [JsonPropertyName("mp_when_safe")]
    public ManaWhenSafeRule ManaWhenSafe { get; set; } = new();

    /// <summary>Whether a drinking threshold is a percentage rather than an amount.</summary>
    [JsonPropertyName("potion_use_percent")]
    public bool PotionUsePercent { get; set; }

    /// <summary>Whether the drinking tab lists what is in the bag.</summary>
    [JsonPropertyName("potion_show_inventory")]
    public bool PotionShowInventory { get; set; }

    [JsonPropertyName("buff_enabled")]
    public bool HelperEnabled { get; set; }

    /// <summary>The spells and items to keep up.</summary>
    [JsonPropertyName("buff_items")]
    public List<HelperEntry> HelperEntries { get; set; } = [];

    /// <summary>The same, for the entries built from what is in the bag.</summary>
    [JsonPropertyName("buff_inventory_items")]
    public List<HelperEntry> HelperInventoryEntries { get; set; } = [];

    [JsonPropertyName("status_show_exp")]
    public bool ShowExperience { get; set; }

    [JsonPropertyName("status_whetstone")]
    public bool KeepWeaponSharp { get; set; }

    [JsonPropertyName("status_eat_meat")]
    public bool EatWhenHungry { get; set; }

    [JsonPropertyName("status_transform_enabled")]
    public bool TransformEnabled { get; set; }

    [JsonPropertyName("status_transform_item")]
    public string TransformItem { get; set; } = string.Empty;

    /// <summary>When to transform, as the player wrote it.</summary>
    [JsonPropertyName("status_transform_cond")]
    public string TransformCondition { get; set; } = string.Empty;

    [JsonPropertyName("status_antidote_enabled")]
    public bool AntidoteEnabled { get; set; }

    [JsonPropertyName("status_antidote_item")]
    public string AntidoteItem { get; set; } = string.Empty;

    [JsonPropertyName("fkey_macros")]
    public FunctionKeyMacro[] Macros { get; set; } = Fill<FunctionKeyMacro>(FunctionKeyMacros);

    [JsonPropertyName("delete_enabled")]
    public bool DeleteEnabled { get; set; }

    /// <summary>Items to destroy outright.</summary>
    [JsonPropertyName("delete_list")]
    public List<string> DeleteList { get; set; } = [];

    /// <summary>Items to dissolve, which needs a solvent in the bag.</summary>
    [JsonPropertyName("dissolve_list")]
    public List<string> DissolveList { get; set; } = [];

    [JsonPropertyName("shout_enabled")]
    public bool ShoutEnabled { get; set; }

    [JsonPropertyName("shout_interval_sec")]
    public uint ShoutIntervalSeconds { get; set; }

    /// <summary>Sent in turn, one per interval.</summary>
    [JsonPropertyName("shout_messages")]
    public List<string> ShoutMessages { get; set; } = [];

    [JsonPropertyName("misc")]
    public MiscToggles Misc { get; set; } = new();

    /// <summary>What the hunt has been asked to do.</summary>
    [JsonPropertyName("hunt")]
    public HuntSettings Hunt { get; set; } = new();

    [JsonPropertyName("timer_master_enabled")]
    public bool TimersEnabled { get; set; }

    [JsonPropertyName("timer_rows")]
    public TimerRow[] TimerRows { get; set; } = Fill<TimerRow>(Timers);

    /// <summary>
    /// Fills in anything a hand-edited or older file left out.
    /// </summary>
    /// <remarks>
    /// The fixed-size arrays are indexed by the window, so a file written by a version with
    /// fewer rows — or by somebody editing it in a text editor — would otherwise be read
    /// past its end the first time the window is opened.
    /// </remarks>
    public AuxSettings Normalise()
    {
        Potions = Resize(Potions, PotionRows);
        Macros = Resize(Macros, FunctionKeyMacros);
        TimerRows = Resize(TimerRows, Timers);

        ManaWhenSafe ??= new ManaWhenSafeRule();
        Misc ??= new MiscToggles();
        Hunt = (Hunt ?? new HuntSettings()).Normalise();
        HelperEntries ??= [];
        HelperInventoryEntries ??= [];
        DeleteList ??= [];
        DissolveList ??= [];
        ShoutMessages ??= [];

        return this;
    }

    private static T[] Fill<T>(int count) where T : new()
    {
        var rows = new T[count];

        for (var i = 0; i < count; i++)
        {
            rows[i] = new T();
        }

        return rows;
    }

    private static T[] Resize<T>(T[]? rows, int count) where T : new()
    {
        if (rows is null)
        {
            return Fill<T>(count);
        }

        if (rows.Length == count && Array.TrueForAll(rows, row => row is not null))
        {
            return rows;
        }

        var sized = Fill<T>(count);
        Array.Copy(rows, sized, Math.Min(rows.Length, count));

        for (var i = 0; i < count; i++)
        {
            sized[i] ??= new T();
        }

        return sized;
    }
}
