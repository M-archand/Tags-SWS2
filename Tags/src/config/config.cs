using System.Text.Json.Serialization;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using static TagsApi.Tags;

namespace Tags;

public sealed class Config
{
    public Settings Settings { get; set; } = new();
    public Commands Commands { get; set; } = new();
    public Tag Default { get; set; } = new();
    public List<Tag> Tags { get; set; } = [];
}

public sealed class Settings
{
    public string Tag { get; set; } = string.Empty;
    public string DeadName { get; set; } = string.Empty;
    public string NonePrefixName { get; set; } = string.Empty;
    public string TPrefixName { get; set; } = string.Empty;
    public string CTPrefixName { get; set; } = string.Empty;
    public string SpecPrefixName { get; set; } = string.Empty;
    public string NoneTeamChatName { get; set; } = string.Empty;
    public string SpecTeamChatName { get; set; } = string.Empty;
    public string TTeamChatName { get; set; } = string.Empty;
    public string CTTeamChatName { get; set; } = string.Empty;

    [JsonIgnore]
    public Dictionary<Team, string> TeamChatNames { get; set; } = [];

    [JsonIgnore]
    public Dictionary<Team, string> TeamPrefixNames { get; set; } = [];

    public void Init()
    {
        TeamChatNames.Clear();
        TeamChatNames[Team.None] = NoneTeamChatName;
        TeamChatNames[Team.Spectator] = SpecTeamChatName;
        TeamChatNames[Team.T] = TTeamChatName;
        TeamChatNames[Team.CT] = CTTeamChatName;

        TeamPrefixNames.Clear();
        TeamPrefixNames[Team.None] = NonePrefixName;
        TeamPrefixNames[Team.Spectator] = SpecPrefixName;
        TeamPrefixNames[Team.T] = TPrefixName;
        TeamPrefixNames[Team.CT] = CTPrefixName;
    }
}

public sealed class Commands
{
    public string[] TagsReload { get; set; } = [];
    public string[] Visibility { get; set; } = [];
}