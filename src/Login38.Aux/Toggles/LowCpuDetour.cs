using Login38.Interop;

namespace Login38.Aux.Toggles;

/// <summary>
/// Where the detour's entry points live, read out of the game's own module list.
/// </summary>
/// <param name="PeekMessage"><c>user32!PeekMessageA</c> — the function stood in front of.</param>
/// <param name="ForegroundWindow"><c>user32!GetForegroundWindow</c>.</param>
/// <param name="AsyncKeyState"><c>user32!GetAsyncKeyState</c>.</param>
/// <param name="Sleep"><c>kernel32!Sleep</c>.</param>
/// <param name="WindowThreadProcessId"><c>user32!GetWindowThreadProcessId</c>.</param>
/// <remarks>
/// The reference resolved all four in the launcher with <c>GetProcAddress</c> and wrote
/// those addresses into the game. System DLLs do share a base across processes within a
/// boot, which is why that worked — but it is a property of how Windows happens to
/// relocate them rather than a guarantee, and here it decides where a five-byte jump gets
/// written. Reading the target's own export table costs one module lookup.
/// </remarks>
internal sealed record LowCpuApi(
    GameAddress PeekMessage,
    GameAddress ForegroundWindow,
    GameAddress AsyncKeyState,
    GameAddress Sleep,
    GameAddress WindowThreadProcessId)
{
    private const string UserModule = "user32.dll";

    private const string KernelModule = "kernel32.dll";

    /// <summary>Both are loaded before the client's first instruction runs.</summary>
    internal static LowCpuApi Resolve(RemoteProcess process)
    {
        var user = Module(process, UserModule);
        var kernel = Module(process, KernelModule);

        return new LowCpuApi(
            Export(process, user, UserModule, "PeekMessageA"),
            Export(process, user, UserModule, "GetForegroundWindow"),
            Export(process, user, UserModule, "GetAsyncKeyState"),
            Export(process, kernel, KernelModule, "Sleep"),
            Export(process, user, UserModule, "GetWindowThreadProcessId"));
    }

    private static GameAddress Module(RemoteProcess process, string name) =>
        process.FindModule(name) ?? throw new GameProcessException($"{name} is not loaded in the game.");

    private static GameAddress Export(RemoteProcess process, GameAddress module, string moduleName, string export) =>
        process.FindExport(module, export)
        ?? throw new GameProcessException($"{moduleName} at {module} does not export {export}.");
}

/// <summary>
/// Where each part of the cave sits.
/// </summary>
/// <remarks>
/// Worked out from the detour's own length rather than fixed. The reference put the
/// trampoline at a hand-chosen <c>0x60</c> and checked the detour still fitted with
/// <c>debug_assert!</c> — which release builds drop, so a detour that grew past it would
/// have written the trampoline over its own tail in the build players run.
/// </remarks>
internal readonly record struct LowCpuLayout(GameAddress Cave, int DetourLength, int StolenLength)
{
    /// <summary>The stolen bytes, then a jump back into the middle of the real function.</summary>
    public GameAddress Trampoline => Cave + DetourLength;

    private GameAddress Slots => Trampoline + StolenLength + LowCpuDetour.JumpLength;

    /// <summary><c>user32!GetForegroundWindow</c>, called indirectly.</summary>
    public GameAddress ForegroundWindowSlot => Slots;

    /// <summary><c>kernel32!Sleep</c>.</summary>
    public GameAddress SleepSlot => Slots + 4;

    /// <summary>
    /// The game's own process id, compared against whoever owns the window in front.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A process rather than a window, and that is the whole of a defect players reported
    /// as the client no longer moving or attacking continuously. This client keeps more
    /// than one top-level window, so the handle the launcher found is often not the one
    /// holding the focus — see <c>GameWindow.IsForeground</c>, which asks the same question
    /// by process for the same reason. Comparing handles read a client the player was
    /// actively playing as one sitting in the background.
    /// </para>
    /// <para>
    /// Which mattered because of what came next: nothing about clicking the ground to walk
    /// or a monster to attack holds a key down, so the throttle engaged in the gap between
    /// every click. The client woke for the click, took one step or one swing, and slept
    /// again — a game that had to be clicked once per action.
    /// </para>
    /// <para>
    /// The process id also cannot go stale, so unlike the handle it was replaced with there
    /// is nothing to keep following as the client recreates its window.
    /// </para>
    /// </remarks>
    public GameAddress ProcessIdSlot => Slots + 8;

    /// <summary><c>user32!GetAsyncKeyState</c>.</summary>
    public GameAddress AsyncKeyStateSlot => Slots + 12;

    /// <summary><c>user32!GetWindowThreadProcessId</c>.</summary>
    public GameAddress WindowThreadProcessIdSlot => Slots + 16;

    /// <summary>How much to allocate.</summary>
    public int Size => (int)(WindowThreadProcessIdSlot.Value - Cave.Value) + 4;
}

/// <summary>
/// Stops the client burning a core while nobody is looking at it.
/// </summary>
/// <remarks>
/// <para>
/// The client's message loop calls <c>PeekMessageA</c> as fast as it can. That is what a
/// game does, and it is the right thing while somebody is playing; it is not the right
/// thing for the several hours a character spends standing in a market with the window
/// behind everything else.
/// </para>
/// <para>
/// So this stands in front of <c>PeekMessageA</c> and sleeps 50 ms when, and only when,
/// all three of these hold: the call found no message, the window in front belongs to
/// another process, and no key is being held. Sleeping on a call that found a message
/// would let input pile up; sleeping while the player is looking at the game would make
/// it feel broken, and did — see <see cref="LowCpuLayout.ProcessIdSlot"/>.
/// </para>
/// <para>
/// It hooks <c>user32</c> in the game rather than the game's import table on purpose. The
/// 3.8 packer encrypts the client's imports into a daisy chain of jump thunks, so there is
/// no import slot to redirect. Writing into <c>user32</c> is safe because
/// <c>WriteProcessMemory</c> copies the page on write: the game gets a private copy and
/// every other process on the machine keeps the original.
/// </para>
/// </remarks>
internal static class LowCpuDetour
{
    /// <summary>How long to sleep when the client has nothing to do.</summary>
    /// <remarks>
    /// Long enough to drop the core to nothing, short enough that a window brought back to
    /// the front feels immediate.
    /// </remarks>
    internal const byte SleepMilliseconds = 50;

