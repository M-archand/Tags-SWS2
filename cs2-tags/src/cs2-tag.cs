using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.NetMessages;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.ProtobufDefinitions;
using TagsApi;
using Tomlyn.Extensions.Configuration;
using static SwiftlyS2.Shared.Helper;
using static Tags.TagExtensions;
using static TagsApi.Tags;

namespace Tags;

[PluginMetadata(Id = "Tags", Version = "v2", Name = "Tags", Author = "schwarper")]
public sealed class Tags(ISwiftlyCore core) : BasePlugin(core)
{
    public static ISwiftlyCore Instance { get; set; } = null!;
    public static readonly Dictionary<ulong, Tag> PlayerTagsList = [];
    public static readonly Dictionary<ulong, DateTime> PlayerJoinUtc = [];
    public static readonly TagsAPI Api = new();
    public static Config Config { get; set; } = null!;

    // Shop_Flags / async player item load tolerance
    private const int ApplyMaxAttempts = 200;          // 200 * 0.2s = 40s
    private const float ApplyRetryDelaySeconds = 0.2f;
    private static readonly TimeSpan PermissionWarmupWindow = TimeSpan.FromSeconds(40);

    // periodic revalidation so tag updates when permissions are removed/expired
    private const float RevalidateIntervalSeconds = 1.0f;
    private static bool _revalidateLoopEnabled;

