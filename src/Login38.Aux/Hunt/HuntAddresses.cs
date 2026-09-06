using Login38.Interop;

namespace Login38.Aux.Hunt;

/// <summary>
/// Everything the hunt reads or writes in the client, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Every address here was read out of a running <c>TW13081901</c> client or lifted from the
/// disassembly of the routine that uses it, and the comment says which. That matters more
/// than usual for this feature: the whole design is to hand work to the client rather than
/// do it, so a wrong address does not fail loudly — it makes the client do the wrong thing
/// quietly.
/// </para>
/// <para>
/// The reference scattered these across four modules. Keeping them together is the point of
/// the file: a client update moves all of them at once, and the only defence is being able
/// to see all of them at once.
/// </para>
/// </remarks>
internal static class HuntAddresses
{
    /// <summary>The client's own state; 3 while there is a character in the world.</summary>
    internal static GameAddress GameState => new(0x009AB5E8);

    /// <summary>The record for the person playing.</summary>
    internal static GameAddress LocalPlayer => new(0x00C2D2B8);

    // The attack chain, read from ClickAttack_funcB at 0x5A3770.
    //
    // One call to that function sends the first blow and schedules AutoAttack_funcA on the
    // client's own scheduler, at the client's own weapon interval. It reschedules itself
    // until the target dies. Nothing here times an attack.

    /// <summary><c>void __thiscall ClickAttack(LEntity* target, int x, int y)</c>.</summary>
    internal static GameAddress ClickAttack => new(0x005A3770);

    /// <summary>What the chain is aimed at. Cleared by the client when the target dies.</summary>
    internal static GameAddress AttackTarget => new(0x00C2D2B4);

    /// <summary>Where the walk engine is heading, in the entity coordinate space.</summary>
    internal static GameAddress DestinationX => new(0x009ABCA4);

    /// <inheritdoc cref="DestinationX"/>
    internal static GameAddress DestinationY => new(0x009ABCA8);

    /// <summary>
    /// The client's own "somebody asked for a move", which its walk engine will not step
    /// without.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read once, at <c>0x5A58E2</c> inside <c>WalkEngine_tick</c>, and it decides everything
    /// after it:
    /// </para>
    /// <code>
    /// MOVZX ECX, byte ptr [0x00C2D29F]
    /// TEST  ECX, ECX
    /// JNZ   0x005A5920          ; set: on into the branch that steps
    /// ...                       ; clear: two more tests, then stop
    /// </code>
    /// <para>
    /// Every one of the client's own ways of starting a walk sets it first — the re-lock at
    /// <c>0x4F5E17</c>, the click handler at <c>0x4F6BF5</c>, and the stop helper at
    /// <c>0x5A5136</c> — so anything that writes a destination and kicks the engine without it
    /// has written a destination the engine will read and then decline to act on. That is a
    /// character standing perfectly still with every flag looking right, which is exactly what
    /// it did: sixty-one seconds, destination set, mode set, no gate shut, no movement.
    /// </para>
    /// </remarks>
    internal static GameAddress MoveRequested => new(0x00C2D29F);

    /// <summary>How the client is interacting; 3 is chase-and-attack.</summary>
    internal static GameAddress InteractionMode => new(0x009AB31C);

    /// <summary>Set while the destination is worth walking to.</summary>
    /// <remarks>
    /// The walk engine returns immediately when this is clear, so refreshing a destination
    /// means setting it again as well as writing the coordinates.
    /// </remarks>
    internal static GameAddress WalkTargetValid => new(0x009AB3DF);

    /// <summary>Keeps the chain alive. The client does <em>not</em> clear this on a kill.</summary>
    /// <remarks>
    /// Read straight after a kill: the client had cleared <see cref="AttackTarget"/> and
    /// <see cref="HoverTarget"/> and dropped <see cref="InteractionMode"/> back to 1, but
    /// this and <see cref="WalkTargetValid"/> were still set. "The chain stopped" and "the
    /// flags are clean" are not the same state, so stopping writes both itself.
    /// </remarks>
    internal static GameAddress AutoAttack => new(0x00AC450C);

    /// <summary>The client's own attack cooldown. Read only, and only for the log.</summary>
    internal static GameAddress NextAttackTick => new(0x00C2D27C);

