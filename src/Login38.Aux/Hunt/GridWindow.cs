using Login38.Interop;

namespace Login38.Aux.Hunt;

/// <summary>
/// A copy of the client's collision window, and the client's own rules for crossing it.
/// </summary>
/// <remarks>
/// <para>
/// The client has no reachability query and its walk engine hill climbs, so it cannot go
/// round anything: <c>ComputeStepHeading</c> scores the eight headings by how near they leave
/// it and takes the best, with no memory and no lookahead. Replaying that climb answers
/// "would the client get there by itself", which turns out to be a much smaller question than
/// "is there a way" — and the difference is every monster round a corner.
/// </para>
/// <para>
/// So the grid is copied out instead and searched properly. One read of 670KB covers the
/// whole window, which is cheaper than one remote call, and what comes back is exact: the
/// step deltas and the corner offsets are the client's own tables, read at the same time
/// rather than written down here.
/// </para>
/// <para>
/// Nothing in the client is called and nothing is written. That matters for where this runs:
/// a snapshot is a read of memory the game thread is free to be changing, so a torn copy is
/// possible and is harmless — a cell that flips between two readings is a cell that was going
/// to be re-read next pass anyway.
/// </para>
/// <para>
/// Replicating rather than calling is a deliberate exception to the rule <see cref="WalkProbe"/>
/// follows, and it is only safe because what is replicated is <em>data</em>. The logic in
/// <see cref="Passable"/> is four lines of <c>TileBlocked</c>; the four offset tables and the
/// eight step deltas that make it mean anything are read out of the process every time this
/// is built. A search cannot be done a remote call at a time — a flood fill over thirty
/// thousand cells is thirty thousand of them — so the choice is this or no search at all.
/// </para>
/// </remarks>
internal sealed class GridWindow
{
    /// <summary>How long the whole window is, in bytes.</summary>
    /// <remarks>
    /// 670KB, which is over the threshold that puts an allocation on the large object heap.
    /// That is the reason this class is refreshed rather than rebuilt: one of these every
    /// pass is three megabytes a second of memory that is never compacted, in a process that
    /// runs for as long as somebody is playing.
    /// </remarks>
    internal const int Length = HuntAddresses.GridStride * HuntAddresses.GridRows
        * HuntAddresses.GridCellLength;

    private readonly byte[] _cells;
    private readonly int[] _stepX = new int[WalkProbe.Headings];
    private readonly int[] _stepY = new int[WalkProbe.Headings];
    private readonly int[][] _corners = new int[4][];

    private GridWindow(byte[] cells, int originX, int originY)
    {
        _cells = cells;
        OriginX = originX;
        OriginY = originY;
    }

    /// <summary>An empty one, to be filled by <see cref="Refresh"/>.</summary>
    /// <remarks>
    /// Built once and kept. Everything about it that changes — the cells, where they start,
    /// and the client's own tables — is re-read on every refresh, so a kept one never carries
    /// a stale answer; what is kept is the memory.
    /// </remarks>
    public GridWindow()
        : this(new byte[Length], 0, 0)
    {
    }

    /// <summary>
    /// One built from tables rather than from a process, for tests.
    /// </summary>
    /// <remarks>
    /// The tables are the point of the seam. What a test can hold about this class is that it
    /// applies the client's rule the way the client applies it, and that is only worth
    /// asserting against the client's real offsets — so a test hands in the numbers read out
    /// of a running client rather than a tidy invention, and a change of shape here fails
    /// against them.
    /// </remarks>
    /// <param name="cells">The window, cell by cell, twenty bytes each.</param>
    /// <param name="originX">Where its first cell sits.</param>
    /// <param name="originY">Where its first cell sits.</param>
    /// <param name="steps">A column and row delta per heading.</param>
    /// <param name="corners">Four tables of eight cell offsets, in the client's own order.</param>
    internal static GridWindow For(
        byte[] cells,
        int originX,
        int originY,
        (int X, int Y)[] steps,
        int[][] corners)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(corners);

