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
    public static readonly TagsAPI Api = new();
    public static Config Config { get; set; } = null!;

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

        if (hotReload)
            ReloadTags();
    }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager)
    {
        interfaceManager.AddSharedInterface<ITagApi, TagsAPI>("Tags.Api", Api);
    }

    public override void Unload()
    {
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

        PlayerTagsList[player.SteamID] = player.GetTag();
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event)
    {
        if (@event.UserIdPlayer is not IPlayer player)
            return HookResult.Continue;

        PlayerTagsList.Remove(player.SteamID);
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is not { } player)
            return HookResult.Continue;

        var tag = GetOrCreatePlayerTag(player, false);
        player.SetScoreTag(tag.ScoreTag);
        return HookResult.Continue;
    }

    [ServerNetMessageHandler]
    public HookResult OnMessageSayText2(CUserMessageSayText2 msg)
    {
        if (Core.PlayerManager.GetPlayer(msg.Entityindex - 1) is not { } player)
            return HookResult.Continue;

        if (string.IsNullOrEmpty(msg.Param2))
            return HookResult.Continue;

        var tag = GetOrCreatePlayerTag(player, false);

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

        string prefixname = player.Controller.PawnIsAlive || player.Controller.Team == Team.Spectator ? player.Controller.Team.PrefixName() : Config.Settings.DeadName;
        string teamname = messageProcess.TeamMessage ? player.Controller.Team.Name() : string.Empty;

        Tag playerData = messageProcess.Tag;

        Team team = player.Controller.Team;
        messageProcess.PlayerName = FormatMessage(team, prefixname, teamname, playerData.ChatTag ?? string.Empty, playerData.NameColor ?? string.Empty, messageProcess.PlayerName);
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