    /// <summary>
    /// What the client is part way through attacking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The re-lock at <c>0x4F5DE0</c> sets this as it dispatches a blow, and refuses to do
    /// anything at all while it is set — its outer guard requires this to be null, and takes
    /// <see cref="WalkTargetValid"/> down with it on the way out. So while this points at
    /// something, no click can re-lock, the walk engine returns at once, and neither chain
    /// restarts.
    /// </para>
    /// <para>
    /// Two things clear it: the record it names being removed from the world, and the
    /// client's own input handler at <c>0x4F7050</c>. A player clears it by clicking. A
    /// launcher that never sends input has only the first, so letting go of a monster
    /// without clearing this leaves the client wedged until that monster happens to die —
    /// and it will not die, because nothing can attack it any more. That is a fight that
    /// stops on the last one or two of a pack, which are exactly the ones nothing else is
    /// killing.
    /// </para>
    /// </remarks>
    internal static GameAddress AttackInFlight => new(0x00ABF33C);

    // What the client asks itself before it will move or swing. Both are obfuscated the way
    // hit points are — an index, a key array and a salt — and both are the client's own
    // test rather than a guess at which status effect is which. Anything that stops the
    // character acting shows up in one of them, named or not.

    /// <summary>Non-zero while the walk engine refuses to take a step.</summary>
    /// <remarks>
    /// A state rather than a flag: the walk engine's own tail writes 3, 4, 7 and 8 into it
    /// for the states it enters, and refuses to step whenever it is not zero.
    /// </remarks>
    internal static GameAddress MovementBlocked => new(0x00BDC744);

    /// <summary>Above zero while the client refuses to swing.</summary>
    /// <remarks>
    /// The first thing both <c>Attack_dispatch</c> and <c>AutoAttack_funcA</c> read, and
    /// both give up on it.
    /// </remarks>
    internal static GameAddress AttackBlocked => new(0x00BDC984);

    // The four routines the walk engine decides a step with. Every one takes the position
    // it is asked about as an argument rather than reading the player's, which is what
    // makes them answerable for somewhere the character is not standing — and that is the
    // whole of how "would the client get there" is asked without guessing at it.
    //
    // Reimplementing them was the alternative and it is the worse one. The step it takes
    // for a heading comes out of a table at 0x00ACCDF8 that is empty in the file and filled
    // at start-up, so a copy could not even be written without a running client, and every
    // detail copied is a detail that can be copied wrong or go stale.

    /// <summary>
    /// <c>char __cdecl TileBlocked(int x, int y, unsigned heading)</c> — non-zero refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads bit 0 of up to four cells around the move, through four offset tables at
    /// <c>0x965F88</c>. A straight heading is refused when its own cell carries the bit; a
    /// diagonal when either cell of either pair does, which is looser than it sounds and is
    /// why a hand-written corner rule gets a map like this wrong.
    /// </para>
    /// <para>
    /// The polarity is settled by the one caller that cannot be argued with — the client's
    /// own walk engine, in <c>ComputeStepHeading</c> at <c>0x5A4D60</c>:
    /// </para>
    /// <code>
    /// uVar3 = FUN_004f5910(px, py, heading);
    /// if ((uVar3 &amp; 0xff) == 0) { score = GridDistance(step, dest); }
    /// else                       { score = 99999999; }
    /// </code>
    /// <para>
    /// Zero is the usable answer and non-zero is the wall. This was inverted once, on the
    /// strength of a standing character occupying a cell that carried the bit — which proves
    /// nothing, because the bit belongs to a <em>heading out of</em> a cell rather than to
    /// standing in one. The cost was a search that walked through walls and a hunt that
    /// locked onto whatever was behind them. Settle a foreign boolean against the code that
    /// consumes it, never against a plausible reading of the data.
    /// </para>
    /// </remarks>
    internal static GameAddress TileBlocked => new(0x004F5910);

    /// <summary>
    /// <c>POINT* __thiscall StepByHeading(POINT* from, POINT* into, int heading)</c>.
    /// </summary>
    internal static GameAddress StepByHeading => new(0x00554C50);

    /// <summary>
    /// <c>int __thiscall GridDistance(POINT* from, POINT* to)</c> — what the engine sorts by.
    /// </summary>
    /// <remarks>
    /// The column difference squared over four, plus the row difference squared. The four is
    /// the aspect of the grid, not a rounding: two columns to a tile across, one row down.
    /// </remarks>
    internal static GameAddress GridDistance => new(0x00554950);

