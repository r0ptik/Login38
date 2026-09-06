using Login38.Aux.Actions;
using Login38.Aux.Game;
using Login38.Aux.Hunt;
using Login38.App.Notifications;
using Login38.Aux.Notifications;
using Login38.Aux.Runtime;
using Login38.Aux.Settings;
using Login38.Aux.Toggles;
using Login38.Core.Configuration;
using Login38.Core.Text;
using Login38.Patching;
using Login38.Patching.Patches;
using Microsoft.Extensions.DependencyInjection;

namespace Login38.App.Services;

/// <summary>Registers everything the launcher needs.</summary>
public static class LauncherServices
{
    /// <summary>Adds the shared services and the patch set, in the order it must run.</summary>
    public static IServiceCollection AddLauncher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ILegacyTextCodec, LegacyTextCodec>();
        services.AddSingleton<LineageConfigFile>();
        services.AddSingleton<ServerCatalog>();
        services.AddSingleton<IServerProbe, TcpServerProbe>();
        services.AddSingleton<PatchPipeline>();
        services.AddSingleton<GameLaunchService>();
        services.AddSingleton<IHelperWindows, HelperWindows>();

        return services.AddGamePatches().AddAuxFeatures();
    }

    /// <summary>
    /// Registers the in-game helper features.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scoped rather than singleton, and that is the whole point of the scope: one of these
    /// per running game. All of it holds per-game state — which character is playing, what
    /// the player has switched on, whether a toggle has already complained about an address
    /// it cannot read. The reference kept the same state in process-wide statics, so with
    /// two clients up the second one's toggles silently did nothing.
    /// </para>
    /// <para>
    /// The store is the exception: it is a directory and a logger, the same for every game,
    /// and the file it writes is named after the character rather than the client.
    /// </para>
    /// <para>
    /// Registration order is the order tasks are visited within a pass, which is not a
    /// correctness constraint the way the patch sequence is — each task reads the same
    /// snapshot of the game whichever position it holds. The profile task comes first
    /// anyway, because it is what decides whose settings the rest of the pass is acting on.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAuxFeatures(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ProfileStore>();

        // A file beside the launcher, the same for every game, and the same whether one is
        // running at all: the window offers its lists before anything has been started.
        services.AddSingleton<ItemCatalog>();

        services.AddScoped<AuxSettingsSource>();
        services.AddScoped<AuxHost>();

        // Whether the player has started the helper on this game. Scoped, so a launcher
        // driving two clients has two of them and Home moves the one in front.
        services.AddScoped<HelperSwitch>();

        // Scoped like the rest: it remembers what the client's own item function looked
        // like the first time it was called, so it can refuse to call it once the packer
        // has moved it. That is a fact about one client.
        services.AddScoped<GameActions>();
        services.AddScoped<Spells>();
        services.AddScoped<EntityScan>();
        services.AddScoped<MonsterScan>();
        services.AddScoped<NotificationHook>();
        services.AddScoped<NotificationBoard>();
        services.AddScoped<SpriteIndex>();
        services.AddScoped<SpriteArtwork>();
        services.AddScoped<GameChat>();
        services.AddScoped<HelperDispatch>();
        services.AddScoped<InventoryWatch>();

        services.AddScoped<IGameToggle, AllDayToggle>();
        services.AddScoped<IGameToggle, LowCpuToggle>();
        services.AddScoped<IGameToggle, DamageToggle>();
        services.AddScoped<IGameToggle, ShowClockToggle>();
        services.AddScoped<IGameToggle, UnderwaterPumpToggle>();

        // Both, and the same instance: the scanner has to ask the toggle where the
        // table it fills in ended up.
        services.AddScoped<MonsterColourToggle>();
        services.AddScoped<IGameToggle>(services => services.GetRequiredService<MonsterColourToggle>());

        // Both, and the same instance: the window's per-row "start again" button has to
        // reach the task that is counting.
        services.AddScoped<TimerTask>();

        services.AddScoped<HelperKeyTask>();

        // The hunt's three pieces. The hook and the chain hold state that belongs to one
        // game — where the cave went, what is being fought — so they are scoped with it
        // rather than shared, which is what the reference got wrong with two clients up.
        services.AddScoped<TargetScan>();
        services.AddScoped<HuntSwitch>();
        services.AddScoped<HuntOffer>();
        services.AddScoped<SpellWatch>();
        services.AddScoped<ChaseHook>();
        services.AddScoped<ClickHook>();
        services.AddScoped<AttackChain>();
        services.AddScoped<RouteWalk>();
        services.AddScoped<SkillVolley>();
        services.AddScoped<CastWatch>();
        services.AddScoped<HuntTask>();

        services.AddScoped<IAuxTask, AnnouncementTask>();
        services.AddScoped<IAuxTask, ProfileTask>();
        services.AddScoped<IAuxTask, ToggleTask>();
        services.AddScoped<IAuxTask, PotionTask>();
        services.AddScoped<IAuxTask, EscapeTask>();
        services.AddScoped<IAuxTask, RelocateTask>();
        services.AddScoped<IAuxTask>(services => services.GetRequiredService<TimerTask>());
        services.AddScoped<IAuxTask, InventoryTask>();
        services.AddScoped<IAuxTask, SpellTask>();
        services.AddScoped<IAuxTask, ShoutTask>();
        services.AddScoped<IAuxTask, HotkeyTask>();
        services.AddScoped<IAuxTask>(services => services.GetRequiredService<HelperKeyTask>());
        services.AddScoped<IAuxTask, DeleteTask>();
        services.AddScoped<IAuxTask, HelperTask>();
        services.AddScoped<IAuxTask, StatusTask>();
        services.AddScoped<IAuxTask, ExperienceTask>();
        services.AddScoped<IAuxTask, MonsterColourTask>();
        services.AddScoped<IAuxTask, NotificationTask>();
        services.AddScoped<IAuxTask, OverlayTask>();

        // Both, and the same instance: the window shows what is being fought.
        services.AddScoped<IAuxTask>(services => services.GetRequiredService<HuntTask>());

        return services;
    }

    /// <summary>
    /// Registers the patch set. Registration order is execution order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a sequence, not a set, and the ordering carries three real constraints.
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// The winsock redirect goes in first, before the protection bypass, so that a client
    /// which connects unusually early still reaches the right server.
    /// </item>
    /// <item>
    /// The protection bypass comes next because it is what waits for the client to unpack
    /// itself. Every signature after it searches bytes that are ciphertext until it
    /// returns.
    /// </item>
    /// <item>
    /// The morph table comes straight after the bypass. The client reads its table early,
    /// and the window between finishing unpacking and reading it is not long — anything
    /// that scans a signature in between could close it.
    /// </item>
    /// <item>
    /// The run-cycle hook comes after the morph table, which is what puts anything in the
    /// slots it reads — and it asks whether that actually happened, not whether it was
    /// configured to.
    /// </item>
    /// <item>
    /// HP/MP runs before AC/MR. Both replace the same two character-select setters; the
    /// second installs versions carrying armour class as well, and keeps writing the
    /// globals the first one's getters read. Reversed, the character-select screen loses
    /// its hit points.
    /// </item>
    /// </list>
    /// <para>
    /// The rest are independent of each other and appear in the order the reference ran
    /// them, so a difference in behaviour against a live client is a difference in the
    /// patches rather than in their sequence.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddGamePatches(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IGamePatch, ConnectRedirectPatch>();
        services.AddSingleton<IGamePatch, TimeProtectionBypassPatch>();
        services.AddSingleton<IGamePatch, MorphTablePatch>();
        services.AddSingleton<IGamePatch, SmoothRunPatch>();
        services.AddSingleton<IGamePatch, LoginHookPatch>();
        services.AddSingleton<IGamePatch, AntiCheatBypassPatch>();
        services.AddSingleton<IGamePatch, CrtWatsonPatch>();
        services.AddSingleton<IGamePatch, ChatWidthPatch>();
        services.AddSingleton<IGamePatch, ImageLimitPatch>();
        services.AddSingleton<IGamePatch, PngLimitPatch>();
        services.AddSingleton<IGamePatch, InventoryLimitPatch>();
        services.AddSingleton<IGamePatch, EquipmentSlotsPatch>();
        services.AddSingleton<IGamePatch, HitPointExpansionPatch>();
        services.AddSingleton<IGamePatch, ArmourResistanceExpansionPatch>();
        services.AddSingleton<IGamePatch, DynamicDialogPatch>();
        services.AddSingleton<IGamePatch, DynamicIconPatch>();
        services.AddSingleton<IGamePatch, ItemDescriptionColourPatch>();
        services.AddSingleton<IGamePatch, ItemDescriptionLengthPatch>();
        services.AddSingleton<IGamePatch, LongItemStatusPatch>();
        services.AddSingleton<IGamePatch, SimplifiedChineseTextPatch>();

        // The rest wait for the client to reach a state, so they run in a later phase
        // whatever their position here. Within a phase this order still holds, and two
        // more constraints live in it: the title is changed before the present hook goes
        // in, so the hook's own window lookup never races the rename; and the input box
        // background comes after the present hook, because it only runs when that hook is
        // not there and cannot know that until it has been tried.
        services.AddSingleton<IGamePatch, WindowTitlePatch>();
        services.AddSingleton<IGamePatch, PresentHookPatch>();
        services.AddSingleton<IGamePatch, SurfacePixelFormatPatch>();
        services.AddSingleton<IGamePatch, InputBoxBackgroundPatch>();
        services.AddSingleton<IGamePatch, MovePacketEncryptionPatch>();

        return services;
    }
}
