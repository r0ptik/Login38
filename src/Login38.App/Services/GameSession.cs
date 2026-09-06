using Login38.Aux.Settings;
using Login38.Core.Net;
using Login38.Core.Servers;
using Login38.Interop;
using Login38.Patching;
using Microsoft.Extensions.Logging;

namespace Login38.App.Services;

/// <summary>
/// One running game, from the moment it is created until it exits.
/// </summary>
/// <remarks>
/// <para>
/// The reference started a second copy of the launcher to do this work, so that the first
/// could exit while patching continued. Here it is a background task: the launcher stays
/// running anyway to drive the helper features, so there was never anything for the second
/// process to buy.
/// </para>
/// <para>
/// Patching is deliberately not awaited by the caller. The first patch waits for the
/// client's packer to finish, which takes seconds and occasionally most of a minute, and
/// there is nothing useful to do with that time except show progress.
/// </para>
/// </remarks>
public sealed class GameSession : IAsyncDisposable
{
    /// <summary>
    /// How long to wait for the player to reach the world before giving up on the patches
    /// that need them there.
    /// </summary>
    /// <remarks>
    /// Generous: this covers reading the server list, choosing a character and whatever
    /// queue the server puts them through.
    /// </remarks>
    private static readonly TimeSpan InWorldTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to wait for the client to show its window.
    /// </summary>
    /// <remarks>
    /// It unpacks itself, loads its data and initialises DirectDraw first, so this is
    /// seconds even on a fast machine — and much longer on a cold start behind a real-time
    /// scanner.
    /// </remarks>
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(90);

    private readonly LaunchedGameProcess _game;
    private readonly InstanceSlot _slot;
    private readonly PacketEncryptProxy? _relay;
    private readonly AuxSession _aux;
    private readonly CancellationTokenSource _cancellation;
    private readonly ILogger _logger;

    private bool _disposed;

    /// <summary>
    /// Completes once the startup patches have been attempted, which is when the client's
    /// own code has been decrypted and is worth looking at.
    /// </summary>
    private readonly TaskCompletionSource _unpacked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal GameSession(
        LaunchedGameProcess game,
        InstanceSlot slot,
        GamePatchContext context,
        PatchPipeline pipeline,
        AuxSession aux,
        ILogger logger,
        PacketEncryptProxy? relay,
        CancellationToken cancellationToken)
    {
        _game = game;
        _slot = slot;
        _relay = relay;
        _aux = aux;
        _logger = logger;
        HuntingOffered = context.Aux.InternalBotEnabled;

        // Told to the helper loop as well as to the window. The window's copy only decides
        // which tab is drawn and what is written back; this is what the loop reads before it
        // will touch the client at all.
        if (HuntingOffered)
        {
            aux.Offer.Offer();
        }
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Patching = Task.Run(() => PatchAsync(context, pipeline, _cancellation.Token), CancellationToken.None);
        Helping = Task.Run(() => HelpAsync(_cancellation.Token), CancellationToken.None);
        Completion = Task.Run(WaitForExitAsync, CancellationToken.None);
    }

    /// <summary>The game's process id.</summary>
    public uint ProcessId => _game.Id;

    /// <summary>Whether the operator's config offers automatic hunting.</summary>
    /// <remarks>
    /// Taken once, at launch, because it belongs to the server list this game was started
    /// from and cannot change while it is running.
    /// </remarks>
    public bool HuntingOffered { get; }

    /// <summary>Completes when every phase of patching has been attempted.</summary>
    public Task<IReadOnlyList<PatchOutcome>> Patching { get; }

    /// <summary>Completes when the helper features have stopped.</summary>
    public Task Helping { get; }

    /// <summary>
    /// This game's helper settings, for the window to show and edit.
    /// </summary>
    /// <remarks>
    /// Per game rather than per launcher: with several clients up, each has its own
    /// character playing and its own set of switches.
    /// </remarks>
    public AuxSettingsSource AuxSettings => _aux.Settings;

    /// <summary>The rest of this game's helper, for the window that shows its settings.</summary>
    public AuxSession Helper => _aux;

    /// <summary>
    /// Completes once the client has finished unpacking and the startup patches are done.
    /// </summary>
    /// <remarks>
    /// The long part of a launch, and the part a player is actually waiting through: the
    /// client decrypts itself before any of its code can be matched against, which on a
    /// cold start is most of a minute. Everything after this happens while the game is on
    /// screen.
    /// </remarks>
    public Task Unpacked => _unpacked.Task;