    /// <summary>
    /// <c>char __thiscall InRangeCheck(POINT* from, int x, int y, int range)</c>.
    /// </summary>
    /// <remarks>
    /// <c>|dx| &lt;= range * 2 &amp;&amp; |dy| &lt;= range</c> — a box, not a circle, and the
    /// doubling is the same grid aspect again. The walk stops at range 1.
    /// </remarks>
    internal static GameAddress InRangeCheck => new(0x0040EC00);

    /// <summary>
    /// What the re-lock routine at <c>0x4F5DE0</c> reads to decide whether to keep chasing.
    /// </summary>
    /// <remarks>
    /// Cleared from thirty-five places in the client, so writing it once per pass loses the
    /// race and the character stops mid-chase. It is pinned by a hook at the re-lock
    /// routine's entry instead.
    /// </remarks>
    internal static GameAddress HoverTarget => new(0x00ABF440);

    /// <summary>The re-lock routine, and the hook site that pins <see cref="HoverTarget"/>.</summary>
    internal static GameAddress ReTargetFromHover => new(0x004F5DE0);

    // Kicking the walk engine, read from ClickHandler at 0x4F6990.
    //
    // Setting the globals above is not enough on its own and never was. The engine is a
    // scheduled callback: it runs, takes one step, posts its own follow-up ticks through
    // 0x590BA0, and when it decides there is nothing to do it stops posting and nothing
    // brings it back. A left click is what starts it — ClickHandler calls
    // ReTarget_fromHover and then calls WalkEngine_tick outright.
    //
    // Written from outside with nothing ticking, the globals simply sit there until the
    // player happens to click, and then the character walks to a monster it was told about
    // seconds ago. That is the whole of what "it only moves when I click" was.

    /// <summary>
    /// The walk engine's tick, which is also how a click starts one.
    /// </summary>
    /// <remarks>
    /// <c>void __stdcall WalkEngine_tick(void)</c>, no arguments. It steps, decides whether
    /// the target is in reach, calls the attack itself, and posts its own follow-up ticks —
    /// so one call is a whole approach and a whole fight, not one step.
    /// </remarks>
    internal static GameAddress WalkEngineTick => new(0x005A51A0);

    /// <summary>
    /// <c>uint __fastcall PlayerBusy(LEntity* player)</c> — non-zero while mid-animation.
    /// </summary>
    /// <remarks>
    /// The click path's own guard, and it matters again now that the engine is called
    /// rather than posted. Running it through the middle of an animation is a character
    /// being told to take another step part way through the one it is taking.
    /// </remarks>
    internal static GameAddress PlayerBusy => new(0x005AB150);

    /// <summary>
    /// The scheduler's pump, and the hook site that replays a click on the game's thread.
    /// </summary>
    /// <remarks>
    /// <c>bool SchedulerPump(void)</c>: while the first entry is due, take it off and call
    /// it. The main loop calls this every frame, which makes its entry the one place a
    /// launcher can borrow that is both frequent and unmistakably the game's own thread.
    /// </remarks>
    internal static GameAddress SchedulerPump => new(0x00590B30);

    /// <summary>Set while a cast is queued and waiting for the walk engine to fire it.</summary>
    /// <remarks>
    /// <para>
    /// The spell book sets this at <c>0x73DA14</c> and clears it at <c>0x73DE87</c>. What
    /// fires the cast is not the spell book but the tail of <c>WalkEngine_tick</c>, which
    /// reads this, picks a kind from <c>0xC2D280</c>, calls into <c>0x73EC10</c>, and then
    /// ORs a bit into the mode word.
    /// </para>
    /// <para>
    /// Which makes it something a replayed click must never run through. The engine is
    /// called from the cave at a moment the client did not schedule, and if a cast is queued
    /// but not yet built — its object at <c>0xC3131C</c> still null, which is how it was
    /// found on a frozen character — the cast is fired at nothing, never completes, and
    /// leaves its mode bit set. That word is only ever ORed into, so the bit stays for the
    /// rest of the session and the character never walks again.
    /// </para>
    /// </remarks>
    internal static GameAddress CastQueued => new(0x00C2D29C);

