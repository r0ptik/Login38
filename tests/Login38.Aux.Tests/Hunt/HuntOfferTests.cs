using Login38.Aux.Hunt;
using Shouldly;

namespace Login38.Aux.Tests.Hunt;

/// <summary>
/// Covers the operator's switch as the helper loop reads it.
/// </summary>
/// <remarks>
/// The switch used to live only where the settings window writes its copy back, so it
/// decided which tab was drawn and nothing else: the loop read the character's saved profile
/// straight, and a profile holding the hunt switch on was hunted with on a server that had
/// never offered hunting. Players reported it as the client needing a left click for every
/// step and every swing, because a hunt nobody asked for was writing the client's walk and
/// attack flags underneath them.
/// </remarks>
public sealed class HuntOfferTests
{
    // The answer that touches nothing. A game whose offer was never read is a game the hunt
    // has to leave alone, so the default cannot be the permissive one.
    [Fact]
    public void IsNotOfferedUntilSomethingSaysSo() => new HuntOffer().IsOffered.ShouldBeFalse();

    [Fact]
    public void IsOfferedOnceItHasBeenSaid()
    {
        var offer = new HuntOffer();

        offer.Offer();

        offer.IsOffered.ShouldBeTrue();
    }

    // The offer belongs to the server list the game was launched from and cannot change
    // while it is running, so saying it twice is saying it once.
    [Fact]
    public void StaysOfferedWhenSaidAgain()
    {
        var offer = new HuntOffer();

        offer.Offer();
        offer.Offer();

        offer.IsOffered.ShouldBeTrue();
    }
}
