using System.Diagnostics;
using Login38.Aux.Actions;
using Login38.Aux.Game;
using Login38.Aux.Runtime;
using Login38.Interop;
using Microsoft.Extensions.Logging;

namespace Login38.Aux.Hunt;

/// <summary>
/// Picks something to fight and lets the client do the fighting.
/// </summary>
/// <remarks>
/// <para>
/// The whole of this task is choosing. Once a monster is chosen it writes five globals and
/// makes one call, and then has nothing to do until the target changes: the client walks
/// there, closes, swings at its own weapon interval, and tears the chain down when the
/// thing dies. Collision, stepping, heading and cooldown never leave it.
/// </para>
/// <para>
/// That is the entire difference from the reference, which drove all of it from outside —
/// its own A* over parsed map files, its own click points fed to the walk engine every
/// pass, its own cooldown table. Two engines steering one character is what made it shake.
/// </para>
/// <para>
/// Nothing here drinks, buffs, cures or picks anything up. <c>PotionTask</c>,
/// <c>HelperTask</c>, <c>StatusTask</c> and <c>InventoryTask</c> already do those and run
/// on this same loop, so a hunt that did them too would be a second helper racing the
/// first to send the same potion.
/// </para>
/// </remarks>
public sealed class HuntTask : IAuxTask, IAuxTaskShutdown
{
    /// <summary>
    /// How long the client may do nothing before the walk is started again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Long enough that an ordinary pause between steps is not one — the client walks a
    /// tile in a few hundred milliseconds — and short enough that a chase which has quietly
    /// stopped is picked up before the monster is gone. It is not a timer: the restart only
    /// happens if the client's own scheduler agrees nothing is running.
    /// </para>
    /// <para>
    /// It is also the slow way round, and only the fallback. When the client says outright
    /// that its attack chain has given up there is nothing to wait for and this is skipped —
    /// see <see cref="AttackChain.Running"/>. Waiting the full second there is what a bow
    /// standing still for a second after every step its target took actually was.
    /// </para>
    /// </remarks>
    /// <summary>How far a target may move in one pass and still have walked there.</summary>
    /// <remarks>In tiles, and the grid is two columns to one of them across.</remarks>
    private const int Blink = 3;

    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the client may do literally nothing before its gates are forced open.
    /// </summary>
    /// <remarks>
    /// Longer than any stall, because a stall is about one monster and this is about the
    /// client. Ten seconds of neither moving nor swinging, with a target held the whole
    /// time, is not a bad target — it is a flag nobody is left alive to clear.
    /// </remarks>
    private static readonly TimeSpan Frozen = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the character may stand over one target with nothing to show for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last resort, and the only one that catches a server which is refusing silently.
    /// Nothing the launcher can read says "that attack was thrown away": the client starts
    /// the blow, plays the animation, pushes its cooldown forward and waits, and the server
    /// drops the packet with no reply at all. Every watch built on what the client is doing
    /// agrees the fight is going fine. So this asks the one question the client cannot
    /// answer wrongly — has anything actually changed — and gives up when the answer has
    /// been no for long enough.
    /// </para>
    /// <para>
    /// Long, because it is the safety net and not the plan: a monster that dies normally
    /// never gets near it, and the cost of being wrong is resting something that was going
    /// to die eventually. Short enough that a character is not left shooting a corner for a
    /// whole minute.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Fruitless = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a target may be swung at, in reach, without losing any health.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Short, because it can afford to be. This is not a timer on how long something is
    /// taking to die — a tough monster is still losing health while it takes its time, and
    /// every point of it starts this again. Only a target that cannot be hurt at all holds
    /// one number, and the ones that cannot are the ones worth leaving: a monster round a
    /// corner, where the server refuses the shot for want of line of sight, or one still
    /// burrowed, where it refuses it outright. Neither is solved by standing closer, which
    /// is the whole reason this is needed on top of the walk.
    /// </para>
    /// <para>
    /// Three seconds is more than one attack at any weapon speed, so an ordinary fight has
    /// changed this at least once before it could fire, and it is short enough that a corner
    /// costs a moment rather than a fight. It can afford to be aggressive because the first
    /// thing it does is cheap: closing in keeps the target and walks at it. Only the last
    /// step, when there is nowhere nearer to stand, actually gives a monster up.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Unhittable = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How recently the target has to have lost health for the fight to count as going.
    /// </summary>
    /// <summary>How far the character may drift and still count as not having moved.</summary>
    /// <remarks>
    /// Under any real approach — closing on something is tiles, not one — and over any
    /// amount of shuffling against a wall, which is what a stuck chase looks like from
    /// outside: a little movement every now and then, and no arrival.
    /// </remarks>
    private const int Roaming = 2;

    /// <summary>
    /// How long a leg may go unreached before it is given up as the wrong one.
    /// </summary>
    /// <remarks>
    /// Long enough to walk one: a leg is at most a screen across and the client covers a tile
    /// in a few hundred milliseconds. Short enough that a leg the character cannot actually
    /// get to — because something moved into the way, or the window scrolled under the
    /// search — costs a few seconds rather than the whole fight.
    /// </remarks>
    private static readonly TimeSpan Legging = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long a steered character may stand still before the walk is given back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Being hit at a corner froze the character outright. The client's own handling of a blow
    /// leaves a tick in the scheduler, so <see cref="AttackChain.TickQueued"/> reads busy for
    /// ever and the last gate of <see cref="Follow"/> — which only re-kicks when the queue is
    /// empty — never fires again. Meanwhile <see cref="RouteWalk.Hold"/> clears the attack
    /// flags on every pass a route is held, so the character could neither walk nor fight, and
    /// the leg timeout only ever rebuilt the same route and did the same thing again.
    /// </para>
    /// <para>
    /// The target is dropped rather than the route, which is the difference between this
    /// working and this being the wall shuffle again. The first version handed the walk back
    /// to the client for a few seconds — and handing a walk back in front of a wall <em>is</em>
    /// the shuffle, because the client's climb is what shuffles. A monster the character
    /// cannot get to is a monster to leave alone, which is what every other giving-up path
    /// here does and what was asked for in the first place.
    /// </para>
    /// <para>
    /// Five seconds and not the two it started at. Walking a route is not smooth: a leg ends,
    /// the next one is worked out, <see cref="Follow"/> waits a pass for the client's queue —
    /// any of which can hold a character on one square for a second or two with nothing wrong.
    /// At two seconds this fired on ordinary corners, and what it does is give up on the
    /// target, so the character walked half way to a monster and then set off after a
    /// different one. Nothing was attacking it; this was.
    /// </para>
    /// <para>
    /// Nothing is lost by waiting: the freeze this exists for is permanent, and
    /// <see cref="Nudging"/> re-kicks the walk four hundred milliseconds into it. By five
    /// seconds a character that has not moved one square has had a dozen kicks and taken none
    /// of them, which is the difference between a pause and a wedge.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Wedged = TimeSpan.FromSeconds(5);

    /// <summary>How long a kick goes unanswered before another one is worth sending.</summary>
    /// <remarks>
    /// Two passes. The queue check exists so the client is not given a second step where it
    /// wanted one, and a character that has not moved in that long did not take the first.
    /// </remarks>
    private static readonly TimeSpan Nudging = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How long a change of target has to stand before another one is allowed.
    /// </summary>
    /// <remarks>
    /// Being hit is not an event, it is a rate: a character in a pack takes a blow about
    /// once a second, and each one asks "what is on top of me" again. With two monsters at
    /// the same distance the answer alternates, and every alternation cuts the attack chain
    /// and starts a new one — so the character swaps targets several times a second and
    /// kills nothing, which is what a screen full of monsters and no corpses looks like.
    /// </remarks>
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(3);

    /// <summary>How long one turn of a rotation lasts when no weapon is setting the pace.</summary>
    /// <remarks>See <see cref="Progress"/>, which is where the pace comes from otherwise.</remarks>
    private static readonly TimeSpan Turning = TimeSpan.FromSeconds(1);

    /// <summary>How long the client is believed when it says the character cannot act.</summary>
    /// <remarks>
    /// Longer than anything that holds a character in this game, and short enough that a
    /// misreading costs a pause rather than a session.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly TargetScan _scan;
    private readonly ChaseHook _chase;
    private readonly ClickHook _click;
    private readonly RouteWalk _walk;
    private readonly AttackChain _chain;
    private readonly SkillVolley _volley;
    private readonly CastWatch _watch;
    private readonly HuntOffer _offer;
    private readonly ILogger<HuntTask> _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StallWatch _stall = new();

    /// <summary>
    /// The search that decides what is worth attacking.
    /// </summary>
    /// <remarks>
    /// Kept rather than made per pass. It owns the copy of the client's collision window and
    /// the distance map over it, both of which are large enough to be allocated off the large
    /// object heap; a fresh pair five times a second would be megabytes that never get
    /// compacted, for a launcher that runs as long as somebody plays.
    /// </remarks>
    private readonly PathFinder _search = new();

    /// <summary>
    /// The same reading, kept across target changes.
    /// </summary>
    /// <remarks>
    /// <see cref="_stall"/> is reset every time a target is let go, so it can never measure
    /// more than one target's worth of nothing happening — which is exactly what a wedged
    /// client looks like, target after target. This one is only ever reset by the client
    /// actually doing something.
    /// </remarks>
    private readonly StallWatch _frozen = new();