    /// <summary>Set while a tick is already pending, so a second kick would double up.</summary>
    internal static GameAddress TickPending => new(0x00C2D29D);

    /// <summary>
    /// Raised while the auto-attack chain is rescheduling itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Attack_dispatch</c> raises it as it posts <c>AutoAttack_funcA</c>, and its own
    /// entry guard refuses while it is set — so this is what stops the two from running at
    /// once. <c>AutoAttack_funcA</c> lowers it on every path where it gives up.
    /// </para>
    /// <para>
    /// Every path but two. It returns without lowering it when the attack gate at
    /// <see cref="AttackBlocked"/> is above zero, and when the local player is null. Either
    /// of those at the wrong moment leaves the flag raised with nothing scheduled to lower
    /// it, and from then on <c>Attack_dispatch</c> refuses for the rest of the session: the
    /// character locks on to things and never swings at them.
    /// </para>
    /// <para>
    /// Read as one on a character that had stopped, twice, with the walk switched off and
    /// the target twenty tiles away.
    /// </para>
    /// </remarks>
    internal static GameAddress AutoAttackRunning => new(0x009AB320);

    /// <summary>
    /// How close the client thinks it has to be to swing, in tiles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One byte, written by a packet handler at <c>0x540FB3</c> and read in three places:
    /// <c>AutoAttack_funcA</c> at <c>0x5A3254</c>, the walk engine at <c>0x5A4EE8</c>, and
    /// the casting path at <c>0x734B6E</c>. Zero means "no answer from the server", and
    /// then the client falls back to <see cref="ReachTable"/>.
    /// </para>
    /// <para>
    /// This server never sends that packet, so it is zero on every character — measured,
    /// not assumed. Read anyway rather than skipped, because reading it is the client's own
    /// order of preference and a server that starts sending it should win: see
    /// <see cref="WeaponReach"/>.
    /// </para>
    /// </remarks>
    internal static GameAddress AttackReach => new(0x00C2D2CA);

    /// <summary>
    /// How close the client walks before it starts swinging, one entry per weapon class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ComputeStepHeading</c> at <c>0x5A4D60</c>, in the mode a monster is chased in:
    /// </para>
    /// <code>
    /// local_38 = 1;
    /// if (weapon &lt; 0x58) local_38 = DAT_008D2CB8[weapon];
    /// if (DAT_00C2D2CA != 0) local_38 = DAT_00C2D2CA;
    /// InRange_check(player, dest, local_38 + targetSize)
    /// </code>
    /// <para>
    /// Read off a running client: 2 for the melee classes at 24–27, 14 for the bow classes
    /// at 20–23, 9 for the claws at 62–65, and 30 for 16–19.
    /// </para>
    /// </remarks>
    internal static GameAddress ReachTable => new(0x008D2CB8);

    /// <summary>How many entries <see cref="ReachTable"/> has, from the client's own bound.</summary>
    internal const uint ReachTableLength = 0x58;

    /// <summary>The equipped weapon's class, obfuscated the usual way.</summary>
    /// <remarks>
    /// What <see cref="ReachTable"/> is indexed with, and the only thing that changes when a
    /// bow is put away — which is why putting the bow away was what made the character move
    /// properly again.
    /// </remarks>
    internal static GameAddress WeaponClass => new(0x00BDC7D4);

    /// <summary>
    /// A monster's health, as a percentage, at <c>+0x59</c> of its record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server's answer and the only one there is. The client has no idea what it has
    /// hurt — it starts a blow, plays the animation and moves its cooldown on whether or not
    /// the server allowed any of it — so this is the single field that separates a fight
    /// from a character swinging at something it is not permitted to hit.
    /// </para>
    /// <para>
    /// Read off a live client, sampled four times a second on whatever the client had
    /// targeted: <c>FF 4F 3C 29 16 01</c>, <c>5B 52 4A 41 38 2F 26 1E 16 0D</c>,
    /// <c>28 22 1D 18 13 0D 08 03 00</c>. Full to dead, one step per blow that landed.
    /// </para>
    /// <para>
    /// Next to <see cref="EntityIsGone"/> at <c>+0x58</c>, which is the same story one field
    /// later.
    /// </para>
    /// </remarks>
    internal const int EntityHealth = 0x59;

