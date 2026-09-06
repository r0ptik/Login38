using Login38.Aux.Settings;
using Login38.Interop;
using Microsoft.Extensions.Logging;

namespace Login38.Aux.Toggles;

/// <summary>
/// Lets the client idle instead of spinning a core.
/// </summary>
/// <remarks>
/// <para>
/// A hook rather than a byte flip, so unlike the other toggles this one has something to
/// remember: where its cave is and what it took from the front of <c>PeekMessageA</c>.
/// That state belongs to one game. The reference kept it in a process-wide static, so with
/// two clients running the second one's switch did nothing at all and the first one's
/// switch would have restored the second one's bytes.
/// </para>
/// <para>
/// The cave is allocated once and kept for the life of the game, including while the
/// toggle is off. Freeing it is not safe — a thread can still be inside the trampoline —
/// and allocating a new one per flip costs a 64 KB reservation each time, because that is
/// the granularity <c>VirtualAllocEx</c> reserves at whatever size is asked for.
/// </para>
/// </remarks>
public sealed class LowCpuToggle : IGameToggle
{
    private readonly ILogger<LowCpuToggle> _logger;

    private GameAddress? _cave;
    private Installed? _installed;
    private bool _reported;

    public LowCpuToggle(ILogger<LowCpuToggle> logger) => _logger = logger;

    /// <inheritdoc/>
    public string Name => "low-cpu";

    /// <inheritdoc/>
    public bool WantedBy(AuxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.Misc.LowCpu;
    }

    /// <summary>What the hook needs to be able to take itself out again.</summary>
    private sealed record Installed(
        LowCpuLayout Layout, GameAddress PeekMessage, byte[] Stolen, byte[] Jump);

    /// <inheritdoc/>
    public bool Apply(RemoteProcess process, bool wanted)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            // Whether the hook is in is a question about the game, not about what this
            // object remembers: the client can be restarted, and another tool can take the
            // hook out from under it.
            if (_installed is { } present && !StillThere(process, present))
            {
                _logger.LogInformation("{Toggle}: the hook is no longer in place", Name);
                _installed = null;
            }

            // Nothing to do for a hook that is already in: what it compares against is the
            // game's process id, which is fixed for the life of the game. The window handle
            // it used to hold was not, and keeping that one current was a whole method.
            var result = (wanted, _installed) switch
            {
                (true, null) => Install(process),
                (true, not null) => true,
                (false, { } installed) => Remove(process, installed),
                (false, null) => true,
            };

            if (result)
            {
                _reported = false;
            }

            return result;
        }
        catch (GameProcessException e)
        {
            return Report($"{Name}: {e.Message}");
        }
    }

    private bool Install(RemoteProcess process)
    {
        var api = LowCpuApi.Resolve(process);
        var stolen = process.ReadBytes(api.PeekMessage, LowCpuDetour.StolenLength);

        // Somebody else's detour. Writing over it would take out their trampoline, and
        // taking the bytes for our own would send their calls into the middle of it.
        if (AlreadyHooked(stolen))
        {
            return Report(
                $"{Name}: PeekMessageA at {api.PeekMessage} already starts with " +
                $"{BytePattern.Format(stolen.AsSpan(0, 1))}, so something else is hooked there.");
        }

        // How much room it needs does not depend on where it goes, so this can be worked
        // out before there is anywhere to put it.
        var size = LowCpuDetour.LayoutFor(new GameAddress(0), stolen.Length).Size;

        _cave ??= process.AllocateExecutable(size);
        var layout = LowCpuDetour.LayoutFor(_cave.Value, stolen.Length);

        process.WriteCode(_cave.Value, LowCpuDetour.Build(_cave.Value, api, process.Id, stolen));

        var jump = new ShellcodeBuilder(api.PeekMessage).JumpTo(_cave.Value).Build();

        // Last, and this is the ordering that matters: the cave has to be complete before
        // anything is sent to it, and this write is what starts sending.
        process.WriteCode(api.PeekMessage, jump);

        _installed = new Installed(layout, api.PeekMessage, stolen, jump);

        _logger.LogInformation(
            "{Toggle} switched on; PeekMessageA at {Peek} now goes through {Cave}",
            Name, api.PeekMessage, _cave.Value);

        return true;
    }

    private bool Remove(RemoteProcess process, Installed installed)
    {
        process.WriteCode(installed.PeekMessage, installed.Stolen);
        _installed = null;

        // The cave stays. A thread can be inside the trampoline right now, and it has to
        // be able to finish returning through it.
        _logger.LogInformation("{Toggle} switched off", Name);

        return true;
    }

    /// <summary>
    /// Whether something else is already standing in front of this function.
    /// </summary>
    /// <remarks>
    /// A jump at the very first byte is what a detour looks like, and it is not
    /// what any real function prologue looks like.
    /// </remarks>
    internal static bool AlreadyHooked(ReadOnlySpan<byte> prologue) =>
        !prologue.IsEmpty && prologue[0] is 0xE9 or 0xEB;

    /// <summary>Whether the jump this toggle wrote is still the one at the front.</summary>
    private static bool StillThere(RemoteProcess process, Installed installed)
    {
        Span<byte> current = stackalloc byte[installed.Jump.Length];

        return process.TryReadBytes(installed.PeekMessage, current) && current.SequenceEqual(installed.Jump);
    }

    /// <summary>Logs once per spell of trouble rather than once per pass.</summary>
    private bool Report(string message)
    {
        if (!_reported)
        {
            _logger.LogWarning("{Message}", message);
            _reported = true;
        }

        return false;
    }
}