    /// <summary>
    /// How long the character has held one spot, whatever the client says it is doing.
    /// </summary>
    /// <remarks>
    /// Reset with the target rather than with the pass, so it measures one fight and not one
    /// pause in one. See <see cref="Fruitless"/> for why the other two watches cannot answer
    /// this.
    /// </remarks>
    private readonly RootWatch _rooted = new();

    /// <summary>
    /// Whether the target is actually being hurt, which is the server's answer and not the
    /// client's. See <see cref="Unhittable"/>.
    /// </summary>
    private readonly HealthWatch _health = new();

    /// <summary>
    /// A shorter reach forced on the client for the current target, or null for the
    /// player's own.
    /// </summary>
    /// <remarks>
    /// Set only after a target has been swung at and not hurt, and dropped with the target.
    /// See <see cref="WeaponReach.Closer"/> for why walking closer is the answer to both of
    /// the things that cause it.
    /// </remarks>
    private int? _closing;

    private readonly Dictionary<uint, TimeSpan> _ignored = [];

    /// <summary>
    /// The way to somewhere this target can be attacked from, and how far along it the
    /// character has been sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The route is held; the destination walks forward along it and never back. That
    /// direction is the whole of why this is stable — the furthest square the client can reach
    /// unaided changes as the character moves, most of all near the corner it is being sent
    /// round, so a destination chosen afresh each pass is a different destination each pass
    /// and the character is pulled back and forth across the same two tiles.
    /// </para>
    /// <para>
    /// Moving the destination on before the character arrives is also what makes a corner
    /// smooth. Arriving means the engine stops, clears its own walk flag and has to be started
    /// again; extending the leg while it is still walking means it never stops at all.
    /// </para>
    /// </remarks>
    private IReadOnlyList<(int X, int Y)>? _route;

    /// <inheritdoc cref="_route"/>
    private int _leg;

    /// <summary>Where the target stood when the route was worked out.</summary>
    private (int X, int Y) _routeFor;

    /// <inheritdoc cref="_route"/>
    private TimeSpan _legSince;

    private HuntTarget? _target;

    /// <summary>Whether the client is holding something this hunt put there.</summary>
    /// <remarks>
    /// What tells a teardown worth doing from one that wipes the player's own state. See
    /// <see cref="Release"/>.
    /// </remarks>
    private bool _armed;

    private uint _lastHitPoints;
    private TimeSpan? _heldSince;
    private bool _doubted;
    private TimeSpan? _forcedAt;

    /// <summary>The client's own reach, as settled this pass.</summary>
    private int _swing = WeaponReach.Default;
    private RemoteProcess? _process;