    /// <summary>How many bytes are taken from the front of <c>PeekMessageA</c>.</summary>
    /// <remarks>The length of the <c>jmp rel32</c> written over them.</remarks>
    internal const int StolenLength = 5;

    /// <summary>The length of a <c>jmp rel32</c>.</summary>
    internal const int JumpLength = 5;

    /// <summary><c>PeekMessageA</c> takes five arguments and cleans them up itself.</summary>
    internal const byte ArgumentBytes = 0x14;

    /// <summary>The bit <c>GetAsyncKeyState</c> sets for a key that is down now.</summary>
    internal const ushort KeyDownBit = 0x8000;

    /// <summary>The first virtual key worth asking about.</summary>
    internal const byte FirstVirtualKey = 1;

    /// <summary>One past the last. <c>0xFF</c> is not a key.</summary>
    internal const uint VirtualKeyLimit = 0xFF;

    /// <summary>Works out where everything in the cave goes.</summary>
    /// <remarks>
    /// The detour's length does not depend on any of the addresses in it — every
    /// instruction it uses is a fixed size — so building it against a placeholder gives
    /// the real length, and that is what everything after it is placed from.
    /// </remarks>
    internal static LowCpuLayout LayoutFor(GameAddress cave, int stolenLength)
    {
        var placeholder = new LowCpuLayout(cave, 0, stolenLength);

        return new LowCpuLayout(cave, BuildDetour(placeholder).Length, stolenLength);
    }

    /// <summary>Builds the whole cave: detour, trampoline, then the four slots.</summary>
    internal static byte[] Build(
        GameAddress cave, LowCpuApi api, uint processId, ReadOnlySpan<byte> prologue)
    {
        var layout = LayoutFor(cave, prologue.Length);
        var code = new List<byte>(layout.Size);

        code.AddRange(BuildDetour(layout));

        // The trampoline is the front of PeekMessageA, moved here, followed by a jump to
        // the rest of it. Calling this is calling the real function.
        code.AddRange(prologue);
        code.AddRange(
            new ShellcodeBuilder(layout.Trampoline + prologue.Length)
                .JumpTo(api.PeekMessage + StolenLength)
                .Build());

        code.AddRange(Slot(api.ForegroundWindow.Value));
        code.AddRange(Slot(api.Sleep.Value));
        code.AddRange(Slot(processId));
        code.AddRange(Slot(api.AsyncKeyState.Value));
        code.AddRange(Slot(api.WindowThreadProcessId.Value));

        return [.. code];
    }

    private static byte[] Slot(uint value) => BitConverter.GetBytes(value);

    private static byte[] BuildDetour(LowCpuLayout layout)
    {
        var code = new ShellcodeBuilder(layout.Cave);

        // Hand the caller's five arguments to the real function. Each push moves esp, so
        // reading the same displacement five times walks backwards through them and leaves
        // them in the order the call wants.
        for (var i = 0; i < 5; i++)
        {
            code.PushStackSlot(ArgumentBytes);
        }

        code.CallTo(layout.Trampoline);

        // A call that found a message is a busy client. Sleeping here would let input pile
        // up behind the delay.
        code.TestEaxEax();
        var hasMessage = code.ShortJumpIfNotZero();

        // Somebody looking at the game is somebody playing — asked by process rather than
        // by handle, because the client keeps more than one top-level window and which of
        // them holds the focus is its own business.
        //
        // The out parameter is a slot of the stack rather than of the cave, so two threads
        // inside PeekMessageA at once cannot overwrite each other's answer. The zero pushed
        // first is what stays there if the call fails, and no process is process zero.
        code.CallIndirect(layout.ForegroundWindowSlot);
        code.PushImm8(0);
        code.PushEsp();
        code.PushEax();
        code.CallIndirect(layout.WindowThreadProcessIdSlot);
        code.PopEax();
        code.CmpEaxPtr(layout.ProcessIdSlot);
        var inFront = code.ShortJumpIfZero();

        // A held key means the client is being driven, whether or not it is in front — the
        // helper's own macros do exactly that to a window in the background.
        code.PushEbx();
        code.XorEbxEbx();
        code.IncEbx();

        var nextKey = code.Here();
        code.PushEbx();
        code.CallIndirect(layout.AsyncKeyStateSlot);
        code.TestAx(KeyDownBit);
        var keyDown = code.ShortJumpIfNotZero();
        code.IncEbx();
        code.CmpEbx(VirtualKeyLimit);
        code.ShortJumpBack(0x7C, nextKey);

        code.PopEbx();
        code.PushImm8(SleepMilliseconds);
        code.CallIndirect(layout.SleepSlot);
        var slept = code.ShortJumpAlways();

        code.MarkLabel(keyDown);
        code.PopEbx();

        code.MarkLabel(inFront);
        code.MarkLabel(slept);

        // The real call returned zero, and so does this. Setting it again costs a byte and
        // means the sleep path cannot leak whatever GetAsyncKeyState or Sleep left behind.
        code.XorEaxEax();

        code.MarkLabel(hasMessage);
        code.RetAndPop(ArgumentBytes);

        return code.Build();
    }
}