    public override void Load(bool hotReload)
    {
        Instance = Core;

        const string ConfigFileName = "config.toml";
        const string ConfigSection = "Tags";
        Core.Configuration
            .InitializeTomlWithModel<Config>(ConfigFileName, ConfigSection)
            .Configure(cfg => cfg.AddTomlFile(
                ConfigFileName,
                optional: false,
                reloadOnChange: true));

        ServiceCollection services = new();
        services.AddSwiftly(Core)
            .AddOptionsWithValidateOnStart<Config>()
            .BindConfiguration(ConfigSection);

        var provider = services.BuildServiceProvider();
        Config = provider.GetRequiredService<IOptions<Config>>().Value;

        foreach (string command in Config.Commands.TagsReload)
            Core.Command.RegisterCommand(command, Command_Tags_Reload, true, "tags.reload");

        foreach (string command in Config.Commands.Visibility)
            Core.Command.RegisterCommand(command, Command_Visibility, true, "tags.visibility");

        Tags.Config.Settings.Init();

        // Align with Shop_Flags (it applies permissions on world update)
        Core.Scheduler.NextWorldUpdate(() => ReloadTags());

        // start periodic permission->tag revalidation loop
        _revalidateLoopEnabled = true;
        ScheduleRevalidateLoop();

        if (hotReload)
            ReloadTags();
    }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager)
    {
        interfaceManager.AddSharedInterface<ITagApi, TagsAPI>("Tags.Api", Api);
    }

    public override void Unload()
    {
        _revalidateLoopEnabled = false;

        PlayerTagsList.Clear();
        PlayerJoinUtc.Clear();
    }

    private static void ScheduleRevalidateLoop()
    {
        if (!_revalidateLoopEnabled || Instance == null)
            return;

        Instance.Scheduler.DelayBySeconds(RevalidateIntervalSeconds, () =>
        {
            if (!_revalidateLoopEnabled || Instance == null)
                return;

            Instance.Scheduler.NextWorldUpdate(() =>
            {
                if (!_revalidateLoopEnabled || Instance == null)
                    return;

                RevalidateAllPlayers();
                ScheduleRevalidateLoop();
            });
        });
    }

    private static void RevalidateAllPlayers()
    {
        var players = Instance.PlayerManager.GetAllPlayers();
        foreach (var player in players)
        {
            if (player == null || !player.IsValid || player.IsFakeClient || player.SteamID == 0)
                continue;

            // update tag immediately when permissions disappear, without reconnect
            player.RevalidateTagFromPermissions();
        }
    }

    public static void Command_Tags_Reload(ICommandContext context)
    {
        ReloadConfig();
        ReloadTags();
    }

    public void Command_Visibility(ICommandContext context)
    {
        if (!context.IsSentByPlayer)
            return;

        var player = context.Sender!;
        var localizer = Core.Translation.GetPlayerLocalizer(player);

        if (player.GetVisibility())
        {
            player.SetVisibility(false);
            context.Reply(Config.Settings.Tag.Colored() + localizer["Tags are now hidden"]);
        }
        else
        {
            player.SetVisibility(true);
            context.Reply(Config.Settings.Tag.Colored() + localizer["Tags are now visible"]);
        }
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerConnect(EventPlayerConnectFull @event)
    {
        if (@event.UserIdPlayer is not IPlayer player)
            return HookResult.Continue;

        if (player.IsFakeClient || player.SteamID == 0)
            return HookResult.Continue;

        PlayerJoinUtc[player.SteamID] = DateTime.UtcNow;

        // don't lock-in default tag
        PlayerTagsList.Remove(player.SteamID);

        // attempt apply early, retry on world update (ShopCore async)
        ScheduleApplyAttemptWorld(player, attempt: 1, force: true);

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event)
    {
        if (@event.UserIdPlayer is not IPlayer player)
            return HookResult.Continue;

        PlayerTagsList.Remove(player.SteamID);
        PlayerJoinUtc.Remove(player.SteamID);
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is not { } player)
            return HookResult.Continue;

        if (player.IsFakeClient || player.SteamID == 0)
            return HookResult.Continue;

        ScheduleApplyAttemptWorld(player, attempt: 1, force: false);
        return HookResult.Continue;
    }

    // ✅ NEW: apply tag immediately when the player joins/switches team (no need to wait for next round/spawn)
    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerTeam(EventPlayerTeam @event)
    {
        if (@event.UserIdPlayer is not IPlayer player)
            return HookResult.Continue;

        if (player.IsFakeClient || player.SteamID == 0)
            return HookResult.Continue;

        ScheduleApplyAttemptWorld(player, attempt: 1, force: true);
        return HookResult.Continue;
    }

    private static void ScheduleApplyAttemptWorld(IPlayer player, int attempt, bool force)
    {
        Instance.Scheduler.NextWorldUpdate(() =>
        {
            if (TryApplyTag(player, force))
                return;

            if (attempt >= ApplyMaxAttempts)
                return;

            Instance.Scheduler.DelayBySeconds(
                ApplyRetryDelaySeconds,
                () => ScheduleApplyAttemptWorld(player, attempt + 1, force: true)
            );
        });
    }

    private static bool TryApplyTag(IPlayer player, bool force)
    {
        if (player == null || !player.IsValid || player.IsFakeClient || player.SteamID == 0)
            return false;

        if (PlayerJoinUtc.TryGetValue(player.SteamID, out var joinedUtc))
        {
            if ((DateTime.UtcNow - joinedUtc) <= PermissionWarmupWindow)
                force = true;
        }

        var tag = GetOrCreatePlayerTag(player, force);

        // Respect visibility (hide -> default scoretag)
        player.SetScoreTag(player.GetVisibility() ? tag.ScoreTag : Tags.Config.Default.ScoreTag);

        return true;
    }

    [ServerNetMessageHandler]
    public HookResult OnMessageSayText2(CUserMessageSayText2 msg)
    {
        if (Core.PlayerManager.GetPlayer(msg.Entityindex - 1) is not { } player)
            return HookResult.Continue;

        if (player.IsFakeClient || player.SteamID == 0)
            return HookResult.Continue;

        if (string.IsNullOrEmpty(msg.Param2))
            return HookResult.Continue;

        bool force = false;
        if (PlayerJoinUtc.TryGetValue(player.SteamID, out var joinedUtc))
        {
            if ((DateTime.UtcNow - joinedUtc) <= PermissionWarmupWindow)
                force = true;
        }

        var tag = GetOrCreatePlayerTag(player, force);

        MessageProcess messageProcess = new()
        {
            Player = player,
            Tag = !player.GetVisibility() ? Config.Default.Clone() : tag.Clone(),
            Message = msg.Param2.RemoveCurlyBraceContent(),
            PlayerName = msg.Param1,
            ChatSound = msg.Chat,
            TeamMessage = !msg.Messagename.Contains("All")
        };

        if (string.IsNullOrEmpty(messageProcess.Message))
            return HookResult.Handled;

        HookResult hookResult = Api.MessageProcessPre(messageProcess);
        if (hookResult >= HookResult.Stop)
            return hookResult;

        string prefixname =
            player.Controller.PawnIsAlive || player.Controller.Team == Team.Spectator
                ? player.Controller.Team.PrefixName()
                : Config.Settings.DeadName;

        string teamname = messageProcess.TeamMessage ? player.Controller.Team.Name() : string.Empty;

        Tag playerData = messageProcess.Tag;

        Team team = player.Controller.Team;
        messageProcess.PlayerName = FormatMessage(
            team,
            prefixname,
            teamname,
            playerData.ChatTag ?? string.Empty,
            playerData.NameColor ?? string.Empty,
            messageProcess.PlayerName
        );

        messageProcess.Message = FormatMessage(team, playerData.ChatColor ?? string.Empty, messageProcess.Message);

        hookResult = Api.MessageProcess(messageProcess);
        if (hookResult >= HookResult.Stop)
            return hookResult;

        msg.Messagename = $" {messageProcess.PlayerName}\u0001: {messageProcess.Message}";
        msg.Chat = playerData.ChatSound;

        Api.MessageProcessPost(messageProcess);
        return HookResult.Continue;
    }
}