    /// <summary>
    /// What <see cref="EntityHealth"/> holds before the server has ever sent a health bar.
    /// </summary>
    /// <remarks>
    /// Which is to say: nothing has ever hurt this one. It is what the NPC spawn packet
    /// writes — <c>w.WriteC(0xFF)</c> — and it is left there until something lands, so a
    /// monster still reading it after a while of being shot at is a monster the server is
    /// refusing to let us touch.
    /// </remarks>
    internal const byte HealthUnknown = 0xFF;

    /// <summary>
    /// The scheduler's own queue: a count, a capacity, and a pointer to the entries.
    /// </summary>
    /// <remarks>
    /// Read rather than inferred, because "is the walk engine running" has no flag. One
    /// posted tick is one step, so posting into a queue that already holds one is a
    /// character that moves at double speed — which is what it did.
    /// </remarks>
    internal static GameAddress Scheduler => new(0x00C2D218);

    /// <summary>Where the entry array's address sits inside <see cref="Scheduler"/>.</summary>
    internal const int SchedulerEntries = 8;

    /// <summary>Bytes per entry: due time, callback, argument.</summary>
    internal const int SchedulerEntryLength = 0x0C;

    /// <summary>Where the callback sits inside an entry.</summary>
    internal const int SchedulerCallback = 4;

    /// <summary>More than the client has ever queued, and a bound on a length it hands out.</summary>
    /// <remarks>
    /// A count read out of a process that is free to be mid-write is a count that can be
    /// anything. Twenty entries is a busy moment; a thousand is a bad read.
    /// </remarks>
    internal const int SchedulerLimit = 1024;

    /// <summary>The other two the click path refuses to kick through.</summary>
    /// <remarks>
    /// Not chased down to a meaning. What matters is that <c>ClickHandler</c> will not call
    /// the engine while either is set, and a kick that ignores a condition the client
    /// respects is a kick that runs the engine somewhere the client would not have.
    /// </remarks>
    internal static GameAddress KickBlockerA => new(0x00C2D2C8);

    /// <inheritdoc cref="KickBlockerA"/>
    internal static GameAddress KickBlockerB => new(0x00C2D2C9);

    // The collision grid, read from TilePassable at 0x4F5910.

    /// <summary>The cell array. 33,540 cells of 20 bytes, allocated at <c>0x4E6C20</c>.</summary>
    internal static GameAddress GridCells => new(0x00ABF4C0);

    /// <summary>The index origin. Both move as the player crosses a block boundary.</summary>
    /// <remarks>
    /// <c>0x4F50E0</c> recycles blocks and then adds to these, so they change while the
    /// player walks and not only on a map change. Read them on every scan.
    /// </remarks>
    internal static GameAddress GridOriginX => new(0x00ABF978);

    /// <inheritdoc cref="GridOriginX"/>
    internal static GameAddress GridOriginY => new(0x00ABF97C);

    /// <summary>Columns to a row of the cell array.</summary>
    /// <remarks>
    /// <c>FUN_004F4BD0</c>, which is the client's whole index arithmetic:
    /// <c>(y - originY) * 0x100 + (x - originX)</c>. The array is a window that scrolls with
    /// the player, not the map.
    /// </remarks>
    internal const int GridStride = 0x100;

    /// <summary>How many rows of that window are allocated.</summary>
    /// <remarks>33,540 cells over a stride of 256. Anything past it is another map's memory.</remarks>
    internal const int GridRows = 33_540 / GridStride;

    /// <summary>How long one cell is.</summary>
    internal const int GridCellLength = 0x14;

    /// <summary>Where the attribute word sits in a cell, and which bit of it matters.</summary>
    /// <remarks>
    /// Bit 0 set means <em>blocked</em>, and it belongs to a heading rather than to the
    /// square: see <see cref="TileBlocked"/>, whose only unarguable reader is the client's own
    /// walk engine. A character standing on a cell that carries the bit is ordinary and says
    /// nothing about whether it may stand there.
    /// </remarks>
    internal const int GridCellAttribute = 4;

    /// <inheritdoc cref="GridCellAttribute"/>
    internal const ushort GridBlocked = 1;

