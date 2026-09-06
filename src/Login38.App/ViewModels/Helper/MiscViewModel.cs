using CommunityToolkit.Mvvm.ComponentModel;
using Login38.Aux.Settings;

namespace Login38.App.ViewModels.Helper;

/// <summary>
/// The switches that do not belong to a list.
/// </summary>
/// <remarks>
/// Every one of these reaches into the running client — a byte table rewritten, a branch
/// redirected, a detour installed. They are applied by the toggle task on its next pass
/// rather than from here, so a switch moved before a game is up is simply a switch that is
/// already on when one starts. The reference wrote each patch from the window's own thread
/// as the switch moved, which is why it needed a game handle to open a settings window.
/// </remarks>
public sealed partial class MiscViewModel : ObservableObject
{
    /// <summary>Keep the world lit as if it were noon.</summary>
    [ObservableProperty]
    private bool _allDay;

    /// <summary>Keep breathing underwater.</summary>
    [ObservableProperty]
    private bool _underwaterPump;

    /// <summary>Let the client idle instead of spinning a core.</summary>
    [ObservableProperty]
    private bool _lowCpu;

    /// <summary>Colour a monster's name by how dangerous it is.</summary>
    [ObservableProperty]
    private bool _monsterLevelColour;

    /// <summary>Show the time of day.</summary>
    [ObservableProperty]
    private bool _showClock;

    /// <summary>Show damage numbers.</summary>
    [ObservableProperty]
    private bool _showAttackDamage;

    /// <summary>Show them at the target's feet rather than over its head.</summary>
    [ObservableProperty]
    private bool _damageAtFeet;

    /// <summary>Name what goes into the bag, in the corner of the screen.</summary>
    [ObservableProperty]
    private bool _pickupToast;

    /// <summary>Show what a kill was worth, drifting up the middle of the screen.</summary>
    [ObservableProperty]
    private bool _gainDrift;

    /// <summary>Reads the switches out of the settings.</summary>
    public void Load(MiscToggles misc)
    {
        ArgumentNullException.ThrowIfNull(misc);

        AllDay = misc.AllDay;
        UnderwaterPump = misc.UnderwaterPump;
        LowCpu = misc.LowCpu;
        MonsterLevelColour = misc.MonsterLevelColour;
        ShowClock = misc.ShowClock;
        ShowAttackDamage = misc.ShowAttackDamage;
        DamageAtFeet = misc.DamageAtFeet;
        PickupToast = misc.PickupToast;
        GainDrift = misc.GainDrift;
    }

    /// <summary>Writes them back.</summary>
    public MiscToggles ToToggles() => new()
    {
        AllDay = AllDay,
        UnderwaterPump = UnderwaterPump,
        LowCpu = LowCpu,
        MonsterLevelColour = MonsterLevelColour,
        ShowClock = ShowClock,
        ShowAttackDamage = ShowAttackDamage,
        DamageAtFeet = DamageAtFeet,
        PickupToast = PickupToast,
        GainDrift = GainDrift,
    };
}
