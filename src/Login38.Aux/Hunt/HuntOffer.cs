namespace Login38.Aux.Hunt;

/// <summary>
/// Whether the server list this game was started from offers automatic hunting.
/// </summary>
/// <remarks>
/// <para>
/// The operator's switch, told to the helper loop rather than only to the settings window.
/// It used to be enforced in one place — where the window writes its copy back — which
/// meant it decided what was drawn and nothing else. The loop read the character's saved
/// profile directly, so a profile that held the hunt switch on was hunted with whatever the
/// operator had said, and a player who had never asked for hunting had a launcher writing
/// to their client's walk and attack flags all session.
/// </para>
/// <para>
/// Off until something says otherwise, because that is the answer that touches nothing. A
/// game whose offer was never read is a game the hunt leaves alone.
/// </para>
/// <para>
/// One per game, like everything else the hunt keeps: two clients can be launched from two
/// server lists, and only one of them may offer this.
/// </para>
/// </remarks>
public sealed class HuntOffer
{
    private volatile bool _offered;

    /// <summary>Whether the hunt may touch this client at all.</summary>
    public bool IsOffered => _offered;

    /// <summary>Says the operator's list offers it.</summary>
    /// <remarks>
    /// One way on purpose. The offer belongs to the list the game was launched from and
    /// cannot change while it is running, so there is nothing to take back.
    /// </remarks>
    public void Offer() => _offered = true;
}