    /// <summary>
    /// The bit that means the client will send the character somewhere else from here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured rather than guessed. Over a whole loaded window of a cave floor the attribute
    /// word takes four values and no more — 0x0000, 0x0001, 0x0008 and 0x0009 — except for a
    /// single cell carrying 0x000B, and that cell is the cave mouth. Its map descriptor agrees:
    /// <c>map8.map</c> holds one link, <c>to28</c>, at the same square.
    /// </para>
    /// <para>
    /// One cell in thirty-three thousand, which is what makes it worth trusting. A bit that
    /// meant something ordinary would be on thousands of them.
    /// </para>
    /// </remarks>
    internal const ushort GridTeleport = 0x0002;

    /// <summary>How far from a teleport the hunt refuses to walk, in tiles.</summary>
    /// <remarks>
    /// One. The square itself already refuses a step, so this is about not standing beside it:
    /// a character parked on the cave mouth is a character one server-side nudge away from
    /// another floor, and a hunt set going on one floor should stay on it.
    /// </remarks>
    internal const int TeleportClearance = 1;

    /// <summary>
    /// A bit the launcher writes into its own copy of the grid, meaning a creature is there.
    /// </summary>
    /// <remarks>
    /// The client's collision grid holds terrain and nothing else, but its walk engine will not
    /// step onto a square another creature is standing on — so a route worked out from terrain
    /// alone goes straight through the monster beside the one being hunted, and the character
    /// stands there being re-kicked until the leg times out. Read off the log as five seconds
    /// on one square with a destination four tiles away and nothing in the client's own state
    /// looking wrong.
    /// </remarks>
    internal const ushort GridCrowded = 0x8000;

    /// <summary>
    /// Eight pairs of column and row deltas, one per heading.
    /// </summary>
    /// <remarks>
    /// <c>StepByHeading</c> at <c>0x554C50</c> is nothing but a read of this:
    /// <c>x + DAT_00ACCDF8[h * 2]</c>, <c>y + DAT_00ACCDFC[h * 2]</c>. Read live it is
    /// <c>(0,-1) (2,-1) (2,0) (2,1) (0,1) (-2,1) (-2,0) (-2,-1)</c> — two columns to a tile
    /// across and one row down, which is the same aspect <see cref="GridDistance"/> divides
    /// by four for.
    /// </remarks>
    internal static GameAddress StepTable => new(0x00ACCDF8);

    /// <summary>How long one entry of <see cref="StepTable"/> is.</summary>
    internal const int StepTableEntry = 8;

    /// <summary>
    /// The four cell-offset tables <see cref="TileBlocked"/> decides a heading with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eight ints each, indexed by heading, and each entry is a cell-index offset — so a
    /// <c>(dx, dy)</c> comes out of it as <c>dy * <see cref="GridStride"/> + dx</c>.
    /// </para>
    /// <para>
    /// A straight heading reads the last of them alone. A diagonal is refused when either
    /// cell of either pair carries the bit, across all four — which is much looser than "both
    /// corners must be clear" and is the reason a hand-written corner rule gets this map
    /// wrong. The order here is the order the client tests them in.
    /// </para>
    /// </remarks>
    internal static GameAddress CornerTableA => new(0x00965F88);

    /// <inheritdoc cref="CornerTableA"/>
    internal static GameAddress CornerTableB => new(0x00965FA8);

    /// <inheritdoc cref="CornerTableA"/>
    internal static GameAddress CornerTableC => new(0x00965FC8);

    /// <summary>The one a straight heading is decided by on its own.</summary>
    /// <inheritdoc cref="CornerTableA"/>
    internal static GameAddress CornerTableD => new(0x00965FE8);

    // Per-sprite type table, read from Entity_combatTypeClass at 0x5AEBE0.

    /// <summary>A pointer to a byte per sprite id, saying what kind of thing it draws.</summary>
    /// <remarks>
    /// <c>0x5AEBE0</c> starts by indexing this with the entity's sprite id, and everything
    /// it does afterwards refines that first answer. <see cref="ActorType"/> is the only
    /// value the hunt cares about.
    /// </remarks>
    internal static GameAddress SpriteTypeTable => new(0x009A8F18);

    /// <summary>The value <see cref="SpriteTypeTable"/> holds for players and monsters.</summary>
    /// <remarks>
    /// Ground items read 0 and the client's own effects read 9, which is what keeps thirty
    /// dropped items and a pile of sparkles out of the candidate list. Town people read
    /// 0x0E: a scan of a town with twenty-six records in it — shopkeepers, a cow, the
    /// notice boy — offered nothing to attack, which is the answer wanted there.
    /// </remarks>
    internal const byte ActorType = 0x0A;