        var window = new GridWindow(cells, originX, originY);

        window.OriginX = originX;
        window.OriginY = originY;

        for (var heading = 0; heading < WalkProbe.Headings; heading++)
        {
            window._stepX[heading] = steps[heading].X;
            window._stepY[heading] = steps[heading].Y;
        }

        for (var table = 0; table < corners.Length; table++)
        {
            window._corners[table] = corners[table];
        }

        return window;
    }

    /// <summary>Where the window's first cell sits, in the client's own coordinates.</summary>
    /// <remarks>
    /// It scrolls. <c>0x4F50E0</c> recycles blocks and adds to the origin as the player
    /// crosses a boundary, so a snapshot taken before a walk describes a different window
    /// from one taken after it.
    /// </remarks>
    public int OriginX { get; private set; }

    /// <inheritdoc cref="OriginX"/>
    public int OriginY { get; private set; }

    /// <summary>
    /// Takes a fresh copy over the top of this one.
    /// </summary>
    /// <returns>Whether there is a world to copy.</returns>
    /// <remarks>
    /// The origin is read before the cells and the tables after them, which is deliberate but
    /// not a guarantee: the game thread is free to scroll the window mid-read, so a copy can
    /// disagree with itself along one edge. That is harmless and is not worth locking for — a
    /// cell that moved between two readings is a cell about to be read again next pass, and
    /// the cost of a wrong one is a target picked or passed over for a fifth of a second.
    /// </remarks>
    public bool Refresh(RemoteProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!process.TryRead<uint>(HuntAddresses.GridCells, out var cells) || cells == 0
            || !process.TryRead<int>(HuntAddresses.GridOriginX, out var originX)
            || !process.TryRead<int>(HuntAddresses.GridOriginY, out var originY))
        {
            return false;
        }

        OriginX = originX;
        OriginY = originY;

        if (!process.TryReadBytes(new GameAddress(cells), _cells) || !Learn(process))
        {
            return false;
        }

        Fence();

        return true;
    }

    /// <summary>
    /// Walls off the squares around anything that would send the character elsewhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hunt is set going on a floor and belongs on that floor. Walking onto a cave mouth
    /// takes the character to a map where nothing it had been doing means anything — a route
    /// across ground it has left, an ignore list of monsters that are not there — and the way
    /// back is a walk it has no idea how to make.
    /// </para>
    /// <para>
    /// Written into the launcher's copy rather than the client's, like the crowding mark, so
    /// the player's own walking is untouched: this refuses the hunt's routes and nothing else.
    /// </para>
    /// </remarks>
    internal void Fence()
    {
        // Gathered before anything is written, because Close writes the same word this reads
        // and a fenced square would otherwise be read as a teleport of its own.
        List<(int X, int Y)>? mouths = null;

        for (var cell = 0; cell < HuntAddresses.GridStride * HuntAddresses.GridRows; cell++)
        {
            if (!Attribute(cell, HuntAddresses.GridTeleport))
            {
                continue;
            }

            mouths ??= [];
            mouths.Add((
                (cell % HuntAddresses.GridStride) + OriginX,
                (cell / HuntAddresses.GridStride) + OriginY));
        }

        if (mouths is null)
        {
            return;
        }

        foreach (var (x, y) in mouths)
        {
            // Two columns to a tile across, one row down.
            for (var dy = -HuntAddresses.TeleportClearance; dy <= HuntAddresses.TeleportClearance; dy++)
            {
                for (var dx = -HuntAddresses.TeleportClearance * 2;
                     dx <= HuntAddresses.TeleportClearance * 2;
                     dx++)
                {
                    Close(x + dx, y + dy);
                }
            }
        }
    }

    /// <summary>
    /// Whether the client would allow this heading out of this square.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TileBlocked</c> at <c>0x4F5910</c>, in as many lines, and then negated — that
    /// routine answers "is this refused", which its only unarguable reader,
    /// <c>ComputeStepHeading</c>, treats as the wall case. A straight heading is refused when
    /// its own cell carries the bit; a diagonal when either cell of either pair does, which
    /// is looser than it sounds and is the part no hand-written corner rule gets right.
    /// </para>
    /// <para>
    /// The offsets and the deltas are read out of the process, so the only thing written down
    /// here is the shape of the test.
    /// </para>
    /// </remarks>
    public bool Passable(int x, int y, int heading)
    {
        var cell = Cell(x, y);

        if ((heading & 1) == 0)
        {
            return !Bit(cell + _corners[3][heading]);
        }

        var a = Bit(cell + _corners[0][heading]);
        var d = Bit(cell + _corners[3][heading]);

        if (!a && !d)
        {
            return true;
        }

        var b = Bit(cell + _corners[1][heading]);
        var c = Bit(cell + _corners[2][heading]);

        return !((d && c) || (d && b) || (a && c) || (a && b));
    }

    /// <summary>Where a heading lands, as <c>StepByHeading</c> would put it.</summary>
    public (int X, int Y) Step(int x, int y, int heading) =>
        (x + _stepX[heading], y + _stepY[heading]);

    /// <summary>Whether a square is inside the window at all.</summary>
    /// <remarks>
    /// The window is a slice of the map and the search runs to its edge. A square outside it
    /// is not impassable — it is unknown — but the two have to be treated the same, because
    /// the alternative is indexing another map's memory and believing what comes back.
    /// </remarks>
    public bool Holds(int x, int y)
    {
        var column = x - OriginX;
        var row = y - OriginY;

        return column >= 0 && column < HuntAddresses.GridStride
            && row >= 0 && row < HuntAddresses.GridRows;
    }

    /// <summary>Puts the blocked bit on one cell, for a test to draw a wall with.</summary>
    /// <remarks>
    /// A map here is one bit per cell, so building one out of a byte array by hand reads as
    /// arithmetic rather than as a map. This is the only thing that writes to the copy, and
    /// it writes to the copy — the client's own grid is never touched by anything in here.
    /// </remarks>
    /// <summary>Whether one square carries the blocked bit at all.</summary>
    /// <remarks>
    /// <para>
    /// Asked of a square rather than of a heading out of one, which is what
    /// <see cref="Passable"/> answers and is a different question. Movement obeys the corner
    /// tables; a blow or an arrow does not, and testing it against them refuses shots that
    /// hug a wall — the exact shots wanted at a monster standing against one.
    /// </para>
    /// <para>
    /// Outside the window counts as blocked, because nothing can be seen through ground the
    /// client is not holding collision for.
    /// </para>
    /// </remarks>
    public bool Blocked(int x, int y) => !Holds(x, y) || Bit(Cell(x, y));

    /// <summary>
    /// Whether a creature is standing on a square, in this copy of the grid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked by the search about a square it is thinking of stepping <em>into</em>, which is
    /// the one thing the client's own tables cannot express. They describe leaving a cell —
    /// the straight heading north reads the cell it is leaving, not the one it is entering —
    /// so a bit set where a creature stands does not stop a step from the south, and no
    /// arrangement of bits would. A creature is not terrain and does not get to pretend.
    /// </para>
    /// <para>
    /// Movement only. Something standing between the character and a monster is in the way of
    /// a step and not of an arrow, and counting it as solid would put line of sight back where
    /// it started, with monsters in a pack unhittable because of each other.
    /// </para>
    /// </remarks>
    public bool Crowded(int x, int y) =>
        Holds(x, y) && Attribute(Cell(x, y), HuntAddresses.GridCrowded);

    /// <summary>
    /// Says a creature is standing on a tile, in this copy of the grid only.
    /// </summary>
    /// <remarks>
    /// Both columns of it, because two make a tile across and a creature standing on one is
    /// standing on the other. Written into the copy and gone on the next
    /// <see cref="Refresh"/>, which is what makes it safe to be wrong about: a monster that
    /// moves costs one route and no more.
    /// </remarks>
    public void Crowd(int x, int y)
    {
        var column = x & ~1;

        Mark(column, y);
        Mark(column + 1, y);
    }

    private void Mark(int x, int y)
    {
        if (!Holds(x, y))
        {
            return;
        }

        var at = (Cell(x, y) * HuntAddresses.GridCellLength) + HuntAddresses.GridCellAttribute;
        var attribute = (ushort)(BitConverter.ToUInt16(_cells, at) | HuntAddresses.GridCrowded);

        BitConverter.TryWriteBytes(_cells.AsSpan(at), attribute);
    }

    internal void Close(int x, int y)
    {
        if (!Holds(x, y))
        {
            return;
        }

        var at = (Cell(x, y) * HuntAddresses.GridCellLength) + HuntAddresses.GridCellAttribute;
        var attribute = (ushort)(BitConverter.ToUInt16(_cells, at) | HuntAddresses.GridBlocked);

        BitConverter.TryWriteBytes(_cells.AsSpan(at), attribute);
    }

    /// <summary>The client's own sort key, and the reason it is not a straight line.</summary>
    /// <remarks>
    /// <c>GridDistance</c> at <c>0x554950</c>: the column difference squared over four plus
    /// the row difference squared. The four is the grid being twice as fine across as it is
    /// down, which is the same aspect the step deltas carry.
    /// </remarks>
    public static int Distance(int ax, int ay, int bx, int by)
    {
        var across = ax - bx;
        var down = ay - by;

        return (across * across / 4) + (down * down);
    }

    /// <summary>Reads the tables that make the rules mean something.</summary>
    private bool Learn(RemoteProcess process)
    {
        Span<byte> steps = stackalloc byte[WalkProbe.Headings * HuntAddresses.StepTableEntry];

        if (!process.TryReadBytes(HuntAddresses.StepTable, steps))
        {
            return false;
        }

        for (var heading = 0; heading < WalkProbe.Headings; heading++)
        {
            var at = heading * HuntAddresses.StepTableEntry;

            _stepX[heading] = BitConverter.ToInt32(steps[at..]);
            _stepY[heading] = BitConverter.ToInt32(steps[(at + 4)..]);
        }

        GameAddress[] tables =
        [
            HuntAddresses.CornerTableA,
            HuntAddresses.CornerTableB,
            HuntAddresses.CornerTableC,
            HuntAddresses.CornerTableD,
        ];

        Span<byte> offsets = stackalloc byte[WalkProbe.Headings * sizeof(int)];

        for (var table = 0; table < tables.Length; table++)
        {
            if (!process.TryReadBytes(tables[table], offsets))
            {
                return false;
            }

            _corners[table] = new int[WalkProbe.Headings];

            for (var heading = 0; heading < WalkProbe.Headings; heading++)
            {
                _corners[table][heading] = BitConverter.ToInt32(offsets[(heading * sizeof(int))..]);
            }
        }

        return true;
    }

    private int Cell(int x, int y) =>
        ((y - OriginY) * HuntAddresses.GridStride) + (x - OriginX);

    /// <summary>
    /// Whether a cell index carries the blocked bit, with anything off the end saying no.
    /// </summary>
    private bool Bit(int cell) => Attribute(cell, HuntAddresses.GridBlocked);

    private bool Attribute(int cell, ushort mask)
    {
        var at = cell * HuntAddresses.GridCellLength;

        if (at < 0 || at + HuntAddresses.GridCellAttribute + sizeof(ushort) > _cells.Length)
        {
            return false;
        }

        return (BitConverter.ToUInt16(_cells, at + HuntAddresses.GridCellAttribute) & mask) != 0;
    }
}
