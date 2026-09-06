using Login38.Aux.Game;
using Login38.Aux.Hunt;
using Login38.Aux.Runtime;
using Login38.Aux.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Login38.App.Services;

/// <summary>
/// The helper features belonging to one running game.
/// </summary>
/// <remarks>
/// <para>
/// One of these per game, not one per launcher. Everything inside holds per-game state —
/// which character is playing, whether a toggle has already complained about an address,
/// what the player has switched on — and the launcher can be driving several clients at
/// once. The reference kept all of it in process-wide statics, so a second client got a
/// helper that thought it had already done its work.
/// </para>
/// <para>
/// A container scope rather than hand-built objects, so a new task or toggle is registered
/// in one place and appears here without this file changing.
/// </para>
/// </remarks>
public sealed class AuxSession : IDisposable
{
    private readonly IServiceScope _scope;

    /// <param name="scopes">The launcher's container, to take one game's scope out of.</param>
    public AuxSession(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scope = scopes.CreateScope();
        Host = _scope.ServiceProvider.GetRequiredService<AuxHost>();
        Settings = _scope.ServiceProvider.GetRequiredService<AuxSettingsSource>();
        Inventory = _scope.ServiceProvider.GetRequiredService<InventoryWatch>();
        Spells = _scope.ServiceProvider.GetRequiredService<SpellWatch>();
        Hunting = _scope.ServiceProvider.GetRequiredService<HuntSwitch>();
        Offer = _scope.ServiceProvider.GetRequiredService<HuntOffer>();
        Timers = _scope.ServiceProvider.GetRequiredService<TimerTask>();
        Keys = _scope.ServiceProvider.GetRequiredService<HelperKeyTask>();
    }

    /// <summary>The loop the helper features run on.</summary>
    public AuxHost Host { get; }

    /// <summary>
    /// What this game's helper is doing, for the window to read and write.
    /// </summary>
    /// <remarks>
    /// Per game, so the helper window shows and edits the settings of whichever client is
    /// selected rather than one set shared by all of them.
    /// </remarks>
    public AuxSettingsSource Settings { get; }

    /// <summary>What is in this character's bag, for the window's dropdowns.</summary>
    public InventoryWatch Inventory { get; }

    /// <summary>What this character has learned, for the rotation's dropdown.</summary>
    /// <remarks>
    /// Per game like the rest. Two clients are two characters with two spell books, and a
    /// list shared between them offers each of them the other one's skills.
    /// </remarks>
    public SpellWatch Spells { get; }

    /// <summary>
    /// How a task says it has turned the hunt off, for the window that draws the switch.
    /// </summary>
    public HuntSwitch Hunting { get; }

    /// <summary>
    /// Whether this game's server list offers hunting, for the loop rather than the window.
    /// </summary>
    public HuntOffer Offer { get; }

    /// <summary>
    /// The timers, so the window's per-row button can start one of them counting again.
    /// </summary>
    /// <remarks>
    /// The task itself rather than a signal to it. The reference passed a table of atomic
    /// counters to the window and had the timer thread watch for one of them to move,
    /// which is a message queue built out of integers — and it held the table weakly, so
    /// the button could silently do nothing.
    /// </remarks>
    public TimerTask Timers { get; }

    /// <summary>
    /// The two keys this game answers to, so the launcher can hear about Home.
    /// </summary>
    /// <remarks>
    /// Per game for the same reason as the rest: a key belongs to whichever client is in
    /// front, and a launcher driving two of them shows the settings of that one. Insert is
    /// handled inside the scope and does not come out here — nothing in the launcher needs
    /// to know whether the helper is running.
    /// </remarks>
    public HelperKeyTask Keys { get; }

    public void Dispose() => _scope.Dispose();
}