    // Entity record, offsets from the disassembly of 0x5AEBE0 and 0x5A3770.

    /// <summary>
    /// The set of creature ids currently acting on the character.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A vector: the id array at <see cref="AggressorIds"/>, how many of them are live at
    /// <see cref="AggressorCount"/>, and the room there is for them at twelve. The client adds
    /// to it from every action routine that plays a creature's action, once it has asked the
    /// action's own target list whether the player is in it; it removes from it when a
    /// creature stops; and it empties the whole thing on the way out of the world, in the same
    /// breath as it clears <see cref="LocalPlayer"/>.
    /// </para>
    /// <para>
    /// Read and never written. The ids in it are <see cref="EntityId"/> values and can be
    /// looked straight up in the scan — checked against a running client, where the set held
    /// the two monsters that were biting the character and emptied to one when the first of
    /// them died. See <see cref="Aggressors"/>.
    /// </para>
    /// </remarks>
    internal static GameAddress Aggressors => new(0x00C2D360);

    /// <summary>The id array inside <see cref="Aggressors"/>, as a byte offset into it.</summary>
    internal const int AggressorIds = 4;

    /// <summary>How many of <see cref="AggressorIds"/> are live, as a byte offset.</summary>
    /// <remarks>
    /// A signed int and not the short count an action's own target list carries. The two are
    /// different shapes and the client reads each with the right one — this through
    /// <c>cmp edx,[ecx+8]</c> in both the add and the remove.
    /// </remarks>
    internal const int AggressorCount = 8;


    /// <summary>The id a packet aims at. Distinct per creature.</summary>
    /// <remarks>
    /// Not the number inside the label at <see cref="EntityLabel"/> — that one is a template
    /// shared by every monster of a kind, and three of the same monster standing together
    /// all carry it.
    /// </remarks>
    internal const int EntityId = 0x0C;

    /// <summary>An action code. 8 is dying, 13 is burrowed, and the rest comes and goes.</summary>
    /// <remarks>
    /// <para>
    /// The same player record read 3 in one sample and 0 in another, and dropped items read
    /// 0 as well, so there is no "is it alive" test here — only "is it dying".
    /// </para>
    /// <para>
    /// The server writes its <c>NpcActionStatus</c> into the spawn packet this comes from,
    /// and every hidden state it has ends up here. See <see cref="IsHiddenAction"/> for the
    /// four values that means, and for why the byte is the only signal there is.
    /// </para>
    /// </remarks>
    internal const int EntityAction = 0x14;

    /// <summary>Burrowed: the default the server sends for <c>NpcHiddenSink</c>.</summary>
    internal const byte ActionSunk = 13;

    /// <summary>
    /// Hidden: the value shared by the rest of them.
    /// </summary>
    /// <remarks>
    /// <c>NpcActionStatus</c> falls back to this for <c>NpcHiddenFly</c>, <c>NpcHiddenIce</c>
    /// and <c>NpcHiddenShellman</c>, and four sinking monsters carry it explicitly.
    /// </remarks>
    internal const byte ActionHidden = 4;

    /// <summary>Airborne: the value one flying monster carries instead of 4.</summary>
    internal const byte ActionFlying = 11;

    /// <summary>Burrowed, for the one monster the server gives its own value.</summary>
    internal const byte ActionSunkDeep = 28;