    public HuntTask(
        TargetScan scan,
        ChaseHook chase,
        ClickHook click,
        RouteWalk walk,
        AttackChain chain,
        SkillVolley volley,
        CastWatch watch,
        HuntOffer offer,
        ILogger<HuntTask> logger)
    {
        _scan = scan;
        _chase = chase;
        _click = click;
        _walk = walk;
        _chain = chain;
        _volley = volley;
        _watch = watch;
        _offer = offer;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "hunt";

    /// <summary>
    /// Five times a second.
    /// </summary>
    /// <remarks>
    /// The same cadence as the client's own scheduler tick. Faster would buy nothing:
    /// between target changes this task reads four numbers and decides they are the same
    /// four numbers.
    /// </remarks>
    public TimeSpan Interval => TimeSpan.FromMilliseconds(200);

    /// <summary>What is being fought, for the window to show.</summary>
    public HuntTarget? Target => _target;

    /// <inheritdoc/>
    public void Tick(AuxContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Before anything, including remembering the process, so that Stopping has nothing
        // to write either. A build whose operator has not offered hunting must not have its
        // client touched at all, and this switch used to be enforced only where the settings
        // window writes its copy back — which decided what was drawn and nothing else. The
        // loop went on reading whatever the character's profile held, so a profile with the
        // hunt switch on was hunted with on a server that had never offered it, and the
        // player got a launcher steering their character.
        if (!_offer.IsOffered)
        {
            return;
        }

        _process = context.Process;
        var settings = context.Settings.Hunt;

        if (!settings.Enabled || !context.IsInWorld)
        {
            LetGo(context.Process);
            _volley.Reset();
            _watch.Reset();

            return;
        }

        // Every pass, because the client can be restarted and another tool can take the
        // site. Both return early when the jump they wrote is still there.
        //
        // Neither is optional. Without the chase hook the client re-locks onto whatever the
        // mouse is over, which is usually nothing; without the click hook nothing on the
        // game's thread ever acts on what this writes.
        if (!_chase.Install(context.Process) || !_click.Install(context.Process))
        {
            return;
        }

        // Not in that list, because the hunt works without it — badly, guessing who hit the
        // character from who is nearest, which is what it did before this existed. A client
        // that will not take this hook should still fight.

        if (ClientState.Where(context.Process) is not { } player)
        {
            return;
        }

        Forget();

        // Before anything is decided, because a floor change rebuilds the world. The ids in
        // the ignore list belong to monsters that are not here, the route in hand is a path
        // across the map that was left, and the collision window is a different window —
        // so a character that walked into a cave mouth carried on walking legs that meant
        // nothing where it had arrived, which is what standing at a teleport looked like.
        if (Arrived(context.Process, context.Player.MapId))
        {
            return;
        }

        // Every pass, and before anything decides what to do with a turn: the server's answer
        // to a cast arrives when it arrives, and a pass that returns early below is still a
        // pass on which it might have.
        _volley.Refused(
            _watch.Refused(context.Process),
            new CastConditions(
                context.Player.WeightPercent,
                context.Player.ManaPoints,
                context.Player.HitPoints,
                player.X,
                player.Y),
            _clock.Elapsed);

        // Every pass, because a weapon can be swapped mid-hunt and the reach goes with it.
        //
        // A rotation the server is refusing to cast for swings whatever it has instead. A
        // character whose list is nothing but skills does not swing at all by default — that
        // is what an unticked weapon row means — and leaving it that way while the skills are
        // stood down would be a character standing next to a monster doing nothing.
        _swings = SkillVolley.Swings(settings) || _volley.Silenced;
        _swing = WeaponReach.Apply(context.Process, Standoff(context.Process, settings));

        // Nothing the hunt does means anything while the client itself will not act. Held
        // still, the character does not move and does not swing, which is indistinguishable
        // from a monster it cannot reach — so without this the stall watch times out, rests
        // a perfectly good target, and comes round again having thrown away the fight it was
        // in the middle of.
        if (Waiting(context.Process))
        {
            _stall.Reset();

            return;
        }

        // Read before anything acts on it, and compared against the last pass rather than
        // against a maximum: what matters is that it went down, not how far down it is.
        var hitPoints = context.Player.HitPoints.Current;
        var hurt = _lastHitPoints > 0 && hitPoints < _lastHitPoints;

        _lastHitPoints = hitPoints;

        // Every blow landed, and only then, so this says one line per hit rather than five a
        // second. Which of the three ways retaliation declines is the whole question, and
        // from outside they all look the same: the character carries on with something else
        // while a monster chews on it.
        if (hurt && settings.Retaliate && Retaliate(context.Process, settings, player))
        {
            return;
        }

        // Read every pass so its baseline stays current, acted on only while something is
        // being fought: a character with nothing to do is idle, not wedged.
        var frozen = _frozen.Idle(player.X, player.Y, Progress(context.Process), _clock.Elapsed);

        if (_target is not null && frozen >= Frozen)
        {
            Force(context.Process, frozen);
        }

        if (_target is null || !Keep(context.Process, settings, player))
        {
            Engage(context.Process, settings, player);
        }

        // After the choosing, never instead of it. A skill is something extra thrown at
        // what the client is already fighting; the weapon is what kills things, and a pass
        // that cast instead of engaging would be a character standing still casting.
        if (_target is { } engaged)
        {
            _volley.Fire(
                context.Process,
                settings,
                engaged,
                player,
                context.Player.ManaPoints,
                context.Player.HitPoints,
                Casting(context.Process) || Cooling(context.Process),
                Progress(context.Process),

                // Distance only, at the skill's range rather than the weapon's — the client's
                // own test, which is what the cast is going to be measured against. Not the
                // line as well: see PathFinder.Within for why tracing it here silenced whole
                // fights depending on which way the monster lay.
                range => PathFinder.Within(player.X, player.Y, engaged, range),
                _clock.Elapsed);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The game may be gone, in which case there is nothing to write and nothing to say
    /// about it. If it is still there — the launcher closing while the player keeps
    /// playing — leaving the chain armed would leave the character fighting on its own.
    /// </remarks>
    public void Stopping()
    {
        if (_process is not { } process)
        {
            return;
        }

        try
        {
            LetGo(process);
            _click.Remove(process);
            _chase.Remove(process);
        }
        catch (GameProcessException)
        {
            // Expected: this runs after the game exits at least as often as before.
        }
    }

    /// <summary>
    /// Whether the fight in progress is still worth staying in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two ways it stops being, and both are the record's: it has been taken out of the
    /// world, or the address holds something else now. Everything else is waited out.
    /// </para>
    /// <para>
    /// That is narrower than it was, on purpose. It used to let go the moment the client was
    /// not holding an attack target — but the client is not holding one for a moment after
    /// every request, and every kick can be refused, so on a bad pass the hunt let go of a
    /// perfectly good monster and went looking for another. With three within reach that is
    /// a rotation: pick, let go, pick the next, and nothing is ever hit. It also kept
    /// resetting the clock the retaliation guard waits on, so being attacked never produced
    /// a retaliation either.
    /// </para>
    /// <para>
    /// So not being locked on is treated as what it is — something to ask for again — and
    /// letting go is left to the record and to the stall watch.
    /// </para>
    /// </remarks>
    private bool Keep(RemoteProcess process, HuntSettings settings, (int X, int Y) player)
    {
        if (_target is not { } target)
        {
            return false;
        }

        if (Read(process, target) is not { } now)
        {
            _logger.LogDebug("{Name} is done", target.Name);
            Release(process);

            return false;
        }

        // Where it is now, not where it was when it was picked. Everything downstream that
        // asks how far away the target is — the guard that stops a blow landing from
        // turning into a change of target — was reading coordinates from before the walk,
        // so a monster the character had spent five seconds closing on still measured
        // twenty tiles away and the guard never fired.
        var blinked = Blinked(target, now);

        _target = now;

        // A monster that was put somewhere rather than one that walked. Everything measured
        // so far — the route, how long the character has stood still, how long the thing has
        // gone without losing a point — was measured about a fight that is no longer where it
        // was, and carrying those numbers across is what made a blink cost several seconds:
        // the stall watch, already part way through its count, would time out almost at once
        // and rest the monster for the whole ignore period.
        if (blinked)
        {
            // Asked now rather than after the stall watch has spent its seconds noticing that
            // the character is standing still. Either there is a way to where it went, and the
            // walk starts this pass, or there is not and something else is worth doing.
            if (!Reaches(process, settings, player, now, _swing))
            {
                _logger.LogInformation(
                    "{Name} moved to ({X},{Y}), which there is no way to from ({PX},{PY}); "
                    + "letting it go", target.Name, now.X, now.Y, player.X, player.Y);

                // Not rested. It refused nothing and did nothing wrong — it was moved — and
                // the pass after this one may well find it somewhere reachable. Resting it
                // here is how a blink turned into the ignore period as well as the stall.
                Release(process);

                return false;
            }

            _logger.LogDebug(
                "{Name} moved from ({WasX},{WasY}) to ({X},{Y}) in one pass; planning again",
                target.Name, target.X, target.Y, now.X, now.Y);

            Wander();
            _stall.Reset();
            _rooted.Reset();
            _health.Reset();
            _stoodSince = _clock.Elapsed;
        }

        // Nothing is expected to be happening until the character is close enough to swing
        // and the client has actually locked on, so the clock starts on arrival rather than
        // on the pick. Counting the walk in would fire this on any target far enough away
        // to take five seconds to reach.
        if (InReach(player, now) && AttackChain.Engaged(process))
        {
            if (_health.Unchanged(now.Id, now.Health, _clock.Elapsed) >= Unhittable)
            {
                // Closer first, and only give up when there is nowhere closer to go. What
                // stops a shot landing is a corner or a monster still underground, and both
                // of those are answered by walking in rather than by finding another
                // target — which is just as well, because neither can be predicted from
                // anything the client knows.
                //
                // Closer only if the walk would actually get there, which has to be asked
                // and was not. A monster five tiles off behind a wall arrives in no steps
                // at a reach of eight, so nothing has tested the ground between the two;
                // halving the reach then sends the client at a wall it cannot go round,
                // because its climb has no lookahead. That is a character wedged against
                // stone until a watchdog notices, and it was.
                if (WeaponReach.Closer(_swing) is { } nearer)
                {
                    if (Reaches(process, settings, player, now, nearer))
                    {
                        _logger.LogInformation(
                            "{Name} at ({X},{Y}) has not lost a point in {Seconds}s from "
                            + "({PX},{PY}) at reach {Reach}; closing to {Nearer} — {State}",
                            target.Name, now.X, now.Y, (int)Unhittable.TotalSeconds,
                            player.X, player.Y, _swing, nearer, ClientState.Describe(process));

                        _closing = nearer;
                        _health.Reset();

                        return true;
                    }

                    _logger.LogWarning(
                        "{Name} at ({X},{Y}) has not lost a point in {Seconds}s from "
                        + "({PX},{PY}) at reach {Reach} and the walk does not reach "
                        + "{Nearer}; leaving it alone for {Ignore}s",
                        target.Name, now.X, now.Y, (int)Unhittable.TotalSeconds,
                        player.X, player.Y, _swing, nearer, settings.IgnoreSeconds);

                    _ignored[target.Id] =
                        _clock.Elapsed + TimeSpan.FromSeconds(settings.IgnoreSeconds);
                    Release(process);

                    return false;
                }

                _logger.LogWarning(
                    "{Name} at ({X},{Y}) has not lost a point in {Seconds}s from ({PX},{PY}) "
                    + "with nothing left to close, health {Health}; leaving it alone for "
                    + "{Ignore}s — {State}",
                    target.Name, now.X, now.Y, (int)Unhittable.TotalSeconds,
                    player.X, player.Y, now.Health, settings.IgnoreSeconds,
                    ClientState.Describe(process));

                _ignored[target.Id] = _clock.Elapsed + TimeSpan.FromSeconds(settings.IgnoreSeconds);
                Release(process);

                return false;
            }
        }
        else
        {
            _health.Reset();
        }

        // Before anything that talks to the attack chain. When the way to this one turns a
        // corner the client cannot take it, so the walk is steered leg by leg until the
        // character is somewhere its own engine can finish from — and only then is the chain
        // allowed to drive, exactly as before.
        if (Steer(process, settings, player, now))
        {
            return true;
        }

        // Before the stall watch, because the stall watch cannot see this one. It counts
        // the client's attack cooldown as progress and the client pushes that forward on
        // every blow it starts — so a server refusing every one of them silently reads, from
        // in here, exactly like a fight going well. See Fruitless.
        if (_rooted.Rooted(player.X, player.Y, Roaming, _clock.Elapsed) >= Fruitless)
        {
            _logger.LogWarning(
                "{Seconds}s stood over {Name} at ({X},{Y}) from ({PX},{PY}) with reach {Reach} "
                + "and it is still alive; leaving it alone for {Ignore}s — {State}",
                (int)Fruitless.TotalSeconds, target.Name, now.X, now.Y, player.X, player.Y,
                _swing, settings.IgnoreSeconds, ClientState.Describe(process));

            _ignored[target.Id] = _clock.Elapsed + TimeSpan.FromSeconds(settings.IgnoreSeconds);
            Release(process);

            return false;
        }

        var idle = _stall.Idle(player.X, player.Y, Progress(process), _clock.Elapsed);

        // Nothing is rewritten while the client is getting on with it. Every pass that
        // touched the destination was a pass that could add a step the client did not ask
        // for, and two steps where one was wanted is a character that suddenly speeds up.
        if (idle < TimeSpan.FromSeconds(settings.StallSeconds))
        {
            if (!_swings)
            {
                // A rotation of nothing but skills. The chain is what swings the weapon, so
                // it is held down rather than nudged — and held every pass, because the
                // client re-arms it from its own paths.
                RouteWalk.Hold(process);
            }
            else if (!AttackChain.Engaged(process))
            {
                // Asked for outright rather than nudged. The client is not locked onto
                // anything, so there is no chain running for a queued tick to belong to, and
                // the flags have to be set again anyway.
                _chain.Engage(process, target);
            }
            else if ((idle >= Quiet || _reaching || !AttackChain.Running(process))
                && _chain.Resume(process))
            {
                // Straight away when the client's attack chain has given up, rather than
                // after a second of waiting to notice. Its give-up path for a target that
                // stepped out of range clears its own running flag, leaves the attack target
                // pointing at the monster, and schedules nothing — so from out here it reads
                // as a fight in progress while the character stands there. At a bow's standoff
                // a monster leaves range on most of its steps, which is what one to three
                // seconds of not shooting something already in range was.
                //
                // Nothing is doubled by asking early: Resume refuses outright while the
                // scheduler still holds a walk tick, so a click only goes in when both of the
                // client's chains are dead and nothing was going to happen anyway.
                // Every field the client decides for itself, as it stood when the decision
                // was made. Enough to tell a chase that has not started from one that has
                // started and been refused.
                _logger.LogDebug(
                    "{Name} at ({X},{Y}), player at ({PX},{PY}), mode {Mode}, dest ({DX},{DY}); nudged",
                    target.Name, now.X, now.Y, player.X, player.Y,
                    Number(process, HuntAddresses.InteractionMode),
                    Number(process, HuntAddresses.DestinationX),
                    Number(process, HuntAddresses.DestinationY));
            }

            return true;
        }

        _logger.LogWarning(
            "nothing has happened about {Name} at ({X},{Y}) from ({PX},{PY}), reach {Reach}, "
            + "for {Seconds}s; leaving it alone for {Ignore}s — {State}",
            target.Name, now.X, now.Y, player.X, player.Y, _swing,
            settings.StallSeconds, settings.IgnoreSeconds, ClientState.Describe(process));

        _ignored[target.Id] = _clock.Elapsed + TimeSpan.FromSeconds(settings.IgnoreSeconds);
        Release(process);

        return false;
    }

    /// <summary>
    /// Turns on whatever the client says is acting on the character, if it is not already.
    /// </summary>
    /// <returns>Whether the target changed.</returns>
    /// <remarks>
    /// <para>
    /// The client knows, and always did. It keeps a set of the creatures acting on the
    /// character — see <see cref="Aggressors"/> — so this is no longer a guess about what is
    /// close enough to have landed the blow, and everything that made a guess bearable has
    /// gone with it: the wait before swapping, the "it lost a point recently" hold, the
    /// "already on the way there" hold. Those were ways of distrusting an answer. This answer
    /// does not need distrusting, it needs acting on.
    /// </para>
    /// <para>
    /// Read off the log of the version before this one: five thousand lines of "hit; staying
    /// on it" and not one change of target, because every one of those guards fires while a
    /// fight is going on and a fight is when the character is being hit.
    /// </para>
    /// <para>
    /// Only on the pass where the hit points actually fell, which is the caller's doing, so a
    /// fight in progress costs two reads per blow landed rather than two per pass.
    /// </para>
    /// </remarks>
    private bool Retaliate(RemoteProcess process, HuntSettings settings, (int X, int Y) player)
    {
        var blamed = Aggressors.Ids(process);

        if (blamed.Count == 0)
        {
            // Damage with nobody behind it: poison, a trap, standing in something. There is
            // nothing to turn on, and the guess this replaced would have picked a bystander.
            _logger.LogInformation(
                "hit, and the client has nobody acting on the character; staying on {Name}",
                _target?.Name ?? "nothing");

            return false;
        }

        // Already fighting one of them. Not "the target is alive and well", which is what the
        // old holds amounted to — this is the client agreeing that the thing being hit is one
        // of the things hitting back.
        if (_target is { } current && blamed.Contains(current.Id))
        {
            return false;
        }

        if (Culprit(process, settings, player, blamed) is not { } culprit)
        {
            _logger.LogInformation(
                "hit by {Count} thing(s), none of them in reach from ({X},{Y}); staying on {Name}",
                blamed.Count, player.X, player.Y, _target?.Name ?? "nothing");

            return false;
        }

        _logger.LogInformation(
            "hit by {Name} ({Id:X8}) while going for {Old}; turning on it",
            culprit.Name, culprit.Id, _target?.Name ?? "nothing");

        _ignored.Remove(culprit.Id);
        Release(process);

        return Start(process, culprit);
    }

    /// <summary>Finds something and starts on it.</summary>
    private void Engage(RemoteProcess process, HuntSettings settings, (int X, int Y) player)
    {
        if (Look(process, settings, player, settings.RangeSteps) is not { } target)
        {
            // Nothing to go for, so nothing should be left armed. Read off a stopped
            // character: g_auto_attack still set and g_attack_target still pointing at a
            // corpse, because this path used to return without touching either. The client
            // reads that as a chain in progress and its own halves wait on each other.
            //
            // Only what this hunt armed, though. A barren pass is every pass while there is
            // nothing on the map, and cutting a chain nobody started means writing the
            // client's walk and attack flags five times a second underneath a player who is
            // walking and swinging for themselves.
            if (_armed)
            {
                _chain.Stop(process);
                _armed = false;
            }

            return;
        }

        if (!Start(process, target))
        {
            // The client refused it. Offering the same monster again next pass would ask
            // the same question two hundred milliseconds later and get the same answer for
            // as long as the character stands there.
            _ignored[target.Id] = _clock.Elapsed + TimeSpan.FromSeconds(settings.IgnoreSeconds);
        }
    }

    /// <summary>
    /// Which of the things acting on the character is worth turning on.
    /// </summary>
    /// <returns>It, or null when none of them is.</returns>
    /// <remarks>
    /// <para>
    /// Everything the picker refuses is still refused. Being hit by something is not a reason
    /// to fight it if the player has said not to — a blacklist that retaliation walks round is
    /// not a blacklist — and the same goes for anything the client names that is not a monster
    /// at all, which is how a player standing behind the character is left alone.
    /// </para>
    /// <para>
    /// The ignore list is the one thing not honoured, and deliberately: a monster parked
    /// because the hunt could not hurt it is a different question from a monster that is
    /// hurting the character.
    /// </para>
    /// <para>
    /// Nearest first, because the set arrives in the order the client learned of it and that
    /// order means nothing here. Two things biting at once should be answered by the one in
    /// the character's face rather than by whichever started first.
    /// </para>
    /// <para>
    /// Hittable from where the character is standing this instant, which is a much narrower
    /// thing than reachable and is the whole of the difference between retaliating and being
    /// led away. Something biting the character is within reach of it by definition, so this
    /// costs nothing in the case retaliation exists for; what it refuses is an archer landing
    /// one arrow from across the room while the character is half way to something else.
    /// </para>
    /// </remarks>
    private HuntTarget? Culprit(
        RemoteProcess process,
        HuntSettings settings,
        (int X, int Y) player,
        IReadOnlyList<uint> blamed)
    {
        if (!_search.Refresh(process))
        {
            return null;
        }

        HuntTarget? nearest = null;
        var closest = 0;

        foreach (var monster in TargetPicker.Wanted(_scan.All(process), settings, new HashSet<uint>()))
        {
            if (!blamed.Contains(monster.Id))
            {
                continue;
            }

            // Not the one just walked away from. Two things biting at once is the last way
            // the client's own account can still flap a target, and it flaps by handing back
            // what was let go of a moment ago.
            if (_left is { } last
                && last.Id == monster.Id
                && _clock.Elapsed - last.When < Settled)
            {
                continue;
            }

            if (!_search.Hits(player.X, player.Y, monster, _swing))
            {
                continue;
            }

            // Tiles, not cells: two grid columns make one tile across and one row makes one
            // down, so a column counts half of what a row does.
            var away = Math.Abs(monster.X - player.X) + (Math.Abs(monster.Y - player.Y) * 2);

            if (nearest is null || away < closest)
            {
                nearest = monster;
                closest = away;
            }
        }

        if (nearest is { } found)
        {
            _logger.LogInformation(
                "hit by {Name} ({Id:X8}), by the client's own account", found.Name, found.Id);
        }

        return nearest;
    }

    /// <summary>
    /// The nearest monster the client's own walk would get to, within a step budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One question about the whole screen rather than one per monster, because the cost is
    /// in the asking: the reachability is measured inside the client, on a thread of its
    /// own, and a thread per monster would be a thread per monster.
    /// </para>
    /// <para>
    /// Null when nothing qualifies and null when the client could not be asked, which are
    /// different and are deliberately not distinguished here — neither is something to
    /// attack. The second says so in the log rather than silently.
    /// </para>
    /// </remarks>
    /// <param name="budget">How many steps of walking is worth it.</param>
    /// <summary>
    /// Something nearer than the one that was taken, when there is one worth mentioning.
    /// </summary>
    /// <remarks>
    /// Straight-line tiles, deliberately, because that is the measure the player is using
    /// when they say a monster was right there. Half the distance is the bar for saying
    /// anything: two monsters a step apart do not need explaining.
    /// </remarks>
    private static HuntTarget? Overlooked(
        IReadOnlyList<HuntTarget> wanted,
        int?[] steps,
        (int X, int Y) player,
        HuntTarget taken)
    {
        var away = Tiles(taken, player);
        HuntTarget? nearest = null;

        for (var i = 0; i < wanted.Count && i < steps.Length; i++)
        {
            if (wanted[i].Id == taken.Id || Tiles(wanted[i], player) * 2 >= away)
            {
                continue;
            }

            if (nearest is null || Tiles(wanted[i], player) < Tiles(nearest.Value, player))
            {
                nearest = wanted[i];
            }
        }

        return nearest;
    }

    /// <summary>Why a monster was passed over, as far as this can tell.</summary>
    private string Why(HuntTarget monster) =>
        _ignored.ContainsKey(monster.Id) ? "resting"
        : _search.Attack(monster, _swing) is { } taken ? $"{taken} steps in"
        : "with no square to shoot it from";

    /// <summary>
    /// How far apart two things are, in tiles rather than in grid cells.
    /// </summary>
    /// <remarks>Two columns make a tile across and one row makes one down.</remarks>
    /// <summary>
    /// Notices a floor change and throws away everything that was about the last one.
    /// </summary>
    /// <returns>Whether this pass belongs to a map the hunt has not looked at yet.</returns>
    /// <remarks>
    /// <para>
    /// One pass is given up on purpose. Every reading already taken this pass — where the
    /// character is standing, what is on the map, what it was fighting — was taken against a
    /// map it has left, and there is nothing to be gained by acting on any of it a fifth of a
    /// second earlier than the next pass would.
    /// </para>
    /// <para>
    /// A zero is not a map. The client reads that between leaving one and arriving at the
    /// next, and treating it as a floor of its own would throw the hunt away twice for one
    /// journey.
    /// </para>
    /// </remarks>
    private bool Arrived(RemoteProcess process, uint map)
    {
        if (map == 0 || _map == map)
        {
            return false;
        }

        var was = _map;

        _map = map;

        // Nothing to throw away on the first sight of a character.
        if (was is null)
        {
            return false;
        }

        _logger.LogInformation(
            "the character has gone from map {Was} to map {Now}; the hunt starts again there",
            was, map);

        Release(process);
        Wander();

        _ignored.Clear();
        _left = null;
        _frozen.Reset();
        _volley.Reset();
        _watch.Reset();

        return true;
    }

    /// <summary>
    /// Whether a target was moved rather than having walked.
    /// </summary>
    /// <remarks>
    /// Measured in tiles between two passes a fifth of a second apart, over which nothing on
    /// the map covers more than one square. Three is generous enough that a fast monster on a
    /// pass the launcher was late for still reads as walking, and far short of any blink.
    /// </remarks>
    internal static bool Blinked(HuntTarget was, HuntTarget now) =>
        Math.Abs(now.X - was.X) > Blink * 2 || Math.Abs(now.Y - was.Y) > Blink;

    private static int Tiles(HuntTarget target, (int X, int Y) player) =>
        Math.Max(Math.Abs(target.X - player.X) / 2, Math.Abs(target.Y - player.Y));

    private HuntTarget? Look(
        RemoteProcess process,
        HuntSettings settings,
        (int X, int Y) player,
        int budget)
    {
        var wanted = TargetPicker.Wanted(
            _scan.All(process), settings, _ignored.Keys.ToHashSet());

        if (wanted.Count == 0)
        {
            // Nothing to ask about. Worth leaving before the search rather than after it: an
            // empty screen is the common case while the player is between packs.
            return null;
        }

        // One question, and it is the right one: can the character get to a square it could
        // attack this from, and does the shot land from there. Both halves are a search over
        // the client's own grid — see PathFinder — so it goes round things, and the same
        // sentence covers a sword and a bow.
        //
        // What the client's own walk engine would manage on its own is deliberately not asked
        // any more. It hill climbs and so cannot round a corner, and gating the choice on that
        // meant refusing every monster that needed one. It does not have to manage it: the
        // route is handed to it a leg at a time, and each leg is a straight run it can do —
        // see Steer and RouteWalk.
        if (!_search.Search(process, player.X, player.Y, budget))
        {
            return null;
        }

        var steps = new int?[wanted.Count];

        for (var i = 0; i < wanted.Count; i++)
        {
            steps[i] = _search.Attack(wanted[i], _swing);
        }

        // No way in, no going round it. The refusal used to be lifted after three seconds of
        // finding nothing, on the grounds that a character which picks nothing never moves and
        // so never changes its own answer - and what that bought was the hunt locking onto
        // exactly the monsters it had just refused, melee and ranged alike, three seconds later
        // every time. Standing still is a worse outcome for one pass and a better one for a
        // session.
        var chosen = TargetPicker.Nearest(wanted, steps, player, _swing);

        // How many were on screen against how many the client's walk would actually get to.
        // That gap is the whole of what "there are monsters everywhere and it is not
        // fighting" looks like from outside, and it is unreadable without both numbers.
        var reached = steps.Count(taken => taken is not null);

        // Said out loud when the answer is surprising — nothing reachable at all, or the
        // pick is not one of the near ones — and quietly the rest of the time. The gap
        // between how many are on screen and how many the client's walk would get to is the
        // whole of what "there are monsters everywhere and it is not fighting" looks like.
        if (reached == 0 && wanted.Count > 0)
        {
            _logger.LogInformation(
                "{Wanted} worth attacking and none of them can be both got to and hit from "
                + "({X},{Y}) at reach {Reach} inside {Budget} steps",
                wanted.Count, player.X, player.Y, _swing, budget);
        }
        else if (chosen is { } taken && Overlooked(wanted, steps, player, taken) is { } near)
        {
            // The pick is not the one standing closest, which is the thing that looks wrong
            // from a chair and is usually right: the near one is behind something. Usually,
            // not always — so it says which one it passed over and what it knew about it,
            // because "it walked past a monster in my face" is unanswerable without that.
            _logger.LogInformation(
                "taking {Name} {Steps} steps off over {Near}, {Away} tiles away and {Why}; "
                + "{Wanted} worth attacking, {Reached} with a way in, {Rested} resting",
                taken.Name, _search.Attack(taken, _swing) ?? -1, near.Name,
                Tiles(near, player), Why(near), wanted.Count, reached, _ignored.Count);
        }
        else
        {
            _logger.LogDebug(
                "{Wanted} worth attacking, {Reached} with a way in within {Budget}; taking {Name}",
                wanted.Count, reached, budget, chosen?.Name ?? "nothing");
        }

        return chosen;
    }

    /// <summary>
    /// Opens whatever the client has latched, when it has plainly stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every flag <see cref="ClientGates"/> clears is one the client sets on its way into
    /// something and clears on its way out — with code that only runs while the client is
    /// still going. Both of its self-driving chains dying at once leaves nothing to run
    /// that code, and the flag stays set for the rest of the session.
    /// </para>
    /// <para>
    /// The queued cast is the one that does it. The replayed click gives up the whole pass
    /// while one is queued, and the only thing that fires a queued cast is the walk engine's
    /// tail — so queued with no tick scheduled, each waits for the other and the character
    /// stands there being hit until the game is restarted. That is the report this exists to
    /// answer, word for word.
    /// </para>
    /// <para>
    /// Rate limited to one attempt per <see cref="Frozen"/>, and it says which gate was shut
    /// before it writes anything. If this ever fires with nothing latched then the wedge is
    /// somewhere else and the log will say so rather than leaving it to be guessed at again.
    /// </para>
    /// </remarks>
    private void Force(RemoteProcess process, TimeSpan frozen)
    {
        if (_forcedAt is { } last && _clock.Elapsed - last < Frozen)
        {
            return;
        }

        _forcedAt = _clock.Elapsed;

        var opened = ClientGates.Open(process);

        if (opened.Count == 0)
        {
            _logger.LogWarning(
                "{Name} has been held for {Seconds:0}s with every client gate already open; "
                + "the hold is somewhere this cannot reach — {State}",
                _target?.Name ?? "nothing", frozen.TotalSeconds, ClientState.Describe(process));

            return;
        }

        _logger.LogWarning(
            "nothing has happened for {Seconds:0}s and the client was still holding {Gates}; "
            + "cleared, and letting go of {Name} so the next pass starts clean — {State}",
            frozen.TotalSeconds, string.Join(", ", opened), _target?.Name ?? "nothing",
            ClientState.Describe(process));

        Release(process);
    }

    /// <summary>
    /// Whether to sit this pass out because the client will not act, and for how long that
    /// answer is allowed to be yes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Being held is measured in seconds. A reading that stays true for a minute is not a
    /// character that is held for a minute, it is a global being read wrong — and a hunt
    /// that quietly does nothing for ever on the strength of one misread address is a worse
    /// failure than one that hunts through a paralysis. So the refusal expires, loudly.
    /// </para>
    /// <para>
    /// The cost of being wrong the other way is bounded too: a genuine hold that outlasts
    /// this just means the stall watch gets to see the last of it.
    /// </para>
    /// </remarks>
    private bool Waiting(RemoteProcess process)
    {
        if (!Held(process))
        {
            _heldSince = null;
            _doubted = false;

            return false;
        }

        _heldSince ??= _clock.Elapsed;

        if (_clock.Elapsed - _heldSince < Patience)
        {
            return true;
        }

        if (!_doubted)
        {
            // The mode word only ever has bits ORed into it — its writer at 0x579CB0 takes
            // the old value and adds one — so a mode the client forgets to leave is a mode
            // it stays in for the rest of the session. Read off a character that had been
            // standing still for minutes: bit 4 set, nothing pending that would explain it,
            // and the walk engine refusing every step because of it. Nothing was ever going
            // to clear that but a fresh client.
            _logger.LogWarning(
                "the client has said it cannot act for over {Seconds}s, which is longer than "
                + "anything holds a character; clearing its mode word and carrying on",
                Patience.TotalSeconds);

            ObfuscatedStat.TryClear(process, HuntAddresses.MovementBlocked);
            _doubted = true;
        }

        return false;
    }

    /// <summary>
    /// Whether the client is refusing to move or to swing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client's own two tests, read rather than inferred: the walk engine will not take
    /// a step while one is set, and both halves of the attack chain give up on the other.
    /// Being held, rooted, asleep or stunned all land in them, and so does whatever else the
    /// server invents later — which is the point of asking the client instead of hunting for
    /// the bit that means paralysed.
    /// </para>
    /// <para>
    /// Unreadable counts as free. These are obfuscated, and their key arrays move; refusing
    /// to hunt because a read failed would be a launcher that stops for its own reasons.
    /// </para>
    /// </remarks>
    private static bool Held(RemoteProcess process) =>
        (ObfuscatedStat.TryRead(process, HuntAddresses.MovementBlocked, out var walking)
            && walking != 0)
        || (ObfuscatedStat.TryRead(process, HuntAddresses.AttackBlocked, out var swinging)
            && swinging > 0);

    /// <summary>Whether the client would already be swinging rather than walking.</summary>
    /// <remarks>
    /// <c>InRangeCheck</c>'s test at <c>0x40EC00</c>, in as many characters: a box twice as
    /// wide as it is tall, because two columns make a tile across and one row makes one down.
    /// </remarks>
    private bool InReach((int X, int Y) player, HuntTarget target) =>
        Math.Abs(target.X - player.X) <= _swing * 2 && Math.Abs(target.Y - player.Y) <= _swing;

    /// <summary>
    /// Whether there is a way to a square within <paramref name="reach"/> of this one.
    /// </summary>
    /// <remarks>
    /// The same question <see cref="Look"/> asks of every candidate, asked again of one at a
    /// distance nobody has asked about yet. A target chosen at a bow's reach was never tested
    /// for the ground at arm's length, so the first pass that decides to walk in is the first
    /// pass that needs an answer about it.
    /// </remarks>
    private bool Reaches(
        RemoteProcess process,
        HuntSettings settings,
        (int X, int Y) player,
        HuntTarget target,
        int reach)
    {
        if (!_search.Search(process, player.X, player.Y, settings.RangeSteps))
        {
            // Unreadable is not the same as blocked, and treating it as blocked would refuse
            // to close in over a difficulty of the launcher's own.
            return true;
        }

        return _search.Attack(target, reach) is not null;
    }

    /// <summary>
    /// Walks the character along a leg of the route, when the client would not get there.
    /// </summary>
    /// <returns>Whether the walk was taken over this pass.</returns>
    /// <remarks>
    /// <para>
    /// Asked in that order on purpose. Most passes the client can finish the job by itself —
    /// open ground, or the last few squares — and on those this does nothing at all and the
    /// chain drives as it always has. It only takes over where the client demonstrably cannot
    /// go: a route whose far end its own climb would not reach.
    /// </para>
    /// <para>
    /// Nothing is remembered. The route is found again from where the character is standing,
    /// so a leg that ended somewhere unexpected, a monster that moved and a kick the client
    /// ignored all come out in the wash on the next pass rather than accumulating into a plan
    /// that no longer matches the world.
    /// </para>
    /// </remarks>
    private bool Steer(
        RemoteProcess process, HuntSettings settings, (int X, int Y) player, HuntTarget target)
    {
        _reaching = false;

        // Standing still *with a route in hand*, rather than standing still. A character
        // killing something in melee does not move for as long as the fight lasts, and reading
        // that as being stuck is what made the next target — picked from the same square the
        // instant the last one died — given up on its first pass, and the one after it, until
        // everything nearby was resting and the character stood there for the length of the
        // rest. Read off the log as three targets dropped in a second and a half, each of them
        // "has not moved in 5s" while the character had been fighting on that square the whole
        // time.
        if (_stood != player || _route is null)
        {
            _stood = player;
            _stoodSince = _clock.Elapsed;
        }

        // Before anything about walking, because walking is only ever a way to get into a
        // position and this asks whether the character is already in one. Without it a target
        // that becomes shootable half way along a route is not shot at until the route
        // finishes — a bow rounding a corner with a clear shot and carrying on walking.
        if (!_search.Refresh(process))
        {
            return false;
        }

        if (_search.Hits(player.X, player.Y, target, _swing))
        {
            _reaching = true;
            Wander();

            return false;
        }

        // Stood still with a route in hand for longer than walking a leg could take. Whatever
        // is holding the character is not something more destinations will move, and holding on
        // costs it the attack chain as well — Hold clears the flags every pass — so the walk
        // goes back to the client and stays there for a while. Being hit at a corner is the way
        // in: the client keeps a tick queued over a blow, which is the very thing the last gate
        // of Follow waits on, so nothing re-kicked and nothing could fight back either.
        if (_route is not null && _clock.Elapsed - _stoodSince >= Wedged)
        {
            _logger.LogWarning(
                "{Name} is round a corner but the character has not moved from ({X},{Y}) in "
                + "{Seconds}s; leaving it alone for {Ignore}s — {State}",
                target.Name, player.X, player.Y, (int)Wedged.TotalSeconds,
                settings.IgnoreSeconds, ClientState.Describe(process));

            _ignored[target.Id] = _clock.Elapsed + TimeSpan.FromSeconds(settings.IgnoreSeconds);
            Release(process);

            return false;
        }

        // Already ours. The question of whether the client could have managed this by itself
        // is not asked again — that question is what flaps, because its answer changes from
        // one square to the next and the boundary between the two answers is the wall.
        //
        // Not on the last leg, though, and that is worth its own paragraph. Hold clears the
        // attack flags every pass it runs, so for as long as a route is held the character
        // cannot swing at anything. That is right while there is walking left to do and wrong
        // once there is not: the last leg is the one square between the character and a place
        // it can shoot from, the client's own chase wants to cross that square too, and the
        // two of them have stopped pulling in different directions. Holding through it buys
        // nothing and costs the seconds between arriving next to a monster and hitting it —
        // a monster in plain view and a character standing still, which is what it looked
        // like from outside.
        if (_route is { } held && _leg + 1 < held.Count)
        {
            RouteWalk.Hold(process);
        }

        if (Wandering(target) && !Plan(process, settings, player, target))
        {
            return false;
        }

        return Follow(process, settings, player);
    }

    /// <summary>Whether the route in hand is no longer worth following.</summary>
    /// <remarks>
    /// Three ways, and all of them are about the world rather than about progress: there is no
    /// route, it has been walked to the end, or the thing it was a route to has moved far
    /// enough that it is a route to somewhere else now. The timeout is the fourth and is a
    /// backstop for a leg the character cannot actually reach — something standing in the way,
    /// or the window scrolling under the search.
    /// </remarks>
    private bool Wandering(HuntTarget target) =>
        _route is null
        || _leg >= _route.Count
        || Math.Abs(target.X - _routeFor.X) > Roaming * 2
        || Math.Abs(target.Y - _routeFor.Y) > Roaming
        || _clock.Elapsed - _legSince >= Legging;

    /// <summary>Works out a way to somewhere this target can be attacked from.</summary>
    /// <returns>Whether there is one worth walking.</returns>
    private bool Plan(
        RemoteProcess process, HuntSettings settings, (int X, int Y) player, HuntTarget target)
    {
        // With everything else on the map counted as something to walk round. Only here, and
        // deliberately: choosing a target is allowed to be optimistic — if the way to one turns
        // out to be full of monsters, this fails and another is chosen — while a route is the
        // thing the character actually has to walk, and one that goes through a creature is one
        // it will stand in front of until the leg times out.
        if (!_search.Search(
                process, player.X, player.Y, settings.RangeSteps, _scan.All(process), target.Id)
            || _search.Route(target, _swing) is not { Count: > 0 } route)
        {
            Wander();

            return false;
        }

        // The client's own engine can finish this. Leave it alone: it steps, animates and
        // sends the packets, and two things steering one character is what this whole design
        // exists to avoid. Asked only while the walk is not already this task's.
        //
        // Asked about what the client is actually going to do, too, which is chase the monster
        // and stop on the weapon's reach — not walk to the square this task picked to attack
        // from. Those are different destinations reached by different paths, and asking about
        // the wrong one hands a target to an engine that then walks left and right in front of
        // a wall: the way round to the firing square was clear, and the way to the monster was
        // straight through.
        // Only when the client is going to do anything with it. A skills-only rotation has the
        // chain held down, so handing the walk back is handing it to nobody: the character
        // stands where it is and casts at something out of range for ever.
        if (_swings
            && _route is null
            && _search.Chases(player.X, player.Y, target, _swing, settings.RangeSteps))
        {
            Wander();

            return false;
        }

        _logger.LogInformation(
            "{Name} is {Steps} steps away round a corner from ({PX},{PY})",
            target.Name, route.Count, player.X, player.Y);

        _route = route;
        _leg = 0;
        _routeFor = (target.X, target.Y);
        _legSince = _clock.Elapsed;

        return true;
    }

    /// <summary>
    /// Sends the character at the furthest square of the route it can reach unaided.
    /// </summary>
    /// <remarks>
    /// Forward only. The search is asked which squares the client's own climb would arrive at
    /// from here, and the answer moves about as the character does; taking the furthest one
    /// <em>at or beyond where it has already been sent</em> is what turns a wandering answer
    /// into a monotonic one, and monotonic is the whole reason this does not oscillate.
    /// </remarks>
    private bool Follow(RemoteProcess process, HuntSettings settings, (int X, int Y) player)
    {
        if (_route is not { } route)
        {
            return false;
        }

        var next = _leg;

        for (var i = route.Count - 1; i > _leg; i--)
        {
            if (_search.Climbs(player.X, player.Y, route[i].X, route[i].Y, settings.RangeSteps))
            {
                next = i;

                break;
            }
        }

        // Only when the leg actually moves on, so that a walk which is going nowhere still
        // times out rather than looking busy for ever.
        if (next > _leg)
        {
            _leg = next;
            _legSince = _clock.Elapsed;

            return _walk.To(process, route[next].X, route[next].Y);
        }

        // Nothing further along that the client would take from where it is standing, and the
        // character is on the leg or nearly on it. Move on — by one square, and never further.
        //
        // The first version of this looked from the far end of the route for whatever the
        // climb could reach *from the leg*, and sent the character straight there. That is a
        // guess about a position it has not reached yet, and the guess is worst exactly where
        // it is made: the main scan comes up empty at corners, so the jump is always round one,
        // and a destination round a corner handed to a hill climb is a character pressing into
        // the wall until the leg times out. Left and right in front of a wall, and then round
        // it six seconds later — which is what it looked like.
        //
        // One square is not a guess. Consecutive squares of a route are neighbours, so the
        // step is one the engine can take from either end of it, and the next pass's main scan
        // takes the long run as soon as there is one to take.
        if (Approaching(player, route[_leg]))
        {
            if (_leg + 1 < route.Count)
            {
                _leg++;
                _legSince = _clock.Elapsed;

                return _walk.To(process, route[_leg].X, route[_leg].Y);
            }

            // The end of it. Mode 3 stops within one tile of where it was sent, so a
            // character that has arrived at the last square of a route is standing next to it
            // about as often as on it — and next to it is not where the shot is, or the route
            // would have ended a square earlier. Handing the walk back there is what the
            // pacing was: the client's own chase takes over, walks at the monster instead of
            // round the wall, and presses into it. Read off the log as "1 steps away round a
            // corner from (32762,32752)" seven passes running, the character on that square
            // the whole time and not one destination issued.
            //
            // So walk one square past it instead, and let the stop box land on the square the
            // route was for. A blocked square beyond is fine and is not worth testing for: the
            // climb walks at it and stops where it stops improving, which is the same square.
            //
            // Nothing ends the route here any more. Steer ends it the moment the shot exists,
            // which is the only thing the route was ever for, and the leg timeout ends it when
            // the shot does not arrive.
            if (Beyond(player, route[_leg]) is { } past)
            {
                return _walk.To(process, past.X, past.Y);
            }
        }

        // Still on the way. The client is either walking there or has stopped; asking while it
        // walks is a second step where it wanted one — see AttackChain.Resume — but never
        // asking again is worse, and was: the engine clears MoveRequested as it runs, so one
        // request buys one tick and then nothing re-arms it. Read off the log as a destination
        // issued at 32690,32751 and the character still on 32690,32751 twelve seconds later.
        // Or has not moved in two passes, whatever the queue says. The queue check is there so
        // the client is not handed a second step where it wanted one, and a character standing
        // on the same square did not take the first — while a tick that outlives a blow leaves
        // the check reading busy for as long as anyone is willing to wait.
        if (!AttackChain.TickQueued(process) || Stalled)
        {
            return _walk.To(process, route[_leg].X, route[_leg].Y);
        }

        return true;
    }

    /// <summary>
    /// The square one further on from a leg, seen from where the character is standing.
    /// </summary>
    /// <returns>It, or null when the route runs off the edge of the window.</returns>
    /// <remarks>
    /// Reflection rather than a heading, so it works for the diagonal legs as well: the
    /// character, the leg and this are three points on a line, one step apart. The client is
    /// then two of its own steps from the destination instead of one, which is the difference
    /// between a walk it will take and a walk it thinks it has already made.
    /// </remarks>
    private (int X, int Y)? Beyond((int X, int Y) player, (int X, int Y) leg)
    {
        var x = (leg.X * 2) - player.X;
        var y = (leg.Y * 2) - player.Y;

        return _search.Grid.Holds(x, y) ? (x, y) : null;
    }

    /// <summary>
    /// Whether the character is close enough to a leg to be given the one after it.
    /// </summary>
    /// <remarks>
    /// Two tiles, which is wider than the engine's own arrival box and narrower than anything
    /// that could be called a guess. The point is to hand over the next destination while the
    /// character is still walking, so ask the question from the leg rather than from here — but
    /// only once here and the leg are close enough that the two climbs run over the same
    /// ground. Further out than this and it is a corner being cut, which is a character walking
    /// into the wall the route existed to go round.
    /// </remarks>
    private static bool Approaching((int X, int Y) player, (int X, int Y) leg) =>
        Math.Abs(player.X - leg.X) <= 4 && Math.Abs(player.Y - leg.Y) <= 2;

    /// <summary>
    /// How far off to stand, which is the tightest thing being asked for rather than the
    /// weapon's own reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client will not walk a character into range of a skill — see
    /// <see cref="SkillVolley.Closest"/>, and <c>FUN_0073C260</c>, which checks the range and
    /// then waits for a second click rather than closing. So a bow character standing off at
    /// eight tiles with a three-tile skill in the rotation never casts it, and nothing in
    /// either half notices: the walk arrived, the rotation took its turn, the range refused
    /// it, and round it went.
    /// </para>
    /// <para>
    /// Said once when it changes. Walking a bow into melee is a surprising thing to do on a
    /// player's behalf, and it is worth being able to read why.
    /// </para>
    /// </remarks>
    private int Standoff(RemoteProcess process, HuntSettings settings)
    {
        var standoff = _closing ?? settings.StandoffTiles;

        // A row that could not go off for distance. Walking in is the answer a weapon gets for
        // free — the client's own attack chain closes and then swings — and a rotation that
        // instead skipped the row left the character standing exactly where it could not cast
        // from, for as long as the fight lasted.
        if (_volley.Approach is { } wanted && wanted < standoff)
        {
            standoff = wanted;
        }

        if (_volley.Closest(process, settings) is not { } near || near >= standoff)
        {
            _closest = null;

            return standoff;
        }

        if (_closest != near)
        {
            _closest = near;
            _logger.LogInformation(
                "the rotation holds a skill that only reaches {Near} tiles, so the character "
                + "will stand at that rather than at {Standoff}",
                near, standoff);
        }

        return near;
    }

    /// <summary>Gives up the route, so the client has the walk back.</summary>
    private void Wander()
    {
        _route = null;
        _leg = 0;
    }

    /// <summary>
    /// Whether the target could be hit from where the character is standing, as of this pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by <see cref="Steer"/>, which has to work it out anyway to decide whether there is
    /// any walking left to do, and read by the nudge — where it removes the second of waiting
    /// that <see cref="Quiet"/> otherwise imposes.
    /// </para>
    /// <para>
    /// That second exists so the client is not given a step it did not ask for, which is a
    /// character that suddenly moves at twice its speed. A target already in reach is not being
    /// walked to, so there is no step to double and nothing for the wait to protect: what it
    /// protects against instead is the client's attack chain looking alive — engaged, running,
    /// a tick in the queue — while the tick it holds is a walk that has arrived and will not
    /// swing. From out here that is indistinguishable from a fight going well, and it reads as
    /// a pause before hitting something standing right next to the character.
    /// </para>
    /// <para>
    /// Asking early costs nothing. <see cref="AttackChain.Resume"/> refuses outright while the
    /// scheduler still holds a tick, so during a healthy fight this is a read and a refusal
    /// every pass, and a click only goes in once both of the client's chains are dead.
    /// </para>
    /// </remarks>
    private bool _reaching;

    /// <summary>Where the character was when it was last seen to move, and when.</summary>
    private (int X, int Y)? _stood;

    /// <inheritdoc cref="_stood"/>
    private TimeSpan _stoodSince;

    /// <summary>Whether the character has not moved for long enough to re-send a kick.</summary>
    private bool Stalled => _clock.Elapsed - _stoodSince >= Nudging;

    /// <summary>What was let go of last, and when. <see cref="Culprit"/> reads it.</summary>
    private (uint Id, TimeSpan When)? _left;

    /// <summary>Which floor the hunt last looked at, so a change of them can be noticed.</summary>
    private uint? _map;

    /// <summary>The skill range the standoff was last cut to, so it is said once.</summary>
    private int? _closest;

    /// <summary>
    /// Whether the rotation lets the weapon be used, as settled this pass.
    /// </summary>
    /// <remarks>
    /// False turns the whole of the client's attack chain off and leaves the walking to
    /// <see cref="RouteWalk"/>, because in the client the two are one thing: the chase is what
    /// the chain does on its way to swinging, and there is no way found so far to have the
    /// first without the second. A skills-only rotation therefore walks the way a route walks
    /// and stands where <see cref="Standoff"/> puts it.
    /// </remarks>
    private bool _swings = true;

    /// <summary>Whether the client is holding a cast its own next tick will fire.</summary>
    private static bool Casting(RemoteProcess process) =>
        process.TryRead<byte>(HuntAddresses.CastQueued, out var queued) && queued != 0;

    /// <summary>Whether the client's own cast cooldown has run out yet.</summary>
    /// <remarks>
    /// <para>
    /// The rotation used to be paced by the weapon alone, which was near enough while every
    /// cast went through the client's own entry point — that refuses one it has not had time
    /// for. It is assembled out of the client's pieces now, and none of those pieces says no,
    /// so the pacing has to be read rather than assumed.
    /// </para>
    /// <para>
    /// Read straight from the word the client writes for its own cooldown display, and
    /// compared against this process's tick count because the client's clock is the same
    /// <c>GetTickCount</c> — but only while it says so. A client on a performance counter
    /// gets no cooldown read at all and paces on the weapon, which is where this started.
    /// </para>
    /// </remarks>
    private static bool Cooling(RemoteProcess process) =>
        process.TryRead<uint>(GameFunctions.CastClockIsTicks, out var ticks)
        && ticks == 0
        && process.TryRead<uint>(GameFunctions.CastReadyAt, out var ready)
        && ready != 0
        && unchecked((int)(ready - (uint)Environment.TickCount)) > 0;

    /// <summary>The client's attack cooldown, which it pushes forward on every blow.</summary>
    private static uint Cooldown(RemoteProcess process) =>
        process.TryRead<uint>(HuntAddresses.NextAttackTick, out var tick) ? tick : 0;

    /// <summary>
    /// The number everything here reads as "something happened", which is the weapon's
    /// cooldown until the weapon stops being used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cooldown is the client's own account of a blow being started, and three things are
    /// built on it: the rotation takes a turn per move of it, and the stall and frozen watches
    /// read a move of it as progress. All three are silently wrong for a rotation that never
    /// swings — it does not move at all, so the rotation takes one turn and then none for ever,
    /// and the watches decide within seconds that a character casting steadily is wedged.
    /// </para>
    /// <para>
    /// So the clock changes with the weapon. A second a turn is slow enough to be a rotation
    /// rather than a spray, and fast enough that what limits the character is the client's own
    /// cast cooldown rather than this. Nothing is lost by the watches going quiet: what they
    /// are for is a fight that is not happening, and <see cref="Unhittable"/> answers that from
    /// the target's health, which is the server's word rather than the client's.
    /// </para>
    /// </remarks>
    private uint Progress(RemoteProcess process) => _swings
        ? Cooldown(process)
        : (uint)(_clock.Elapsed.Ticks / Turning.Ticks);

    /// <summary>Pins a monster and starts the client on it.</summary>
    /// <remarks>
    /// The hook first. The chain's own first act is to call the re-lock this hook sits on,
    /// so a chain started before the pin re-locks onto whatever the mouse happens to be
    /// over — which is usually nothing.
    /// </remarks>
    private bool Start(RemoteProcess process, HuntTarget target)
    {
        if (!_chase.Aim(process, target.Address.Value))
        {
            return false;
        }

        // From here the client is holding something this hunt put there, which is the only
        // thing Release is entitled to take back out.
        _armed = true;

        // The chain only when the rotation asks for the weapon. Without it the client neither
        // chases nor swings, and the walking is the route walker's — see _swings.
        if (_swings && !_chain.Engage(process, target))
        {
            return false;
        }

        _target = target;
        _stall.Reset();

        // From the top, so the order a player wrote is the order each monster gets rather than
        // wherever the last one happened to leave off.
        _volley.Restart();

        _logger.LogInformation("hunting {Name} ({Id:X8})", target.Name, target.Id);

        return true;
    }

    /// <summary>
    /// Stops the chain and forgets the target.
    /// </summary>
    /// <remarks>
    /// Whether or not a target was being held. The flags in the client are not a reflection
    /// of what this object remembers: a pass can leave the auto-attack flag set and the
    /// hover target pinned with nothing recorded here, and then switching the hunt off would
    /// have left the character being driven by settings nobody was maintaining any more.
    /// </remarks>
    private void LetGo(RemoteProcess process)
    {
        Release(process);

        // The standoff goes with the hunt. It is the launcher's opinion about where to
        // stand, not the client's, and the player is entitled to their own client back the
        // moment they switch this off.
        WeaponReach.Clear(process);
    }

    /// <summary>
    /// Lets go of the current target.
    /// </summary>
    /// <remarks>
    /// The hook is aimed at nothing before the chain is cut, so the next re-lock reads zero
    /// and the client stops chasing by its own logic. Cutting first would leave a window
    /// where the pin is still pointing at something the chain no longer holds.
    /// </remarks>
    private void Release(RemoteProcess process)
    {
        // Which one, and when. The only kind of target flapping the client's own account of a
        // blow can still produce is two things biting at once, and what that looks like is one
        // being let go of and picked up again a moment later — so that is the one thing
        // refused. See Culprit.
        if (_target is { } leaving)
        {
            _left = (leaving.Id, _clock.Elapsed);
        }

        // Only what this hunt armed. Both of these write the client's own flags, and
        // Release runs on every pass the hunt is switched off — so doing it unconditionally
        // cleared WalkTargetValid and AttackTarget five times a second underneath a player
        // who was playing for themselves. The walk engine returns the moment that flag is
        // clear, which reads as a character that takes one step per click and then stops.
        if (_armed)
        {
            _chase.Aim(process, 0);
            _chain.Stop(process);
            _armed = false;
        }

        _target = null;
        _stall.Reset();
        _rooted.Reset();
        _health.Reset();

        // With the target, because it was that target's problem. The next one starts at
        // whatever distance the player asked for.
        _closing = null;

        // With the target, because it was a way to that target. Carrying one over is a
        // character walking to where the last fight was.
        _route = null;
        _leg = 0;
    }

    /// <summary>Drops targets whose rest is over.</summary>
    private void Forget()
    {
        if (_ignored.Count == 0)
        {
            return;
        }

        var now = _clock.Elapsed;

        foreach (var id in _ignored.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToList())
        {
            _ignored.Remove(id);
        }
    }

    /// <summary>Re-reads a target, or null if the record is no longer it.</summary>
    /// <remarks>
    /// <para>
    /// The id as well as the address, because an address is only good for the pass that
    /// found it: the client is free to free a record and allocate something else there, and
    /// a stale address that happens to still hold this vtable would otherwise be attacked
    /// as though it were the same creature.
    /// </para>
    /// <para>
    /// The flag at <c>+0x58</c> is the last signal to arrive, not the first. It is set when
    /// the server takes the record out of the world, which is a packet after the death — so
    /// waiting only for it leaves the hunt holding a corpse for the whole death animation,
    /// re-arming the attack chain at something already dead. That is a character swinging at
    /// nothing, and it is longest with a bow, where there is flight time and eight tiles
    /// between the two.
    /// </para>
    /// <para>
    /// What arrives with the killing blow is the action byte reading
    /// <see cref="HuntAddresses.DyingAction"/>. That one value is a death and not a guess:
    /// it is what <see cref="ChaseDetour"/> already unpins on and what
    /// <see cref="TargetScan"/> already refuses to pick, so the client has stopped chasing
    /// on it either way. Reading the action byte more broadly would be the guess, and was:
    /// an earlier attempt let go of creatures that were merely mid-animation.
    /// </para>
    /// <para>
    /// Burrowing is the other reason to let go, and it is the case the picker cannot cover on
    /// its own: a monster that goes under <em>while</em> it is being fought. The server does
    /// that for four of them at a third of their health, and without this the hunt holds a
    /// target nothing lands on until a watchdog gives up, which is a whole rotation poured
    /// into the ground.
    /// </para>
    /// <para>
    /// <see cref="HuntAddresses.ActionSunk"/> alone here, and not the rest of
    /// <see cref="HuntAddresses.IsHiddenAction"/>, which is a harder line than the picker
    /// draws and deliberately so. The picker skipping a monster for a pass costs a pass; this
    /// dropping a target costs the fight — the client's chain is released and started again,
    /// so the swing that was mid-animation never lands and the character stands there
    /// twitching. The other three values are small enough to be an action a creature is
    /// playing this instant, which is exactly what happened.
    /// </para>
    /// <para>
    /// Health of zero is <em>not</em> one of these, however much it looks like death. The
    /// server sends a percentage it worked out by integer division, so anything under one
    /// per cent of its maximum reports zero while it is still standing — and abandoning
    /// everything at the last sliver of its health is a hunt that never finishes anything.
    /// </para>
    /// <para>
    /// The health is re-read rather than carried over, which is not a detail:
    /// <see cref="HealthWatch"/> asks whether it has changed, and a value copied from the
    /// pass that picked the target never changes at all. Carrying it would have turned "this
    /// one cannot be hurt" into "every target, after five seconds".
    /// </para>
    /// </remarks>
    private static HuntTarget? Read(RemoteProcess process, HuntTarget target)
    {
        Span<byte> record = stackalloc byte[HuntAddresses.EntityLength];

        if (!process.TryReadBytes(target.Address, record))
        {
            return null;
        }

        var health = record[HuntAddresses.EntityHealth];
        var action = record[HuntAddresses.EntityAction];

        if (BitConverter.ToUInt32(record[HuntAddresses.EntityId..]) != target.Id
            || record[HuntAddresses.EntityIsGone] != 0
            || action == HuntAddresses.DyingAction
            || action == HuntAddresses.ActionSunk)
        {
            return null;
        }

        return target with
        {
            X = BitConverter.ToInt32(record[HuntAddresses.EntityX..]),
            Y = BitConverter.ToInt32(record[HuntAddresses.EntityY..]),
            Health = health,
            Action = action,
        };
    }

    /// <summary>A global, for the log, or -1 when it could not be read.</summary>
    private static int Number(RemoteProcess process, GameAddress address) =>
        process.TryRead<int>(address, out var value) ? value : -1;

}
