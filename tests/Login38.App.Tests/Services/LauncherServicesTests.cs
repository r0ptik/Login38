using Login38.App.Services;
using Login38.App.ViewModels;
using Login38.Aux.Runtime;
using Login38.Aux.Settings;
using Login38.Aux.Toggles;
using Login38.Patching;
using Login38.Patching.Patches;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Login38.App.Tests.Services;

/// <summary>
/// Covers the patch sequence, which is a correctness property rather than a preference.
/// </summary>
/// <remarks>
/// Container registration order is what decides the order the pipeline runs them in, so
/// the ordering constraints live here rather than anywhere they could be read as advice.
/// </remarks>
public sealed class LauncherServicesTests
{
    private static IReadOnlyList<IGamePatch> Patches()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLauncher();

        return [.. services.BuildServiceProvider().GetServices<IGamePatch>()];
    }

    private static int PositionOf<T>(IReadOnlyList<IGamePatch> patches)
        where T : IGamePatch
    {
        for (var i = 0; i < patches.Count; i++)
        {
            if (patches[i] is T)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void RegistersEveryPatch() => Patches().Count.ShouldBe(25);

    [Fact]
    public void GivesEveryPatchADistinctName()
    {
        var names = Patches().Select(p => p.Name).ToList();

        names.Distinct().Count().ShouldBe(names.Count);
    }

    // The client can reach out before the protection bypass has finished waiting for it
    // to unpack. Redirected first, it still reaches the right server.
    [Fact]
    public void RedirectsConnectionsBeforeAnythingElse()
    {
        var patches = Patches();

        PositionOf<ConnectRedirectPatch>(patches).ShouldBe(0);
    }

    // Everything after this searches for signatures. Until it returns, those bytes are
    // still encrypted and every search would miss.
    [Fact]
    public void WaitsForDecryptionBeforeAnySignatureSearch()
    {
        var patches = Patches();
        var bypass = PositionOf<TimeProtectionBypassPatch>(patches);

        foreach (var searching in (int[])
                 [
                     PositionOf<AntiCheatBypassPatch>(patches),
                     PositionOf<ImageLimitPatch>(patches),
                     PositionOf<PngLimitPatch>(patches),
                     PositionOf<InventoryLimitPatch>(patches),
                     PositionOf<EquipmentSlotsPatch>(patches),
                     PositionOf<HitPointExpansionPatch>(patches),
                     PositionOf<ArmourResistanceExpansionPatch>(patches),
                 ])
        {
            searching.ShouldBeGreaterThan(bypass);
        }
    }

    // The client reads its morph table shortly after it finishes unpacking. Anything
    // between the two closes the window this has to land in.
    [Fact]
    public void HooksTheMorphTableImmediatelyAfterDecryption()
    {
        var patches = Patches();

        PositionOf<MorphTablePatch>(patches)
            .ShouldBe(PositionOf<TimeProtectionBypassPatch>(patches) + 1);
    }

    // Slots 98 and 99 are empty until the morph table has been preprocessed and written
    // into the client.
    [Fact]
    public void FillsTheRunSlotsBeforeHookingThem()
    {
        var patches = Patches();

        PositionOf<MorphTablePatch>(patches).ShouldBeLessThan(PositionOf<SmoothRunPatch>(patches));
    }

    // Both replace the character-select setters at 0x00544910 and 0x00544940. AC/MR's
    // versions carry hit points as well; HP/MP's do not carry armour class. Reversed, the
    // character-select screen shows no hit points.
    [Fact]
    public void WidensHitPointsBeforeArmourClass()
    {
        var patches = Patches();

        PositionOf<HitPointExpansionPatch>(patches)
            .ShouldBeLessThan(PositionOf<ArmourResistanceExpansionPatch>(patches));
    }

    [Fact]
    public void RunsMostPatchesAtStartup() =>
        Patches().Count(p => p.Phase == PatchPhase.Startup).ShouldBe(19);

    // On its own, because it waits for the client to load Winsock — about half a minute
    // into a launch. While it sat among the startup patches, every other patch and the
    // helper that starts after them waited behind it for a client already on screen.
    [Fact]
    public void DefersTheConnectRedirectUntilTheClientNeedsTheNetwork() =>
        Patches().Single(p => p.Phase == PatchPhase.Networking)
            .ShouldBeOfType<ConnectRedirectPatch>();

    // These need a window to act on, which the client does not have for the first several
    // seconds of a launch.
    [Fact]
    public void DefersTheWindowPatchesUntilThereIsAWindow() =>
        Patches().Where(p => p.Phase == PatchPhase.WindowVisible).Select(p => p.Name)
            .ShouldBe(["window-title", "present-hook", "surface-pixel-format", "input-box-background"]);

    [Fact]
    public void DefersTheMovementPatchUntilThePlayerIsInTheWorld() =>
        Patches().Single(p => p.Phase == PatchPhase.InWorld)
            .ShouldBeOfType<MovePacketEncryptionPatch>();

    // The present hook finds the window for itself. Renaming afterwards would be a race
    // between the rename and the hook's own lookup.
    [Fact]
    public void RenamesTheWindowBeforeHookingItsPresentation()
    {
        var patches = Patches();

        PositionOf<WindowTitlePatch>(patches).ShouldBeLessThan(PositionOf<PresentHookPatch>(patches));
    }

    // Both compose the input box background, so only one of them runs — and which one is
    // decided by whether the hook actually went in, which is not known until it has been
    // tried.
    [Fact]
    public void TriesThePresentHookBeforeItsFallback()
    {
        var patches = Patches();

        PositionOf<PresentHookPatch>(patches)
            .ShouldBeLessThan(PositionOf<InputBoxBackgroundPatch>(patches));
    }

    // The window is resolved from the container at startup, so a missing registration is
    // a crash on launch with no build error to warn about it.
    [Fact]
    public void ResolvesEverythingTheWindowNeeds()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLauncher();
        services.AddSingleton<MainViewModel>();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<GameLaunchService>().ShouldNotBeNull();
        provider.GetRequiredService<PatchPipeline>().ShouldNotBeNull();
        provider.GetRequiredService<MainViewModel>().ShouldNotBeNull();
    }

    [Fact]
    public void RegistersEveryHelperTask()
    {
        using var scope = Container().CreateScope();

        scope.ServiceProvider.GetServices<IAuxTask>().Select(t => t.Name)
            .ShouldBe(
            [
                "announcement", "profile", "toggles", "potions", "escape", "relocate", "timers",
                "inventory", "spells", "shout", "hotkeys", "helper keys", "delete", "buffs",
                "status", "experience", "monster-colours", "notifications",
                "overlay", "hunt",
            ]);
    }

    [Fact]
    public void RegistersEveryToggle()
    {
        using var scope = Container().CreateScope();

        scope.ServiceProvider.GetServices<IGameToggle>().Select(t => t.Name)
            .ShouldBe(
            [
                "all-day", "low-cpu", "damage-numbers", "show-clock", "underwater-pump",
                "monster-colours",
            ]);
    }

    // The one property that makes the helper safe with more than one client up. Everything
    // in a scope holds state about a particular game — which character is playing, whether
    // a toggle has already complained about an address it cannot read. Shared, the second
    // client gets a helper that believes it has already done the work, which is exactly
    // what the reference's process-wide statics did.
    [Fact]
    public void GivesEachGameItsOwnHelper()
    {
        var provider = Container();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        second.ServiceProvider.GetRequiredService<AuxSettingsSource>()
            .ShouldNotBeSameAs(first.ServiceProvider.GetRequiredService<AuxSettingsSource>());

        second.ServiceProvider.GetRequiredService<AuxHost>()
            .ShouldNotBeSameAs(first.ServiceProvider.GetRequiredService<AuxHost>());

        second.ServiceProvider.GetServices<IGameToggle>().First()
            .ShouldNotBeSameAs(first.ServiceProvider.GetServices<IGameToggle>().First());
    }

    // Named after the character rather than the client, so there is nothing per-game in it.
    [Fact]
    public void SharesOneProfileStoreBetweenGames()
    {
        var provider = Container();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        second.ServiceProvider.GetRequiredService<ProfileStore>()
            .ShouldBeSameAs(first.ServiceProvider.GetRequiredService<ProfileStore>());
    }

    private static ServiceProvider Container()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLauncher();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