    /// <summary>
    /// Whether a creature is in a state nothing the character does can land on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four values <c>NpcActionStatus</c> can return for a hidden creature, and the whole
    /// of what the server ever sends for one: sunk, hidden, airborne, deep. Everything not
    /// hidden goes out as zero.
    /// </para>
    /// <para>
    /// This byte is the only signal there is on this server. The spawn packet also has a
    /// field the client turns into the flag bits at <see cref="EntityFlags"/> — the ones
    /// <see cref="Untouchable"/> reads — but the server writes that field as zero for every
    /// monster it sends, so those bits never come on and the flag test never fires. Which is
    /// why a burrowed monster still ended up being shot at with the flag test already in.
    /// </para>
    /// <para>
    /// Three of the four are shared with something else, and the sharing is worse than it
    /// first looked. The server broadcasts an action of 4 when a hidden monster surfaces and
    /// 11 when one of a particular shape does; worse, <c>buildNpcAttackWithAction</c> puts a
    /// monster's own attack action on the wire, and that number comes out of its skill data
    /// and can be anything. So a small value here is a creature mid-animation at least as
    /// often as it is a creature underground.
    /// </para>
    /// <para>
    /// Which is why this is only used where being wrong costs a pass — choosing what to go
    /// for. It is deliberately <em>not</em> used to let go of a target already being fought:
    /// dropping one releases the client's attack chain and starts another, and a monster
    /// whose action byte flickers through 4 gets that done to it several times a second,
    /// which reads as the character's attack animation disappearing. That one tests
    /// <see cref="ActionSunk"/> and nothing else.
    /// </para>
    /// </remarks>
    internal static bool IsHiddenAction(byte action) =>
        action is ActionSunk or ActionHidden or ActionFlying or ActionSunkDeep;

    /// <summary>Sprite id, unsigned 16-bit. The upper half of the word holds flags.</summary>
    /// <remarks>
    /// The player read <c>0x00080F22</c> beside a corpse reading <c>0x00000F18</c>; both low
    /// words are real sprite numbers. Reading the whole dword finds nothing in any table.
    /// </remarks>
    internal const int EntitySprite = 0x18;

    /// <summary>Set on player characters, clear on monsters.</summary>
    internal const int EntityIsPlayer = 0x27;

    /// <summary>Flags saying what it takes to see or touch this one.</summary>
    /// <remarks>
    /// <para>
    /// Read by <c>0x5AE780</c>, which is what the client's own <c>Attackable</c> at
    /// <c>0x5AF180</c> ends in, which in turn is what its auto-attack chain gives up on.
    /// Every bit in <see cref="Untouchable"/> means "hidden or out of reach unless the
    /// player holds a particular effect or skill" — burrowed and flying among them.
    /// </para>
    /// <para>
    /// Read out here rather than by calling the client's routine. That routine walks the
    /// player's live effect collection, which the game's own thread is free to be changing,
    /// and walking it from anywhere else crashes the client — which is exactly what it did,
    /// fastest in the places with the most going on. A flag word in a record already read is
    /// the same answer for none of the risk.
    /// </para>
    /// </remarks>
    internal const int EntityFlags = 0x20;

    /// <summary>
    /// The bits that make a monster not worth choosing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two of the eight that <c>0x5AE780</c> refuses on: the ones answered by an effect
    /// rather than a skill, which are burrowed and flying. Both were asked for by name.
    /// </para>
    /// <para>
    /// The other six are deliberately not here. This word is not only a visibility mask —
    /// the client passes it straight to its own draw call, clearing bits 12 to 15 and
    /// setting bit 0 on the way, so those bits carry something else as well. Refusing every
    /// monster that has one of them set threw away most of a screen, and the ones it threw
    /// away included whatever was standing next to the character: the hunt would find
    /// nothing within retaliation range and go back to the far one it could still see.
    /// </para>
    /// <para>
    /// The refusal is unconditional where the client's is conditional — each bit has an
    /// effect that answers it — so a character who can genuinely see a burrowed monster
    /// passes it over. That is the safe direction: a monster skipped, rather than a
    /// character standing next to something it cannot hit.
    /// </para>
    /// </remarks>
    internal const uint Untouchable = 0x0080_0000 | 0x0100_0000;

    /// <summary>Tile coordinates, in whatever units the client itself uses.</summary>
    /// <remarks>
    /// Deliberately not decoded. <c>ComputeStepHeading</c> passes these two straight into
    /// the grid index, so the arithmetic that follows consumes exactly what is stored.
    /// </remarks>
    internal const int EntityX = 0x34;

    /// <inheritdoc cref="EntityX"/>
    internal const int EntityY = 0x38;

    /// <summary>Set on a corpse, clear on the living.</summary>
    internal const int EntityIsGone = 0x58;

    /// <summary>The label: <c>name#templateid:sprite</c>, or sometimes just the name.</summary>
    internal const int EntityLabel = 0x60;

    /// <summary>How much of a record is needed to read every field above.</summary>
    internal const int EntityLength = 0x70;

    /// <summary>The action code that means the creature is playing its death.</summary>
    internal const byte DyingAction = 8;
}