    /// <summary>Completes when the game exits.</summary>
    public Task Completion { get; }

    /// <summary>Whether the game is still running.</summary>
    public bool IsRunning => _game.Process.IsRunning;

    private async Task<IReadOnlyList<PatchOutcome>> PatchAsync(
        GamePatchContext context, PatchPipeline pipeline, CancellationToken cancellationToken)
    {
        var outcomes = new List<PatchOutcome>();

        try
        {
            try
            {
                outcomes.AddRange(pipeline.Apply(context, PatchPhase.Startup, cancellationToken));
            }
            finally
            {
                // Whatever happened, this is as unpacked as the client is going to get, and
                // the helper is waiting to be told so.
                _unpacked.TrySetResult();
            }

            // After the client is usable, not before. This one waits for Winsock, which
            // the client loads only when it is about to connect — about half a minute in —
            // and there is nothing about it the other patches or the helper need.
            outcomes.AddRange(pipeline.Apply(context, PatchPhase.Networking, cancellationToken));

            // The later phases wait on a state the client may never reach: it can fail to
            // start, or the player can close it at the character screen. Neither is a
            // failure of this launcher, so the wait ends quietly rather than throwing.
            await RunWhenReadyAsync(
                PatchPhase.WindowVisible, WindowTimeout,
                "The game never showed a window").ConfigureAwait(false);

            await RunWhenReadyAsync(
                PatchPhase.InWorld, InWorldTimeout,
                "The player never entered the world").ConfigureAwait(false);

            async Task RunWhenReadyAsync(PatchPhase phase, TimeSpan timeout, string missed)
            {
                if (await PatchPipeline.WaitForPhaseAsync(context, phase, timeout, cancellationToken)
                        .ConfigureAwait(false))
                {
                    outcomes.AddRange(pipeline.Apply(context, phase, cancellationToken));
                }
                else
                {
                    _logger.LogInformation("{Reason}; skipping the {Phase} patches", missed, phase);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Patching stopped because the launcher is closing");
        }

        return outcomes;
    }

    /// <summary>
    /// Runs the helper features, once there is a client worth reading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Started after the startup patches rather than with the process. Most of the client
    /// is ciphertext until its packer has finished, and a toggle reading its own address
    /// during that window sees bytes that are neither of the two states it knows — so it
    /// correctly refuses to act, and correctly says so in the log. Waiting keeps that line
    /// out of the log of every ordinary launch.
    /// </para>
    /// <para>
    /// Not awaited by anything the player is waiting on. The helper runs for as long as the
    /// game does.
    /// </para>
    /// </remarks>
    private async Task HelpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _unpacked.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _aux.Host.RunAsync(_game.Process, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The launcher is closing before the client finished unpacking. The host does
            // its own tidying up; there is nothing here that has been started yet.
        }
    }

    private async Task WaitForExitAsync()
    {
        try
        {
            await _game.Process.WaitForExitAsync(_cancellation.Token).ConfigureAwait(false);
            _logger.LogInformation("Game {ProcessId} exited", ProcessId);
        }
        catch (OperationCanceledException)
        {
            // The launcher is closing while the game is still running. Leave it alone —
            // the player did not ask for it to be killed.
        }
        finally
        {
            // Released here rather than on dispose: the slot represents a running game, and
            // the game outliving the launcher still counts against the limit only while it
            // is alive. Once it has exited the slot is free whatever the launcher does.
            _slot.Dispose();

            // Same reasoning. A relay with nothing to relay for is a port held open for the
            // rest of the launcher's life.
            if (_relay is not null)
            {
                await _relay.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Stops everything this game had running and hands its resources back.</summary>
    /// <remarks>
    /// Guarded, because there are two owners with a claim on calling it: the launcher, once
    /// the game has exited and there is nothing left to help, and whatever tears down at the
    /// end of the process.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _cancellation.CancelAsync().ConfigureAwait(false);

        // All three are guarded against cancellation internally, so none will throw here.
        await Task.WhenAll(Patching, Helping, Completion).ConfigureAwait(false);

        if (_relay is not null)
        {
            await _relay.DisposeAsync().ConfigureAwait(false);
        }

        // After the helper has stopped, so its last pass still has a game to read and the
        // character's settings are written before the scope that holds them goes.
        _aux.Dispose();

        _cancellation.Dispose();
        _slot.Dispose();
        _game.Dispose();
    }
}
